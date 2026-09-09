using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class SdScanPolicy
{
    public static bool IsMemoryCard(StorageDeviceInfo device) =>
        device.VisualKind.Equals("Sd", StringComparison.OrdinalIgnoreCase) ||
        device.BusTypeText.Trim().ToUpperInvariant() is "SD" or "SDIO" or "MMC";
}
