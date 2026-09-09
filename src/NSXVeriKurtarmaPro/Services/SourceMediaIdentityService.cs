using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal readonly record struct SourceMediaIdentityExpectation(
    int? PhysicalDriveNumber,
    string SerialNumber,
    uint? VolumeSerialNumber,
    long ViewLengthBytes,
    string FileSystem,
    bool IsWholePhysicalDisk,
    bool IsPartitionSource,
    bool IsPortableMountedSource,
    long PartitionOffsetBytes,
    long PartitionLengthBytes);

internal readonly record struct SourceMediaIdentityObservation(
    bool VolumeReady,
    bool TopologyResolved,
    int? PhysicalDriveNumber,
    string SerialNumber,
    uint? VolumeSerialNumber,
    long PhysicalLengthBytes,
    long ViewLengthBytes,
    string FileSystem);

internal readonly record struct SourceMediaIdentityCheck(bool IsSafe, string Message);

internal sealed class SourceMediaIdentityException : IOException
{
    public SourceMediaIdentityException(string message) : base(message)
    {
    }
}

/// <summary>
/// Bir StorageDeviceInfo oluşturulduktan sonra fiziksel aygıt numarası yeniden kullanılabilir,
/// sürücü harfi başka bir medyaya bağlanabilir veya USB/SD kart değiştirilebilir. Ham okuma
/// başlamadan hemen önce mevcut kaynağı yeniden doğrular. Fiziksel kaynaklarda PhysicalDrive
/// kimliği fail-closed kalır; mounted volume kaynaklarında ise volume seri no + dosya sistemi +
/// kapasite birincil kimliktir. Böylece USB/NVMe bridge sürücülerinde ikinci PhysicalDrive
/// handle'ının açılamaması normal taramayı yanlışlıkla engellemez.
/// </summary>
internal static class SourceMediaIdentityService
{
    public static void EnsureCurrentSource(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        SourceMediaIdentityCheck check = CheckCurrentSource(device);
        if (!check.IsSafe)
        {
            throw new SourceMediaIdentityException(
                "Kaynak medya kimliği güvenli biçimde doğrulanamadı. " +
                check.Message +
                " Kaynak aygıtı yeniden seçip taramayı/kurtarmayı o medya üzerinden başlatın.");
        }
    }

    public static SourceMediaIdentityCheck CheckCurrentSource(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (!OperatingSystem.IsWindows())
            return new SourceMediaIdentityCheck(true, "Windows dışı ortamda ham aygıt açma zaten devre dışıdır.");

        SourceMediaIdentityExpectation expected = BuildExpectation(device);
        SourceMediaIdentityObservation observed = ObserveCurrent(device);
        return EvaluateForSafety(expected, observed);
    }

