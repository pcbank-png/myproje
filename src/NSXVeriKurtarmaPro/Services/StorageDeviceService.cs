using System.IO;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class StorageDeviceService
{
    private readonly PartitionDiscoveryService _partitionDiscoveryService = new();
    private readonly object _discoveredPartitionSync = new();
    private readonly Dictionary<string, PartitionCandidate> _discoveredPartitions = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterDiscoveredPartitions(IEnumerable<PartitionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        lock (_discoveredPartitionSync)
        {
            foreach (PartitionCandidate candidate in candidates)
                _discoveredPartitions[candidate.Key] = candidate;
        }
    }
    public bool TryResolveQuickScanPartition(
        StorageDeviceInfo physicalDevice,
        out StorageDeviceInfo? target,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(physicalDevice);
        target = null;
        detail = string.Empty;

        if (!physicalDevice.IsWholePhysicalDisk || physicalDevice.PhysicalDriveNumber is not int physicalDriveNumber)
        {
            detail = "Hızlı Tarama bölüm çözümü için tüm fiziksel disk seçilmelidir.";
            return false;
        }

        if (!PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical))
        {
            detail = $"PhysicalDrive{physicalDriveNumber} geometrisi okunamadı.";
            return false;
        }

        var candidates = new Dictionary<string, PartitionCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (PartitionCandidate candidate in _partitionDiscoveryService.DiscoverPartitionTableCandidates(physical))
        {
            if (candidate.FileSystem is "NTFS" or "FAT" or "FAT12" or "FAT16" or "FAT32" or "exFAT")
                candidates[candidate.Key] = candidate;
        }

        lock (_discoveredPartitionSync)
        {
            foreach (PartitionCandidate candidate in _discoveredPartitions.Values.Where(item =>
                         item.PhysicalDriveNumber == physicalDriveNumber &&
                         item.FileSystem is "NTFS" or "FAT" or "FAT12" or "FAT16" or "FAT32" or "exFAT"))
            {
                candidates[candidate.Key] = candidate;
            }
        }

        List<PartitionCandidate> ordered = candidates.Values
            .Where(candidate => candidate.LengthBytes >= 16L * 1024 * 1024)
            .OrderByDescending(candidate => candidate.LengthBytes)
            .ToList();
        if (ordered.Count == 0)
        {
            detail = $"PhysicalDrive{physicalDriveNumber} partition tablosunda hızlı taranabilir NTFS/FAT12/FAT16/FAT32/exFAT bölüm doğrulanamadı.";
            return false;
        }

        PartitionCandidate largest = ordered[0];
        bool safeSingleDataPartition = ordered.Count == 1 || ordered.Skip(1).All(candidate =>
            candidate.LengthBytes <= 2L * 1024 * 1024 * 1024 ||
            candidate.LengthBytes <= largest.LengthBytes / 20);
        if (!safeSingleDataPartition)
        {
            detail = $"PhysicalDrive{physicalDriveNumber} üzerinde {ordered.Count:N0} ayrı veri bölümü bulundu. Yanlış bölümü seçmemek için bölüm kartını seçin.";
            return false;
        }

        target = CreatePartitionDevice(
            largest,
            physical,
            physicalDevice.VisualKind,
            physicalDevice.CameraContentDetected);
        detail = largest.Scheme.Contains("Legacy Hint", StringComparison.OrdinalIgnoreCase)
            ? $"{physicalDevice.DisplayName} için eski MBR bölüm kaydı bulundu. VBR doğrulaması zayıf olduğu için {largest.FileSystem} Legacy Metadata Rescue salt-okunur olarak başlatılacak."
            : largest.Scheme.Contains("Fast Backup VBR", StringComparison.OrdinalIgnoreCase)
                ? $"{physicalDevice.DisplayName} için partition tablosu kullanılamasa da backup VBR üzerinden {largest.FileSystem} bölümü doğrulandı ve Hızlı Tarama kaynağı otomatik oluşturuldu."
                : largest.Scheme.Contains("Fast VBR Probe", StringComparison.OrdinalIgnoreCase)
                    ? $"{physicalDevice.DisplayName} için partition tablosu kullanılamasa da {largest.FileSystem} VBR doğrudan doğrulandı ve Hızlı Tarama kaynağı otomatik oluşturuldu."
                    : largest.Confidence < 96
                        ? $"{physicalDevice.DisplayName} için primary VBR hasarlı olsa da yedek VBR ile {largest.FileSystem} bölümü doğrulandı ve Hızlı Tarama kaynağı otomatik oluşturuldu."
                        : $"{physicalDevice.DisplayName} için {largest.FileSystem} bölümü partition tablosundan doğrulanıp Hızlı Tarama kaynağı otomatik oluşturuldu.";
        return true;
    }

    /// <summary>
    /// Unified one-button scan icin RAW/format isteyen whole PhysicalDrive kaynagini,
    /// partition tablosu veya backup VBR yeterince guvenliyse ayni diskin salt-okunur
    /// partition gorunumune baglar. Boylece metadata parser dogru partition-relative
    /// offsetlerle calisir; cozum guvenli degilse caller whole-disk RAW taramaya devam eder.
    /// </summary>
    public bool TryResolveUnifiedScanPartition(
        StorageDeviceInfo physicalDevice,
        out StorageDeviceInfo? target,
        out string detail)
    {
        if (!TryResolveQuickScanPartition(physicalDevice, out target, out string quickDetail) || target is null)
        {
            detail = quickDetail;
            return false;
        }

        string confidenceText = target.PartitionScheme.Contains("Legacy Hint", StringComparison.OrdinalIgnoreCase)
            ? "partition kaydi bulundu; VBR zayif olsa da metadata motoru salt-okunur dogrulayacak"
            : target.PartitionScheme.Contains("Fast Backup VBR", StringComparison.OrdinalIgnoreCase)
                ? "partition tablosu kullanilamasa da backup VBR ile bolum dogrulandi"
                : target.PartitionScheme.Contains("Fast VBR Probe", StringComparison.OrdinalIgnoreCase)
                    ? "partition tablosu kullanilamasa da dosya sistemi VBR'i dogrudan dogrulandi"
                    : target.PartitionConfidence < 96
                        ? "primary VBR hasarli olsa da partition/backup VBR ile bolum dogrulandi"
                        : "partition tablosu ve dosya sistemi boot kaydi dogrulandi";

        detail = $"{physicalDevice.DisplayName}: Windows birimi RAW/biçimlendirme gerekli gosterse bile {target.FileSystem} {confidenceText}. " +
                 "Tarama ayni fiziksel aygitin dogru bolum gorunumunde metadata-first baslayacak; ardindan o bolumun tam RAW taramasi kesintisiz surer.";
        return true;
    }

    public bool TryCreateLegacyPhysicalQuickScanView(
        StorageDeviceInfo mountedVolume,
        out StorageDeviceInfo? target,
        out string detail)
    {
        ArgumentNullException.ThrowIfNull(mountedVolume);
        target = null;
        detail = string.Empty;

        string fs = (mountedVolume.FileSystem ?? string.Empty).Trim().ToUpperInvariant();
        if (mountedVolume.IsWholePhysicalDisk || mountedVolume.IsPartitionSource ||
            (fs != "NTFS" && fs != "FAT32" && fs != "EXFAT") ||
            mountedVolume.PhysicalDriveNumber is not int physicalDriveNumber ||
            mountedVolume.PartitionOffsetBytes <= 0 ||
            mountedVolume.PartitionLengthBytes < 16L * 1024 * 1024)
        {
            return false;
        }

        if (!PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical))
            return false;

        bool rotational = physical.Media.MediaKind == StorageMediaKind.Rotational ||
                          mountedVolume.VisualKind.Contains("Hdd", StringComparison.OrdinalIgnoreCase) ||
                          mountedVolume.VisualKind.Contains("FixedDisk", StringComparison.OrdinalIgnoreCase) ||
                          mountedVolume.Kind.Contains("HDD", StringComparison.OrdinalIgnoreCase);
        if (!rotational || physical.Media.MediaKind == StorageMediaKind.SolidState || mountedVolume.TrimEnabled == true)
            return false;

        StorageBusType busType = physical.Descriptor.BusType;
        bool legacyControllerPath = busType is StorageBusType.Sata or StorageBusType.Ata or StorageBusType.Atapi or
            StorageBusType.Scsi or StorageBusType.Raid or StorageBusType.Sas or StorageBusType.Unknown;
        if (!legacyControllerPath)
            return false;

        long available = Math.Max(0, physical.LengthBytes - mountedVolume.PartitionOffsetBytes);
        long viewLength = Math.Min(mountedVolume.PartitionLengthBytes, available);
        if (viewLength < 16L * 1024 * 1024)
            return false;

        target = new StorageDeviceInfo
        {
            RootPath = $@"\\.\PhysicalDrive{physicalDriveNumber}#PART:{mountedVolume.PartitionOffsetBytes:X}:{viewLength:X}",
            DisplayName = $"{mountedVolume.DisplayName} • Physical RAW Quick",
            Kind = $"{mountedVolume.Kind} • Legacy Quick",
            KindGlyph = mountedVolume.KindGlyph,
            VisualKind = mountedVolume.VisualKind,
            FileSystem = fs,
            TotalBytes = viewLength,
            FreeBytes = mountedVolume.FreeBytes,
            IsReady = true,
            PhysicalDriveNumber = physicalDriveNumber,
            IsPartitionSource = true,
            IsRecoveredPartition = false,
            PartitionOffsetBytes = mountedVolume.PartitionOffsetBytes,
            PartitionLengthBytes = viewLength,
            PartitionScheme = "Mounted Physical RAW View",
            PartitionIdentity = $"MOUNTED-{physicalDriveNumber}-{mountedVolume.PartitionOffsetBytes:X}-{viewLength:X}",
            PartitionConfidence = 100,
            HardwareSerialNumber = physical.Descriptor.SerialNumber,
            MediaDetectionSource = physical.Media.DetectionSource,
            BusTypeText = GetBusTypeText(physical.Descriptor.BusType),
            TrimEnabled = physical.Media.TrimEnabled,
            CameraContentDetected = mountedVolume.CameraContentDetected,
            CameraDetectionSource = mountedVolume.CameraDetectionSource,
            HealthSeverity = (int)physical.Health.Severity,
            SmartAvailable = physical.Health.SmartAvailable,
            SmartPredictFailure = physical.Health.PredictFailure,
            SmartReallocatedSectors = physical.Health.ReallocatedSectors,
            SmartPendingSectors = physical.Health.PendingSectors,
            SmartUncorrectableSectors = Math.Max(physical.Health.ReportedUncorrectable, physical.Health.OfflineUncorrectable),
            SmartTemperatureC = physical.Health.TemperatureC,
            HealthSummary = physical.Health.Summary
        };

        detail = $"{mountedVolume.DisplayName} eski/dönel disk olarak algılandı. Hızlı Tarama Windows volume katmanı yerine PhysicalDrive{physicalDriveNumber} üzerindeki aynı bölümün salt-okunur fiziksel görünümünde çalıştırılacak.";
        return true;
    }

    public IReadOnlyList<StorageDeviceInfo> GetDevices()
    {
        var result = new List<StorageDeviceInfo>();
        var physicalDescriptors = new Dictionary<int, StorageDescriptorInfo>();
        var physicalMedia = new Dictionary<int, StorageMediaInfo>();
        var mountedExtents = new List<VolumeDiskExtentInfo>();
        var mountedPhysicalDrives = new HashSet<int>();
        var systemPhysicalDrives = new HashSet<int>();
        var partitionCandidateCache = new Dictionary<int, IReadOnlyList<PartitionCandidate>>();

        // Enumerate physical disks first. Mounted volumes are then correlated against this
        // snapshot. Windows occasionally refuses VOLUME_DISK_EXTENTS / STORAGE_DEVICE_NUMBER
        // on bridge drivers even though the drive letter is perfectly readable. In that case
        // we use a unique serial/partition-geometry match instead of losing the association.
        IReadOnlyList<PhysicalDriveInfo> physicalDrives = PhysicalDriveAccessService.Enumerate();
        foreach (PhysicalDriveInfo physical in physicalDrives)
        {
            physicalDescriptors[physical.Number] = physical.Descriptor;
            physicalMedia[physical.Number] = physical.Media;
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives().OrderBy(d => d.Name))
        {
            try
            {
                bool isRemovable = drive.DriveType == DriveType.Removable;
                bool isFixed = drive.DriveType == DriveType.Fixed;

                if ((!isRemovable && !isFixed) || !drive.IsReady || drive.TotalSize <= 0)
                    continue;

                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd('\\')
                    : drive.VolumeLabel.Trim();

                VolumeDiskExtentInfo? extent = ResolveMountedExtent(
                    drive,
                    physicalDrives,
                    partitionCandidateCache);
                int? physicalDriveNumber = extent?.PhysicalDriveNumber;

                if (physicalDriveNumber is int mountedPhysicalDriveNumber)
                    mountedPhysicalDrives.Add(mountedPhysicalDriveNumber);
                if (extent is VolumeDiskExtentInfo mountedExtent)
                    mountedExtents.Add(mountedExtent);

                StorageDescriptorInfo descriptor = GetDescriptor(drive.RootDirectory.FullName, physicalDriveNumber, physicalDescriptors);
                StorageMediaInfo media = GetMediaInfo(drive.RootDirectory.FullName, physicalDriveNumber, descriptor, physicalMedia);
                StorageHealthInfo health = physicalDriveNumber is int mountedPhysicalDrive
                    ? StorageHealthService.TryGetHealth(mountedPhysicalDrive)
                    : StorageHealthInfo.Unknown;
                bool cameraHardware = IsLikelyCameraHardware(label, descriptor);
                bool cameraLayout = TryDetectCameraLayout(drive.RootDirectory.FullName, out string cameraLayoutSource);
                bool cameraContentDetected = cameraHardware || cameraLayout;
                string cameraDetectionSource = cameraHardware && cameraLayout
                    ? $"Storage descriptor + {cameraLayoutSource}"
                    : cameraHardware ? "Storage descriptor" : cameraLayoutSource;

                string visualKind = GetVisualKind(drive, label, descriptor, media, cameraHardware);
                bool isSystemVolume = IsSystemVolume(drive.RootDirectory.FullName);
                if (isSystemVolume && physicalDriveNumber is int systemPhysicalDrive)
                    systemPhysicalDrives.Add(systemPhysicalDrive);
                string kindText = GetKindText(visualKind, descriptor.BusType, isSystemVolume);

                uint? volumeSerialNumber = VolumeIdentityService.TryGetIdentity(
                    drive.RootDirectory.FullName,
                    out VolumeIdentityInfo volumeIdentity)
                    ? volumeIdentity.SerialNumber
                    : null;

                result.Add(new StorageDeviceInfo
                {
                    RootPath = drive.RootDirectory.FullName,
                    DisplayName = $"{label} ({drive.Name.TrimEnd('\\')})",
                    Kind = kindText,
                    KindGlyph = string.Empty,
                    VisualKind = visualKind,
                    FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true,
                    PhysicalDriveNumber = physicalDriveNumber,
                    PartitionOffsetBytes = extent?.StartingOffset ?? 0,
                    PartitionLengthBytes = extent?.ExtentLength ?? drive.TotalSize,
                    HardwareSerialNumber = descriptor.SerialNumber,
                    VolumeSerialNumber = volumeSerialNumber,
                    MediaDetectionSource = media.DetectionSource,
                    BusTypeText = GetBusTypeText(descriptor.BusType),
                    TrimEnabled = media.TrimEnabled,
                    CameraContentDetected = cameraContentDetected,
                    CameraDetectionSource = cameraDetectionSource,
                    HealthSeverity = (int)health.Severity,
                    SmartAvailable = health.SmartAvailable,
                    SmartPredictFailure = health.PredictFailure,
                    SmartReallocatedSectors = health.ReallocatedSectors,
                    SmartPendingSectors = health.PendingSectors,
                    SmartUncorrectableSectors = Math.Max(health.ReportedUncorrectable, health.OfflineUncorrectable),
                    SmartTemperatureC = health.TemperatureC,
                    HealthSummary = health.Summary
                });
            }
            catch
            {
            }
        }

        foreach (PhysicalDriveInfo physical in physicalDrives)
        {
            bool hasMountedVolume = mountedPhysicalDrives.Contains(physical.Number);
            StorageDeviceInfo wholePhysicalDevice = CreateWholePhysicalDevice(
                physical,
                systemPhysicalDrives.Contains(physical.Number));

            // The landing page is a source picker, not a hardware inventory. If a physical
            // disk is already represented by C:/D:/E:/USB, never show a second whole-disk card.
            // A genuinely unmounted/RAW disk remains visible.
            if (ShouldSurfaceWholePhysicalDisk(hasMountedVolume))
                result.Add(wholePhysicalDevice);

            // Hidden GPT/MBR partitions are not exposed as a separate landing-page workflow.
            // Unified scan resolves valid partition metadata automatically and falls back to RAW.
            var partitionCandidates = new Dictionary<string, PartitionCandidate>(StringComparer.OrdinalIgnoreCase);
            lock (_discoveredPartitionSync)
            {
                foreach (PartitionCandidate candidate in _discoveredPartitions.Values.Where(item => item.PhysicalDriveNumber == physical.Number))
                    partitionCandidates[candidate.Key] = candidate;
            }

            foreach (PartitionCandidate candidate in partitionCandidates.Values.OrderBy(item => item.OffsetBytes))
            {
                bool mounted = mountedExtents.Any(extent =>
                    extent.PhysicalDriveNumber == physical.Number &&
                    Math.Abs(extent.StartingOffset - candidate.OffsetBytes) < physical.LogicalSectorSize * 2L);
                if (!ShouldSurfacePartitionCandidate(explicitlyDiscovered: true, matchesMountedExtent: mounted))
                    continue;

                result.Add(CreatePartitionDevice(candidate, physical, wholePhysicalDevice.VisualKind, wholePhysicalDevice.CameraContentDetected));
            }
        }

        return result
            .GroupBy(device => device.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(device => device.IsPartitionSource ? 1 : device.IsWholePhysicalDisk ? 2 : 0)
            .ThenBy(device => GetMountedDriveSortKey(device.RootPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.PhysicalDriveNumber ?? int.MaxValue)
            .ThenBy(device => device.PartitionOffsetBytes)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private VolumeDiskExtentInfo? ResolveMountedExtent(
        DriveInfo drive,
        IReadOnlyList<PhysicalDriveInfo> physicalDrives,
        Dictionary<int, IReadOnlyList<PartitionCandidate>> partitionCandidateCache)
    {
        string rootPath = drive.RootDirectory.FullName;

        VolumeDiskExtentInfo? directExtent = VolumeDeviceNumberService.TryGetSingleExtent(rootPath);
        if (directExtent.HasValue)
            return directExtent;

        int? directDiskNumber = VolumeDeviceNumberService.TryGetPhysicalDriveNumber(rootPath);
        if (directDiskNumber is int knownDisk)
        {
            PhysicalDriveInfo physical = physicalDrives.FirstOrDefault(item => item.Number == knownDisk);
            if (physical.Number == knownDisk && physical.LengthBytes > 0)
            {
                PartitionCandidate? candidate = FindBestMountedPartitionCandidate(
                    drive,
                    physical,
                    partitionCandidateCache);
                if (candidate is not null)
                    return new VolumeDiskExtentInfo(knownDisk, candidate.OffsetBytes, candidate.LengthBytes);
            }

            return new VolumeDiskExtentInfo(knownDisk, 0, drive.TotalSize);
        }

        StorageDescriptorInfo volumeDescriptor = StorageBusTypeService.TryGetDescriptor(rootPath);
        string volumeSerial = NormalizeDeviceSerial(volumeDescriptor.SerialNumber);
        List<PhysicalDriveInfo> serialMatches = volumeSerial.Length == 0
            ? []
            : physicalDrives
                .Where(item => string.Equals(
                    NormalizeDeviceSerial(item.Descriptor.SerialNumber),
                    volumeSerial,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

        IReadOnlyList<PhysicalDriveInfo> searchSpace = serialMatches.Count == 1
            ? serialMatches
            : physicalDrives;

        var matches = new List<(PhysicalDriveInfo Physical, PartitionCandidate Candidate, long Difference)>();
        foreach (PhysicalDriveInfo physical in searchSpace)
        {
            PartitionCandidate? candidate = FindBestMountedPartitionCandidate(
                drive,
                physical,
                partitionCandidateCache);
            if (candidate is null)
                continue;

            matches.Add((physical, candidate, Math.Abs(candidate.LengthBytes - drive.TotalSize)));
        }

        List<int> matchedDisks = matches.Select(item => item.Physical.Number).Distinct().ToList();
        if (matchedDisks.Count == 1)
        {
            var best = matches
                .Where(item => item.Physical.Number == matchedDisks[0])
                .OrderBy(item => item.Difference)
                .ThenByDescending(item => item.Candidate.Confidence)
                .First();
            return new VolumeDiskExtentInfo(best.Physical.Number, best.Candidate.OffsetBytes, best.Candidate.LengthBytes);
        }

        // A unique hardware serial is strong enough to associate the mounted volume with its
        // physical device even when the partition VBR is damaged and no geometry candidate exists.
        if (serialMatches.Count == 1)
            return new VolumeDiskExtentInfo(serialMatches[0].Number, 0, drive.TotalSize);

        // Super-floppy USB/SD layouts may have no MBR/GPT partition entry at all. If exactly one
        // physical device has the same whole-media size and a compatible bus, correlate it. The
        // uniqueness requirement prevents two equal-size disks from being guessed.
        List<PhysicalDriveInfo> wholeDiskMatches = physicalDrives
            .Where(item => IsWholeDiskMountedLengthMatch(drive.TotalSize, item.LengthBytes))
            .Where(item => IsCompatibleBus(volumeDescriptor.BusType, item.Descriptor.BusType))
            .ToList();
        if (wholeDiskMatches.Count == 1)
            return new VolumeDiskExtentInfo(wholeDiskMatches[0].Number, 0, drive.TotalSize);

        return null;
    }

    private PartitionCandidate? FindBestMountedPartitionCandidate(
        DriveInfo drive,
        PhysicalDriveInfo physical,
        Dictionary<int, IReadOnlyList<PartitionCandidate>> partitionCandidateCache)
    {
        if (!partitionCandidateCache.TryGetValue(physical.Number, out IReadOnlyList<PartitionCandidate>? candidates))
        {
            candidates = _partitionDiscoveryService.DiscoverPartitionTableCandidates(physical);
            partitionCandidateCache[physical.Number] = candidates;
        }

        return candidates
            .Where(candidate => IsSameFileSystem(candidate.FileSystem, drive.DriveFormat))
            .Where(candidate => IsMountedLengthMatch(drive.TotalSize, candidate.LengthBytes))
            .OrderBy(candidate => Math.Abs(candidate.LengthBytes - drive.TotalSize))
            .ThenByDescending(candidate => candidate.Confidence)
            .FirstOrDefault();
    }

    internal static bool IsMountedLengthMatch(long mountedBytes, long partitionBytes)
    {
        if (mountedBytes <= 0 || partitionBytes <= 0)
            return false;

        long difference = Math.Abs(partitionBytes - mountedBytes);
        long tolerance = Math.Max(64L * 1024 * 1024, Math.Min(mountedBytes, partitionBytes) / 20_000);
        return difference <= tolerance;
    }

    internal static bool IsWholeDiskMountedLengthMatch(long mountedBytes, long physicalBytes)
    {
        if (mountedBytes <= 0 || physicalBytes <= 0)
            return false;

        long difference = Math.Abs(physicalBytes - mountedBytes);
        long tolerance = Math.Max(128L * 1024 * 1024, Math.Min(mountedBytes, physicalBytes) / 10_000);
        return difference <= tolerance;
    }

    private static bool IsCompatibleBus(StorageBusType volumeBus, StorageBusType physicalBus) =>
        volumeBus == StorageBusType.Unknown ||
        physicalBus == StorageBusType.Unknown ||
        volumeBus == physicalBus;

    private static bool IsSameFileSystem(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDeviceSerial(string? value) =>
        new string((value ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch) && ch != '-' && ch != '_').ToArray()).Trim();

    internal static bool ShouldSurfaceWholePhysicalDisk(bool hasMountedVolume) => !hasMountedVolume;

    internal static bool ShouldSurfacePartitionCandidate(bool explicitlyDiscovered, bool matchesMountedExtent) =>
        explicitlyDiscovered && !matchesMountedExtent;

    public bool TryCreateWholePhysicalDevice(StorageDeviceInfo sourceDevice, out StorageDeviceInfo? physicalDevice)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);
        physicalDevice = null;

        if (sourceDevice.PhysicalDriveNumber is not int physicalDriveNumber ||
            !PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical))
        {
            return false;
        }

        bool sourceLooksSystem = sourceDevice.Kind.Contains("Sistem", StringComparison.OrdinalIgnoreCase) ||
                                 (!sourceDevice.IsWholePhysicalDisk && IsSystemVolume(sourceDevice.RootPath));
        physicalDevice = CreateWholePhysicalDevice(physical, sourceLooksSystem);
        return true;
    }

    private static StorageDeviceInfo CreateWholePhysicalDevice(PhysicalDriveInfo physical, bool isSystemPhysicalDrive)
    {
        StorageDescriptorInfo descriptor = physical.Descriptor;
        StorageMediaInfo media = physical.Media;
        string productName = string.Join(" ", new[] { descriptor.Vendor, descriptor.Product }
            .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
        if (string.IsNullOrWhiteSpace(productName))
            productName = $"Fiziksel Disk {physical.Number}";

        bool cameraHardware = IsLikelyCameraHardware(productName, descriptor);
        string visualKind = GetPhysicalVisualKind(descriptor, media, cameraHardware);
        string kindText = GetKindText(visualKind, descriptor.BusType, isSystemPhysicalDrive);

        return new StorageDeviceInfo
        {
            RootPath = $@"\\.\PhysicalDrive{physical.Number}",
            DisplayName = $"{productName} • PhysicalDrive{physical.Number}",
            Kind = $"{kindText} • Tüm Disk",
            KindGlyph = string.Empty,
            VisualKind = visualKind,
            FileSystem = "RAW",
            TotalBytes = physical.LengthBytes,
            FreeBytes = 0,
            IsReady = true,
            PhysicalDriveNumber = physical.Number,
            IsWholePhysicalDisk = true,
            HardwareSerialNumber = descriptor.SerialNumber,
            MediaDetectionSource = media.DetectionSource,
            BusTypeText = GetBusTypeText(descriptor.BusType),
            TrimEnabled = media.TrimEnabled,
            CameraContentDetected = cameraHardware,
            CameraDetectionSource = cameraHardware ? "Storage descriptor" : string.Empty,
            HealthSeverity = (int)physical.Health.Severity,
            SmartAvailable = physical.Health.SmartAvailable,
            SmartPredictFailure = physical.Health.PredictFailure,
            SmartReallocatedSectors = physical.Health.ReallocatedSectors,
            SmartPendingSectors = physical.Health.PendingSectors,
            SmartUncorrectableSectors = Math.Max(physical.Health.ReportedUncorrectable, physical.Health.OfflineUncorrectable),
            SmartTemperatureC = physical.Health.TemperatureC,
            HealthSummary = physical.Health.Summary
        };
    }

    private static string GetMountedDriveSortKey(string rootPath)
    {
        string? root = Path.GetPathRoot(rootPath);
        if (!string.IsNullOrWhiteSpace(root) && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':')
            return char.ToUpperInvariant(root[0]).ToString();

        return "ZZZ";
    }

    private static StorageDeviceInfo CreatePartitionDevice(
        PartitionCandidate candidate,
        PhysicalDriveInfo physical,
        string visualKind,
        bool cameraHardware)
    {
        string sourceKind = candidate.IsRecoveredSignature ? "Kayıp Bölüm Adayı" : "Harfsiz Bölüm";
        string displayName = candidate.IsRecoveredSignature
            ? $"Kayıp {candidate.FileSystem} Bölümü • PhysicalDrive{physical.Number}"
            : $"{candidate.Name} • {candidate.FileSystem} • PhysicalDrive{physical.Number}";

        return new StorageDeviceInfo
        {
            RootPath = $@"\\.\PhysicalDrive{physical.Number}#PART:{candidate.OffsetBytes:X}:{candidate.LengthBytes:X}",
            DisplayName = displayName,
            Kind = sourceKind,
            KindGlyph = string.Empty,
            VisualKind = visualKind,
            FileSystem = candidate.FileSystem,
            TotalBytes = candidate.LengthBytes,
            FreeBytes = 0,
            IsReady = true,
            PhysicalDriveNumber = physical.Number,
            IsPartitionSource = true,
            IsRecoveredPartition = candidate.IsRecoveredSignature,
            PartitionOffsetBytes = candidate.OffsetBytes,
            PartitionLengthBytes = candidate.LengthBytes,
            PartitionScheme = candidate.Scheme,
            PartitionIdentity = candidate.Identity,
            PartitionConfidence = candidate.Confidence,
            HardwareSerialNumber = physical.Descriptor.SerialNumber,
            MediaDetectionSource = physical.Media.DetectionSource,
            BusTypeText = GetBusTypeText(physical.Descriptor.BusType),
            TrimEnabled = physical.Media.TrimEnabled,
            CameraContentDetected = cameraHardware,
            CameraDetectionSource = cameraHardware ? "Storage descriptor" : string.Empty,
            HealthSeverity = (int)physical.Health.Severity,
            SmartAvailable = physical.Health.SmartAvailable,
            SmartPredictFailure = physical.Health.PredictFailure,
            SmartReallocatedSectors = physical.Health.ReallocatedSectors,
            SmartPendingSectors = physical.Health.PendingSectors,
            SmartUncorrectableSectors = Math.Max(physical.Health.ReportedUncorrectable, physical.Health.OfflineUncorrectable),
            SmartTemperatureC = physical.Health.TemperatureC,
            HealthSummary = physical.Health.Summary
        };
    }

    private static string GetPhysicalVisualKind(
        StorageDescriptorInfo descriptor,
        StorageMediaInfo media,
        bool cameraHardware)
    {
        // RAW/format isteyen aygitlarda drive-letter bilgisi yoktur; siniflandirma sadece
        // fiziksel STORAGE_DEVICE_DESCRIPTOR + media bilgisinden yapilabilmelidir. Vendor,
        // Product ve Revision'i birlikte kullanmak "Generic Flash Disk" gibi cihazlari
        // yanlislikla SD kart ya da sabit disk saymamizi engeller.
        string searchText = string.Join(' ', new[] { descriptor.Vendor, descriptor.Product, descriptor.Revision }
                .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Replace('_', ' ')
            .Replace('-', ' ')
            .ToUpperInvariant();

        if (descriptor.BusType is StorageBusType.Sd or StorageBusType.Mmc || LooksLikeMemoryCard(searchText))
            return "Sd";

        if (cameraHardware)
            return "Camera";

        if (descriptor.BusType == StorageBusType.Usb)
        {
            if (media.MediaKind == StorageMediaKind.Rotational)
                return "ExternalHdd";

            // "Generic" bir vendor tek basina card-reader kaniti degildir. Cok sayida gercek
            // USB bellek (Generic Flash Disk/USB Disk) bu descriptor'u kullanir. Kart okuyucu
            // ancak SD/MMC/Card Reader ipuclariyla yukarida Sd olarak siniflandirilir.
            if (LooksLikeDedicatedUsbFlashDrive(searchText) ||
                LooksLikeUsbFlash(searchText) ||
                searchText.Contains("TRANSMEMORY", StringComparison.Ordinal))
                return "Usb";

            if (!descriptor.RemovableMedia && media.MediaKind == StorageMediaKind.SolidState)
                return "ExternalSsd";

            return "UnknownUsb";
        }

        // Bazi ucuz USB bridge/controller suruculeri BusType'i Unknown/SCSI raporlar ve
        // RemovableMedia bayragini da yanlis verebilir. Descriptor acikca "Flash Disk/USB Disk"
        // diyorsa ve medya donel degilse bunu USB flash kabul et; aksi halde Generic Flash Disk
        // ekrandaki gibi yanlislikla "Fiziksel Disk" profilinde kalir.
        if ((descriptor.BusType is StorageBusType.Unknown or StorageBusType.Scsi) &&
            media.MediaKind != StorageMediaKind.Rotational &&
            LooksLikeUsbFlash(searchText))
        {
            return "Usb";
        }

        return media.MediaKind switch
        {
            StorageMediaKind.SolidState => "Ssd",
            StorageMediaKind.Rotational => "Hdd",
            _ => "FixedDisk"
        };
    }
    private static StorageDescriptorInfo GetDescriptor(
        string rootPath,
        int? physicalDriveNumber,
        Dictionary<int, StorageDescriptorInfo> cache)
    {
        if (physicalDriveNumber is int diskNumber)
        {
            if (!cache.TryGetValue(diskNumber, out StorageDescriptorInfo descriptor))
            {
                descriptor = StorageBusTypeService.TryGetPhysicalDescriptor(diskNumber);
                if (!descriptor.HasIdentity)
                    descriptor = StorageBusTypeService.TryGetDescriptor(rootPath);

                cache[diskNumber] = descriptor;
            }

            return descriptor;
        }

        return StorageBusTypeService.TryGetDescriptor(rootPath);
    }

    private static StorageMediaInfo GetMediaInfo(
        string rootPath,
        int? physicalDriveNumber,
        StorageDescriptorInfo descriptor,
        Dictionary<int, StorageMediaInfo> cache)
    {
        if (physicalDriveNumber is int diskNumber)
        {
            if (!cache.TryGetValue(diskNumber, out StorageMediaInfo media))
            {
                media = StorageMediaTypeService.TryGetMediaInfo(rootPath, diskNumber, descriptor);
                cache[diskNumber] = media;
            }

            return media;
        }

        return StorageMediaTypeService.TryGetMediaInfo(rootPath, null, descriptor);
    }

    private static string GetVisualKind(
        DriveInfo drive,
        string label,
        StorageDescriptorInfo descriptor,
        StorageMediaInfo media,
        bool cameraHardware)
    {
        return GetPhysicalVisualKind(descriptor, media, cameraHardware);
    }
    private static bool IsSystemVolume(string rootPath)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        string driveRoot = Path.GetPathRoot(rootPath) ?? rootPath;
        return string.Equals(systemRoot, driveRoot, StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsLikelyCameraHardware(string label, StorageDescriptorInfo descriptor)
    {
        string value = string.Join(' ', new[]
        {
            descriptor.Vendor, descriptor.Product
        }.Where(text => !string.IsNullOrWhiteSpace(text)))
        .Replace('_', ' ')
        .ToUpperInvariant();

        if (value.Contains("CAMCORDER", StringComparison.Ordinal) ||
            value.Contains("VIDEO CAMERA", StringComparison.Ordinal) ||
            value.Contains("HANDYCAM", StringComparison.Ordinal))
            return true;

        bool sony = value.Contains("SONY", StringComparison.Ordinal);
        if (sony && (value.Contains("HXR", StringComparison.Ordinal) ||
                     value.Contains("HDR", StringComparison.Ordinal) ||
                     value.Contains("FDR", StringComparison.Ordinal) ||
                     value.Contains("DCR", StringComparison.Ordinal) ||
                     value.Contains("PXW", StringComparison.Ordinal) ||
                     value.Contains("MC2000", StringComparison.Ordinal) ||
                     value.Contains("MC2500", StringComparison.Ordinal)))
            return true;

        bool panasonic = value.Contains("PANASONIC", StringComparison.Ordinal);
        if (panasonic && (value.Contains("HDC", StringComparison.Ordinal) ||
                          value.Contains("HC-", StringComparison.Ordinal) ||
                          value.Contains("AG-", StringComparison.Ordinal) ||
                          value.Contains("AJ-", StringComparison.Ordinal) ||
                          value.Contains("AVCCAM", StringComparison.Ordinal)))
            return true;

        bool canon = value.Contains("CANON", StringComparison.Ordinal);
        if (canon && (value.Contains("VIXIA", StringComparison.Ordinal) ||
                      value.Contains("LEGRIA", StringComparison.Ordinal) ||
                      value.Contains("CAMERA", StringComparison.Ordinal)))
            return true;

        bool jvc = value.Contains("JVC", StringComparison.Ordinal);
        return jvc && (value.Contains("EVERIO", StringComparison.Ordinal) ||
                       value.Contains("CAMERA", StringComparison.Ordinal));
    }

    private static bool TryDetectCameraLayout(string rootPath, out string source)
    {
        source = string.Empty;
        try
        {
            (string RelativePath, string Name)[] strongLayouts =
            [
                (Path.Combine("PRIVATE", "AVCHD", "BDMV"), "PRIVATE/AVCHD/BDMV"),
                (Path.Combine("AVCHD", "BDMV"), "AVCHD/BDMV"),
                (Path.Combine("BDMV", "STREAM"), "BDMV/STREAM"),
                (Path.Combine("PRIVATE", "M4ROOT"), "PRIVATE/M4ROOT"),
                ("MP_ROOT", "MP_ROOT"),
                (Path.Combine("PRIVATE", "SONY"), "PRIVATE/SONY")
            ];

            foreach ((string relativePath, string name) in strongLayouts)
            {
                if (!Directory.Exists(Path.Combine(rootPath, relativePath)))
                    continue;

                source = $"kamera klasör yapısı: {name}";
                return true;
            }

            // DCIM tek başına kamera kanıtı sayılmaz; telefonlar ve sıradan kartlar da kullanır.
            // DCIM + PRIVATE birlikteliği ise kamera/AVCHD medya yerleşimi için güçlü bir işarettir.
            if (Directory.Exists(Path.Combine(rootPath, "DCIM")) &&
                Directory.Exists(Path.Combine(rootPath, "PRIVATE")))
            {
                source = "kamera klasör yapısı: DCIM + PRIVATE";
                return true;
            }
        }
        catch
        {
            // Dizin yapısı okunamıyorsa aygıt yine normal recovery profiliyle listelenir.
        }

        return false;
    }

    private static bool LooksLikeMemoryCard(string value)
    {
        string normalized = value.Replace('_', ' ').Replace('-', ' ');
        return normalized.Contains("MICROSD", StringComparison.Ordinal)
               || normalized.Contains("MICRO SD", StringComparison.Ordinal)
               || normalized.Contains("SD CARD", StringComparison.Ordinal)
               || normalized.Contains("SDCARD", StringComparison.Ordinal)
               || normalized.Contains("SDXC", StringComparison.Ordinal)
               || normalized.Contains("SDHC", StringComparison.Ordinal)
               || normalized.Contains("SD/MMC", StringComparison.Ordinal)
               || normalized.Contains("SD MMC", StringComparison.Ordinal)
               || normalized.Contains("MMC/SD", StringComparison.Ordinal)
               || normalized.Contains("MMC SD", StringComparison.Ordinal)
               || normalized.Contains("MMC", StringComparison.Ordinal)
               || normalized.Contains("TF CARD", StringComparison.Ordinal)
               || normalized.Contains("TFCARD", StringComparison.Ordinal)
               || normalized.Contains("MEMORY CARD", StringComparison.Ordinal)
               || normalized.Contains("MEM CARD", StringComparison.Ordinal)
               || normalized.Contains("MEMORY READER", StringComparison.Ordinal)
               || normalized.Contains("CARD READER", StringComparison.Ordinal)
               || normalized.Contains("CARDREADER", StringComparison.Ordinal)
               || normalized.Contains("FLASH READER", StringComparison.Ordinal)
               || normalized.Contains("MULTI CARD", StringComparison.Ordinal)
               || normalized.Contains("MULTICARD", StringComparison.Ordinal)
               || normalized.Contains("USB READER", StringComparison.Ordinal)
               || normalized.Contains("USB2.0 CRW", StringComparison.Ordinal)
               || normalized.Contains("USB3.0 CRW", StringComparison.Ordinal)
               || normalized.Contains(" CRW", StringComparison.Ordinal)
               || normalized.Contains("COMPACTFLASH", StringComparison.Ordinal)
               || normalized.Contains("COMPACT FLASH", StringComparison.Ordinal)
               || normalized.Contains("CF CARD", StringComparison.Ordinal)
               || normalized.Contains("CFAST", StringComparison.Ordinal)
               || normalized.Contains("SMARTMEDIA", StringComparison.Ordinal)
               || normalized.Contains("SMART MEDIA", StringComparison.Ordinal)
               || normalized.Contains("MEMORYSTICK", StringComparison.Ordinal)
               || normalized.Contains("MEMORY STICK", StringComparison.Ordinal)
               || normalized.Contains("XD CARD", StringComparison.Ordinal)
               || normalized.Contains("X D CARD", StringComparison.Ordinal)
               || normalized.Contains("UHS", StringComparison.Ordinal)
               || normalized.StartsWith("SD ", StringComparison.Ordinal)
               || normalized.Contains(" SD ", StringComparison.Ordinal);
    }

    private static bool LooksLikeDedicatedUsbFlashDrive(string value)
    {
        string normalized = value.Replace('_', ' ').Replace('-', ' ');
        if (LooksLikeMemoryCard(normalized))
            return false;

        return normalized.Contains("THUMB DRIVE", StringComparison.Ordinal)
               || normalized.Contains("THUMBDRIVE", StringComparison.Ordinal)
               || normalized.Contains("PENDRIVE", StringComparison.Ordinal)
               || normalized.Contains("PEN DRIVE", StringComparison.Ordinal)
               || normalized.Contains("JUMPDRIVE", StringComparison.Ordinal)
               || normalized.Contains("JUMP DRIVE", StringComparison.Ordinal)
               || normalized.Contains("DATATRAVELER", StringComparison.Ordinal)
               || normalized.Contains("DATA TRAVELER", StringComparison.Ordinal)
               || normalized.Contains("CRUZER", StringComparison.Ordinal)
               || normalized.Contains("JETFLASH", StringComparison.Ordinal)
               || normalized.Contains("JET FLASH", StringComparison.Ordinal)
               || normalized.Contains("USB FLASH DRIVE", StringComparison.Ordinal)
               || normalized.Contains("USB MEMORY DRIVE", StringComparison.Ordinal)
               || normalized.Contains(" UDISK", StringComparison.Ordinal)
               || normalized.Contains(" U DISK", StringComparison.Ordinal);
    }

    private static bool LooksLikeUsbFlash(string value)
    {
        return value.Contains("FLASH", StringComparison.Ordinal)
               || value.Contains("THUMB", StringComparison.Ordinal)
               || value.Contains("U DISK", StringComparison.Ordinal)
               || value.Contains("UDISK", StringComparison.Ordinal)
               || value.Contains("USB DISK", StringComparison.Ordinal)
               || value.Contains("PENDRIVE", StringComparison.Ordinal)
               || value.Contains("PEN DRIVE", StringComparison.Ordinal);
    }

    private static string GetKindText(string visualKind, StorageBusType busType, bool isSystemVolume)
    {
        string bus = GetBusTypeText(busType);
        string suffix = string.IsNullOrWhiteSpace(bus) ? string.Empty : $" ({bus})";

        return visualKind switch
        {
            "Sd" => "SD / Hafıza Kartı",
            "Camera" => $"Kamera / Dahili Hafıza{suffix}",
            "Usb" => "USB Flash Bellek",
            "ExternalSsd" => $"Harici SSD{suffix}",
            "ExternalHdd" => $"Harici HDD{suffix}",
            "ExternalDisk" => "Harici Disk",
            "UnknownUsb" => "USB Aygıtı (ortam türü belirlenemedi)",
            "Ssd" => isSystemVolume ? $"Sistem SSD{suffix}" : $"SSD{suffix}",
            "Hdd" => isSystemVolume ? $"Sistem HDD{suffix}" : $"HDD{suffix}",
            "FixedDisk" => isSystemVolume ? "Sistem Diski" : "Fiziksel Disk",
            _ => "Disk"
        };
    }

    private static string GetBusTypeText(StorageBusType busType) => busType switch
    {
        StorageBusType.Nvme => "NVMe",
        StorageBusType.Sata => "SATA",
        StorageBusType.Ata => "ATA",
        StorageBusType.Atapi => "ATAPI",
        StorageBusType.Scsi => "SCSI",
        StorageBusType.Raid => "RAID",
        StorageBusType.Sas => "SAS",
        StorageBusType.Usb => "USB",
        StorageBusType.Sd => "SD",
        StorageBusType.Mmc => "MMC",
        StorageBusType.Ufs => "UFS",
        StorageBusType.Scm => "SCM",
        _ => string.Empty
    };
}
