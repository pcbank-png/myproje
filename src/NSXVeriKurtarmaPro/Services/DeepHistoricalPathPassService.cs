using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Deep Scan path-only metadata pass for FAT-family media.
/// Reuses filesystem parsers and enables bounded historical-directory rescue for unified scans.
/// Legacy callers retain their conditional rescue policy. The resolved filesystem is passed through
/// so RAW/stale Windows labels do not prevent metadata recovery. No recovery writes occur here.
/// </summary>
internal static class DeepHistoricalPathPassService
{
    public static bool ShouldRun(StorageDeviceInfo device) => ShouldRun(device.FileSystem);

    internal static bool ShouldRun(string? resolvedFileSystem)
    {
        string fileSystem = (resolvedFileSystem ?? string.Empty).Trim().ToUpperInvariant();
        return fileSystem is "EXFAT" or "FAT" or "FAT12" or "FAT16" or "FAT32";
    }

    public static ScanReport? Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        string? resolvedFileSystem = null,
        bool forceRescue = false)
    {
        string fileSystem = (resolvedFileSystem ?? device.FileSystem ?? string.Empty).Trim().ToUpperInvariant();
        return fileSystem switch
        {
            "EXFAT" => new ExFatQuickScanService().Scan(
                device,
                progress,
                pauseGate,
                cancellationToken,
                deepScanPrelude: false,
                includeExistingFiles: false,
                forceHistoricalPathRescue: forceRescue),

            "FAT32" => new Fat32QuickScanService().Scan(
                device,
                progress,
                pauseGate,
                cancellationToken,
                allowPortableRescue: true,
                includeExistingFiles: false,
                forceHistoricalPathRescue: forceRescue),

            "FAT" or "FAT12" or "FAT16" => new FatQuickScanService().Scan(
                device,
                progress,
                pauseGate,
                cancellationToken,
                allowPortableRescue: true,
                includeExistingFiles: false,
                forceHistoricalPathRescue: forceRescue),

            _ => null
        };
    }
}
