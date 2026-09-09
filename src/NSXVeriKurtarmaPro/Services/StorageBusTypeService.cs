using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

internal enum StorageBusType
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    Iscsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19
}

internal readonly record struct StorageDescriptorInfo(
    StorageBusType BusType,
    bool RemovableMedia,
    string Vendor,
    string Product,
    string Revision,
    string SerialNumber)
{
    public static StorageDescriptorInfo Empty => new(StorageBusType.Unknown, false, string.Empty, string.Empty, string.Empty, string.Empty);

    public string SearchText => string.Join(" ", new[] { Vendor, Product, Revision, SerialNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
    public bool HasIdentity => BusType != StorageBusType.Unknown
                               || !string.IsNullOrWhiteSpace(Vendor)
                               || !string.IsNullOrWhiteSpace(Product)
                               || !string.IsNullOrWhiteSpace(SerialNumber);
}

internal static class StorageBusTypeService
{
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    public static StorageBusType TryGetBusType(string rootPath) => TryGetDescriptor(rootPath).BusType;

    public static StorageDescriptorInfo TryGetDescriptor(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
            return StorageDescriptorInfo.Empty;

        string drive = rootPath.Trim().TrimEnd('\\');
        if (drive.Length < 2 || drive[1] != ':')
            return StorageDescriptorInfo.Empty;

        return TryGetDescriptorFromDevicePath($@"\\.\{drive}");
    }

    public static StorageDescriptorInfo TryGetPhysicalDescriptor(int physicalDriveNumber)
    {
        if (!OperatingSystem.IsWindows() || physicalDriveNumber < 0)
            return StorageDescriptorInfo.Empty;

        return TryGetDescriptorFromDevicePath($@"\\.\PhysicalDrive{physicalDriveNumber}");
    }

    private static StorageDescriptorInfo TryGetDescriptorFromDevicePath(string rawPath)
    {
        using SafeFileHandle handle = CreateFile(
            rawPath,
            0,
            ShareRead | ShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return StorageDescriptorInfo.Empty;

        byte[] query = new byte[12];
        byte[] output = new byte[2048];

        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                query,
                query.Length,
                output,
                output.Length,
                out int returned,
                IntPtr.Zero) || returned < 32)
        {
            return StorageDescriptorInfo.Empty;
        }

        int busValue = BitConverter.ToInt32(output, 28);
        StorageBusType busType = Enum.IsDefined(typeof(StorageBusType), busValue)
            ? (StorageBusType)busValue
            : StorageBusType.Unknown;

        bool removableMedia = output[10] != 0;
        int vendorOffset = BitConverter.ToInt32(output, 12);
        int productOffset = BitConverter.ToInt32(output, 16);
        int revisionOffset = BitConverter.ToInt32(output, 20);
        int serialOffset = BitConverter.ToInt32(output, 24);

        return new StorageDescriptorInfo(
            busType,
            removableMedia,
            ReadAnsiString(output, returned, vendorOffset),
            ReadAnsiString(output, returned, productOffset),
            ReadAnsiString(output, returned, revisionOffset),
            ReadAnsiString(output, returned, serialOffset));
    }

    private static string ReadAnsiString(byte[] buffer, int validLength, int offset)
    {
        if (offset <= 0 || offset >= validLength || offset >= buffer.Length)
            return string.Empty;

        int end = offset;
        int max = Math.Min(validLength, buffer.Length);
        while (end < max && buffer[end] != 0)
            end++;

        if (end <= offset)
            return string.Empty;

        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
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
