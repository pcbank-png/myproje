using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// True Quick Scan pipeline. Quick Scan is metadata-first and never invokes DeepScanService.
/// Old rotational disks whose deleted metadata has been reused may add one sequential Quick
/// Surface/Free-Space pass. That pass is intentionally single-pass and does not execute Deep
/// Scan fragment graphs, reconstruction or retry passes.
/// </summary>
public sealed class QuickScanService
{
    private readonly NtfsQuickScanService _ntfs = new();
    private readonly Fat32QuickScanService _fat32 = new();
    private readonly FatQuickScanService _fat = new();
    private readonly ExFatQuickScanService _exFat = new();

    public ScanReport Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        long resumePosition = 0,
        IReadOnlyList<RecoveryFileItem>? seedResults = null,
        DeepScanTarget scope = DeepScanTarget.All,
        RecoveryScanCheckpoint? resumeCheckpoint = null)
    {
        string fileSystem = (device.FileSystem ?? string.Empty).Trim().ToUpperInvariant();

        progress?.Report(new OperationProgress(
            0,
            "Hızlı Tarama • Metadata",
            $"{device.DisplayName} • {fileSystem} • {device.BusTypeText} • yalnız dosya sistemi metadata kayıtları okunacak; RAW/Derin fallback çalıştırılmayacak."));

        if (!PortableQuickScanPolicy.IsQuickScanFileSystemSupported(fileSystem))
        {
            if (NtfsQuickScanService.IsLegacyRotationalQuickPath(device))
            {
                // Old SATA disks can lose their VBR/partition metadata while the MFT itself is
                // still recoverable. NtfsQuickScanService contains a bounded FILE-record locator
                // for exactly this case; it does not perform full-media RAW carving.
                try
                {
                    ScanReport legacyMetadata = _ntfs.Scan(
                        device,
                        progress,
                        pauseGate,
                        cancellationToken,
                        resumePosition,
                        seedResults,
                        scope,
                        resumeCheckpoint: resumeCheckpoint);
                    return legacyMetadata with
                    {
                        Summary = $"{legacyMetadata.Summary} • True Quick Legacy: bounded MFT metadata rescue; tam disk RAW taraması yapılmadı.",
                        UsedFallback = false
                    };
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                             System.ComponentModel.Win32Exception or OverflowException or ArgumentOutOfRangeException)
                {
                    IReadOnlyList<RecoveryFileItem> preserved = seedResults ?? Array.Empty<RecoveryFileItem>();
                    progress?.Report(new OperationProgress(
                        0,
                        "Hızlı Tarama • Legacy Surface Hazırlanıyor",
                        $"Metadata kurtarma yeterli değil ({ex.Message}). Eski dönel disk için tek geçişli Quick Surface taraması başlatılıyor; Deep Scan motoru kullanılmayacak.",
                        0,
                        device.TotalBytes,
                        preserved.Count));
                    return LegacySurfaceQuickScanService.ScanDeviceSurface(
                        device,
                        progress,
                        pauseGate,
                        cancellationToken,
                        preserved,
                        scope,
                        $"Legacy metadata kullanılamadı ({ex.Message}).",
                        resumeCheckpoint);
                }
            }

            IReadOnlyList<RecoveryFileItem> unsupported = seedResults ?? Array.Empty<RecoveryFileItem>();
            return new ScanReport(
                unsupported,
                $"{fileSystem} dosya sistemi Hızlı Tarama metadata motoru tarafından doğrulanamadı. Quick Scan tam disk RAW taramasına dönüştürülmedi; kayıp içerik için Derin Tarama kullanılabilir.",
                ScanMode.Quick,
                UsedFallback: false);
        }

        try
        {
            ScanReport metadataReport = fileSystem switch
            {
                "NTFS" => _ntfs.Scan(device, progress, pauseGate, cancellationToken, resumePosition, seedResults, scope, resumeCheckpoint: resumeCheckpoint),
                "FAT" or "FAT12" or "FAT16" => _fat.Scan(device, progress, pauseGate, cancellationToken),
                "FAT32" => _fat32.Scan(device, progress, pauseGate, cancellationToken),
                "EXFAT" => _exFat.Scan(device, progress, pauseGate, cancellationToken),
                _ => throw new InvalidOperationException("Desteklenmeyen Hızlı Tarama dosya sistemi.")
            };

            RecoveryMediaProfile profile = RecoveryMediaProfileService.Create(device);
            IReadOnlyList<RecoveryFileItem> enriched = CameraSequenceIntelligenceService.Enrich(
                metadataReport.Files,
                profile.CameraOptimized);

            string summary = metadataReport.Summary;
            if (!summary.Contains("Surface Quick", StringComparison.OrdinalIgnoreCase) &&
                !summary.Contains("Hızlı Yüzey", StringComparison.OrdinalIgnoreCase) &&
                !summary.Contains("Free Space Quick", StringComparison.OrdinalIgnoreCase) &&
                !summary.Contains("Hızlı Boş Alan", StringComparison.OrdinalIgnoreCase))
            {
                summary = $"{summary} • True Quick: metadata/bounded rescue tamamlandı; Deep Scan motoru çalıştırılmadı.";
            }
            else
            {
                summary = $"{summary} • True Quick Surface: tek geçiş; Deep Scan 4-pass/reconstruction çalıştırılmadı.";
            }

            return metadataReport with
            {
                Files = enriched,
                Summary = summary,
                UsedFallback = false
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceMediaIdentityException)
        {
            // Source identity is a safety gate, not a filesystem metadata miss. Never turn it
            // into a successful zero-result Quick Scan; surface the real error to the UI.
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                     System.ComponentModel.Win32Exception or OverflowException or ArgumentOutOfRangeException)
        {
            IReadOnlyList<RecoveryFileItem> preserved = seedResults ?? Array.Empty<RecoveryFileItem>();

            // Mounted USB/SD media must never look like a successful one-second zero-result scan
            // when its filesystem metadata could not actually be read. Surface the real failure
            // to the UI; Deep Scan remains an explicit user choice. Fixed disks keep the existing
            // legacy handling unchanged.
            if (PortableQuickScanPolicy.IsPortableMountedSource(device))
            {
                throw new InvalidDataException(
                    $"{fileSystem} USB/SD Hızlı Tarama metadata'sı okunamadı: {ex.Message}",
                    ex);
            }

            if (NtfsQuickScanService.IsLegacyRotationalQuickPath(device))
            {
                progress?.Report(new OperationProgress(
                    0,
                    "Hızlı Tarama • Quick Surface Hazırlanıyor",
                    $"{fileSystem} metadata okunamadı ({ex.Message}). Eski dönel disk tek geçişli Quick Surface ile devam edecek; Deep Scan motoru çalıştırılmayacak.",
                    0,
                    device.TotalBytes,
                    preserved.Count));
                return LegacySurfaceQuickScanService.ScanDeviceSurface(
                    device,
                    progress,
                    pauseGate,
                    cancellationToken,
                    preserved,
                    scope,
                    $"{fileSystem} metadata okunamadı ({ex.Message}).",
                    resumeCheckpoint);
            }

            progress?.Report(new OperationProgress(
                100,
                "Hızlı Tarama • Metadata Kullanılamadı",
                $"{fileSystem} metadata okunamadı: {ex.Message} • Hızlı Tarama güvenli biçimde sonlandırıldı; Deep Scan otomatik başlatılmadı.",
                0,
                0,
                preserved.Count));

            return new ScanReport(
                preserved,
                $"{fileSystem} Hızlı Tarama metadata katmanı okunamadı ({ex.Message}). Gerekiyorsa Derin Tarama kullanın.",
                ScanMode.Quick,
                UsedFallback: false);
        }
    }
}