    internal static SourceMediaIdentityCheck EvaluateForSafety(
        SourceMediaIdentityExpectation expected,
        SourceMediaIdentityObservation observed)
    {
        bool physicalSource = expected.IsWholePhysicalDisk || expected.IsPartitionSource;

        if (expected.IsPartitionSource)
        {
            if (expected.PartitionOffsetBytes < 0 || expected.PartitionLengthBytes <= 0)
                return Unsafe("Kayıtlı bölüm geometrisi geçersiz.");

            if (expected.PartitionOffsetBytes > long.MaxValue - expected.PartitionLengthBytes)
                return Unsafe("Kayıtlı bölüm geometrisi taşma üretiyor.");
        }

        // Physical/partition sources must always resolve to the same PhysicalDrive.
        if (physicalSource && expected.PhysicalDriveNumber.HasValue)
        {
            if (!observed.TopologyResolved || !observed.PhysicalDriveNumber.HasValue)
                return Unsafe("Kaynağın güncel fiziksel disk eşlemesi çözümlenemedi; eski disk numarasına güvenilmedi.");

            if (expected.PhysicalDriveNumber.Value != observed.PhysicalDriveNumber.Value)
            {
                return Unsafe(
                    $"Kaynak PhysicalDrive değişti: beklenen {expected.PhysicalDriveNumber.Value}, " +
                    $"güncel {observed.PhysicalDriveNumber.Value}.");
            }
        }
        // Mounted volumes are opened by their volume handle. If Windows can still resolve the
        // physical disk we compare it, but failure to open/resolve a second PhysicalDrive is not
        // by itself a reason to reject an otherwise identical mounted volume.
        else if (!physicalSource &&
                 expected.PhysicalDriveNumber.HasValue &&
                 observed.TopologyResolved &&
                 observed.PhysicalDriveNumber.HasValue &&
                 expected.PhysicalDriveNumber.Value != observed.PhysicalDriveNumber.Value)
        {
            // USB flash / SD card readers are the one mounted-volume class where Windows can
            // legitimately expose a different PhysicalDrive mapping between enumeration and the
            // subsequent raw-volume open (bridge re-enumeration, reader slot indirection, etc.).
            // Do not bind Quick/Deep reads to that advisory mapping when the mounted volume itself
            // is still the exact same volume: serial + capacity + filesystem must all match.
            // Fixed disks keep the stricter PhysicalDrive comparison unchanged.
            if (!HasStablePortableVolumeAnchor(expected, observed))
            {
                return Unsafe(
                    $"Kaynak volume farklı bir PhysicalDrive üzerinde görünüyor: beklenen {expected.PhysicalDriveNumber.Value}, " +
                    $"güncel {observed.PhysicalDriveNumber.Value}.");
            }
        }

        if (!physicalSource)
        {
            if (!observed.VolumeReady)
                return Unsafe("Kaynak volume artık hazır değil.");

            if (expected.VolumeSerialNumber.HasValue)
            {
                if (!observed.VolumeSerialNumber.HasValue)
                    return Unsafe("Kaynak volume seri numarası artık okunamıyor; kimlik doğrulaması açık geçmedi.");

                if (expected.VolumeSerialNumber.Value != observed.VolumeSerialNumber.Value)
                    return Unsafe("Kaynak volume seri numarası değişti; sürücü harfine farklı medya bağlanmış olabilir.");
            }

            if (expected.ViewLengthBytes > 0 && observed.ViewLengthBytes != expected.ViewLengthBytes)
            {
                return Unsafe(
                    $"Kaynak volume kapasitesi değişti: beklenen {expected.ViewLengthBytes:N0} B, " +
                    $"güncel {observed.ViewLengthBytes:N0} B.");
            }

            string expectedFileSystem = Normalize(expected.FileSystem);
            if (expectedFileSystem.Length > 0)
            {
                string observedFileSystem = Normalize(observed.FileSystem);
                if (observedFileSystem.Length == 0 ||
                    !string.Equals(expectedFileSystem, observedFileSystem, StringComparison.OrdinalIgnoreCase))
                {
                    return Unsafe("Kaynak volume dosya sistemi kimliği değişti.");
                }
            }
        }

        string expectedSerial = Normalize(expected.SerialNumber);
        if (expectedSerial.Length > 0)
        {
            string observedSerial = Normalize(observed.SerialNumber);
            if (observedSerial.Length == 0)
            {
                // Physical sources still fail closed. A mounted volume can remain safe when its
                // stable volume serial + geometry + filesystem have already matched, because some
                // bridge drivers intermittently hide the hardware serial from a metadata query.
                if (physicalSource || !expected.VolumeSerialNumber.HasValue ||
                    observed.VolumeSerialNumber != expected.VolumeSerialNumber)
                {
                    return Unsafe("Daha önce bilinen kaynak seri numarası artık okunamıyor; kimlik doğrulaması açık geçmedi.");
                }
            }
            else if (!string.Equals(expectedSerial, observedSerial, StringComparison.OrdinalIgnoreCase))
            {
                bool stableMountedVolumeIdentity = !physicalSource &&
                    expected.VolumeSerialNumber.HasValue &&
                    observed.VolumeSerialNumber == expected.VolumeSerialNumber &&
                    (!observed.TopologyResolved || HasStablePortableVolumeAnchor(expected, observed));
                if (!stableMountedVolumeIdentity)
                    return Unsafe("Kaynak aygıt seri numarası değişti; farklı medya takılmış olabilir.");
            }
        }

        if (expected.IsWholePhysicalDisk)
        {
            if (observed.PhysicalLengthBytes <= 0)
                return Unsafe("Fiziksel kaynak kapasitesi yeniden okunamadı.");

            if (expected.ViewLengthBytes > 0 && observed.PhysicalLengthBytes != expected.ViewLengthBytes)
            {
                return Unsafe(
                    $"Fiziksel kaynak kapasitesi değişti: beklenen {expected.ViewLengthBytes:N0} B, " +
                    $"güncel {observed.PhysicalLengthBytes:N0} B.");
            }
        }
        else if (expected.IsPartitionSource)
        {
            if (observed.PhysicalLengthBytes <= 0)
                return Unsafe("Bölümün bağlı olduğu fiziksel disk kapasitesi yeniden okunamadı.");

            long partitionEnd = expected.PartitionOffsetBytes + expected.PartitionLengthBytes;
            if (partitionEnd > observed.PhysicalLengthBytes)
                return Unsafe("Kayıtlı bölüm sınırları güncel fiziksel diskin dışına taşıyor.");

            if (expected.ViewLengthBytes > 0 && expected.ViewLengthBytes != expected.PartitionLengthBytes)
                return Unsafe("Kayıtlı bölüm görünüm uzunluğu ile bölüm geometrisi eşleşmiyor.");
        }

        return new SourceMediaIdentityCheck(true, "Kaynak medya kimliği güncel kaynakla doğrulandı.");
    }

