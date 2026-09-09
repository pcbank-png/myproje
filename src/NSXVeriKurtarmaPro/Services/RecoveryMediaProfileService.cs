using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Universal Recovery Core I/O profile. The recovery algorithms stay identical on every
/// medium; only read geometry, buffering and media-specific reconstruction priority change.
/// This prevents a fast/portable device from silently falling back to a weaker scan path.
/// </summary>
public sealed record RecoveryMediaProfile(
    string Key,
    string DisplayName,
    int ScanBlockSize,
    int ScanOverlap,
    int ReaderBufferSize,
    bool CameraOptimized,
    bool AdvancedVideoReconstruction,
    bool TrimAware,
    bool? TrimEnabled)
{
    public bool SafeScanPreferred { get; init; }
    public string HealthSummary { get; init; } = string.Empty;

    public string DiagnosticText
    {
        get
        {
            string camera = CameraOptimized ? " • Kamera/AVCHD" : string.Empty;
            string trim = TrimAware
                ? TrimEnabled switch
                {
                    true => " • TRIM aktif",
                    false => " • TRIM pasif",
                    _ => " • TRIM bilinmiyor"
                }
                : string.Empty;
            string health = SafeScanPreferred ? " • SMART Safe Scan" : string.Empty;
            return $"{DisplayName}{camera}{trim}{health}";
        }
    }
}

internal static class RecoveryMediaProfileService
{
    private const int KiB = 1024;
    private const int MiB = 1024 * KiB;

    public static RecoveryMediaProfile Create(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        string visual = (device.VisualKind ?? string.Empty).Trim();
        string bus = (device.BusTypeText ?? string.Empty).Trim();
        bool cameraOptimized = device.CameraContentDetected ||
                               visual.Equals("Camera", StringComparison.OrdinalIgnoreCase);

        if (bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "nvme",
                "NVMe yüksek bant genişliği profili",
                32 * MiB,
                64 * KiB,
                2 * MiB,
                cameraOptimized,
                true,
                true,
                device.TrimEnabled));
        }

        if (visual is "Ssd" or "ExternalSsd")
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "ssd",
                visual == "ExternalSsd" ? "Harici SSD profili" : "SSD profili",
                16 * MiB,
                64 * KiB,
                1 * MiB,
                cameraOptimized,
                true,
                true,
                device.TrimEnabled));
        }

        if (visual == "Hdd" || visual == "ExternalHdd")
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "hdd",
                visual == "ExternalHdd" ? "Harici HDD profili" : "Fiziksel HDD profili",
                8 * MiB,
                64 * KiB,
                512 * KiB,
                cameraOptimized,
                true,
                false,
                device.TrimEnabled));
        }

        if (visual == "Sd")
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "sd",
                cameraOptimized ? "SD / kamera medya profili" : "SD / hafıza kartı profili",
                8 * MiB,
                64 * KiB,
                512 * KiB,
                cameraOptimized,
                true,
                false,
                device.TrimEnabled));
        }

        if (visual == "Camera")
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "camera",
                "USB kamera / dahili hafıza profili",
                8 * MiB,
                128 * KiB,
                512 * KiB,
                true,
                true,
                false,
                device.TrimEnabled));
        }

        if (visual is "Usb" or "UnknownUsb")
        {
            return ApplyHealth(device, new RecoveryMediaProfile(
                "usb",
                cameraOptimized
                    ? "USB kamera medya profili"
                    : visual == "UnknownUsb" ? "USB uyumlu kurtarma profili" : "USB Flash profili",
                8 * MiB,
                64 * KiB,
                512 * KiB,
                cameraOptimized,
                true,
                false,
                device.TrimEnabled));
        }

        return ApplyHealth(device, new RecoveryMediaProfile(
            "universal",
            "Universal salt-okunur medya profili",
            8 * MiB,
            64 * KiB,
            512 * KiB,
            cameraOptimized,
            true,
            false,
            device.TrimEnabled));
    }

    private static RecoveryMediaProfile ApplyHealth(StorageDeviceInfo device, RecoveryMediaProfile profile) =>
        profile with
        {
            SafeScanPreferred = device.HealthSafeScanRecommended,
            HealthSummary = device.HealthSummary ?? string.Empty
        };
}
