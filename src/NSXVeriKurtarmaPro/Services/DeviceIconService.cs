using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// 6 Simge Sistemi - Depolama cihazlarını doğru simgelerle gösterir
/// Simgeler: HDD 🖲️ | SSD ⚡ | USB 🔌 | Kart 📱 | External 💾 | Format ⚠️
/// </summary>
public sealed class DeviceIconService
{
    public enum DeviceIcon
    {
        Hdd,           // Sabit Disk
        Ssd,           // Solid State Drive
        Usb,           // USB Bellek
        Card,          // SD/Hafıza Kartı
        External,      // Harici Disk
        Format,        // Biçimlendirme Gerekli
        Unknown        // Bilinmeyen
    }

    public DeviceIcon GetDeviceIcon(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        // Format gerekli durumu kontrol et
        if (IsFormattingRequired(device))
            return DeviceIcon.Format;

        // VisualKind'a göre simge seç
        return device.VisualKind switch
        {
            "Hdd" => DeviceIcon.Hdd,
            "Ssd" => DeviceIcon.Ssd,
            "Usb" => DeviceIcon.Usb,
            "Sd" => DeviceIcon.Card,
            "ExternalHdd" => DeviceIcon.External,
            "ExternalSsd" => DeviceIcon.External,
            "ExternalDisk" => DeviceIcon.External,
            "Camera" => DeviceIcon.Card,
            "UnknownUsb" => DeviceIcon.Usb,
            _ => DetermineIconByMediaType(device)
        };
    }

    public string GetDeviceIconGlyph(DeviceIcon icon)
    {
        return icon switch
        {
            DeviceIcon.Hdd => "🖲️",      // Sabit Disk
            DeviceIcon.Ssd => "⚡",       // SSD
            DeviceIcon.Usb => "🔌",       // USB
            DeviceIcon.Card => "📱",      // Kart
            DeviceIcon.External => "💾",  // External
            DeviceIcon.Format => "⚠️",    // Format
            _ => "❓"                      // Bilinmeyen
        };
    }

    public string GetDeviceIconPath(DeviceIcon icon)
    {
        return icon switch
        {
            DeviceIcon.Hdd => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/fixed_disk.png",
            DeviceIcon.Ssd => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/internal_ssd.png",
            DeviceIcon.Usb => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/usb_flash.png",
            DeviceIcon.Card => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/sd_card.png",
            DeviceIcon.External => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/external_disk.png",
            DeviceIcon.Format => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/format_warning.png",
            _ => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/unknown_device.png"
        };
    }

    public string GetDeviceDescription(DeviceIcon icon)
    {
        return icon switch
        {
            DeviceIcon.Hdd => "Sabit Disk (HDD) - Dönel Disk",
            DeviceIcon.Ssd => "Solid State Drive (SSD) - Hızlı Disk",
            DeviceIcon.Usb => "USB Flash Bellek",
            DeviceIcon.Card => "SD / Hafıza Kartı",
            DeviceIcon.External => "Harici Depolama",
            DeviceIcon.Format => "Biçimlendirme Gerekli",
            _ => "Bilinmeyen Cihaz"
        };
    }

    private DeviceIcon DetermineIconByMediaType(StorageDeviceInfo device)
    {
        // Medya türüne göre belirle
        if (device.MediaDetectionSource.Contains("Rotational", StringComparison.OrdinalIgnoreCase) ||
            device.BusTypeText.Contains("SATA", StringComparison.OrdinalIgnoreCase))
            return DeviceIcon.Hdd;

        if (device.MediaDetectionSource.Contains("SolidState", StringComparison.OrdinalIgnoreCase) ||
            device.BusTypeText.Contains("NVMe", StringComparison.OrdinalIgnoreCase))
            return DeviceIcon.Ssd;

        if (device.BusTypeText.Contains("USB", StringComparison.OrdinalIgnoreCase))
            return DeviceIcon.Usb;

        if (device.BusTypeText.Contains("SD", StringComparison.OrdinalIgnoreCase) ||
            device.BusTypeText.Contains("MMC", StringComparison.OrdinalIgnoreCase))
            return DeviceIcon.Card;

        return DeviceIcon.Unknown;
    }

    private static bool IsFormattingRequired(StorageDeviceInfo device)
    {
        // Windows "formatla gerekli" göstermesi durumu
        if (!device.IsReady || device.TotalBytes <= 0)
            return true;

        // Dosya sistemi bilgisi eksikse
        if (string.IsNullOrWhiteSpace(device.FileSystem) || device.FileSystem.Equals("RAW", StringComparison.OrdinalIgnoreCase))
            return !device.IsWholePhysicalDisk && !device.IsPartitionSource;

        return false;
    }
}
