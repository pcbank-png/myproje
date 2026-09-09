using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace NSXVeriKurtarmaPro.Services;

internal readonly record struct PhysicalDriveIdentityInfo(
    int Number,
    long LengthBytes,
    int LogicalSectorSize,
    StorageDescriptorInfo Descriptor);

internal readonly record struct PhysicalDriveInfo(
    int Number,
    long LengthBytes,
    int LogicalSectorSize,
    StorageDescriptorInfo Descriptor,
    StorageMediaInfo Media,
    StorageHealthInfo Health)
{
    public string RawPath => $@"\\.\PhysicalDrive{Number}";
}

internal static class PhysicalDriveAccessService
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageAccessAlignmentProperty = 6;

    public static IReadOnlyList<PhysicalDriveInfo> Enumerate(int maxDriveNumber = 63)
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var result = new List<PhysicalDriveInfo>();
        for (int number = 0; number <= Math.Max(0, maxDriveNumber); number++)
        {
            if (!TryGetInfo(number, out PhysicalDriveInfo info))
                continue;

            result.Add(info);
        }

        return result;
    }

    public static bool TryGetInfo(int number, out PhysicalDriveInfo info)
    {
        info = default;
        if (!TryGetIdentityInfo(number, out PhysicalDriveIdentityInfo identity))
            return false;

        string rawPath = $@"\\.\PhysicalDrive{number}";
        StorageMediaInfo media = StorageMediaTypeService.TryGetMediaInfo(rawPath, number, identity.Descriptor);
        StorageHealthInfo health = StorageHealthService.TryGetHealth(number);
        info = new PhysicalDriveInfo(
            number,
            identity.LengthBytes,
            identity.LogicalSectorSize,
            identity.Descriptor,
            media,
            health);
        return true;
    }

    public static bool TryGetIdentityInfo(int number, out PhysicalDriveIdentityInfo info)
    {
        info = default;
        if (!OperatingSystem.IsWindows() || number < 0)
            return false;

        string rawPath = $@"\\.\PhysicalDrive{number}";

        // Some USB/NVMe bridge drivers allow a metadata-only handle but reject
        // IOCTL_DISK_GET_LENGTH_INFO on that handle. The actual scanner opens the same
        // device with GENERIC_READ, so retry the identity probe with read access before
        // declaring the source topology unverifiable.
        if (!TryReadIdentityFromHandle(rawPath, desiredAccess: 0, out long length, out int logicalSectorSize) &&
            !TryReadIdentityFromHandle(rawPath, desiredAccess: GenericRead, out length, out logicalSectorSize))
        {
            return false;
        }

        StorageDescriptorInfo descriptor = StorageBusTypeService.TryGetPhysicalDescriptor(number);
        info = new PhysicalDriveIdentityInfo(number, length, logicalSectorSize, descriptor);
        return true;
    }

    private static bool TryReadIdentityFromHandle(
        string rawPath,
        uint desiredAccess,
        out long length,
        out int logicalSectorSize)
    {
        length = 0;
        logicalSectorSize = 512;

        using SafeFileHandle handle = CreateFile(
            rawPath,
            desiredAccess,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return false;

        length = TryGetLength(handle);
        if (length <= 0)
            return false;

        logicalSectorSize = TryGetLogicalSectorSize(handle);
        return true;
    }

    public static bool IsAccessible(int number)
    {
        if (!OperatingSystem.IsWindows() || number < 0)
            return false;

        using SafeFileHandle handle = CreateFile(
            $@"\\.\PhysicalDrive{number}",
            0,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        return !handle.IsInvalid;
    }

    public static bool TryParsePhysicalDriveNumber(string? value, out int number)
    {
        number = -1;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string normalized = value.Trim();
        const string rawPrefix = @"\\.\PhysicalDrive";
        if (!normalized.StartsWith(rawPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        int index = rawPrefix.Length;
        int start = index;
        while (index < normalized.Length && char.IsDigit(normalized[index]))
            index++;

        return index > start && int.TryParse(normalized[start..index], out number) && number >= 0;
    }

    private static long TryGetLength(SafeFileHandle handle)
    {
        byte[] output = new byte[8];
        bool ok = DeviceIoControl(
            handle,
            IoctlDiskGetLengthInfo,
            IntPtr.Zero,
            0,
            output,
            output.Length,
            out int returned,
            IntPtr.Zero);

        return ok && returned >= 8 ? Math.Max(0, BitConverter.ToInt64(output, 0)) : 0;
    }

    private static int TryGetLogicalSectorSize(SafeFileHandle handle)
    {
        byte[] query = new byte[12];
        Buffer.BlockCopy(BitConverter.GetBytes(StorageAccessAlignmentProperty), 0, query, 0, 4);
        byte[] output = new byte[64];

        bool ok = DeviceIoControl(
            handle,
            IoctlStorageQueryProperty,
            query,
            query.Length,
            output,
            output.Length,
            out int returned,
            IntPtr.Zero);

        if (ok && returned >= 24)
        {
            uint logical = BitConverter.ToUInt32(output, 16);
            if (logical is >= 512 and <= 65536 && (logical & (logical - 1)) == 0)
                return (int)logical;
        }

        return 512;
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
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

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
