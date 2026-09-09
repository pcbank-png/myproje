using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace NSXVeriKurtarmaPro.Services;

public readonly record struct VolumeDiskExtentInfo(int PhysicalDriveNumber, long StartingOffset, long ExtentLength);

internal enum PhysicalDiskResolutionStatus
{
    Resolved,
    Unknown,
    Ambiguous
}

internal readonly record struct PhysicalDiskResolution(
    PhysicalDiskResolutionStatus Status,
    int? PhysicalDriveNumber,
    string Detail)
{
    public bool IsResolved => Status == PhysicalDiskResolutionStatus.Resolved && PhysicalDriveNumber.HasValue;
}

public static class VolumeDeviceNumberService
{
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int InitialExtentBufferBytes = 64;
    private const int MaximumExtentBufferBytes = 64 * 1024;

    public static int? TryGetPhysicalDriveNumber(string path)
    {
        if (!TryOpenVolume(path, out SafeFileHandle? handle) || handle is null)
            return null;

        using (handle)
        {
            var number = new StorageDeviceNumber();
            int size = Marshal.SizeOf<StorageDeviceNumber>();
            bool ok = DeviceIoControl(
                handle,
                IoctlStorageGetDeviceNumber,
                IntPtr.Zero,
                0,
                ref number,
                size,
                out _,
                IntPtr.Zero);
            return ok ? unchecked((int)number.DeviceNumber) : null;
        }
    }

    public static VolumeDiskExtentInfo? TryGetSingleExtent(string path)
    {
        if (!TryReadVolumeDiskExtents(path, out IReadOnlyList<VolumeDiskExtentInfo> extents))
            return null;

        return extents.Count == 1 ? extents[0] : null;
    }

    internal static PhysicalDiskResolution ResolvePhysicalDisk(string path)
    {
        if (!TryReadVolumeDiskExtents(path, out IReadOnlyList<VolumeDiskExtentInfo> extents))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Volume -> physical disk extent mapping could not be read.");
        }

        StorageBusType volumeBus = StorageBusTypeService.TryGetBusType(path);
        if (extents.Count != 1)
            return EvaluateTopologyForSafety(extents, physicalDriveVerified: false, volumeBus, StorageBusType.Unknown);

