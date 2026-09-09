using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class PortableQuickScanPolicy
{
    public const long FatDirectoryRescueMaxBytes = 512L * 1024 * 1024;

    public static bool IsPortableMountedSource(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.IsWholePhysicalDisk || device.IsPartitionSource)
            return false;

        // Quick Scan'in mevcut davranisini koru: mounted harici HDD/SSD de
        // removable/portable kaynak politikasinda kalir. Deep Scan'in flash/SD
        // dusuk-gecikmeli yolu icin asagidaki daha dar helper kullanilir.
        string visual = Normalize(device.VisualKind);
        if (visual is "USB" or "SD" or "EXTERNALHDD" or "EXTERNALSSD" or "EXTERNALDISK" or "CAMERA" or "UNKNOWNUSB")
            return true;

        string bus = Normalize(device.BusTypeText);
        return bus.Contains("USB", StringComparison.Ordinal) ||
               bus is "SD" or "SDIO" or "MMC" ||
               bus.Contains("SD CARD", StringComparison.Ordinal) ||
               bus.Contains("MICROSD", StringComparison.Ordinal) ||
               bus.Contains("MMC", StringComparison.Ordinal) ||
               bus.Contains("CARD READER", StringComparison.Ordinal);
    }

    /// <summary>
    /// USB flash, SD/microSD ve kamera medyasini kaynagin drive-letter, partition-view veya
    /// whole-PhysicalDrive olmasindan bagimsiz tanir. Deep Scan bu sinyali yalnizca okuma
    /// geometrisini ve canlilik/progress politikasini ayarlamak icin kullanir; tarama kapsami
    /// degismez ve kaynak yine salt-okunur kalir.
    /// </summary>
    public static bool IsPortableRecoverySource(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        string visual = Normalize(device.VisualKind);
        if (visual is "USB" or "SD" or "CAMERA" or "UNKNOWNUSB")
            return true;

        // External HDD/SSD fiziksel olarak USB uzerinden gelebilir fakat flash/kart icin
        // kullandigimiz dusuk-gecikmeli metadata/RAW politikasi bunlara zorla uygulanmaz.
        if (visual is "EXTERNALHDD" or "EXTERNALSSD" or "HDD" or "SSD")
            return false;

        string bus = Normalize(device.BusTypeText);
        return bus.Contains("USB", StringComparison.Ordinal) ||
               bus is "SD" or "SDIO" or "MMC" ||
               bus.Contains("SD CARD", StringComparison.Ordinal) ||
               bus.Contains("MICROSD", StringComparison.Ordinal) ||
               bus.Contains("MMC", StringComparison.Ordinal) ||
               bus.Contains("CARD READER", StringComparison.Ordinal);
    }

    public static bool IsFatFamily(string? fileSystem)
    {
        string fs = Normalize(fileSystem);
        return fs is "FAT" or "FAT12" or "FAT16" or "FAT32";
    }

    public static bool IsQuickScanFileSystemSupported(string? fileSystem)
    {
        string fs = Normalize(fileSystem);
        return fs is "NTFS" or "FAT" or "FAT12" or "FAT16" or "FAT32" or "EXFAT";
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