    private static SourceMediaIdentityExpectation BuildExpectation(StorageDeviceInfo device)
    {
        long viewLength = device.IsPartitionSource && device.PartitionLengthBytes > 0
            ? device.PartitionLengthBytes
            : Math.Max(0, device.TotalBytes);

        return new SourceMediaIdentityExpectation(
            device.PhysicalDriveNumber,
            device.HardwareSerialNumber ?? string.Empty,
            device.VolumeSerialNumber,
            viewLength,
            device.FileSystem ?? string.Empty,
            device.IsWholePhysicalDisk,
            device.IsPartitionSource,
            IsPortableMountedSource(device),
            Math.Max(0, device.PartitionOffsetBytes),
            Math.Max(0, device.PartitionLengthBytes));
    }

    private static SourceMediaIdentityObservation ObserveCurrent(StorageDeviceInfo device)
    {
        bool volumeReady = device.IsWholePhysicalDisk || device.IsPartitionSource;
        long currentViewLength = 0;
        string currentFileSystem = device.FileSystem ?? string.Empty;
        uint? currentVolumeSerial = null;
        string directVolumeHardwareSerial = string.Empty;
        int? currentPhysicalDrive = null;
        bool topologyResolved = false;

        if (device.IsWholePhysicalDisk || device.IsPartitionSource)
        {
            currentPhysicalDrive = device.PhysicalDriveNumber;
            topologyResolved = currentPhysicalDrive.HasValue;
        }
        else
        {
            if (VolumeIdentityService.TryGetIdentity(device.RootPath, out VolumeIdentityInfo volumeIdentity))
            {
                volumeReady = volumeIdentity.IsReady;
                if (volumeReady)
                {
                    currentViewLength = volumeIdentity.TotalBytes;
                    currentFileSystem = volumeIdentity.FileSystem;
                    currentVolumeSerial = volumeIdentity.SerialNumber;
                }
            }
            else
            {
                volumeReady = false;
            }

            directVolumeHardwareSerial = StorageBusTypeService.TryGetDescriptor(device.RootPath).SerialNumber ?? string.Empty;

            PhysicalDiskResolution resolution = VolumeDeviceNumberService.ResolveSourcePhysicalDisk(device.RootPath);
            if (resolution.IsResolved)
            {
                currentPhysicalDrive = resolution.PhysicalDriveNumber;
                topologyResolved = currentPhysicalDrive.HasValue;
            }
        }

        long physicalLength = 0;
        string serial = string.Empty;
        if (currentPhysicalDrive is int physicalDriveNumber)
        {
            if (PhysicalDriveAccessService.TryGetIdentityInfo(physicalDriveNumber, out PhysicalDriveIdentityInfo identity))
            {
                physicalLength = Math.Max(0, identity.LengthBytes);
                serial = identity.Descriptor.SerialNumber ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(serial))
            {
                StorageDescriptorInfo descriptor = device.IsWholePhysicalDisk || device.IsPartitionSource
                    ? StorageBusTypeService.TryGetPhysicalDescriptor(physicalDriveNumber)
                    : StorageBusTypeService.TryGetDescriptor(device.RootPath);
                serial = descriptor.SerialNumber ?? string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(serial) && !string.IsNullOrWhiteSpace(directVolumeHardwareSerial))
            serial = directVolumeHardwareSerial;

        if (device.IsWholePhysicalDisk)
            currentViewLength = physicalLength;
        else if (device.IsPartitionSource)
            currentViewLength = Math.Max(0, device.PartitionLengthBytes);

        return new SourceMediaIdentityObservation(
            volumeReady,
            topologyResolved,
            currentPhysicalDrive,
            serial,
            currentVolumeSerial,
            physicalLength,
            currentViewLength,
            currentFileSystem);
    }

    internal static bool HasStablePortableVolumeAnchor(
        SourceMediaIdentityExpectation expected,
        SourceMediaIdentityObservation observed)
    {
        if (expected.IsWholePhysicalDisk || expected.IsPartitionSource || !expected.IsPortableMountedSource)
            return false;

        if (!observed.VolumeReady ||
            !expected.VolumeSerialNumber.HasValue ||
            !observed.VolumeSerialNumber.HasValue ||
            expected.VolumeSerialNumber.Value != observed.VolumeSerialNumber.Value)
        {
            return false;
        }

        if (expected.ViewLengthBytes > 0 && observed.ViewLengthBytes != expected.ViewLengthBytes)
            return false;

        string expectedFileSystem = Normalize(expected.FileSystem);
        string observedFileSystem = Normalize(observed.FileSystem);
        return expectedFileSystem.Length > 0 &&
               observedFileSystem.Length > 0 &&
               string.Equals(expectedFileSystem, observedFileSystem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPortableMountedSource(StorageDeviceInfo device)
    {
        if (device.IsWholePhysicalDisk || device.IsPartitionSource)
            return false;

        string visualKind = Normalize(device.VisualKind);
        return visualKind.Equals("Usb", StringComparison.OrdinalIgnoreCase) ||
               visualKind.Equals("Sd", StringComparison.OrdinalIgnoreCase);
    }

    private static SourceMediaIdentityCheck Unsafe(string message) => new(false, message);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim();
}
