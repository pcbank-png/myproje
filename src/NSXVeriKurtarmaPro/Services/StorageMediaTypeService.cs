using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace NSXVeriKurtarmaPro.Services;

internal enum StorageMediaKind
{
    Unknown = 0,
    SolidState = 1,
    Rotational = 2
}

internal readonly record struct StorageMediaInfo(
    StorageMediaKind MediaKind,
    bool? IncursSeekPenalty,
    bool? TrimEnabled,
    string DetectionSource)
{
    public static StorageMediaInfo Unknown => new(StorageMediaKind.Unknown, null, null, "Unknown");
}

/// <summary>
/// Depolama ortamının SSD/HDD türünü Windows storage stack üzerinden belirler.
/// Birincil kanıt: BusType + StorageDeviceSeekPenaltyProperty.
/// Sorgu sonuç vermediğinde model/kapasite tahmini yapılmaz; tür bilinmiyor kalır.
/// </summary>
internal static class StorageMediaTypeService
{
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    // STORAGE_PROPERTY_ID
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StorageDeviceTrimProperty = 8;

    public static StorageMediaInfo TryGetMediaInfo(
        string rootPath,
        int? physicalDriveNumber,
        StorageDescriptorInfo descriptor)
    {
        if (!OperatingSystem.IsWindows())
            return StorageMediaInfo.Unknown;

        // NVMe/UFS/SCM dönel plaka değildir. BusType bunu kesin olarak söyler; buna rağmen
        // TRIM bilgisini atlamıyoruz. Fiziksel/volume storage property sorguları aşağıda devam eder.
        bool busImpliesSolidState = descriptor.BusType is StorageBusType.Nvme or StorageBusType.Ufs or StorageBusType.Scm;

        bool? seekPenalty = null;
        bool? trimEnabled = null;
        string source = string.Empty;

        if (physicalDriveNumber is int diskNumber && diskNumber >= 0)
        {
            string physicalPath = $@"\\.\PhysicalDrive{diskNumber}";
            QueryDevice(physicalPath, ref seekPenalty, ref trimEnabled, ref source, "PhysicalDrive");
        }

        // Fiziksel aygıt erişimi sürücü/izin nedeniyle sonuç vermezse volume handle üzerinden aynı
        // Windows storage property sorgusunu tekrar dene.
        if ((seekPenalty is null || trimEnabled is null) && TryBuildVolumeDevicePath(rootPath, out string volumePath))
            QueryDevice(volumePath, ref seekPenalty, ref trimEnabled, ref source, "Volume");

        if (busImpliesSolidState)
        {
            string evidence = string.IsNullOrWhiteSpace(source)
                ? $"BusType:{descriptor.BusType}"
                : $"BusType:{descriptor.BusType};{source}";
            return new StorageMediaInfo(
                StorageMediaKind.SolidState,
                seekPenalty ?? false,
                trimEnabled,
                evidence);
        }

        if (seekPenalty is false)
        {
            return new StorageMediaInfo(
                StorageMediaKind.SolidState,
                seekPenalty,
                trimEnabled,
                string.IsNullOrWhiteSpace(source) ? "SeekPenalty:false" : $"{source};SeekPenalty:false");
        }

        if (seekPenalty is true)
        {
            return new StorageMediaInfo(
                StorageMediaKind.Rotational,
                seekPenalty,
                trimEnabled,
                string.IsNullOrWhiteSpace(source) ? "SeekPenalty:true" : $"{source};SeekPenalty:true");
        }

        return new StorageMediaInfo(StorageMediaKind.Unknown, seekPenalty, trimEnabled, "Unknown");
    }

    private static void QueryDevice(
        string devicePath,
        ref bool? seekPenalty,
        ref bool? trimEnabled,
        ref string source,
        string sourceName)
    {
        using SafeFileHandle handle = CreateFile(
            devicePath,
            0,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return;

        seekPenalty ??= TryQueryBooleanDescriptor(handle, StorageDeviceSeekPenaltyProperty);
        trimEnabled ??= TryQueryBooleanDescriptor(handle, StorageDeviceTrimProperty);

        if (seekPenalty is not null || trimEnabled is not null)
            source = sourceName;
    }

    private static bool? TryQueryBooleanDescriptor(SafeFileHandle handle, int propertyId)
    {
        // STORAGE_PROPERTY_QUERY: PropertyId (4) + QueryType (4) + AdditionalParameters (1 + padding)
        byte[] query = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(propertyId), 0, query, 0, 4);
        // QueryType = PropertyStandardQuery = 0; byte[] zaten sıfırlandı.

        // SEEK_PENALTY/TRIM descriptor: Version (4), Size (4), BOOLEAN (1)
        byte[] output = new byte[32];
        bool ok = DeviceIoControl(
            handle,
            IoctlStorageQueryProperty,
            query,
            query.Length,
            output,
            output.Length,
            out int returned,
            IntPtr.Zero);

        if (!ok || returned < 9)
            return null;

        uint size = BitConverter.ToUInt32(output, 4);
        if (size < 9)
            return null;

        return output[8] != 0;
    }

    private static bool TryBuildVolumeDevicePath(string rootPath, out string volumePath)
    {
        volumePath = string.Empty;
        string? root = Path.GetPathRoot(rootPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
            return false;

        volumePath = $@"\\.\{root[..2]}";
        return true;
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
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);
}
