using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Universal scan entry-point helper. Windows can expose a readable volume/partition while
/// DriveInfo.FileSystem is empty, RAW or stale after format/reconnect. The unified recovery
/// pipeline therefore verifies the on-disk boot record before deciding which metadata parser
/// to use. Detection is strictly read-only; unsupported/corrupt layouts simply fall back to
/// the full RAW pass.
/// </summary>
internal static class FileSystemAutoDetectionService
{
    private static readonly byte[] NtfsSignature = "NTFS    "u8.ToArray();
    private static readonly byte[] ExFatSignature = "EXFAT   "u8.ToArray();
    private static readonly byte[] Fat32Signature = "FAT32   "u8.ToArray();
    private static readonly byte[] Fat16Signature = "FAT16   "u8.ToArray();
    private static readonly byte[] Fat12Signature = "FAT12   "u8.ToArray();

    public static string ResolveForUnifiedScan(
        StorageDeviceInfo device,
        OperationPauseGate? pauseGate,
        RecoveryMediaProfile mediaProfile,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(device);

        string declared = Normalize(device.FileSystem);
        if (IsMetadataSupported(declared))
        {
            diagnostic = $"dosya sistemi {declared} Windows/aygit bilgisinden dogrulandi";
            return declared;
        }

        // A whole physical disk normally starts with MBR/GPT, not a filesystem VBR. The
        // universal RAW pass is authoritative there; partition cards exposed by the device
        // service are metadata-aware and will be handled when selected.
        if (device.IsWholePhysicalDisk)
        {
            diagnostic = "tum fiziksel disk kaynagi: bolumden bagimsiz RAW tarama aktif";
            return string.Empty;
        }

        try
        {
            using RawDeviceReader reader = RawDeviceReader.OpenDevice(device, pauseGate, mediaProfile);
            int probeLength = (int)Math.Min(4096L, Math.Max(512L, reader.VolumeLength));
            if (probeLength < 512)
            {
                diagnostic = "VBR okunamayacak kadar kisa; RAW fallback";
                return string.Empty;
            }

            byte[] boot = reader.ReadBytes(0, probeLength);
            string detected = DetectBootFileSystem(boot, reader.VolumeLength);
            if (IsMetadataSupported(detected))
            {
                diagnostic = string.IsNullOrWhiteSpace(declared)
                    ? $"VBR uzerinden {detected} otomatik algilandi"
                    : $"Windows {declared} bildirdi; VBR uzerinden {detected} otomatik algilandi";
                return detected;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                      System.ComponentModel.Win32Exception or InvalidDataException or
                                      ArgumentException or NotSupportedException)
        {
            diagnostic = $"dosya sistemi otomatik algilanamadi ({ex.GetType().Name}); RAW fallback";
            return string.Empty;
        }

        diagnostic = string.IsNullOrWhiteSpace(declared)
            ? "dosya sistemi bilinmiyor; tam RAW tarama aktif"
            : $"{declared} icin metadata parser yok; tam RAW tarama aktif";
        return string.Empty;
    }

    public static bool IsMetadataSupported(string? fileSystem) => Normalize(fileSystem) is
        "NTFS" or "EXFAT" or "FAT" or "FAT12" or "FAT16" or "FAT32";

    internal static string DetectBootFileSystem(ReadOnlySpan<byte> boot, long availableBytes = 0)
    {
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            return string.Empty;

        if (boot.Length >= 11 && boot.Slice(3, 8).SequenceEqual(NtfsSignature))
            return "NTFS";
        if (boot.Length >= 11 && boot.Slice(3, 8).SequenceEqual(ExFatSignature))
            return "EXFAT";
        if (boot.Length >= 90 && boot.Slice(82, 8).SequenceEqual(Fat32Signature))
            return "FAT32";
        if (boot.Length >= 62 && boot.Slice(54, 8).SequenceEqual(Fat16Signature))
            return "FAT16";
        if (boot.Length >= 62 && boot.Slice(54, 8).SequenceEqual(Fat12Signature))
            return "FAT12";

        return DetectFatByBpb(boot, availableBytes);
    }

    private static string DetectFatByBpb(ReadOnlySpan<byte> boot, long availableBytes)
    {
        if (boot.Length < 64)
            return string.Empty;

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        byte sectorsPerCluster = boot[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
        byte fatCount = boot[16];
        ushort rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
        ushort totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(19, 2));
        ushort fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(32, 4));
        uint fatSize32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(36, 4));

        if (!ValidSectorSize(bytesPerSector) || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128 ||
            reservedSectors == 0 || fatCount is 0 or > 4)
        {
            return string.Empty;
        }

        long totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (totalSectors <= 0)
            return string.Empty;

        long fatSize = fatSize16 != 0 ? fatSize16 : fatSize32;
        if (fatSize <= 0)
            return string.Empty;

        long rootDirSectors = ((long)rootEntryCount * 32 + (bytesPerSector - 1)) / bytesPerSector;
        long dataSectors = totalSectors - (reservedSectors + (long)fatCount * fatSize + rootDirSectors);
        if (dataSectors <= 0)
            return string.Empty;

        long clusterCount = dataSectors / sectorsPerCluster;
        string detected = clusterCount switch
        {
            < 4085 => "FAT12",
            < 65525 => "FAT16",
            _ => "FAT32"
        };

        long length;
        try
        {
            length = checked(totalSectors * bytesPerSector);
        }
        catch (OverflowException)
        {
            return string.Empty;
        }

        if (length <= 0 || (availableBytes > 0 && length > availableBytes + Math.Max(4096, bytesPerSector * 16L)))
            return string.Empty;

        // FAT32 uses a zero FAT16 size and zero fixed-root entry count. Reject impossible
        // hybrids so damaged/random sectors are not promoted to metadata parsers.
        if (detected == "FAT32" && (fatSize16 != 0 || rootEntryCount != 0))
            return string.Empty;
        if (detected is "FAT12" or "FAT16" && (fatSize16 == 0 || rootEntryCount == 0))
            return string.Empty;

        return detected;
    }

    private static string Normalize(string? fileSystem)
    {
        string value = (fileSystem ?? string.Empty).Trim().ToUpperInvariant();
        return value switch
        {
            "EXFAT" => "EXFAT",
            "FAT12" => "FAT12",
            "FAT16" => "FAT16",
            "FAT32" => "FAT32",
            "FAT" => "FAT",
            "NTFS" => "NTFS",
            _ => value
        };
    }

    private static bool ValidSectorSize(int value) => value is 512 or 1024 or 2048 or 4096;
    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;
}
