using NSXVeriKurtarmaPro.Infrastructure;

namespace NSXVeriKurtarmaPro.Models;

public sealed class StorageDeviceInfo : ObservableObject
{
    private bool _isSelected;

    public required string RootPath { get; init; }
    public required string DisplayName { get; init; }
    public required string Kind { get; init; }
    public required string KindGlyph { get; init; }
    public required string VisualKind { get; init; }
    public required string FileSystem { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsReady { get; init; }
    public int? PhysicalDriveNumber { get; init; }
    public bool IsWholePhysicalDisk { get; init; }
    public bool IsPartitionSource { get; init; }
    public bool IsRecoveredPartition { get; init; }
    public long PartitionOffsetBytes { get; init; }
    public long PartitionLengthBytes { get; init; }
    public string PartitionScheme { get; init; } = string.Empty;
    public string PartitionIdentity { get; init; } = string.Empty;
    public int PartitionConfidence { get; init; }
    public string HardwareSerialNumber { get; init; } = string.Empty;
    public uint? VolumeSerialNumber { get; init; }
    public string MediaDetectionSource { get; init; } = string.Empty;
    public string BusTypeText { get; init; } = string.Empty;
    public bool? TrimEnabled { get; init; }
    public bool CameraContentDetected { get; init; }
    public string CameraDetectionSource { get; init; } = string.Empty;
    public int HealthSeverity { get; init; }
    public bool SmartAvailable { get; init; }
    public bool SmartPredictFailure { get; init; }
    public long SmartReallocatedSectors { get; init; }
    public long SmartPendingSectors { get; init; }
    public long SmartUncorrectableSectors { get; init; }
    public int? SmartTemperatureC { get; init; }
    public string HealthSummary { get; init; } = string.Empty;
    public bool HealthSafeScanRecommended => HealthSeverity >= 2;
    public bool HasHealthInfo => SmartAvailable || HealthSeverity > 0;
    public string HealthText => HealthSeverity switch
    {
        >= 3 => "SMART • Kritik • Güvenli Tarama",
        2 => "SMART • Dikkat • Güvenli Tarama",
        1 => "SMART • İyi",
        _ => "SMART • Bilinmiyor"
    };
    public string StatusText => IsReady ? "Bağlı" : "Hazır Değil";
    public string DeviceImageSource
    {
        get
        {
            return VisualKind switch
            {
                "Sd" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/sd_card.png",
                "UnknownUsb" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/unknown_device.png",
                "Usb" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/usb_flash.png",
                "ExternalSsd" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/external_disk.png",
                "ExternalHdd" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/external_disk.png",
                "ExternalDisk" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/external_disk.png",
                "Camera" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/external_disk.png",
                "Ssd" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/internal_ssd.png",
                "Hdd" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/fixed_disk.png",
                "FixedDisk" => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/fixed_disk.png",
                _ => "/NSXVeriKurtarmaPro;component/Assets/DeviceVisuals/fixed_disk.png"
            };
        }
    }
    public double UsedPercent
    {
        get => IsWholePhysicalDisk || IsPartitionSource || TotalBytes <= 0
            ? 0
            : Math.Clamp((double)(TotalBytes - FreeBytes) / TotalBytes * 100d, 0d, 100d);
        // ProgressBar.Value RangeBase üzerinde varsayılan olarak TwoWay bağlanabilir.
        // Bu değer cihaz kapasitesinden hesaplandığı için dışarıdan yazılan değeri bilinçli olarak yok sayıyoruz.
        set { }
    }
    public string CapacityText
    {
        get
        {
            string fs = string.IsNullOrWhiteSpace(FileSystem) ? "RAW" : FileSystem;
            string suffix = IsWholePhysicalDisk
                ? " • Tüm Fiziksel Disk"
                : IsPartitionSource
                    ? $" • {PartitionScheme}{(IsRecoveredPartition ? " • Kayıp Bölüm Adayı" : string.Empty)}"
                    : string.Empty;
            return $"{FormatBytes(TotalBytes)} • {fs}{suffix}";
        }
    }
    public string PhysicalDeviceText => PhysicalDriveNumber is int n ? $"PhysicalDrive{n}" : "Fiziksel aygıt: bilinmiyor";
    public string SourceKey => IsPartitionSource && PhysicalDriveNumber is int partitionDisk
        ? $"physical:{partitionDisk}:partition:{PartitionOffsetBytes}:{PartitionLengthBytes}"
        : IsWholePhysicalDisk && PhysicalDriveNumber is int wholeDisk
            ? $"physical:{wholeDisk}:whole"
            : RootPath;
    public string SpaceText => IsWholePhysicalDisk
        ? $"Salt okunur • {FormatBytes(TotalBytes)} fiziksel aygıt"
        : IsPartitionSource
            ? $"Başlangıç {FormatBytes(PartitionOffsetBytes)} • Güven %{Math.Clamp(PartitionConfidence, 0, 100)}"
            : $"{FormatBytes(FreeBytes)} boş / {FormatBytes(TotalBytes)} toplam";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00";
        return $"{value.ToString(format, System.Globalization.CultureInfo.CurrentCulture)} {units[index]}";
    }
}
