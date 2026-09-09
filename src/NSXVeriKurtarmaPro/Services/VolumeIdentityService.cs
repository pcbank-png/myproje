using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

internal readonly record struct VolumeIdentityInfo(
    bool IsReady,
    uint? SerialNumber,
    string FileSystem,
    long TotalBytes);

/// <summary>
/// Mounted-volume identity that does not depend on resolving a second PhysicalDrive handle.
/// This is important for USB/NVMe bridge drivers that expose the volume normally but reject
/// topology/descriptor IOCTLs on a separate physical handle.
/// </summary>
internal static class VolumeIdentityService
{
    public static bool TryGetIdentity(string rootPath, out VolumeIdentityInfo identity)
    {
        identity = default;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(rootPath));
            if (string.IsNullOrWhiteSpace(root) || root.Length < 3 || root[1] != ':')
                return false;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                identity = new VolumeIdentityInfo(false, null, string.Empty, 0);
                return true;
            }

            uint? serial = null;
            string fileSystem = drive.DriveFormat ?? string.Empty;
            var fsName = new StringBuilder(64);
            if (GetVolumeInformation(
                    root,
                    null,
                    0,
                    out uint serialNumber,
                    out _,
                    out _,
                    fsName,
                    fsName.Capacity))
            {
                serial = serialNumber;
                if (fsName.Length > 0)
                    fileSystem = fsName.ToString();
            }

            identity = new VolumeIdentityInfo(
                true,
                serial,
                fileSystem,
                Math.Max(0, drive.TotalSize));
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);
}