        int physicalDriveNumber = extents[0].PhysicalDriveNumber;
        bool verified = PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical);
        StorageBusType physicalBus = verified ? physical.Descriptor.BusType : StorageBusType.Unknown;
        return EvaluateTopologyForSafety(extents, verified, volumeBus, physicalBus);
    }

    /// <summary>
    /// Write-target identity resolution used immediately before recovery/image output.
    /// A mounted destination volume is considered a direct single-disk target when Windows
    /// reports exactly one disk extent (or, as a fallback, one storage device number), while
    /// multi-extent and explicitly virtual/RAID/Storage Spaces topologies remain fail-closed.
    ///
    /// PhysicalDriveAccessService is used as an additional topology signal when available,
    /// but failure to open the physical device is not itself treated as ambiguity. Some valid
    /// SATA/NVMe/USB bridge drivers allow volume IOCTLs while denying an independent raw-disk
    /// descriptor open to a standard user. The volume->disk mapping is the safety-critical
    /// identity here; no media scan or source read is performed.
    /// </summary>
    internal static PhysicalDiskResolution ResolveDestinationPhysicalDisk(string path)
    {
        StorageBusType volumeBus = StorageBusTypeService.TryGetBusType(path);
        // Windows 10/11 sistem diskleri Intel RST/VMD gibi denetleyiciler altında tek bir
        // normal SSD olsa bile BusType=RAID raporlayabilir. Sırf RAID etiketi nedeniyle
        // C:\ / Masaüstü hedefini reddetmek yanlış negatiftir. Hedefte güvenlik kararını
        // asıl olarak volume -> physical disk extent kimliği verir: tek extent + tutarlı
        // device number güvenlidir; Storage Spaces / VHD gibi gerçekten sanal katmanlar
        // ve çoklu extent yapıları ise yine fail-closed kalır.
        if (IsUnsafeVirtualDestinationBus(volumeBus))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Destination volume uses a virtual or pooled bus ({volumeBus}).");
        }

        bool hasExtents = TryReadVolumeDiskExtents(path, out IReadOnlyList<VolumeDiskExtentInfo> extents);
        int? deviceNumber = TryGetPhysicalDriveNumber(path);

        if (hasExtents)
        {
            if (extents.Count == 0)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Unknown,
                    null,
                    "Destination volume has no verifiable physical disk extent.");
            }

            if (extents.Count != 1)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Destination volume has {extents.Count} physical extents.");
            }

            int extentDisk = extents[0].PhysicalDriveNumber;
            if (deviceNumber.HasValue && deviceNumber.Value != extentDisk)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Destination volume identity mismatch: extent=PhysicalDrive{extentDisk}, device=PhysicalDrive{deviceNumber.Value}.");
            }

            if (PhysicalDriveAccessService.TryGetInfo(extentDisk, out PhysicalDriveInfo physical) &&
                IsUnsafeVirtualDestinationBus(physical.Descriptor.BusType))
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Destination physical disk uses a virtual or pooled bus ({physical.Descriptor.BusType}).");
            }

            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Resolved,
                extentDisk,
                $"PhysicalDrive{extentDisk} via destination volume extent");
        }

        if (!deviceNumber.HasValue)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Destination volume did not expose a physical disk extent or storage device number.");
        }

        if (PhysicalDriveAccessService.TryGetInfo(deviceNumber.Value, out PhysicalDriveInfo fallbackPhysical) &&
            IsUnsafeVirtualDestinationBus(fallbackPhysical.Descriptor.BusType))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Destination physical disk uses a virtual or pooled bus ({fallbackPhysical.Descriptor.BusType}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            deviceNumber.Value,
            $"PhysicalDrive{deviceNumber.Value} via destination volume device number");
    }

    /// <summary>
    /// Recovery-source identity resolution used only for the same-physical-disk safety check.
    /// The source is never written, so a controller reporting BusType=RAID (common with
    /// Intel RST/VMD) must not by itself make a normal single-extent volume ambiguous.
    /// We accept exactly one Windows volume extent (or one consistent device number fallback),
    /// while true virtual/pool and multi-extent layouts remain fail-closed.
    /// </summary>
    internal static PhysicalDiskResolution ResolveRecoverySourcePhysicalDisk(string path)
    {
        StorageBusType volumeBus = StorageBusTypeService.TryGetBusType(path);
        if (IsUnsafeVirtualDestinationBus(volumeBus))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Recovery source volume uses a virtual or pooled bus ({volumeBus}).");
        }

        bool hasExtents = TryReadVolumeDiskExtents(path, out IReadOnlyList<VolumeDiskExtentInfo> extents);
        int? deviceNumber = TryGetPhysicalDriveNumber(path);

        if (hasExtents)
        {
            if (extents.Count == 0)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Unknown,
                    null,
                    "Recovery source volume has no verifiable physical disk extent.");
            }

            if (extents.Count != 1)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Recovery source volume has {extents.Count} physical extents.");
            }

            int extentDisk = extents[0].PhysicalDriveNumber;
            if (deviceNumber.HasValue && deviceNumber.Value != extentDisk)
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Recovery source identity mismatch: extent=PhysicalDrive{extentDisk}, device=PhysicalDrive{deviceNumber.Value}.");
            }

            if (PhysicalDriveAccessService.TryGetInfo(extentDisk, out PhysicalDriveInfo physical) &&
                IsUnsafeVirtualDestinationBus(physical.Descriptor.BusType))
            {
                return new PhysicalDiskResolution(
                    PhysicalDiskResolutionStatus.Ambiguous,
                    null,
                    $"Recovery source physical disk uses a virtual or pooled bus ({physical.Descriptor.BusType}).");
            }

            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Resolved,
                extentDisk,
                $"PhysicalDrive{extentDisk} via recovery source volume extent");
        }

        if (!deviceNumber.HasValue)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Recovery source volume did not expose a physical disk extent or storage device number.");
        }

        if (PhysicalDriveAccessService.TryGetInfo(deviceNumber.Value, out PhysicalDriveInfo fallbackPhysical) &&
            IsUnsafeVirtualDestinationBus(fallbackPhysical.Descriptor.BusType))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Recovery source physical disk uses a virtual or pooled bus ({fallbackPhysical.Descriptor.BusType}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            deviceNumber.Value,
            $"PhysicalDrive{deviceNumber.Value} via recovery source volume device number");
    }

    /// <summary>
    /// Read-only source identity resolution. Unlike destination safety, this path does not
    /// require a second independent PhysicalDrive open when Windows already reports a
    /// single direct extent/device number for the mounted source volume. This avoids false
    /// negatives on USB/NVMe bridge drivers while keeping multi-extent/virtual topologies
    /// fail-closed. Destination safety continues to use ResolvePhysicalDisk.
    /// </summary>
    internal static PhysicalDiskResolution ResolveSourcePhysicalDisk(string path)
    {
        StorageBusType volumeBus = StorageBusTypeService.TryGetBusType(path);

        if (TryReadVolumeDiskExtents(path, out IReadOnlyList<VolumeDiskExtentInfo> extents))
            return EvaluateSourceTopologyForSafety(extents, volumeBus);

        int? deviceNumber = TryGetPhysicalDriveNumber(path);
        if (!deviceNumber.HasValue)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Source volume did not expose a physical disk extent or storage device number.");
        }

        if (IsAmbiguousStorageBus(volumeBus))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Source volume uses an aggregated or virtual bus ({volumeBus}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            deviceNumber.Value,
            $"PhysicalDrive{deviceNumber.Value} via source volume device number");
    }

    internal static PhysicalDiskResolution EvaluateSourceTopologyForSafety(
        IReadOnlyList<VolumeDiskExtentInfo> extents,
        StorageBusType volumeBus)
    {
        if (extents.Count == 0)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Source volume has no verifiable physical disk extent.");
        }

        if (extents.Count != 1)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Source volume has {extents.Count} physical extents.");
        }

        if (IsAmbiguousStorageBus(volumeBus))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Source storage topology is virtualized or aggregated ({volumeBus}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            extents[0].PhysicalDriveNumber,
            $"PhysicalDrive{extents[0].PhysicalDriveNumber} via source volume extent");
    }


    internal static PhysicalDiskResolution EvaluateTopologyForSafety(
        IReadOnlyList<VolumeDiskExtentInfo> extents,
        bool physicalDriveVerified,
        StorageBusType volumeBus,
        StorageBusType physicalBus)
    {
        if (extents.Count == 0)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Volume has no verifiable physical disk extent.");
        }

        // Dynamic/spanned volumes are intentionally fail-closed even when several extents
        // currently report the same disk. A topology change must never turn a safety check
        // into an accidental write to the recovery source.
        if (extents.Count != 1)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Volume has {extents.Count} physical extents.");
        }

        int physicalDriveNumber = extents[0].PhysicalDriveNumber;
        if (!physicalDriveVerified)
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                $"PhysicalDrive{physicalDriveNumber} could not be independently verified.");
        }

        if (IsAmbiguousStorageBus(volumeBus) || IsAmbiguousStorageBus(physicalBus))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Storage topology is virtualized or aggregated ({volumeBus}/{physicalBus}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            physicalDriveNumber,
            $"PhysicalDrive{physicalDriveNumber}");
    }

    internal static PhysicalDiskResolution ResolvePhysicalDisk(int physicalDriveNumber)
    {
        if (physicalDriveNumber < 0 ||
            !PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Unknown,
                null,
                "Physical disk identity could not be verified.");
        }

        if (IsAmbiguousStorageBus(physical.Descriptor.BusType))
        {
            return new PhysicalDiskResolution(
                PhysicalDiskResolutionStatus.Ambiguous,
                null,
                $"Physical disk uses an aggregated or virtual bus ({physical.Descriptor.BusType}).");
        }

        return new PhysicalDiskResolution(
            PhysicalDiskResolutionStatus.Resolved,
            physicalDriveNumber,
            $"PhysicalDrive{physicalDriveNumber}");
    }

    private static bool TryReadVolumeDiskExtents(
        string path,
        out IReadOnlyList<VolumeDiskExtentInfo> extents)
    {
        extents = [];
        if (!TryOpenVolume(path, out SafeFileHandle? handle) || handle is null)
            return false;

        using (handle)
        {
            int bufferSize = InitialExtentBufferBytes;
            while (bufferSize <= MaximumExtentBufferBytes)
            {
                byte[] output = new byte[bufferSize];
                bool ok = DeviceIoControl(
                    handle,
                    IoctlVolumeGetVolumeDiskExtents,
                    IntPtr.Zero,
                    0,
                    output,
                    output.Length,
                    out int returned,
                    IntPtr.Zero);

                if (!ok)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is ErrorMoreData or ErrorInsufficientBuffer)
                    {
                        bufferSize = checked(bufferSize * 2);
                        continue;
                    }

                    return false;
                }

                if (returned < 8)
                    return false;

                uint count = BitConverter.ToUInt32(output, 0);
                if (count == 0 || count > 2048)
                    return false;

                long requiredBytes = 8L + count * 24L;
                if (requiredBytes > returned || requiredBytes > output.Length)
                {
                    if (bufferSize == MaximumExtentBufferBytes)
                        return false;

                    bufferSize = Math.Min(MaximumExtentBufferBytes, checked(bufferSize * 2));
                    continue;
                }

                var parsed = new List<VolumeDiskExtentInfo>((int)count);
                for (int index = 0; index < count; index++)
                {
                    int offset = 8 + index * 24;
                    int diskNumber = BitConverter.ToInt32(output, offset);
                    long startingOffset = BitConverter.ToInt64(output, offset + 8);
                    long extentLength = BitConverter.ToInt64(output, offset + 16);
                    if (diskNumber < 0 || startingOffset < 0 || extentLength <= 0)
                        return false;

                    parsed.Add(new VolumeDiskExtentInfo(diskNumber, startingOffset, extentLength));
                }

                extents = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool IsAmbiguousStorageBus(StorageBusType busType) =>
        busType is StorageBusType.Raid
            or StorageBusType.Virtual
            or StorageBusType.FileBackedVirtual
            or StorageBusType.Spaces;

    // Destination writes may safely target a Windows volume presented through Intel RST/VMD
    // as BusType=RAID when that volume resolves to one consistent PhysicalDrive. Do not relax
    // true virtualization/pooling: those remain ambiguous because the target cannot be reduced
    // to one stable block device identity.
    private static bool IsUnsafeVirtualDestinationBus(StorageBusType busType) =>
        busType is StorageBusType.Virtual
            or StorageBusType.FileBackedVirtual
            or StorageBusType.Spaces;

    private static bool TryOpenVolume(string path, out SafeFileHandle? handle)
    {
        handle = null;
        if (!OperatingSystem.IsWindows())
            return false;

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            return false;

        string volumePath = $@"\\.\{root[..2]}";
        SafeFileHandle opened = CreateFile(
            volumePath,
            0,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (opened.IsInvalid)
        {
            opened.Dispose();
            return false;
        }

        handle = opened;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        ref StorageDeviceNumber lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
