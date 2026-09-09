using System.Diagnostics;
using System.Buffers;
using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Dosya sistemine bağlı kalmadan ham sektörleri tarar. Standart imza carving'e ek olarak
/// MP4/MOV kutu parçaları (mdat/moof/styp/moov) ve MTS/M2TS transport stream akışlarını
/// tanıyarak başlığı kısmen kaybolmuş video adaylarını da listeler.
/// </summary>
public sealed class DeepScanService
{
    private readonly Func<RawDeviceReader>? _openReader;
    public DeepScanService() { }
    internal DeepScanService(Func<RawDeviceReader> openReader) => _openReader = openReader;

    private const int DefaultScanBlockSize = 8 * 1024 * 1024;
    private const int DefaultScanOverlap = 4096;
    private const int MaxResults = 500000;
    private const int MaxMetadataResults = 500000;
    private const int MaxRawResults = 350000;

    // Ultra video scan phase one only visits bytes that can begin a supported
    // container/stream anchor. The expensive parsers run exclusively on this shortlist.
    private static readonly SearchValues<byte> VideoAnchorLeadBytes = SearchValues.Create(
    [
        0x00, 0x06, 0x1A, 0x1F, 0x2E, 0x30, 0x36, 0x47, 0x84, 0xB7,
        (byte)'B', (byte)'D', (byte)'F', (byte)'K', (byte)'N', (byte)'O', (byte)'R', (byte)'S',
        (byte)'f', (byte)'m', (byte)'s'
    ]);

    // 32-byte standard ISO-BMFF başlığı. Orijinal ftyp bölümü silinmiş fakat
    // mdat + moov yapısı korunmuş videolarda en yaygın 32-byte yerleşimi korur.
    private static readonly byte[] AsfHeaderSignature =
    [
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    ];

    private static readonly byte[] AsfDataObjectSignature =
    [
        0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    ];

    private static readonly byte[] OleHeaderSignature =
    [
        0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1
    ];

    private static readonly byte[] StandardMp4FtypHeader =
    [
        0x00, 0x00, 0x00, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x02, 0x00,
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2',
        (byte)'a', (byte)'v', (byte)'c', (byte)'1', (byte)'m', (byte)'p', (byte)'4', (byte)'1'
    ];

    private static readonly byte[] MxfHeaderPrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x02
    };

    private static readonly byte[] MxfEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0D, 0x01, 0x03, 0x01
    ];

    private static readonly byte[] MxfAvidEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0E, 0x04, 0x03, 0x01
    ];

    private static readonly byte[] MxfCanopusEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x0A,
        0x0E, 0x0F, 0x03, 0x01
    ];

    private static readonly byte[] WtvHeaderSignature = new byte[]
    {
        0xB7, 0xD8, 0x00, 0x20, 0x37, 0x49, 0xDA, 0x11,
        0xA6, 0x4E, 0x00, 0x07, 0xE9, 0x5E, 0xAD, 0x8D
    };

    private static readonly byte[] RoqHeaderSignature = new byte[]
    {
        0x84, 0x10, 0xFF, 0xFF, 0xFF, 0xFF
    };

    public ScanReport Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        long resumeOffset = 0,
        IReadOnlyList<RecoveryFileItem>? seedResults = null,
        DeepScanTarget scope = DeepScanTarget.All,
        bool skipMetadataStage = false,
        RecoveryScanCheckpoint? resumeCheckpoint = null,
        bool unifiedFastScan = false)
    {
        if (!device.IsReady || device.TotalBytes <= 0)
            throw new InvalidOperationException("Seçili aygıt taramaya hazır değil.");

        RecoveryMediaProfile mediaProfile = RecoveryMediaProfileService.Create(device);
        string metadataFileSystem = FileSystemAutoDetectionService.ResolveForUnifiedScan(
            device,
            pauseGate,
            mediaProfile,
            out string metadataFileSystemDiagnostic);
        bool preserveTransportBoundaries = scope.Includes(DeepScanTarget.Video);
        bool responsivePortableScan = PortableDeepScanPolicy.IsResponsivePortableSource(device, mediaProfile);
        using DeepScanProgressPulse? progressPulse = responsivePortableScan && progress is not null
            ? new DeepScanProgressPulse(
                progress,
                cancellationToken,
                PortableDeepScanPolicy.GetProgressHeartbeat(responsivePortableScan))
            : null;
        progress = progressPulse ?? progress;
        int responsiveBlockSize = PortableDeepScanPolicy.GetInitialScanBlockSize(mediaProfile, responsivePortableScan);
        if (responsiveBlockSize > 0 && responsiveBlockSize != mediaProfile.ScanBlockSize)
            mediaProfile = mediaProfile with { ScanBlockSize = responsiveBlockSize };

        // Unified scan is metadata-first, then full RAW. USB flash/SD/camera media must not
        // spend minutes in a 512 MB / 1 GB extended metadata-rescue path before the user sees
        // the first RAW result. Keep that prelude bounded on responsive portable sources; if
        // metadata is too damaged, the complete sequential RAW pass immediately takes over.
        bool allowExtendedMetadataRescue = !(unifiedFastScan && responsivePortableScan);
        int scanBlockSize = mediaProfile.ScanBlockSize > 0 ? mediaProfile.ScanBlockSize : DefaultScanBlockSize;
        int scanOverlap = mediaProfile.ScanOverlap > 0 ? mediaProfile.ScanOverlap : DefaultScanOverlap;
        if (unifiedFastScan && responsivePortableScan)
        {
            // Keep common photos/documents entirely inside the already-read block whenever they
            // cross an 8 MB boundary. The overlap is sequential I/O and is far cheaper than
            // hundreds of random source-media seeks during candidate validation.
            scanOverlap = Math.Max(scanOverlap, 512 * 1024);
        }

        var results = seedResults is { Count: > 0 }
            ? seedResults.Where(item => MatchesScope(item, scope)).ToList()
            : new List<RecoveryFileItem>();
        NtfsPathCorrelationService pathCorrelation = NtfsPathCorrelationService.Build(results);
        int pathCorrelatedCount = pathCorrelation.ApplyTo(results);
        RecoveredPathCorrelationService recoveredPathCorrelation = RecoveredPathCorrelationService.Build(results);
        int recoveredPathCorrelatedCount = recoveredPathCorrelation.ApplyTo(results);
        int deepHistoricalPathMetadataCount = 0;
        var deepHistoricalPathEvidence = new List<RecoveryFileItem>();
        var historicalFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rawDirectoryIndexRecovery = new NtfsDirectoryIndexRecoveryService();
        var exactStarts = new HashSet<long>(
            results.Where(item => item.SourceKind == RecoverySourceKind.RawContiguous && item.SourceOffset >= 0)
                .Select(item => item.SourceOffset));
        long total = device.TotalBytes;
        long offset = Math.Clamp(resumeOffset, 0, total);
        bool metadataStageCompleted = skipMetadataStage || resumeCheckpoint?.MetadataStageCompleted == true || offset > 0;
        long lastProgressReport = offset;
        int sequence = Math.Max(1, results.Count + 1);
        int rebuiltVideoCount = results.Count(item =>
            item.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı");
        int metadataCount = results.Count(item => item.SourceKind != RecoverySourceKind.RawContiguous);
        int rawResultCount = results.Count(item => item.SourceKind == RecoverySourceKind.RawContiguous);
        int reportedResultCount = results.Count;
        int falsePositiveRejected = 0;
        bool ultraVideoScan = scope == DeepScanTarget.Video;
        // The public one-button scan is discovery-first. Embedded H264/H265 fragment hunting
        // and graph reconstruction remain available to legacy/video-only paths and Preview/Repair,
        // but they must not turn a normal all-file scan into hundreds of random media seeks.
        bool advancedVideoReconstruction = !unifiedFastScan &&
                                           scope.Includes(DeepScanTarget.Video) &&
                                           mediaProfile.AdvancedVideoReconstruction;
        bool scanEmbeddedVideoAnchors = !unifiedFastScan && advancedVideoReconstruction && !ultraVideoScan;
        long unreadableBytes = 0;
        long trimZeroFilledBytes = 0;
        byte[] buffer = new byte[scanBlockSize + scanOverlap];
        var orphanFragmentNodes = (resumeCheckpoint?.PendingFragmentNodes ?? [])
            .Select(item => item.ToItem())
            .OfType<RecoveryFileItem>()
            .Where(item => item.SourceOffset >= 0 && item.SizeBytes >= 4096)
            .GroupBy(item => (item.SourceOffset, FileTypeHelper.Normalize(item.Extension), item.TransformKind))
            .Select(group => group.First())
            .ToList();
        var orphanStarts = new HashSet<long>(orphanFragmentNodes.Select(item => item.SourceOffset));
        int lastCheckpointedOrphanCount = orphanFragmentNodes.Count;
        // Keep the sequential discovery sweep I/O-bound on every disk type. Large/truncated
        // candidates are queued for full validation after the fast surface pass instead of
        // blocking the current block for seconds or minutes.
        var deferredRawCandidates = new List<DeferredRawCandidate>();
        var deferredRawStarts = new HashSet<long>();
        var verifiedTransportRanges = new List<(long Start, long End)>();

        // Derin tarama önce erişilebilir dosya sistemi kayıtlarını toplar, ardından tüm medyayı
        // RAW carving ile tarar. USB/SD üzerinde Quick Scan'e ait 512 MB / 1 GB rescue yolları
        // burada tekrar çalıştırılmaz; aynı alan zaten eksiksiz RAW geçişte okunacaktır.
        try
        {
            if (metadataStageCompleted)
            {
                Report(
                    progress,
                    offset,
                    total,
                    results,
                    rebuiltVideoCount,
                    $"Tarama devam ediyor • Konum {RecoveryFileItem.FormatBytes(offset)} / {RecoveryFileItem.FormatBytes(total)}",
                    ref reportedResultCount,
                    titleOverride: responsivePortableScan
                        ? ultraVideoScan
                            ? "Ultra Video • Pass 2/4 • Adaptive RAW"
                            : "Derin Tarama • Pass 2/4 • Adaptive RAW"
                        : null,
                    stagedPortableProgress: responsivePortableScan);
            }
            else
            {
                string metadataTitle = ultraVideoScan
                    ? "Ultra Video • Pass 1/4 • Kayıt Analizi"
                    : "Derin Tarama • Pass 1/4 • Dosya Kayıtları";
                string responsiveNotice = responsivePortableScan
                    ? " USB/SD metadata-first etkin: gerçek dosya sistemi kataloğu tamamlanır, sonuçlar canlı listelenir; ardından tam RAW taramaya geçilir."
                    : string.Empty;

                progress?.Report(new OperationProgress(
                    0,
                    metadataTitle,
                    ultraVideoScan
                        ? $"Pass 1/4: dosya sistemi kayıtları analiz ediliyor; ardından medya yapısı ve ham veri alanları adaptif olarak taranacak.{responsiveNotice} Profil: {mediaProfile.DiagnosticText}."
                        : $"Pass 1/4: dosya sistemi kayıtları analiz ediliyor; ardından tüm medya alanında adaptif ham veri taraması yapılacak.{responsiveNotice} Profil: {mediaProfile.DiagnosticText}.",
                    0,
                    total,
                    results.Count,
                    null,
                    new RecoveryScanCheckpoint
                    {
                        PassNumber = 1,
                        Stage = "metadata",
                        ResumePosition = 0,
                        ResumeTotal = total,
                        MetadataStageCompleted = false
                    }));

                var metadataByKey = new Dictionary<string, RecoveryFileItem>(StringComparer.OrdinalIgnoreCase);
                foreach (RecoveryFileItem existingItem in results)
                {
                    string existingKey = BuildDedupeKey(existingItem);
                    metadataByKey.TryAdd(existingKey, existingItem);
                }

                var metadataSeen = new HashSet<string>(
                    metadataByKey.Keys,
                    StringComparer.OrdinalIgnoreCase);
                IProgress<OperationProgress>? metadataProgress = progress is null
                    ? null
                    : new InlineProgress<OperationProgress>(metadataUpdate =>
                    {
                        if (metadataUpdate.NewHistoricalFolders is { Count: > 0 })
                        {
                            foreach (string folderPath in metadataUpdate.NewHistoricalFolders)
                            {
                                if (!string.IsNullOrWhiteSpace(folderPath))
                                    historicalFolderPaths.Add(folderPath);
                            }
                        }

                        // Fixed/internal disks retain the established Stage 5 update cadence.
                        // The no-result heartbeat path is deliberately scoped to USB flash/SD/camera.
                        if (!responsivePortableScan &&
                            metadataUpdate.NewFiles is not { Count: > 0 } &&
                            metadataUpdate.NewHistoricalFolders is not { Count: > 0 })
                            return;

                        var forwarded = new List<RecoveryFileItem>();
                        if (metadataUpdate.NewFiles is { Count: > 0 })
                        {
                            IEnumerable<RecoveryFileItem> candidates = OrderMetadataCandidates(
                                metadataUpdate.NewFiles,
                                scope,
                                mediaProfile);

                            foreach (RecoveryFileItem candidate in candidates)
                            {
                                if (!ultraVideoScan && results.Count >= MaxMetadataResults)
                                    break;

                                string key = BuildDedupeKey(candidate);
                                if (!metadataSeen.Add(key))
                                    continue;

                                RecoveryConfidenceService.ApplyMetadataConfidence(candidate);
                                results.Add(candidate);
                                metadataByKey[key] = candidate;
                                RegisterMetadataSourceStart(exactStarts, candidate);
                                forwarded.Add(candidate);
                            }
                        }

                        metadataCount = results.Count(item => item.SourceKind != RecoverySourceKind.RawContiguous);
                        reportedResultCount = results.Count;
                        string metadataDetail = string.IsNullOrWhiteSpace(metadataUpdate.Detail)
                            ? "Dosya sistemi kayıtları salt-okunur analiz ediliyor."
                            : metadataUpdate.Detail;
                        double sourcePercent = Math.Clamp(metadataUpdate.Percent, 0d, 100d);
                        double visiblePercent = responsivePortableScan
                            ? PortableDeepScanPolicy.MapMetadataPercent(sourcePercent)
                            : 0d;
                        string visibleTitle = responsivePortableScan
                            ? metadataTitle
                            : ultraVideoScan ? "Ultra Video • Kayıt Analizi" : "Derin Tarama • Kayıt Analizi";
                        string visibleDetail = responsivePortableScan
                            ? $"Pass 1/4 • {metadataUpdate.Title} • {metadataDetail} • {metadataFileSystemDiagnostic} • metadata %{sourcePercent:0.0} • bulunan {results.Count:N0} • ardından tam RAW tarama."
                            : $"{metadataDetail} • {metadataFileSystemDiagnostic} • zaman bilgisi bulunan kayıtlar öncelikli değerlendiriliyor.";
                        progress.Report(new OperationProgress(
                            visiblePercent,
                            visibleTitle,
                            visibleDetail,
                            0,
                            total,
                            results.Count,
                            forwarded.Count > 0 ? forwarded : null,
                            new RecoveryScanCheckpoint
                            {
                                PassNumber = 1,
                                Stage = "metadata",
                                ResumePosition = 0,
                                ResumeTotal = total,
                                MetadataStageCompleted = false
                            },
                            metadataUpdate.NewHistoricalFolders));
                    });

                CancellationToken metadataToken = cancellationToken;
                ScanReport? metadataReport = null;

                metadataReport = metadataFileSystem switch
                {
                        "NTFS" => new NtfsQuickScanService().Scan(
                            device,
                            metadataProgress,
                            pauseGate,
                            metadataToken,
                            scope: scope,
                            allowExtendedRescue: allowExtendedMetadataRescue,
                            enableUsnPriority: PortableDeepScanPolicy.EnableNtfsUsnPriority(device, mediaProfile),
                            includeExistingFiles: true),
                    "EXFAT" => new ExFatQuickScanService().Scan(
                        device,
                        metadataProgress,
                        pauseGate,
                        metadataToken,
                        deepScanPrelude: unifiedFastScan && responsivePortableScan,
                        includeExistingFiles: true),
                    "FAT" or "FAT12" or "FAT16" => new FatQuickScanService().Scan(
                        device,
                        metadataProgress,
                        pauseGate,
                        metadataToken,
                        allowPortableRescue: allowExtendedMetadataRescue,
                        includeExistingFiles: true),
                    "FAT32" => new Fat32QuickScanService().Scan(
                        device,
                        metadataProgress,
                        pauseGate,
                        metadataToken,
                        allowPortableRescue: allowExtendedMetadataRescue,
                        includeExistingFiles: true),
                    _ => null
                };

                var finalForwarded = new List<RecoveryFileItem>();
                if (metadataReport is not null)
                {
                    foreach (string folderPath in metadataReport.HistoricalFolders)
                    {
                        if (!string.IsNullOrWhiteSpace(folderPath))
                            historicalFolderPaths.Add(folderPath);
                    }

                    IEnumerable<RecoveryFileItem> metadataItems = OrderMetadataCandidates(
                        metadataReport.Files,
                        scope,
                        mediaProfile);

                    foreach (RecoveryFileItem item in metadataItems)
                    {
                        if (!ultraVideoScan && results.Count >= MaxMetadataResults)
                            break;

                        string key = BuildDedupeKey(item);
                        if (!metadataSeen.Add(key))
                            continue;

                        RecoveryConfidenceService.ApplyMetadataConfidence(item);
                        results.Add(item);
                        metadataByKey[key] = item;
                        RegisterMetadataSourceStart(exactStarts, item);
                        finalForwarded.Add(item);
                    }
                }

                // One surviving deleted file does not account for other deleted directory trees.
                // Search bounded historical metadata on USB/SD too, before RAW carving, which
                // cannot recover original names on its own.
                bool needsHistoricalPathRescue = unifiedFastScan ||
                    (metadataReport is not null &&
                     !metadataReport.Files.Any(item => !item.IsExistingFile));
                if (DeepHistoricalPathPassService.ShouldRun(metadataFileSystem) && needsHistoricalPathRescue)
                {
                    progress?.Report(new OperationProgress(
                        PortableDeepScanPolicy.MetadataPhaseEndPercent,
                        ultraVideoScan
                            ? "Ultra Video • Pass 1/4 • Özgün Yol Metadata"
                            : "Derin Tarama • Pass 1/4 • Özgün Yol Metadata",
                        "Hızlı Tarama'da doğrulanan FAT/exFAT tarihsel parent/child yolu Derin Tarama için de salt-okunur toplanıyor; RAW motoru henüz değiştirilmedi.",
                        0,
                        total,
                        results.Count));

                    IProgress<OperationProgress>? historicalPathProgress = progress is null
                        ? null
                        : new InlineProgress<OperationProgress>(pathUpdate =>
                        {
                            if (pathUpdate.NewHistoricalFolders is { Count: > 0 })
                            {
                                foreach (string folderPath in pathUpdate.NewHistoricalFolders)
                                {
                                    if (!string.IsNullOrWhiteSpace(folderPath))
                                        historicalFolderPaths.Add(folderPath);
                                }
                            }

                            string pathDetail = string.IsNullOrWhiteSpace(pathUpdate.Detail)
                                ? "Tarihsel dizin metadata'sı analiz ediliyor."
                                : pathUpdate.Detail;
                            progress.Report(new OperationProgress(
                                PortableDeepScanPolicy.MetadataPhaseEndPercent,
                                ultraVideoScan
                                    ? "Ultra Video • Pass 1/4 • Özgün Yol Metadata"
                                    : "Derin Tarama • Pass 1/4 • Özgün Yol Metadata",
                                $"{pathUpdate.Title} • {pathDetail}",
                                pathUpdate.ProcessedBytes,
                                pathUpdate.TotalBytes,
                                results.Count,
                                null,
                                new RecoveryScanCheckpoint
                                {
                                    PassNumber = 1,
                                    Stage = "historical-path-metadata",
                                    ResumePosition = 0,
                                    ResumeTotal = total,
                                    MetadataStageCompleted = false
                                },
                                pathUpdate.NewHistoricalFolders));
                        });

                    try
                    {
                        ScanReport? historicalPathReport = DeepHistoricalPathPassService.Scan(
                            device,
                            historicalPathProgress,
                            pauseGate,
                            cancellationToken,
                            resolvedFileSystem: metadataFileSystem,
                            forceRescue: unifiedFastScan);

                        if (historicalPathReport is not null)
                        {
                            foreach (string folderPath in historicalPathReport.HistoricalFolders)
                            {
                                if (!string.IsNullOrWhiteSpace(folderPath))
                                    historicalFolderPaths.Add(folderPath);
                            }

                            foreach (RecoveryFileItem pathItem in OrderMetadataCandidates(
                                         historicalPathReport.Files,
                                         scope,
                                         mediaProfile))
                            {
                                if (!string.IsNullOrWhiteSpace(pathItem.RecoveredOriginalPath))
                                    deepHistoricalPathEvidence.Add(pathItem);

                                string key = BuildDedupeKey(pathItem);
                                if (metadataByKey.TryGetValue(key, out RecoveryFileItem? existingItem) &&
                                    existingItem is not null)
                                {
                                    if (MergeRecoveredPathEvidence(existingItem, pathItem))
                                        deepHistoricalPathMetadataCount++;
                                    continue;
                                }

                                if (!ultraVideoScan && results.Count >= MaxMetadataResults)
                                    break;
                                if (!metadataSeen.Add(key))
                                    continue;

                                RecoveryConfidenceService.ApplyMetadataConfidence(pathItem);
                                results.Add(pathItem);
                                metadataByKey[key] = pathItem;
                                // Do not add historical-path-only evidence to exactStarts. RAW
                                // carving must still inspect the same physical offset and may
                                // recover a stronger payload; the zero-I/O correlation layer will
                                // attach this proven path afterwards.
                                finalForwarded.Add(pathItem);
                                if (!string.IsNullOrWhiteSpace(pathItem.RecoveredOriginalPath))
                                    deepHistoricalPathMetadataCount++;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SourceMediaIdentityException)
                    {
                        throw;
                    }
                    catch (Exception pathEx) when (pathEx is IOException or InvalidDataException or UnauthorizedAccessException or
                                                       System.ComponentModel.Win32Exception or OverflowException or
                                                       ArgumentOutOfRangeException or NotSupportedException)
                    {
                        progress?.Report(new OperationProgress(
                            PortableDeepScanPolicy.MetadataPhaseEndPercent,
                            "Derin Tarama • Özgün Yol Metadata İzole Edildi",
                            $"Tarihsel yol metadata pass'i tamamlanamadı ({pathEx.Message}); RAW/Video motorları değiştirilmeden tam Derin Tarama devam edecek.",
                            0,
                            total,
                            results.Count));
                    }
                }

                // Metadata stage may have recovered original NTFS paths and portable FAT/exFAT
                // extents. Rebuild both zero-I/O correlation indexes so RAW candidates restored
                // from a session or produced by an earlier pass can inherit only proven paths.
                pathCorrelation = NtfsPathCorrelationService.Build(results);
                pathCorrelatedCount += pathCorrelation.ApplyTo(results);
                recoveredPathCorrelation = RecoveredPathCorrelationService.Build(
                    results.Concat(deepHistoricalPathEvidence));
                recoveredPathCorrelatedCount += recoveredPathCorrelation.ApplyTo(results);

                metadataCount = results.Count(item => item.SourceKind != RecoverySourceKind.RawContiguous);
                reportedResultCount = results.Count;
                metadataStageCompleted = true;
                string metadataCompletionDetail = metadataReport is null
                    ? $"Pass 1/4 için kullanılabilir metadata parser bulunamadı • {metadataFileSystemDiagnostic} • Pass 2/4 evrensel RAW tarama şimdi başlıyor; disk türünden bağımsız hiçbir medya alanı atlanmayacak."
                    : $"Pass 1/4 tamamlandı • {metadataCount:N0} gerçek dosya sistemi kaydı doğrulandı • Pass 2/4 tam RAW tarama şimdi başlıyor; hiçbir medya alanı atlanmayacak.";

                if (responsivePortableScan)
                {
                    progress?.Report(new OperationProgress(
                        PortableDeepScanPolicy.MetadataPhaseEndPercent,
                        metadataTitle,
                        metadataCompletionDetail,
                        0,
                        total,
                        results.Count,
                        finalForwarded.Count > 0
                            ? RecoveryScanPriorityService.OrderNewestFirst(finalForwarded).ToArray()
                            : null,
                        new RecoveryScanCheckpoint
                        {
                            PassNumber = 1,
                            Stage = "metadata-complete",
                            ResumePosition = 0,
                            ResumeTotal = total,
                            MetadataStageCompleted = true
                        }));
                }
                else if (metadataReport is not null)
                {
                    Report(progress, 0, total, results, rebuiltVideoCount, null, ref reportedResultCount);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ScanReport(results, "Derin tarama kullanıcı isteğiyle durduruldu.", ScanMode.Deep);
        }
        catch (SourceMediaIdentityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                      System.ComponentModel.Win32Exception or OverflowException or
                                      ArgumentOutOfRangeException or NotSupportedException)
        {
            metadataStageCompleted = true;
            if (responsivePortableScan)
            {
                progress?.Report(new OperationProgress(
                    PortableDeepScanPolicy.MetadataPhaseEndPercent,
                    ultraVideoScan
                        ? "Ultra Video • Pass 1/4 • RAW'a Geçiliyor"
                        : "Derin Tarama • Pass 1/4 • RAW'a Geçiliyor",
                    $"Dosya sistemi metadata ön analizi tamamlanamadı ({ex.Message}) • Pass 2/4 tam RAW tarama devam edecek; kaynak üzerinde hiçbir yazma yapılmadı.",
                    0,
                    total,
                    results.Count,
                    null,
                    new RecoveryScanCheckpoint
                    {
                        PassNumber = 1,
                        Stage = "metadata-bypassed",
                        ResumePosition = 0,
                        ResumeTotal = total,
                        MetadataStageCompleted = true
                    }));
            }
        }
        catch (Exception ex) when (responsivePortableScan && ex is not OutOfMemoryException)
        {
            // Corrupt removable-media metadata must never strand Deep Scan in Pass 1. Source
            // identity and user cancellation were handled above; any remaining parser fault is
            // isolated to the optional prelude and the complete RAW pass remains authoritative.
            metadataStageCompleted = true;
            progress?.Report(new OperationProgress(
                PortableDeepScanPolicy.MetadataPhaseEndPercent,
                ultraVideoScan
                    ? "Ultra Video • Pass 1/4 • RAW'a Geçiliyor"
                    : "Derin Tarama • Pass 1/4 • RAW'a Geçiliyor",
                $"Bozuk/uyumsuz metadata ön analizi güvenli biçimde izole edildi ({ex.GetType().Name}: {ex.Message}) • Pass 2/4 tam RAW tarama devam edecek.",
                0,
                total,
                results.Count,
                null,
                new RecoveryScanCheckpoint
                {
                    PassNumber = 1,
                    Stage = "metadata-isolated",
                    ResumePosition = 0,
                    ResumeTotal = total,
                    MetadataStageCompleted = true
                }));
        }
        catch (Exception) when (!responsivePortableScan)
        {
            // Preserve the established fixed/internal disk behavior: an optional metadata
            // prelude failure must never stop the proven full RAW path.
            metadataStageCompleted = true;
        }

        progressPulse?.SetActivity(new OperationProgress(
            PortableDeepScanPolicy.MapRawPercent(offset, total),
            ultraVideoScan ? "Ultra Video • Pass 2/4 • Kaynak Hazırlığı" : "Derin Tarama • Pass 2/4 • Kaynak Hazırlığı",
            "USB/SD salt-okunur RAW kaynak tanıtıcısı açılıyor; motor canlılık sinyali bu hazırlık sırasında da devam eder.",
            offset,
            total,
            results.Count));

        using RawDeviceReader reader = _openReader?.Invoke() ?? RawDeviceReader.OpenDevice(device, pauseGate, mediaProfile);
        reader.HeatMap.RestoreState(resumeCheckpoint);
        if (reader.VolumeLength > 0)
        {
            total = reader.VolumeLength;
            offset = Math.Clamp(offset, 0, total);
        }

        // Deep recovery must not trust the *current* filesystem cluster size as a hard
        // boundary. After formatting/repartitioning an old HDD, previous files can begin on
        // sector boundaries that are no longer cluster boundaries in the new filesystem.
        // Sector alignment is the safe common denominator across NTFS/FAT/exFAT media.
        var rawGeometry = new RawScanGeometry(0, Math.Max(512, reader.SectorSize));
        var adaptiveScan = new AdaptiveDeepScanController(mediaProfile, rawGeometry.Alignment);
        adaptiveScan.RestoreState(resumeCheckpoint);
        long rawProgressIntervalBytes = PortableDeepScanPolicy.GetProgressIntervalBytes(responsivePortableScan);
        TimeSpan rawProgressHeartbeat = PortableDeepScanPolicy.GetProgressHeartbeat(responsivePortableScan);
        long lastProgressReportTimestamp = Stopwatch.GetTimestamp();
        int requiredBufferLength = checked(adaptiveScan.MaxBlockSize + scanOverlap);
        if (buffer.Length < requiredBufferLength)
            buffer = new byte[requiredBufferLength];

        bool rawStageAlreadyCompleted = resumeCheckpoint is { RawScanCompleted: true, PassNumber: >= 3 } &&
                                        total > 0 && offset >= total;
        if (!rawStageAlreadyCompleted)
        {
            progress?.Report(new OperationProgress(
                responsivePortableScan
                    ? PortableDeepScanPolicy.MapRawPercent(offset, total)
                    : total <= 0 ? 0 : Math.Clamp(offset * 100d / total, 0d, 99.5d),
                ultraVideoScan ? "Ultra Video • Pass 2/4 • Adaptive RAW" : "Derin Tarama • Pass 2/4 • Adaptive RAW",
                $"Pass 2/4: hiçbir bayt atlanmadan sıralı RAW carving başladı" +
                (responsivePortableScan ? " • USB/SD düşük gecikmeli başlangıç ve canlı motor sinyali aktif" : string.Empty) +
                $" • {adaptiveScan.DiagnosticText} • Profil: {mediaProfile.DiagnosticText}.",
                offset,
                total,
                results.Count,
                null,
                CreateCheckpoint(
                    2,
                    "deep-raw",
                    offset,
                    total,
                    metadataStageCompleted: true,
                    rawScanCompleted: offset >= total && total > 0,
                    fragmentStageCompleted: false,
                    finalValidationStarted: false,
                    reader,
                    adaptiveScan,
                    orphanFragmentNodes,
                    lastCheckpointedOrphanCount)));
        }

        while (offset < total && (ultraVideoScan || rawResultCount < MaxRawResults) && !cancellationToken.IsCancellationRequested)
        {
            pauseGate?.Wait(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                break;

            int activeScanBlockSize = adaptiveScan.CurrentBlockSize;
            int request = (int)Math.Min((long)activeScanBlockSize + scanOverlap, total - offset);
            int blockRawStart = rawResultCount;
            int blockOrphanStart = orphanFragmentNodes.Count;
            int blockSignatureMatches = 0;
            long nextPortableScanProgress = offset + PortableDeepScanPolicy.RawInBlockProgressBytes;

            progressPulse?.SetActivity(new OperationProgress(
                PortableDeepScanPolicy.MapRawPercent(offset, total),
                ultraVideoScan ? "Ultra Video • Pass 2/4 • Kaynak Okuma" : "Derin Tarama • Pass 2/4 • Kaynak Okuma",
                $"USB/SD motoru aktif • {RecoveryFileItem.FormatBytes(offset)} konumundan {RecoveryFileItem.FormatBytes(request)} salt-okunur blok okunuyor.",
                offset,
                total,
                results.Count));

            long readStartedAt = Stopwatch.GetTimestamp();
            int bytesRead = reader.ReadBestEffort(
                offset,
                buffer.AsSpan(0, request),
                out long blockUnreadableBytes);
            double readElapsedMilliseconds = Stopwatch.GetElapsedTime(readStartedAt).TotalMilliseconds;
            unreadableBytes += blockUnreadableBytes;

            if (bytesRead <= 0)
                break;

            int scanLength = Math.Min(activeScanBlockSize, bytesRead);
            progressPulse?.SetActivity(new OperationProgress(
                PortableDeepScanPolicy.MapRawPercent(offset, total),
                ultraVideoScan ? "Ultra Video • Pass 2/4 • Blok Analizi" : "Derin Tarama • Pass 2/4 • Blok Analizi",
                $"USB/SD motoru aktif • {RecoveryFileItem.FormatBytes(scanLength)} blok okundu ({readElapsedMilliseconds:0} ms) • dosya imzaları ve kurtarma adayları doğrulanıyor.",
                offset,
                total,
                results.Count));

            if (mediaProfile.TrimAware && IsLikelyZeroFilledBlock(buffer.AsSpan(0, scanLength)))
                trimZeroFilledBytes += scanLength;

            // PRO YOL V6: harvest orphaned NTFS INDX records from the block already read by
            // Deep Scan. This performs no extra media I/O and can recover historical folder
            // edges even when the owning directory MFT record was deleted/reused.
            if (!unifiedFastScan || metadataFileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                rawDirectoryIndexRecovery.InspectRawBuffer(
                    buffer.AsSpan(0, bytesRead),
                    offset,
                    reader.SectorSize,
                    cancellationToken);
            }

            int candidateSearchStart = 0;
            while (candidateSearchStart < scanLength &&
                   (ultraVideoScan || rawResultCount < MaxRawResults) &&
                   !cancellationToken.IsCancellationRequested)
            {
                if (preserveTransportBoundaries && ultraVideoScan)
                {
                    candidateSearchStart = SkipVerifiedTransportPayload(verifiedTransportRanges, offset, candidateSearchStart, scanLength);
                    if (candidateSearchStart >= scanLength) break;
                }
                int i = ultraVideoScan
                    ? responsivePortableScan
                        ? FindNextPortableVideoAnchorLead(buffer, candidateSearchStart, scanLength)
                        : FindNextVideoAnchorLead(buffer, candidateSearchStart, scanLength)
                    : FindNextSectorAlignedCandidate(
                        buffer,
                        offset,
                        candidateSearchStart,
                        scanLength,
                        rawGeometry.Alignment);

                if (i < 0)
                    break;

                // Always move the search cursor forward before any continue path below.
                candidateSearchStart = i + 1;

                if (responsivePortableScan && offset + i >= nextPortableScanProgress)
                {
                    long inspected = Math.Min(total, offset + i);
                    progressPulse?.SetActivity(new OperationProgress(
                        PortableDeepScanPolicy.MapRawPercent(inspected, total),
                        ultraVideoScan ? "Ultra Video • Pass 2/4 • Hızlı İmza Geçişi" : "Derin Tarama • Pass 2/4 • Hızlı İmza Geçişi",
                        $"USB/SD hızlı geçiş • blok içi {RecoveryFileItem.FormatBytes(i)} / {RecoveryFileItem.FormatBytes(scanLength)} incelendi • uzun adaylar ana taramayı bekletmeden erteleniyor.",
                        inspected,
                        total,
                        results.Count));
                    nextPortableScanProgress = inspected + PortableDeepScanPolicy.RawInBlockProgressBytes;
                }

                if ((i & 0x0FFF) == 0)
                {
                    pauseGate?.Wait(cancellationToken);
                    if (cancellationToken.IsCancellationRequested)
                        break;
                }

                SignatureKind? matchedKind = ultraVideoScan
                    ? TryMatchVideoAt(buffer, i, bytesRead)
                    : TryMatchAt(
                        buffer,
                        i,
                        bytesRead,
                        includeEmbeddedVideoSearch: !responsivePortableScan && !unifiedFastScan);
                if (matchedKind is null)
                    continue;

                if (!SignatureMayMatchScope(matchedKind.Value, scope))
                    continue;

                blockSignatureMatches++;
                long candidateStart = offset + i;

                if (preserveTransportBoundaries && IsTransportPayloadSignature(matchedKind.Value) &&
                    verifiedTransportRanges.Any(range => candidateStart > range.Start && candidateStart < range.End))
                    continue;

                if (preserveTransportBoundaries && matchedKind == SignatureKind.MpegTs &&
                    !IsTransportCandidateRunStart(reader, buffer, i, bytesRead, offset, resumeOffset))
                    continue;

                // Standard files begin on an allocation-sector boundary. Use the raw
                // volume sector size rather than the current filesystem cluster size so old
                // files from a previous format are not rejected. Video fragment anchors are
                // intentionally exempt because they may live inside a larger container.
                if (RequiresFileBoundaryAlignment(matchedKind.Value) &&
                    !rawGeometry.IsAligned(candidateStart))
                    continue;

                if (!exactStarts.Add(candidateStart))
                    continue;

                if (!unifiedFastScan)
                {
                    progressPulse?.SetActivity(new OperationProgress(
                        PortableDeepScanPolicy.MapRawPercent(offset, total),
                        ultraVideoScan ? "Ultra Video • Pass 2/4 • Aday Doğrulama" : "Derin Tarama • Pass 2/4 • Aday Doğrulama",
                        $"USB/SD motoru aktif • {matchedKind.Value} adayı {RecoveryFileItem.FormatBytes(candidateStart)} konumunda salt-okunur doğrulanıyor.",
                        offset,
                        total,
                        results.Count));
                }

                RawFileAnalysis? analysis = null;
                bool analysisFromCurrentBlock = false;
                bool deferredByBudget = false;
                if (unifiedFastScan)
                {
                    analysis = FastBufferedCandidateAnalyzer.TryAnalyze(
                        matchedKind.Value,
                        buffer.AsSpan(i, bytesRead - i),
                        total - candidateStart);
                    analysisFromCurrentBlock = analysis is not null;
                }

                if (analysis is null)
                {
                    try
                    {
                        analysis = AnalyzeRawCandidateWithBudget(
                            reader,
                            matchedKind.Value,
                            candidateStart,
                            total,
                            responsivePortableScan,
                            unifiedFastScan,
                            cancellationToken,
                            out deferredByBudget, preserveTransportBoundaries);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or OverflowException or InvalidDataException)
                    {
                        analysis = null;
                    }
                }

                if (deferredByBudget)
                {
                    // A single huge/truncated ZIP/PDF/video must never hold the current disk
                    // block hostage. Preserve it for full validation after the sequential
                    // discovery pass, then continue immediately.
                    exactStarts.Remove(candidateStart);
                    if (deferredRawStarts.Add(candidateStart) &&
                        (!unifiedFastScan || deferredRawCandidates.Count < PortableDeepScanPolicy.UnifiedDeferredCandidateLimit))
                        deferredRawCandidates.Add(new DeferredRawCandidate(matchedKind.Value, candidateStart));
                    continue;
                }

                if (analysis is null || analysis.Length <= 0 || analysis.Length > total - candidateStart)
                {
                    if (advancedVideoReconstruction &&
                        SignatureMayMatchScope(matchedKind.Value, DeepScanTarget.Video) &&
                        orphanStarts.Add(candidateStart))
                    {
                        RecoveryFileItem? orphan = CreateOrphanFragmentNode(
                            reader,
                            matchedKind.Value,
                            candidateStart,
                            total,
                            sequence + orphanFragmentNodes.Count,
                            cancellationToken);
                        if (orphan is not null)
                            orphanFragmentNodes.Add(orphan);
                    }
                    continue;
                }

                if (preserveTransportBoundaries && matchedKind == SignatureKind.MpegTs)
                    verifiedTransportRanges.Add((candidateStart, candidateStart + analysis.Length));

                string extension = FileTypeHelper.Normalize(analysis.Extension);
                if (!FileTypeHelper.IsSupported(extension) || !MatchesScope(extension, scope))
                    continue;

                bool currentBlockHeaderValid = IsCurrentBlockHeaderValid(
                    buffer, i, bytesRead, extension, analysis.Length, analysis);
                if (RequiresFileBoundaryAlignment(matchedKind.Value) &&
                    !(unifiedFastScan
                        ? currentBlockHeaderValid
                        : ValidateWholeFileCandidateHeader(reader, candidateStart, analysis.Length, extension)))
                    continue;

                RecoveryConfidenceResult confidence = unifiedFastScan
                    ? BuildUnifiedFastConfidence(analysis, currentBlockHeaderValid, analysisFromCurrentBlock)
                    : RecoveryConfidenceService.EvaluateRawCandidate(
                        reader,
                        candidateStart,
                        analysis.Length,
                        extension,
                        analysis.RecoveryState,
                        analysis.PrependStandardMp4Header,
                        analysis.AppendData is { Length: > 0 });
                if (confidence.RejectAsFalsePositive)
                {
                    falsePositiveRejected++;
                    continue;
                }

                bool rebuiltVideo = analysis.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı";
                if (rebuiltVideo)
                    rebuiltVideoCount++;

                string fileNamePrefix = rebuiltVideo ? "Yeniden_Insa" : "Kurtarilan";
                string fileName = $"{fileNamePrefix}_{sequence:000000}_{candidateStart:X}.{extension.ToLowerInvariant()}";

                RecoveryFileItem recovered = new()
                {
                    FileName = fileName,
                    Extension = extension,
                    SizeBytes = analysis.Length,
                    RecoveryState = analysis.RecoveryState,
                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                    SourceText = analysis.RecoveryState == "Video Akışı"
                        ? "Derin RAW video akışı"
                        : rebuiltVideo ? "Derin RAW video yeniden inşa" : "Derin RAW tarama",
                    SourceKind = RecoverySourceKind.RawContiguous,
                    SourceOffset = candidateStart,
                    PrefixData = analysis.PrependStandardMp4Header ? StandardMp4FtypHeader.ToArray() : null,
                    SuffixData = analysis.AppendData,
                    TransformKind = analysis.TransformKind,
                    RecoveryConfidenceScore = confidence.Score,
                    RecoveryConfidenceGrade = confidence.Grade,
                    RecoveryConfidenceSummary = confidence.Summary
                };
                if (pathCorrelation.TryApply(recovered))
                    pathCorrelatedCount++;
                if (!recovered.HasRecoveredOriginalPath && recoveredPathCorrelation.TryApply(recovered))
                    recoveredPathCorrelatedCount++;
                results.Add(recovered);

                rawResultCount++;
                sequence++;

                if (responsivePortableScan && PortableDeepScanPolicy.IsLiveResultPriority(matchedKind.Value))
                {
                    // Do not wait for the end of the current adaptive block. A verified photo,
                    // document or archive is user-visible immediately; Report() advances the
                    // result cursor so the normal block report cannot duplicate it.
                    Report(
                        progress,
                        Math.Min(total, Math.Max(offset, candidateStart + 1)),
                        total,
                        results,
                        rebuiltVideoCount,
                        $"Canlı sonuç • {extension} doğrulandı • bulunan {results.Count:N0} • RAW tarama devam ediyor.",
                        ref reportedResultCount,
                        titleOverride: "Derin Tarama • Canlı Sonuç",
                        stagedPortableProgress: true);
                }

                // Do not jump to candidateEnd. On aged HDDs a fragmented file can make a
                // contiguous parser find an unrelated footer far ahead. Treating that as a
                // trusted end used to skip large disk ranges and hide thousands of files.
                // Sector-aligned candidate stepping keeps the general scan bounded without
                // sacrificing old-format coverage; Ultra Video keeps its fragment anchor pass.
            }

            if (scanEmbeddedVideoAnchors && !cancellationToken.IsCancellationRequested)
            {
                progressPulse?.SetActivity(new OperationProgress(
                    PortableDeepScanPolicy.MapRawPercent(offset, total),
                    "Derin Tarama • Pass 2/4 • Video Anchor Analizi",
                    $"USB/SD motoru aktif • {RecoveryFileItem.FormatBytes(scanLength)} blok içinde gerçek TS/M2TS, MP4/MOV ve codec anchor'ları doğrulanıyor; rastgele baytlar parser'a gönderilmiyor.",
                    offset,
                    total,
                    results.Count));

                int videoSearchStart = 0;
                while (videoSearchStart < scanLength &&
                       rawResultCount < MaxRawResults &&
                       !cancellationToken.IsCancellationRequested)
                {
                    if (preserveTransportBoundaries)
                    {
                        videoSearchStart = SkipVerifiedTransportPayload(verifiedTransportRanges, offset, videoSearchStart, scanLength);
                        if (videoSearchStart >= scanLength) break;
                    }
                    int i = responsivePortableScan
                        ? FindNextPortableVideoAnchorLead(buffer, videoSearchStart, scanLength)
                        : FindNextVideoAnchorLead(buffer, videoSearchStart, scanLength);
                    if (i < 0)
                        break;

                    videoSearchStart = i + 1;
                    SignatureKind? matchedKind = TryMatchVideoAt(buffer, i, bytesRead);
                    if (matchedKind is null || !SignatureMayMatchScope(matchedKind.Value, scope))
                        continue;

                    blockSignatureMatches++;
                    long candidateStart = offset + i;
                    if (preserveTransportBoundaries && verifiedTransportRanges.Any(range => candidateStart > range.Start && candidateStart < range.End))
                        continue;
                    if (preserveTransportBoundaries && matchedKind == SignatureKind.MpegTs &&
                        !IsTransportCandidateRunStart(reader, buffer, i, bytesRead, offset, resumeOffset))
                        continue;
                    if (RequiresFileBoundaryAlignment(matchedKind.Value) && !rawGeometry.IsAligned(candidateStart))
                        continue;
                    if (!exactStarts.Add(candidateStart))
                        continue;

                    progressPulse?.SetActivity(new OperationProgress(
                        PortableDeepScanPolicy.MapRawPercent(offset, total),
                        "Derin Tarama • Pass 2/4 • Video Adayı Doğrulama",
                        $"USB/SD motoru aktif • {matchedKind.Value} video adayı {RecoveryFileItem.FormatBytes(candidateStart)} konumunda salt-okunur doğrulanıyor.",
                        offset,
                        total,
                        results.Count));

                    RawFileAnalysis? analysis;
                    bool deferredByBudget = false;
                    try
                    {
                        analysis = AnalyzeRawCandidateWithBudget(
                            reader,
                            matchedKind.Value,
                            candidateStart,
                            total,
                            responsivePortableScan,
                            unifiedFastScan,
                            cancellationToken,
                            out deferredByBudget, preserveTransportBoundaries);
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or OverflowException or InvalidDataException)
                    {
                        analysis = null;
                    }

                    if (deferredByBudget)
                    {
                        exactStarts.Remove(candidateStart);
                        if (deferredRawStarts.Add(candidateStart))
                            deferredRawCandidates.Add(new DeferredRawCandidate(matchedKind.Value, candidateStart));
                        continue;
                    }

                    if (analysis is null || analysis.Length <= 0 || analysis.Length > total - candidateStart)
                    {
                        if (orphanStarts.Add(candidateStart))
                        {
                            RecoveryFileItem? orphan = CreateOrphanFragmentNode(
                                reader,
                                matchedKind.Value,
                                candidateStart,
                                total,
                                sequence + orphanFragmentNodes.Count,
                                cancellationToken);
                            if (orphan is not null)
                                orphanFragmentNodes.Add(orphan);
                        }
                        continue;
                    }

                    string extension = FileTypeHelper.Normalize(analysis.Extension);
                    if (preserveTransportBoundaries && matchedKind == SignatureKind.MpegTs)
                        verifiedTransportRanges.Add((candidateStart, candidateStart + analysis.Length));
                    if (!FileTypeHelper.IsVideo(extension) || !MatchesScope(extension, scope))
                        continue;

                    if (RequiresFileBoundaryAlignment(matchedKind.Value) &&
                        !ValidateWholeFileCandidateHeader(reader, candidateStart, analysis.Length, extension))
                        continue;

                    RecoveryConfidenceResult confidence = RecoveryConfidenceService.EvaluateRawCandidate(
                        reader,
                        candidateStart,
                        analysis.Length,
                        extension,
                        analysis.RecoveryState,
                        analysis.PrependStandardMp4Header);
                    if (confidence.RejectAsFalsePositive)
                    {
                        falsePositiveRejected++;
                        continue;
                    }

                    bool rebuiltVideo = analysis.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı";
                    if (rebuiltVideo)
                        rebuiltVideoCount++;

                    string fileNamePrefix = rebuiltVideo ? "Yeniden_Insa" : "Kurtarilan";
                    var universalRecovered = new RecoveryFileItem
                    {
                        FileName = $"{fileNamePrefix}_{sequence:000000}_{candidateStart:X}.{extension.ToLowerInvariant()}",
                        Extension = extension,
                        SizeBytes = analysis.Length,
                        RecoveryState = analysis.RecoveryState,
                        TypeGlyph = FileTypeHelper.GetGlyph(extension),
                        SourceText = analysis.RecoveryState == "Video Akışı"
                            ? "Universal Core • RAW video akışı"
                            : rebuiltVideo ? "Universal Core • video yeniden inşa" : "Universal Core • RAW video",
                        SourceKind = RecoverySourceKind.RawContiguous,
                        SourceOffset = candidateStart,
                        PrefixData = analysis.PrependStandardMp4Header ? StandardMp4FtypHeader.ToArray() : null,
                        SuffixData = analysis.AppendData,
                        TransformKind = analysis.TransformKind,
                        RecoveryConfidenceScore = confidence.Score,
                        RecoveryConfidenceGrade = confidence.Grade,
                        RecoveryConfidenceSummary = confidence.Summary
                    };
                    if (pathCorrelation.TryApply(universalRecovered))
                        pathCorrelatedCount++;
                    if (!universalRecovered.HasRecoveredOriginalPath && recoveredPathCorrelation.TryApply(universalRecovered))
                        recoveredPathCorrelatedCount++;
                    results.Add(universalRecovered);

                    rawResultCount++;
                    sequence++;
                }
            }

            adaptiveScan.Observe(new AdaptiveDeepScanObservation(
                scanLength,
                blockUnreadableBytes,
                blockSignatureMatches,
                rawResultCount - blockRawStart,
                orphanFragmentNodes.Count - blockOrphanStart,
                readElapsedMilliseconds));

            offset += scanLength;
            bool progressHeartbeatDue = responsivePortableScan &&
                Stopwatch.GetElapsedTime(lastProgressReportTimestamp) >= rawProgressHeartbeat;
            bool hasUnreportedResults = responsivePortableScan && results.Count > reportedResultCount;
            if (offset - lastProgressReport >= rawProgressIntervalBytes ||
                progressHeartbeatDue ||
                hasUnreportedResults ||
                offset >= total)
            {
                string adaptiveDetail =
                    $"Pass 2/4 • I/O {RecoveryFileItem.FormatBytes(Math.Min(offset, total))} / {RecoveryFileItem.FormatBytes(total)}" +
                    $" • {adaptiveScan.DiagnosticText}" +
                    $" • aday {blockSignatureMatches:N0}" +
                    (blockUnreadableBytes > 0 ? $" • okunamayan {RecoveryFileItem.FormatBytes(blockUnreadableBytes)}" : string.Empty) +
                    (mediaProfile.TrimAware && trimZeroFilledBytes > 0 ? $" • sıfır/TRIM alan {RecoveryFileItem.FormatBytes(trimZeroFilledBytes)}" : string.Empty) +
                    (rawDirectoryIndexRecovery.EvidenceCount > 0 ? $" • $I30 yol kanıtı {rawDirectoryIndexRecovery.EvidenceCount:N0}" : string.Empty) +
                    (reader.HeatMap.RangeCount > 0 ? $" • {reader.HeatMap.DiagnosticText}" : string.Empty);
                Report(
                    progress,
                    Math.Min(offset, total),
                    total,
                    results,
                    rebuiltVideoCount,
                    adaptiveDetail,
                    ref reportedResultCount,
                    CreateCheckpoint(
                        2,
                        "deep-raw",
                        Math.Min(offset, total),
                        total,
                        metadataStageCompleted: true,
                        rawScanCompleted: offset >= total && total > 0,
                        fragmentStageCompleted: false,
                        finalValidationStarted: false,
                        reader,
                        adaptiveScan,
                        orphanFragmentNodes,
                        lastCheckpointedOrphanCount),
                    titleOverride: responsivePortableScan
                        ? ultraVideoScan
                            ? "Ultra Video • Pass 2/4 • Adaptive RAW"
                            : "Derin Tarama • Pass 2/4 • Adaptive RAW"
                        : null,
                    stagedPortableProgress: responsivePortableScan);
                lastCheckpointedOrphanCount = orphanFragmentNodes.Count;
                lastProgressReport = offset;
                lastProgressReportTimestamp = Stopwatch.GetTimestamp();
            }
        }

        if (deferredRawCandidates.Count > 0 &&
            !cancellationToken.IsCancellationRequested && offset >= total)
        {
            progress?.Report(new OperationProgress(
                PortableDeepScanPolicy.RawPhaseEndPercent,
                ultraVideoScan
                    ? "Ultra Video • Pass 2/4 • Ertelenen Adaylar"
                    : "Derin Tarama • Pass 2/4 • Ertelenen Adaylar",
                $"Hızlı geçiş tamamlandı • ana taramayı bekletmemek için ertelenen {deferredRawCandidates.Count:N0} büyük/parçalı aday şimdi tam doğrulanıyor.",
                total,
                total,
                results.Count));

            int deferredIndex = 0;
            long deferredDrainStartedAt = Stopwatch.GetTimestamp();
            foreach (DeferredRawCandidate pending in deferredRawCandidates)
            {
                if (unifiedFastScan &&
                    Stopwatch.GetElapsedTime(deferredDrainStartedAt) >= PortableDeepScanPolicy.UnifiedDeferredDrainBudget)
                    break;
                if (cancellationToken.IsCancellationRequested)
                    break;
                pauseGate?.Wait(cancellationToken);
                deferredIndex++;

                // Another pass may already have accepted this same source start.
                if (exactStarts.Contains(pending.Start))
                    continue;

                progressPulse?.SetActivity(new OperationProgress(
                    PortableDeepScanPolicy.RawPhaseEndPercent,
                    ultraVideoScan
                        ? "Ultra Video • Pass 2/4 • Büyük Aday Doğrulama"
                        : "Derin Tarama • Pass 2/4 • Büyük Aday Doğrulama",
                    $"Ertelenen aday {deferredIndex:N0}/{deferredRawCandidates.Count:N0} • {pending.Kind} • {RecoveryFileItem.FormatBytes(pending.Start)} konumu tam doğrulanıyor.",
                    total,
                    total,
                    results.Count));

                RawFileAnalysis? analysis;
                bool deferredAgain = false;
                try
                {
                    analysis = unifiedFastScan
                        ? AnalyzeRawCandidateWithBudget(
                            reader,
                            pending.Kind,
                            pending.Start,
                            total,
                            responsivePortableScan,
                            true,
                            cancellationToken,
                            out deferredAgain,
                            preserveTransportBoundaries)
                        : RawFileAnalyzer.Analyze(
                            reader,
                            pending.Kind,
                            pending.Start,
                            total,
                            cancellationToken,
                            preserveTransportBoundaries: preserveTransportBoundaries);
                }
                catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or OverflowException or InvalidDataException)
                {
                    analysis = null;
                }

                if (deferredAgain)
                    continue;

                if (analysis is null || analysis.Length <= 0 || analysis.Length > total - pending.Start)
                    continue;

                string extension = FileTypeHelper.Normalize(analysis.Extension);
                if (!FileTypeHelper.IsSupported(extension) || !MatchesScope(extension, scope))
                    continue;

                if (RequiresFileBoundaryAlignment(pending.Kind) &&
                    !ValidateWholeFileCandidateHeader(reader, pending.Start, analysis.Length, extension))
                    continue;

                RecoveryConfidenceResult confidence = unifiedFastScan
                    ? BuildUnifiedFastConfidence(analysis, headerValid: true, fromCurrentBlock: false)
                    : RecoveryConfidenceService.EvaluateRawCandidate(
                        reader,
                        pending.Start,
                        analysis.Length,
                        extension,
                        analysis.RecoveryState,
                        analysis.PrependStandardMp4Header,
                        analysis.AppendData is { Length: > 0 });
                if (confidence.RejectAsFalsePositive)
                {
                    falsePositiveRejected++;
                    continue;
                }

                if (!exactStarts.Add(pending.Start))
                    continue;

                bool rebuiltVideo = analysis.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı";
                if (rebuiltVideo)
                    rebuiltVideoCount++;

                string fileNamePrefix = rebuiltVideo ? "Yeniden_Insa" : "Kurtarilan";
                RecoveryFileItem recovered = new()
                {
                    FileName = $"{fileNamePrefix}_{sequence:000000}_{pending.Start:X}.{extension.ToLowerInvariant()}",
                    Extension = extension,
                    SizeBytes = analysis.Length,
                    RecoveryState = analysis.RecoveryState,
                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                    SourceText = analysis.RecoveryState == "Video Akışı"
                        ? "Derin RAW video akışı"
                        : rebuiltVideo ? "Derin RAW video yeniden inşa" : "Derin RAW tarama",
                    SourceKind = RecoverySourceKind.RawContiguous,
                    SourceOffset = pending.Start,
                    PrefixData = analysis.PrependStandardMp4Header ? StandardMp4FtypHeader.ToArray() : null,
                    SuffixData = analysis.AppendData,
                    TransformKind = analysis.TransformKind,
                    RecoveryConfidenceScore = confidence.Score,
                    RecoveryConfidenceGrade = confidence.Grade,
                    RecoveryConfidenceSummary = confidence.Summary
                };

                if (pathCorrelation.TryApply(recovered))
                    pathCorrelatedCount++;
                if (!recovered.HasRecoveredOriginalPath && recoveredPathCorrelation.TryApply(recovered))
                    recoveredPathCorrelatedCount++;
                results.Add(recovered);
                rawResultCount++;
                sequence++;
                progress?.Report(new OperationProgress(
                    PortableDeepScanPolicy.RawPhaseEndPercent,
                    "Derin Tarama • Pass 2/4 • Ertelenen Adaylar",
                    $"Ertelenen aday {deferredIndex:N0}/{deferredRawCandidates.Count:N0} doğrulandı • bulunan {results.Count:N0}.",
                    total,
                    total,
                    results.Count,
                    new[] { recovered }));
            }
        }

        bool rawStageCompleted = offset >= total || (!ultraVideoScan && rawResultCount >= MaxRawResults);
        bool fragmentStageCompleted = !advancedVideoReconstruction || resumeCheckpoint?.FragmentStageCompleted == true;

        if (advancedVideoReconstruction && !fragmentStageCompleted && !cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new OperationProgress(
                responsivePortableScan
                    ? 98.8d
                    : total > 0 ? Math.Min(99.8, Math.Max(0, offset * 100d / total)) : 99.8,
                ultraVideoScan
                    ? "Ultra Video • Pass 3/4 • Fragment Reconstruction"
                    : "Derin Tarama • Pass 3/4 • Fragment Reconstruction",
                $"Pass 3/4: PTS/DTS, TS continuity, MP4 fragment, GOP/NAL ve klasik mdat zaman cizgileriyle parcalanmis video zincirleri yeniden kuruluyor. Profil: {mediaProfile.DiagnosticText}.",
                Math.Min(offset, total),
                total,
                results.Count,
                null,
                CreateCheckpoint(
                    3,
                    "fragment-reconstruction",
                    Math.Min(offset, total),
                    total,
                    metadataStageCompleted: true,
                    rawScanCompleted: rawStageCompleted,
                    fragmentStageCompleted: false,
                    finalValidationStarted: false,
                    reader,
                    adaptiveScan,
                    orphanFragmentNodes,
                    lastCheckpointedOrphanCount)));
            lastCheckpointedOrphanCount = orphanFragmentNodes.Count;

            List<RecoveryFileItem> fragmentSources = results.Concat(orphanFragmentNodes).ToList();
            IReadOnlyList<RecoveryFileItem> reconstructed =
                FragmentedVideoReconstructionService.BuildCandidates(
                    reader,
                    fragmentSources,
                    cancellationToken);

            if (reconstructed.Count > 0)
            {
                foreach (RecoveryFileItem item in reconstructed)
                    RecoveryConfidenceService.ApplyDerivedConfidence(item);
                results.AddRange(reconstructed);
                rebuiltVideoCount += reconstructed.Count;
            }

            IReadOnlyList<RecoveryFileItem> classicMdat =
                ClassicMp4MdatFragmentStitchingService.BuildCandidates(
                    reader,
                    fragmentSources,
                    cancellationToken);

            if (classicMdat.Count > 0)
            {
                foreach (RecoveryFileItem item in classicMdat)
                    RecoveryConfidenceService.ApplyDerivedConfidence(item);
                results.AddRange(classicMdat);
                rebuiltVideoCount += classicMdat.Count;
            }

            fragmentStageCompleted = true;
        }

        // Apply raw $I30/INDX history collected during the same sequential Deep Scan pass.
        // Once metadata items receive richer original paths, rebuild the extent correlation so
        // already-carved RAW files inherit those paths without another disk read.
        if (rawDirectoryIndexRecovery.EvidenceCount > 0)
        {
            foreach (string folderPath in NtfsHistoricalPathCatalogService.BuildFromIndexEvidence(rawDirectoryIndexRecovery.Evidence))
            {
                if (!string.IsNullOrWhiteSpace(folderPath))
                    historicalFolderPaths.Add(folderPath);
            }

            int indexPathApplied = rawDirectoryIndexRecovery.ApplyIndexOnlyPaths(results);
            if (indexPathApplied > 0)
            {
                pathCorrelation = NtfsPathCorrelationService.Build(results);
                pathCorrelatedCount += pathCorrelation.ApplyTo(results);
                progress?.Report(new OperationProgress(
                    99.75d,
                    "NTFS • Tarihsel Dizin Yolları ($I30)",
                    $"Deep Scan sırasında ek I/O olmadan $I30 kanıtı {rawDirectoryIndexRecovery.EvidenceCount:N0} • yol geliştirilen {indexPathApplied:N0} • RAW yol eşleşmesi {pathCorrelatedCount:N0}." ,
                    Math.Min(offset, total),
                    total,
                    results.Count));
            }
        }

        // A final zero-I/O portable correlation pass catches RAW candidates created after
        // the first metadata stage. Only proven FAT/exFAT physical extents are used.
        recoveredPathCorrelation = RecoveredPathCorrelationService.Build(
            results.Concat(deepHistoricalPathEvidence));
        recoveredPathCorrelatedCount += recoveredPathCorrelation.ApplyTo(results);

        BadSectorRetryResult badSectorRetry = reader.HeatMap.RangeCount > 0
            ? reader.RetryBadSectors(cancellationToken, 4096)
            : new BadSectorRetryResult(0, 0, 0);

        progress?.Report(new OperationProgress(
            99.9,
            "Derin Tarama • Pass 4/4 • Safe Retry + Son Dogrulama",
            $"Pass 4/4: bad-sector heat map tek kontrollu retry ile yeniden okunuyor; ardindan yinelenen adaylar birlestirilip final liste hazirlaniyor. " +
            $"Retry {badSectorRetry.AttemptedSectors:N0} sektor • geri okunan {badSectorRetry.RecoveredSectors:N0} sektor / {RecoveryFileItem.FormatBytes(badSectorRetry.RecoveredBytes)} • " +
            $"{reader.HeatMap.DiagnosticText} • {adaptiveScan.DiagnosticText}.",
            Math.Min(offset, total),
            total,
            results.Count,
            null,
            CreateCheckpoint(
                4,
                "safe-retry-final-validation",
                Math.Min(offset, total),
                total,
                metadataStageCompleted: true,
                rawScanCompleted: rawStageCompleted,
                fragmentStageCompleted: fragmentStageCompleted,
                finalValidationStarted: true,
                reader,
                adaptiveScan,
                resetPendingFragmentNodes: fragmentStageCompleted)));

        Report(
            progress,
            Math.Min(offset, total),
            total,
            results,
            rebuiltVideoCount,
            null,
            ref reportedResultCount,
            CreateCheckpoint(
                4,
                "safe-retry-final-validation",
                Math.Min(offset, total),
                total,
                metadataStageCompleted: true,
                rawScanCompleted: rawStageCompleted,
                fragmentStageCompleted: fragmentStageCompleted,
                finalValidationStarted: true,
                reader,
                adaptiveScan,
                resetPendingFragmentNodes: fragmentStageCompleted),
            percentOverride: responsivePortableScan ? 99.9d : null,
            titleOverride: ultraVideoScan
                ? "Ultra Video • Pass 4/4 • Son Doğrulama"
                : "Derin Tarama • Pass 4/4 • Son Doğrulama",
            stagedPortableProgress: responsivePortableScan);

        foreach (RecoveryFileItem item in results)
        {
            if (item.RecoveryConfidenceScore < 0)
            {
                if (item.SourceKind == RecoverySourceKind.RawContiguous)
                    RecoveryConfidenceService.ApplyDerivedConfidence(item);
                else
                    RecoveryConfidenceService.ApplyMetadataConfidence(item);
            }
        }

        IEnumerable<RecoveryFileItem> orderedResults = results
            .Where(item => MatchesScope(item, scope))
            .GroupBy(BuildDedupeKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(f => f.RecoveryConfidenceScore)
                .ThenByDescending(f => RecoveryStateScore(f.RecoveryState))
                .First());

        orderedResults = scope == DeepScanTarget.Video
            ? orderedResults
                .OrderByDescending(f => RecoveryScanPriorityService.GetPriorityTime(f) ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => f.RecoveryConfidenceScore)
                .ThenBy(f => FileTypeHelper.GetVideoPriority(f.Extension))
                .ThenByDescending(f => RecoveryStateScore(f.RecoveryState))
                .ThenBy(f => f.SourceKind == RecoverySourceKind.RawContiguous ? 1 : 0)
                .ThenByDescending(f => f.SizeBytes)
            : orderedResults
                .OrderByDescending(f => RecoveryScanPriorityService.GetPriorityTime(f) ?? DateTimeOffset.MinValue)
                .ThenByDescending(f => f.RecoveryConfidenceScore)
                .ThenByDescending(f => RecoveryStateScore(f.RecoveryState))
                .ThenBy(f => f.SourceKind == RecoverySourceKind.RawContiguous ? 1 : 0);

        List<RecoveryFileItem> finalResults = ultraVideoScan
            ? orderedResults.ToList()
            : orderedResults.Take(MaxResults).ToList();

        int videoCount = finalResults.Count(f => f.Category == "Video");
        int resolvedOriginalPathCount = finalResults.Count(f => f.HasRecoveredOriginalPath);
        int unresolvedOriginalPathCount = Math.Max(0, finalResults.Count - resolvedOriginalPathCount);
        string scopeText = scope switch
        {
            DeepScanTarget.Photo => "Resim taraması",
            DeepScanTarget.Video => "Ultra video taraması",
            DeepScanTarget.Document => "Dosya taraması",
            DeepScanTarget.PhotoVideo => "Resim + video taraması",
            DeepScanTarget.PhotoDocument => "Resim + dosya taraması",
            DeepScanTarget.VideoDocument => "Video + dosya taraması",
            _ => "Derin tarama"
        };
        string unreadableText = unreadableBytes > 0
            ? $" • Okunamayan sektör alanı: {RecoveryFileItem.FormatBytes(unreadableBytes)}"
            : string.Empty;

        string badSectorSummary = reader.HeatMap.RangeCount > 0 || badSectorRetry.AttemptedSectors > 0
            ? $" • {reader.HeatMap.DiagnosticText} • Safe Retry {badSectorRetry.RecoveredSectors:N0}/{badSectorRetry.AttemptedSectors:N0} sektor"
            : string.Empty;

        List<RecoveryFileItem> scoredFiles = finalResults
            .Where(item => item.RecoveryConfidenceScore >= 0)
            .ToList();
        int highConfidenceFiles = scoredFiles.Count(item => item.RecoveryConfidenceScore >= 80);
        double averageConfidence = scoredFiles.Count > 0
            ? scoredFiles.Average(item => item.RecoveryConfidenceScore)
            : 0d;
        string qualityText = scoredFiles.Count > 0
            ? $" • yüksek güven {highConfidenceFiles:N0}/{scoredFiles.Count:N0} • ort güven %{averageConfidence:0}"
            : string.Empty;
        string trimZeroText = mediaProfile.TrimAware && trimZeroFilledBytes > 0
            ? $" • sıfır/TRIM-benzeri alan {RecoveryFileItem.FormatBytes(trimZeroFilledBytes)}"
            : string.Empty;
        string trimNoVideoWarning = mediaProfile.TrimAware && mediaProfile.TrimEnabled == true &&
                                    scope.Includes(DeepScanTarget.Video) && videoCount == 0 && trimZeroFilledBytes > 0
            ? " • NVMe/SSD TRIM aktif ve sıfırlanmış alan görüldü; fiziksel payload denetleyici tarafından geri döndürülemiyor olabilir"
            : string.Empty;

        string summary = !ultraVideoScan && finalResults.Count >= MaxResults
            ? $"Sonuç kapasitesine ulaşıldı • toplam {finalResults.Count:N0} • video {videoCount:N0} • yeniden oluşturulan video {rebuiltVideoCount:N0}{qualityText}{unreadableText}{trimZeroText}{trimNoVideoWarning} • false-positive elenen {falsePositiveRejected:N0}{badSectorSummary} • özgün yol {resolvedOriginalPathCount:N0} • yolu doğrulanamayan {unresolvedOriginalPathCount:N0} • tarihsel metadata +{deepHistoricalPathMetadataCount:N0} • RAW yol eşleşmesi {recoveredPathCorrelatedCount:N0} • {adaptiveScan.DiagnosticText} • profil {mediaProfile.DiagnosticText}."
            : $"{scopeText} 4-pass pipeline tamamlandı • candidate {finalResults.Count:N0} • video {videoCount:N0} • reconstruction/fragment {rebuiltVideoCount:N0} • metadata {metadataCount:N0}{qualityText}{unreadableText}{trimZeroText}{trimNoVideoWarning} • false-positive elenen {falsePositiveRejected:N0}{badSectorSummary} • özgün yol {resolvedOriginalPathCount:N0} • yolu doğrulanamayan {unresolvedOriginalPathCount:N0} • tarihsel metadata +{deepHistoricalPathMetadataCount:N0} • RAW yol eşleşmesi {recoveredPathCorrelatedCount:N0} • {adaptiveScan.DiagnosticText} • profil {mediaProfile.DiagnosticText}.";

        return new ScanReport(finalResults, summary, ScanMode.Deep)
        {
            HistoricalFolders = historicalFolderPaths
                .OrderBy(path => path.Count(ch => ch == '\\'))
                .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            Diagnostics = new RecoveryScanDiagnostics
            {
                MediaProfileKey = mediaProfile.Key,
                MediaProfileName = mediaProfile.DisplayName,
                ReadSamples = adaptiveScan.ReadSamples,
                ReadBytes = adaptiveScan.ReadBytes,
                ActiveReadMilliseconds = adaptiveScan.ActiveReadMilliseconds,
                AverageReadBytesPerSecond = adaptiveScan.AverageReadBytesPerSecond,
                PeakReadBytesPerSecond = adaptiveScan.PeakReadBytesPerSecond,
                SlowReadSamples = adaptiveScan.SlowReadSamples,
                FastReadSamples = adaptiveScan.FastReadSamples,
                InitialBlockSize = adaptiveScan.BaseBlockSize,
                FinalBlockSize = adaptiveScan.CurrentBlockSize,
                BlockSizeChanges = adaptiveScan.BlockSizeChanges,
                UnreadableBytes = unreadableBytes,
                FalsePositiveRejected = falsePositiveRejected,
                HighConfidenceFiles = highConfidenceFiles,
                ScoredFiles = scoredFiles.Count,
                AverageConfidenceScore = averageConfidence
            }
        };
    }

    internal static int FindNextQuickCandidate(
        byte[] buffer,
        long blockOffset,
        int start,
        int length,
        int alignment) =>
        FindNextSectorAlignedCandidate(buffer, blockOffset, start, length, alignment);

    internal static SignatureKind? TryMatchQuickAt(byte[] buffer, int index, int length) =>
        TryMatchAt(buffer, index, length);

    internal static bool QuickSignatureMayMatchScope(SignatureKind kind, DeepScanTarget scope) =>
        SignatureMayMatchScope(kind, scope);

    internal static bool QuickRequiresFileBoundaryAlignment(SignatureKind kind) =>
        RequiresFileBoundaryAlignment(kind);

    internal static int FindNextPortableVideoAnchorForRegression(
        byte[] buffer,
        int start,
        int length) =>
        FindNextPortableVideoAnchorLead(buffer, start, length);

    internal static bool QuickValidateWholeFileCandidateHeader(
        RawDeviceReader reader,
        long start,
        long length,
        string extension) =>
        ValidateWholeFileCandidateHeader(reader, start, length, extension);

    private static int FindNextSectorAlignedCandidate(
        byte[] buffer,
        long blockOffset,
        int start,
        int length,
        int alignment)
    {
        if (start < 0 || start >= length || alignment <= 0)
            return -1;

        long absoluteStart = blockOffset + start;
        long remainder = absoluteStart % alignment;
        long alignedAbsolute = remainder == 0
            ? absoluteStart
            : absoluteStart + (alignment - remainder);
        long candidate = alignedAbsolute - blockOffset;

        while (candidate >= 0 && candidate < length)
        {
            int index = (int)candidate;
            if (CouldStartSupportedFile(buffer, index, length))
                return index;

            candidate += alignment;
        }

        return -1;
    }

    private static bool CouldStartSupportedFile(byte[] buffer, int index, int length)
    {
        byte value = buffer[index];
        bool directLead = value switch
        {
            0x00 or 0x06 or 0x1A or 0x1F or 0x2E or 0x30 or 0x36 or 0x37 or 0x47 or
            0x76 or 0x84 or 0x89 or 0xB7 or 0xD0 or 0xD9 or 0xFF => true,
            (byte)'%' or (byte)'8' or (byte)'B' or (byte)'D' or (byte)'F' or
            (byte)'G' or (byte)'I' or (byte)'K' or (byte)'M' or (byte)'N' or
            (byte)'O' or (byte)'P' or (byte)'R' or (byte)'S' => true,
            _ => false
        };

        if (directLead)
            return true;

        // M2TS starts with a four-byte arrival timestamp before the first 0x47 packet byte.
        // ISO-BMFF also stores a four-byte box size before ftyp/styp/moof/mdat/moov. Those
        // leading four bytes are data, not a stable magic byte, so do not pre-filter them out.
        if (index + 8 <= length)
        {
            if (buffer[index + 4] == 0x47)
                return true;

            return buffer[index + 4] switch
            {
                (byte)'f' when buffer[index + 5] == (byte)'t' &&
                               buffer[index + 6] == (byte)'y' && buffer[index + 7] == (byte)'p' => true,
                (byte)'s' when buffer[index + 5] == (byte)'t' &&
                               buffer[index + 6] == (byte)'y' && buffer[index + 7] == (byte)'p' => true,
                (byte)'m' when buffer[index + 5] == (byte)'o' &&
                               ((buffer[index + 6] == (byte)'o' &&
                                 (buffer[index + 7] == (byte)'f' || buffer[index + 7] == (byte)'v')) ||
                                (buffer[index + 6] == (byte)'a' && buffer[index + 7] == (byte)'t')) => true,
                _ => false
            };
        }

        return false;
    }

    private static int FindNextPortableVideoAnchorLead(byte[] buffer, int start, int length)
    {
        if (start < 0 || start >= length || length > buffer.Length)
            return -1;

        int cursor = start;
        while (cursor < length)
        {
            int relative = buffer.AsSpan(cursor, length - cursor).IndexOfAny(VideoAnchorLeadBytes);
            if (relative < 0)
                return -1;

            int found = cursor + relative;
            byte value = buffer[found];

            // ISO-BMFF size bytes are arbitrary. Locate the stable fourcc and return the
            // real box boundary. After the first return, boxStart < start suppresses the
            // same box from being rediscovered byte-by-byte.
            if ((value is (byte)'f' or (byte)'m' or (byte)'s') && found >= 4)
            {
                int boxStart = found - 4;
                if (boxStart >= start && LooksLikeIsoVideoBoxStart(buffer, boxStart, length))
                    return boxStart;

                cursor = found + 1;
                continue;
            }

            if (value == 0x47)
            {
                int m2tsStart = found - 4;
                if (m2tsStart >= start &&
                    IsTransportRunStart(buffer, m2tsStart, length, packetSize: 192, syncOffset: 4))
                    return m2tsStart;

                if (IsTransportRunStart(buffer, found, length, packetSize: 188, syncOffset: 0))
                    return found;

                cursor = found + 1;
                continue;
            }

            if (value == 0x00)
            {
                if (IsPlausibleZeroVideoAnchor(buffer, found, length))
                    return found;

                // A formatted/empty flash device can contain multi-megabyte zero runs. Skip
                // those runs in one step while retaining short 00 00 01 / 00 00 00 01 anchors.
                int zeroEnd = found + 1;
                while (zeroEnd < length && buffer[zeroEnd] == 0x00)
                    zeroEnd++;
                cursor = zeroEnd - found >= 16 ? zeroEnd : found + 1;
                continue;
            }

            // Exact container signatures only. Never call the full video matcher for an
            // ordinary lead byte: that matcher also contains heavyweight DV/global-container
            // validation. A cheap prefix gate reduces random USB payload to real signature
            // candidates first, then the existing matcher performs the authoritative check.
            if (CouldStartPortableExactVideoSignature(buffer, found, length) &&
                TryMatchVideoAt(buffer, found, length) is not null)
                return found;

            cursor = found + 1;
        }

        return -1;
    }

    private static bool IsPlausibleZeroVideoAnchor(byte[] buffer, int index, int length)
    {
        if (index < 0 || index + 4 > length)
            return false;

        if (buffer[index + 1] == 0x00 && buffer[index + 2] == 0x01 &&
            buffer[index + 3] is 0xBA or 0xB3)
            return true;

        if (TryGetAnnexBHeader(buffer, index, length, h265: false, out _, out int h264Type) &&
            h264Type is 5 or 7 or 8)
            return true;

        if (TryGetAnnexBHeader(buffer, index, length, h265: true, out _, out int h265Type) &&
            h265Type is 19 or 20 or 21 or 32 or 33 or 34)
            return true;

        return TryMatchLengthPrefixedNalPair(buffer, index, length, h265: false) ||
               TryMatchLengthPrefixedNalPair(buffer, index, length, h265: true);
    }

    internal static bool IsTransportCandidateRunStart(
        RawDeviceReader reader, byte[] buffer, int start, int length, long blockOffset, long resumeOffset)
    {
        int packetSize, syncOffset;
        if (HasSync(buffer, start, length, 188, 0, 5)) { packetSize = 188; syncOffset = 0; }
        else if (HasSync(buffer, start, length, 192, 4, 5)) { packetSize = 192; syncOffset = 4; }
        else return false;

        long absolute = blockOffset + start;
        if (absolute - packetSize < resumeOffset) return true;
        Span<byte> previous = stackalloc byte[192];
        if (start >= packetSize)
            buffer.AsSpan(start - packetSize, packetSize).CopyTo(previous);
        else if (!reader.ReadExact(absolute - packetSize, previous[..packetSize]))
            return true;
        // Require a plausible transport header, not just an incidental 0x47 payload byte.
        return previous[syncOffset] != 0x47 || (previous[syncOffset + 1] & 0x80) != 0 ||
               (previous[syncOffset + 3] & 0x30) == 0;
    }

    private static bool IsTransportPayloadSignature(SignatureKind kind) => kind is
        SignatureKind.MpegTs or SignatureKind.H264AnnexB or SignatureKind.H265AnnexB or
        SignatureKind.H264LengthPrefixed or SignatureKind.H265LengthPrefixed;

    private static int SkipVerifiedTransportPayload(List<(long Start, long End)> ranges, long offset, int start, int length)
    {
        long absolute = offset + start;
        foreach (var range in ranges)
            if (absolute >= range.Start && absolute < range.End)
                return (int)Math.Min(length, range.End - offset);
        return start;
    }

    private static bool IsTransportRunStart(
        byte[] buffer,
        int start,
        int length,
        int packetSize,
        int syncOffset)
    {
        if (start < 0 || !HasSync(buffer, start, length, packetSize, syncOffset, 5))
            return false;

        int previousStart = start - packetSize;
        return previousStart < 0 ||
               previousStart + syncOffset >= length ||
               buffer[previousStart + syncOffset] != 0x47;
    }

    private static bool CouldStartPortableExactVideoSignature(
        byte[] buffer,
        int index,
        int length)
    {
        int remaining = length - index;
        if (index < 0 || remaining <= 0)
            return false;

        byte first = buffer[index];
        return first switch
        {
            0x06 => remaining >= 4 &&
                    buffer[index + 1] == 0x0E &&
                    buffer[index + 2] == 0x2B &&
                    buffer[index + 3] == 0x34,
            0x1A => remaining >= 4 &&
                    buffer[index + 1] == 0x45 &&
                    buffer[index + 2] == 0xDF &&
                    buffer[index + 3] == 0xA3,
            0x1F => (remaining >= 4 &&
                     buffer[index + 1] == 0x43 &&
                     buffer[index + 2] == 0xB6 &&
                     buffer[index + 3] == 0x75) ||
                    (remaining >= 6 * 80 &&
                     buffer[index + 80] == 0x3F &&
                     buffer[index + 160] == 0x3F &&
                     buffer[index + 240] == 0x56 &&
                     buffer[index + 320] == 0x56 &&
                     buffer[index + 400] == 0x56),
            0x2E => remaining >= 4 &&
                    buffer[index + 1] == (byte)'R' &&
                    buffer[index + 2] == (byte)'M' &&
                    buffer[index + 3] == (byte)'F',
            0x30 or 0x36 => remaining >= 4 &&
                           buffer[index + 1] == 0x26 &&
                           buffer[index + 2] == 0xB2 &&
                           buffer[index + 3] == 0x75,
            0x84 => remaining >= 6 &&
                    buffer[index + 1] == 0x10 &&
                    buffer[index + 2] == 0xFF &&
                    buffer[index + 3] == 0xFF &&
                    buffer[index + 4] == 0xFF &&
                    buffer[index + 5] == 0xFF,
            0xB7 => remaining >= 4 &&
                    buffer[index + 1] == 0xD8 &&
                    buffer[index + 2] == 0x00 &&
                    buffer[index + 3] == 0x20,
            (byte)'B' => remaining >= 3 &&
                         buffer[index + 1] == (byte)'I' &&
                         buffer[index + 2] == (byte)'K',
            (byte)'D' => remaining >= 12 &&
                         buffer[index + 1] == (byte)'K' &&
                         buffer[index + 2] == (byte)'I' &&
                         buffer[index + 3] == (byte)'F' &&
                         buffer[index + 8] == (byte)'A' &&
                         buffer[index + 9] == (byte)'V' &&
                         buffer[index + 10] == (byte)'0' &&
                         buffer[index + 11] == (byte)'1',
            (byte)'F' => remaining >= 5 &&
                         buffer[index + 1] == (byte)'L' &&
                         buffer[index + 2] == (byte)'V' &&
                         buffer[index + 3] == 0x01 &&
                         (buffer[index + 4] & 0x01) != 0,
            (byte)'K' => remaining >= 3 &&
                         buffer[index + 1] == (byte)'B' &&
                         buffer[index + 2] == (byte)'2',
            (byte)'N' => remaining >= 4 &&
                         buffer[index + 1] == (byte)'S' &&
                         buffer[index + 2] == (byte)'V' &&
                         buffer[index + 3] == (byte)'f',
            (byte)'O' => remaining >= 5 &&
                         buffer[index + 1] == (byte)'g' &&
                         buffer[index + 2] == (byte)'g' &&
                         buffer[index + 3] == (byte)'S' &&
                         buffer[index + 4] == 0x00,
            (byte)'R' => remaining >= 12 &&
                         buffer[index + 1] == (byte)'I' &&
                         buffer[index + 2] == (byte)'F' &&
                         buffer[index + 3] == (byte)'F' &&
                         buffer[index + 8] == (byte)'A' &&
                         buffer[index + 9] == (byte)'V' &&
                         buffer[index + 10] == (byte)'I' &&
                         buffer[index + 11] == (byte)' ',
            (byte)'S' => remaining >= 4 &&
                         buffer[index + 1] == (byte)'M' &&
                         buffer[index + 2] == (byte)'K' &&
                         buffer[index + 3] is 0x32 or 0x34,
            _ => false
        };
    }

    private static int FindNextVideoAnchorLead(byte[] buffer, int start, int length)
    {
        if (start < 0 || start >= length)
            return -1;

        int cursor = start;
        while (cursor < length)
        {
            int relative = buffer.AsSpan(cursor, length - cursor).IndexOfAny(VideoAnchorLeadBytes);
            if (relative < 0)
                return -1;

            int found = cursor + relative;
            byte value = buffer[found];

            // NVMe TRIM commonly returns long zero-filled ranges. Never call the expensive
            // video parsers once per zero byte; skip the run while still preserving genuine
            // 00 00 01 / 00 00 00 01 NAL and ISO-BMFF size prefixes (short zero runs).
            if (value == 0x00)
            {
                int zeroEnd = found + 1;
                while (zeroEnd < length && buffer[zeroEnd] == 0x00)
                    zeroEnd++;
                if (zeroEnd - found >= 16)
                {
                    cursor = zeroEnd;
                    continue;
                }
            }

            // ISO-BMFF box size bytes are arbitrary. Searching only the size prefix can miss
            // valid ftyp/mdat/moov/moof/styp anchors, especially large files. When the stable
            // fourcc is found, step back four bytes to the actual box boundary.
            if ((value is (byte)'f' or (byte)'m' or (byte)'s') && found >= 4)
            {
                int boxStart = found - 4;
                if (boxStart >= start && LooksLikeIsoVideoBoxStart(buffer, boxStart, length))
                    return boxStart;

                cursor = found + 1;
                continue;
            }

            // A 192-byte M2TS packet has a four-byte arrival timestamp before 0x47.
            // Return the packet prefix only when it does not move behind the requested cursor.
            if (value == 0x47 && found >= start + 4)
                return found - 4;

            return found;
        }

        return -1;
    }

    private static bool LooksLikeIsoVideoBoxStart(byte[] buffer, int index, int length)
    {
        if (index < 0 || index + 8 > length)
            return false;

        bool knownType =
            MatchesBoxType(buffer, index, (byte)'f', (byte)'t', (byte)'y', (byte)'p') ||
            MatchesBoxType(buffer, index, (byte)'s', (byte)'t', (byte)'y', (byte)'p') ||
            MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'f') ||
            MatchesBoxType(buffer, index, (byte)'m', (byte)'d', (byte)'a', (byte)'t') ||
            MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'v');
        if (!knownType)
            return false;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index, 4));
        return size is 0 or 1 || size >= 8;
    }

    internal static bool LooksLikeZipLocalHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 ||
            data[0] != (byte)'P' || data[1] != (byte)'K' || data[2] != 0x03 || data[3] != 0x04)
            return false;

        ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        ushort method = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(26, 2));
        ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(28, 2));

        if (versionNeeded is < 10 or > 63 || fileNameLength is 0 or > 4096 || extraLength > 65535)
            return false;

        // Reject encryption modes the current raw reconstructor cannot verify and reserve-bit noise.
        if ((flags & 0xC000) != 0)
            return false;

        if (method is not (0 or 1 or 6 or 8 or 9 or 12 or 14 or 93 or 95 or 98 or 99))
            return false;

        int headerLength = 30 + fileNameLength + extraLength;
        if (headerLength > data.Length)
            return false;

        ReadOnlySpan<byte> name = data.Slice(30, fileNameLength);
        bool hasVisibleNameByte = false;
        foreach (byte value in name)
        {
            if (value == 0 || value < 0x20)
                return false;
            if (value != (byte)'/' && value != (byte)'\\')
                hasVisibleNameByte = true;
        }

        return hasVisibleNameByte;
    }

    private static bool IsLikelyZeroFilledBlock(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4096)
            return false;

        int step = Math.Max(1, data.Length / 128);
        for (int i = 0; i < data.Length; i += step)
        {
            if (data[i] != 0)
                return false;
        }

        int tailStart = Math.Max(0, data.Length - 4096);
        for (int i = 0; i < Math.Min(4096, data.Length); i += 64)
        {
            if (data[i] != 0 || data[tailStart + i] != 0)
                return false;
        }

        return true;
    }

    private static RecoveryFileItem? CreateOrphanFragmentNode(
        RawDeviceReader reader,
        SignatureKind kind,
        long offset,
        long volumeLength,
        int sequence,
        CancellationToken cancellationToken)
    {
        string? extension = kind switch
        {
            SignatureKind.MpegTs => "TS",
            SignatureKind.IsoBmff or SignatureKind.IsoBmffFragment => "MP4",
            SignatureKind.MpegProgramStream or SignatureKind.MpegVideoStream => "MPG",
            SignatureKind.H264AnnexB or SignatureKind.H264LengthPrefixed => "H264",
            SignatureKind.H265AnnexB or SignatureKind.H265LengthPrefixed => "H265",
            SignatureKind.Matroska or SignatureKind.MatroskaCluster => "MKV",
            _ => null
        };
        if (extension is null || offset < 0 || offset >= volumeLength)
            return null;

        VideoFragmentBoundaryResult? boundary;
        try
        {
            boundary = VideoFragmentBoundaryService.Detect(
                reader,
                kind,
                offset,
                volumeLength,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or OverflowException or InvalidDataException)
        {
            boundary = null;
        }

        if (boundary is null || boundary.StartOffset != offset || boundary.Length < 4096)
            return null;

        long available = volumeLength - offset;
        long length = Math.Min(boundary.Length, available);
        if (length < 4096)
            return null;

        return new RecoveryFileItem
        {
            FileName = $"Graph_Dugumu_{sequence:000000}_{offset:X}.{extension.ToLowerInvariant()}",
            Extension = extension,
            SizeBytes = length,
            RecoveryState = "Video Parçası",
            TypeGlyph = FileTypeHelper.GetGlyph(extension),
            SourceText = "Sektör anchor graph düğümü",
            SourceKind = RecoverySourceKind.RawContiguous,
            SourceOffset = offset,
            TransformKind = kind switch
            {
                SignatureKind.H264LengthPrefixed => RecoveryTransformKind.LengthPrefixedH264ToAnnexB,
                SignatureKind.H265LengthPrefixed => RecoveryTransformKind.LengthPrefixedH265ToAnnexB,
                _ => RecoveryTransformKind.None
            }
        };
    }

    private static IEnumerable<RecoveryFileItem> OrderMetadataCandidates(
        IEnumerable<RecoveryFileItem> candidates,
        DeepScanTarget scope,
        RecoveryMediaProfile mediaProfile)
    {
        IReadOnlyList<RecoveryFileItem> filtered = candidates
            .Where(candidate => MatchesScope(candidate, scope))
            .ToList();
        CameraSequenceIntelligenceService.Enrich(filtered, mediaProfile.CameraOptimized);
        if (!mediaProfile.CameraOptimized || !scope.Includes(DeepScanTarget.Video))
            return RecoveryScanPriorityService.OrderNewestFirst(filtered);

        // Camera/AVCHD media can contain tens of thousands of stills. If the metadata cap
        // is reached, do not let valid MTS/M2TS/MP4/MOV entries be crowded out by photos.
        return filtered
            .OrderBy(candidate => FileTypeHelper.IsVideo(candidate.Extension) ? 0 : 1)
            .ThenBy(candidate => FileTypeHelper.IsVideo(candidate.Extension)
                ? FileTypeHelper.GetVideoPriority(candidate.Extension)
                : int.MaxValue)
            .ThenByDescending(candidate => RecoveryScanPriorityService.GetPriorityTime(candidate) ?? DateTimeOffset.MinValue);
    }

    private static bool MatchesScope(RecoveryFileItem item, DeepScanTarget scope) =>
        MatchesScope(item.Extension, scope);

    private static bool MatchesScope(string extension, DeepScanTarget scope)
    {
        if (scope == DeepScanTarget.All)
            return true;

        if (scope == DeepScanTarget.Video)
            return FileTypeHelper.IsVideo(extension);

        if (scope == DeepScanTarget.Photo)
            return FileTypeHelper.IsPhoto(extension);

        string category = FileTypeHelper.GetCategory(extension);
        return (category == "Fotoğraf" && scope.Includes(DeepScanTarget.Photo)) ||
               (category == "Video" && scope.Includes(DeepScanTarget.Video)) ||
               (category == "Belge" && scope.Includes(DeepScanTarget.Document));
    }

    private static bool SignatureMayMatchScope(SignatureKind kind, DeepScanTarget scope)
    {
        if (scope == DeepScanTarget.All)
            return true;

        bool photo = scope.Includes(DeepScanTarget.Photo) && kind is
            SignatureKind.Jpeg or
            SignatureKind.Png or
            SignatureKind.Gif or
            SignatureKind.Bmp or
            SignatureKind.TiffLittleEndian or
            SignatureKind.TiffBigEndian or
            SignatureKind.FujiRaf or
            SignatureKind.SigmaX3f or
            SignatureKind.WebP or
            SignatureKind.Ico or
            SignatureKind.Jpeg2000 or
            SignatureKind.Jpeg2000Codestream or
            SignatureKind.Psd or
            SignatureKind.Dds or
            SignatureKind.Exr or
            SignatureKind.Riff or
            SignatureKind.IsoBmff;

        bool video = scope.Includes(DeepScanTarget.Video) && kind is
            SignatureKind.Riff or
            SignatureKind.Asf or
            SignatureKind.AsfDataObject or
            SignatureKind.IsoBmff or
            SignatureKind.IsoBmffFragment or
            SignatureKind.MpegTs or
            SignatureKind.MpegProgramStream or
            SignatureKind.MpegVideoStream or
            SignatureKind.H264LengthPrefixed or
            SignatureKind.H265LengthPrefixed or
            SignatureKind.H264AnnexB or
            SignatureKind.H265AnnexB or
            SignatureKind.Matroska or
            SignatureKind.MatroskaCluster or
            SignatureKind.Flv or
            SignatureKind.OggVideo or
            SignatureKind.RealMedia or
            SignatureKind.Mxf or
            SignatureKind.MxfEssence or
            SignatureKind.Dv or
            SignatureKind.Wtv or
            SignatureKind.Nsv or
            SignatureKind.Roq or
            SignatureKind.Bink or
            SignatureKind.Smacker or
            SignatureKind.Av1Ivf;

        bool document = scope.Includes(DeepScanTarget.Document) && kind is
            SignatureKind.Pdf or
            SignatureKind.Zip or
            SignatureKind.Mp3 or
            SignatureKind.AacAdts or
            SignatureKind.Aiff or
            SignatureKind.Au or
            SignatureKind.Midi or
            SignatureKind.SevenZip or
            SignatureKind.Cab or
            SignatureKind.PortableExecutable or
            SignatureKind.SqliteDatabase or
            SignatureKind.SqliteWal or
            SignatureKind.SqliteJournal or
            SignatureKind.Rar or
            SignatureKind.OleCompound;

        return photo || video || document;
    }

    private static void RegisterMetadataSourceStart(HashSet<long> exactStarts, RecoveryFileItem item)
    {
        if (item.SourceKind == RecoverySourceKind.Extents && item.SourceExtents is { Count: > 0 })
        {
            if (item.SourceExtents[0].Offset >= 0)
                exactStarts.Add(item.SourceExtents[0].Offset);
            return;
        }

        if (item.SourceOffset >= 0 && item.SourceKind is
            RecoverySourceKind.FatContiguous or
            RecoverySourceKind.ExFatContiguous or
            RecoverySourceKind.NtfsRunList)
        {
            exactStarts.Add(item.SourceOffset);
        }
    }

    private static bool MergeRecoveredPathEvidence(RecoveryFileItem target, RecoveryFileItem source)
    {
        string? candidate = source.RecoveredOriginalPath;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        // PRO forensic rule: a path obtained while walking the reachable filesystem catalog is
        // already direct parent/child evidence. A later stale/orphan pass may fill a missing path,
        // but it must never replace an existing path merely because the stale string is deeper or
        // longer. That old behavior could make a freshly deleted file appear under a plausible
        // but wrong historical folder when clusters had been reused.
        if (!string.IsNullOrWhiteSpace(target.RecoveredOriginalPath))
            return false;

        target.RecoveredOriginalPath = candidate;
        if (!target.SourceText.Contains("özgün yol", StringComparison.OrdinalIgnoreCase))
            target.SourceText = $"{target.SourceText} • özgün yol metadata";
        return true;
    }

    private static string BuildDedupeKey(RecoveryFileItem item)
    {
        string extension = FileTypeHelper.Normalize(item.Extension);

        if (item.SourceKind == RecoverySourceKind.Extents && item.SourceExtents is { Count: > 0 } extents)
        {
            SourceExtent first = extents[0];
            SourceExtent last = extents[^1];
            return $"EXTENTS|{first.Offset}|{last.Offset}|{extents.Count}|{item.SizeBytes}|{extension}";
        }

        if (item.SourceOffset > 0 &&
            item.SourceKind is RecoverySourceKind.RawContiguous or
                RecoverySourceKind.FatContiguous or
                RecoverySourceKind.ExFatContiguous or
                RecoverySourceKind.NtfsRunList)
            return $"DATA|{item.SourceOffset}|{extension}";

        return $"{item.SourceKind}|{item.SourceText}|{item.FileName}|{item.SizeBytes}|{extension}";
    }

    private static int RecoveryStateScore(string state) => state switch
    {
        "Çok İyi" => 5,
        "İyi" => 4,
        "Parçalı Video" => 4,
        "Yeniden İnşa" => 3,
        "Video Akışı" => 3,
        "Kısmi" => 2,
        "Video Parçası" => 1,
        "Zayıf" => 0,
        _ => 0
    };

    private static bool IsCurrentBlockHeaderValid(
        byte[] buffer,
        int index,
        int bytesRead,
        string extension,
        long length,
        RawFileAnalysis analysis)
    {
        if (analysis.PrependStandardMp4Header ||
            analysis.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı")
            return true;

        if (index < 0 || index >= bytesRead || length <= 0)
            return false;

        int inspectLength = Math.Min(4096, bytesRead - index);
        if (inspectLength <= 0)
            return false;

        return FileHeaderValidator.LooksLike(
            extension,
            buffer.AsSpan(index, inspectLength),
            length);
    }

    private static RecoveryConfidenceResult BuildUnifiedFastConfidence(
        RawFileAnalysis analysis,
        bool headerValid,
        bool fromCurrentBlock)
    {
        bool reconstruction = analysis.PrependStandardMp4Header ||
                              analysis.AppendData is { Length: > 0 } ||
                              analysis.RecoveryState is "Yeniden İnşa" or "Video Parçası" or "Video Akışı";
        if (!headerValid && !reconstruction)
            return new RecoveryConfidenceResult(20, "Zayif", "Hızlı imza kontrolü başarısız", true);

        int score = analysis.RecoveryState switch
        {
            "Çok İyi" => fromCurrentBlock ? 96 : 92,
            "İyi" => fromCurrentBlock ? 90 : 86,
            "Yeniden İnşa" => 78,
            "Video Akışı" => 76,
            "Video Parçası" => 70,
            "Kısmi" => 68,
            _ => fromCurrentBlock ? 84 : 80
        };
        string grade = score switch
        {
            >= 90 => "Cok Yuksek",
            >= 78 => "Yuksek",
            >= 65 => "Iyi",
            >= 55 => "Orta",
            >= 40 => "Dusuk",
            _ => "Zayif"
        };
        string summary = fromCurrentBlock
            ? "Tek geçiş RAM doğrulaması • imza + dosya sonu/yapısı doğrulandı"
            : "Hızlı yapısal parser • imza + kaynak sınırı doğrulandı";
        return new RecoveryConfidenceResult(score, grade, summary, false);
    }

    private static RawFileAnalysis? AnalyzeRawCandidateWithBudget(
        RawDeviceReader reader,
        SignatureKind kind,
        long candidateStart,
        long total,
        bool responsivePortableScan,
        bool unifiedFastScan,
        CancellationToken cancellationToken,
        out bool deferredByBudget,
        bool preserveTransportBoundaries = false)
    {
        deferredByBudget = false;
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        TimeSpan budget = unifiedFastScan
            ? PortableDeepScanPolicy.GetUnifiedCandidateValidationBudget(kind)
            : preserveTransportBoundaries && kind == SignatureKind.MpegTs
                ? PortableDeepScanPolicy.RawLiveImageValidationBudget
                : PortableDeepScanPolicy.GetRawCandidateValidationBudget(kind);

        // Internal SSD/NVMe/HDD scans also stay discovery-first. Long image/archive/video
        // parsers are capped during the sequential sweep and are fully validated only after
        // the surface pass. This keeps first-result latency low without accepting unverified data.
        if (!responsivePortableScan && !unifiedFastScan)
        {
            TimeSpan minimumBudget = TimeSpan.FromMilliseconds(180);
            TimeSpan maximumBudget = preserveTransportBoundaries && kind == SignatureKind.MpegTs
                ? TimeSpan.FromMilliseconds(750)
                : TimeSpan.FromMilliseconds(450);
            budget = budget < minimumBudget
                ? minimumBudget
                : budget > maximumBudget ? maximumBudget : budget;
        }

        budgetCts.CancelAfter(budget);
        try
        {
            RawFileAnalysis? result = RawFileAnalyzer.Analyze(
                reader,
                kind,
                candidateStart,
                total,
                budgetCts.Token,
                preserveTransportBoundaries: preserveTransportBoundaries);

            if (!cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested && result is null)
                deferredByBudget = true;
            return result;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && budgetCts.IsCancellationRequested)
        {
            deferredByBudget = true;
            return null;
        }
    }

    private static SignatureKind? TryMatchVideoAt(byte[] buffer, int index, int length)
    {
        byte first = buffer[index];

        SignatureKind? global = TryMatchAdditionalGlobalVideoAt(buffer, index, length);
        if (global is not null)
            return global;

        // 1) MTS / M2TS / TS transport-stream ailesi. Kamera kayıtlarında ilk hedef.
        if (first == 0x47 && HasSync(buffer, index, length, 188, 0, 5))
            return SignatureKind.MpegTs;

        if (index + 4 < length &&
            buffer[index + 4] == 0x47 &&
            HasSync(buffer, index, length, 192, 4, 5))
            return SignatureKind.MpegTs;

        // 2) MP4/MOV ISO-BMFF ailesi. Gerçek uzantı major brand/video track ile belirlenir.
        if (index + 12 <= length)
        {
            if (MatchesBoxType(buffer, index, (byte)'f', (byte)'t', (byte)'y', (byte)'p'))
                return SignatureKind.IsoBmff;

            bool fragmentBox =
                MatchesBoxType(buffer, index, (byte)'s', (byte)'t', (byte)'y', (byte)'p') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'f') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'d', (byte)'a', (byte)'t') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'v');

            if (fragmentBox)
            {
                uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index, 4));
                if (boxSize == 0 || boxSize == 1 || boxSize >= 8)
                    return SignatureKind.IsoBmffFragment;
            }
        }

        // 3) MPG / MPEG. Program stream ve elementary video ayrı doğrulanır.
        if (first == 0x00 && index + 4 <= length &&
            buffer[index + 1] == 0x00 && buffer[index + 2] == 0x01)
        {
            if (buffer[index + 3] == 0xBA)
                return SignatureKind.MpegProgramStream;
            if (buffer[index + 3] == 0xB3)
                return SignatureKind.MpegVideoStream;
        }

        // 4) AVI. RIFF yalnız AVI ise video kabul edilir; WEBP bu yola hiç girmez.
        if (first == (byte)'R' && index + 12 <= length &&
            buffer[index + 1] == (byte)'I' &&
            buffer[index + 2] == (byte)'F' &&
            buffer[index + 3] == (byte)'F' &&
            buffer[index + 8] == (byte)'A' &&
            buffer[index + 9] == (byte)'V' &&
            buffer[index + 10] == (byte)'I' &&
            buffer[index + 11] == (byte)' ')
            return SignatureKind.Riff;

        // 5+) Diğer global video kapsayıcıları.
        if (first == 0x30 &&
            index + AsfHeaderSignature.Length <= length &&
            buffer.AsSpan(index, AsfHeaderSignature.Length).SequenceEqual(AsfHeaderSignature))
            return SignatureKind.Asf;

        if (first == 0x36 &&
            index + AsfDataObjectSignature.Length <= length &&
            buffer.AsSpan(index, AsfDataObjectSignature.Length).SequenceEqual(AsfDataObjectSignature))
            return SignatureKind.AsfDataObject;

        if (first == 0x1A && index + 4 <= length &&
            buffer[index + 1] == 0x45 && buffer[index + 2] == 0xDF && buffer[index + 3] == 0xA3)
            return SignatureKind.Matroska;

        if (first == 0x1F && index + 4 <= length &&
            buffer[index + 1] == 0x43 && buffer[index + 2] == 0xB6 && buffer[index + 3] == 0x75)
            return SignatureKind.MatroskaCluster;

        if (first == (byte)'F' && index + 9 <= length &&
            buffer[index + 1] == (byte)'L' && buffer[index + 2] == (byte)'V' &&
            buffer[index + 3] == 0x01 && (buffer[index + 4] & 0x01) != 0)
            return SignatureKind.Flv;

        if (first == (byte)'O' && index + 27 <= length &&
            buffer[index + 1] == (byte)'g' && buffer[index + 2] == (byte)'g' &&
            buffer[index + 3] == (byte)'S' && buffer[index + 4] == 0x00)
            return SignatureKind.OggVideo;

        if (first == (byte)'.' && index + 10 <= length &&
            buffer[index + 1] == (byte)'R' && buffer[index + 2] == (byte)'M' && buffer[index + 3] == (byte)'F')
            return SignatureKind.RealMedia;

        if (first == 0x00)
        {
            if (TryMatchAnnexBNalPair(buffer, index, length, h265: false))
                return SignatureKind.H264AnnexB;
            if (TryMatchAnnexBNalPair(buffer, index, length, h265: true))
                return SignatureKind.H265AnnexB;
            if (TryMatchLengthPrefixedNalPair(buffer, index, length, h265: false))
                return SignatureKind.H264LengthPrefixed;
            if (TryMatchLengthPrefixedNalPair(buffer, index, length, h265: true))
                return SignatureKind.H265LengthPrefixed;
        }

        return null;
    }

    private static SignatureKind? TryMatchAt(
        byte[] buffer,
        int index,
        int length,
        bool includeEmbeddedVideoSearch = true)
    {
        byte first = buffer[index];

        SignatureKind? global = TryMatchAdditionalGlobalVideoAt(buffer, index, length);
        if (global is not null)
            return global;

        if (first == 0xFF &&
            index + 3 <= length &&
            buffer[index + 1] == 0xD8 &&
            buffer[index + 2] == 0xFF)
            return SignatureKind.Jpeg;

        if (first == 0xFF && index + 4 <= length && (buffer[index + 1] & 0xE0) == 0xE0)
        {
            // Audio frame probing is deliberately gated by a real MPEG/ADTS sync prefix.
            // This keeps the unified all-type pass fast on photo/video-heavy media.
            ReadOnlySpan<byte> audioProbe = buffer.AsSpan(index, Math.Min(length - index, 32 * 1024));
            if (RawFileAnalyzer.LooksLikeMp3Candidate(audioProbe))
                return SignatureKind.Mp3;
            if (RawFileAnalyzer.LooksLikeAacAdtsCandidate(audioProbe))
                return SignatureKind.AacAdts;
        }

        if (first == 0x89 &&
            index + 8 <= length &&
            buffer[index + 1] == 0x50 &&
            buffer[index + 2] == 0x4E &&
            buffer[index + 3] == 0x47 &&
            buffer[index + 4] == 0x0D &&
            buffer[index + 5] == 0x0A &&
            buffer[index + 6] == 0x1A &&
            buffer[index + 7] == 0x0A)
            return SignatureKind.Png;

        if (first == (byte)'F' && index + 16 <= length &&
            buffer.AsSpan(index, 16).SequenceEqual("FUJIFILMCCD-RAW "u8))
            return SignatureKind.FujiRaf;

        if (first == (byte)'F' && index + 4 <= length &&
            buffer.AsSpan(index, 4).SequenceEqual("FOVb"u8))
            return SignatureKind.SigmaX3f;

        if (first == (byte)'F' && index + 12 <= length &&
            buffer.AsSpan(index, 4).SequenceEqual("FORM"u8) &&
            (buffer.AsSpan(index + 8, 4).SequenceEqual("AIFF"u8) ||
             buffer.AsSpan(index + 8, 4).SequenceEqual("AIFC"u8)))
            return SignatureKind.Aiff;

        if (first == (byte)'G' &&
            index + 6 <= length &&
            buffer[index + 1] == (byte)'I' &&
            buffer[index + 2] == (byte)'F' &&
            buffer[index + 3] == (byte)'8' &&
            (buffer[index + 4] == (byte)'7' || buffer[index + 4] == (byte)'9') &&
            buffer[index + 5] == (byte)'a')
            return SignatureKind.Gif;

        if (first == (byte)'B' &&
            index + 26 <= length &&
            buffer[index + 1] == (byte)'M' &&
            BmpStructureValidator.LooksLikePrefix(
                buffer.AsSpan(index, Math.Min(160, length - index))))
            return SignatureKind.Bmp;

        if (first == (byte)'I' &&
            index + 4 <= length &&
            buffer[index + 1] == (byte)'I' &&
            ((buffer[index + 2] == 0x2A && buffer[index + 3] == 0x00) ||
             (buffer[index + 2] == 0x2B && buffer[index + 3] == 0x00) ||
             (buffer[index + 2] == (byte)'R' && buffer[index + 3] is (byte)'O' or (byte)'S') ||
             (buffer[index + 2] == 0x55 && buffer[index + 3] == 0x00)))
            return SignatureKind.TiffLittleEndian;

        if (first == (byte)'I' && index + 10 <= length &&
            buffer[index + 1] == (byte)'D' && buffer[index + 2] == (byte)'3' &&
            RawFileAnalyzer.LooksLikeMp3Candidate(buffer.AsSpan(index, Math.Min(length - index, 32 * 1024))))
            return SignatureKind.Mp3;

        if (first == (byte)'M' && index + 14 <= length &&
            buffer.AsSpan(index, 4).SequenceEqual("MThd"u8))
            return SignatureKind.Midi;

        if (first == (byte)'M' && index + 36 <= length &&
            buffer.AsSpan(index, 4).SequenceEqual("MSCF"u8))
            return SignatureKind.Cab;

        if (first == (byte)'M' && index + 64 <= length &&
            buffer[index + 1] == (byte)'Z')
            return SignatureKind.PortableExecutable;

        if (first == (byte)'M' &&
            index + 4 <= length &&
            buffer[index + 1] == (byte)'M' &&
            ((buffer[index + 2] == 0x00 && buffer[index + 3] == 0x2A) ||
             (buffer[index + 2] == 0x00 && buffer[index + 3] == 0x2B)))
            return SignatureKind.TiffBigEndian;

        if (first == 0x1A &&
            index + 4 <= length &&
            buffer[index + 1] == 0x45 &&
            buffer[index + 2] == 0xDF &&
            buffer[index + 3] == 0xA3)
            return SignatureKind.Matroska;

        if (first == 0x1F &&
            index + 4 <= length &&
            buffer[index + 1] == 0x43 &&
            buffer[index + 2] == 0xB6 &&
            buffer[index + 3] == 0x75)
            return SignatureKind.MatroskaCluster;

        if (first == (byte)'F' &&
            index + 9 <= length &&
            buffer[index + 1] == (byte)'L' &&
            buffer[index + 2] == (byte)'V' &&
            buffer[index + 3] == 0x01 &&
            (buffer[index + 4] & 0x01) != 0)
            return SignatureKind.Flv;

        if (first == (byte)'O' &&
            index + 27 <= length &&
            buffer[index + 1] == (byte)'g' &&
            buffer[index + 2] == (byte)'g' &&
            buffer[index + 3] == (byte)'S' &&
            buffer[index + 4] == 0x00)
            return SignatureKind.OggVideo;

        if (first == (byte)'.' &&
            index + 10 <= length &&
            buffer[index + 1] == (byte)'R' &&
            buffer[index + 2] == (byte)'M' &&
            buffer[index + 3] == (byte)'F')
            return SignatureKind.RealMedia;

        if (first == (byte)'.' && index + 24 <= length &&
            buffer.AsSpan(index, 4).SequenceEqual(".snd"u8))
            return SignatureKind.Au;

        if (first == (byte)'R' && index + 12 <= length)
        {
            if (buffer[index + 1] == (byte)'I' &&
                buffer[index + 2] == (byte)'F' &&
                buffer[index + 3] == (byte)'F')
            {
                if (buffer[index + 8] == (byte)'W' &&
                    buffer[index + 9] == (byte)'E' &&
                    buffer[index + 10] == (byte)'B' &&
                    buffer[index + 11] == (byte)'P')
                    return SignatureKind.WebP;

                return SignatureKind.Riff;
            }

            if (buffer[index + 1] == (byte)'a' &&
                buffer[index + 2] == (byte)'r' &&
                buffer[index + 3] == (byte)'!' &&
                buffer[index + 4] == 0x1A &&
                buffer[index + 5] == 0x07 &&
                buffer[index + 6] is 0x00 or 0x01)
                return SignatureKind.Rar;
        }


        if (first == 0x00 && index + 12 <= length &&
            buffer[index + 1] == 0x00 && buffer[index + 2] == 0x00 && buffer[index + 3] == 0x0C &&
            buffer[index + 4] == (byte)'j' && buffer[index + 5] == (byte)'P' &&
            buffer[index + 6] == 0x20 && buffer[index + 7] == 0x20 &&
            buffer[index + 8] == 0x0D && buffer[index + 9] == 0x0A &&
            buffer[index + 10] == 0x87 && buffer[index + 11] == 0x0A)
            return SignatureKind.Jpeg2000;

        if (first == 0xFF && index + 4 <= length &&
            buffer[index + 1] == 0x4F && buffer[index + 2] == 0xFF && buffer[index + 3] == 0x51)
            return SignatureKind.Jpeg2000Codestream;

        if (first == (byte)'8' && index + 6 <= length &&
            buffer[index + 1] == (byte)'B' && buffer[index + 2] == (byte)'P' && buffer[index + 3] == (byte)'S' &&
            buffer[index + 4] == 0x00 && buffer[index + 5] is 0x01 or 0x02)
            return SignatureKind.Psd;

        if (first == (byte)'D' && index + 4 <= length &&
            buffer[index + 1] == (byte)'D' && buffer[index + 2] == (byte)'S' && buffer[index + 3] == (byte)' ')
            return SignatureKind.Dds;

        if (first == 0x76 && index + 4 <= length &&
            buffer[index + 1] == 0x2F && buffer[index + 2] == 0x31 && buffer[index + 3] == 0x01)
            return SignatureKind.Exr;

        if (first == 0x00 && index + 6 <= length &&
            buffer[index + 1] == 0x00 &&
            (buffer[index + 2] == 0x01 || buffer[index + 2] == 0x02) &&
            buffer[index + 3] == 0x00)
        {
            bool cursor = buffer[index + 2] == 0x02;
            ReadOnlySpan<byte> iconPrefix = buffer.AsSpan(index, Math.Min(length - index, 4096));
            if (IconContainerValidator.LooksLikePrefix(iconPrefix, cursor))
                return SignatureKind.Ico;
        }

        if (first == 0x30 &&
            index + AsfHeaderSignature.Length <= length &&
            buffer.AsSpan(index, AsfHeaderSignature.Length).SequenceEqual(AsfHeaderSignature))
            return SignatureKind.Asf;

        if (first == 0x36 &&
            index + AsfDataObjectSignature.Length <= length &&
            buffer.AsSpan(index, AsfDataObjectSignature.Length).SequenceEqual(AsfDataObjectSignature))
            return SignatureKind.AsfDataObject;

        if (first == 0xD0 &&
            index + OleHeaderSignature.Length <= length &&
            buffer.AsSpan(index, OleHeaderSignature.Length).SequenceEqual(OleHeaderSignature))
            return SignatureKind.OleCompound;

        if (first == (byte)'%' &&
            index + 4 <= length &&
            buffer[index + 1] == (byte)'P' &&
            buffer[index + 2] == (byte)'D' &&
            buffer[index + 3] == (byte)'F')
            return SignatureKind.Pdf;

        if (first == (byte)'P' &&
            index + 30 <= length &&
            buffer[index + 1] == (byte)'K' &&
            buffer[index + 2] == 0x03 &&
            buffer[index + 3] == 0x04 &&
            LooksLikeZipLocalHeader(buffer.AsSpan(index, Math.Min(length - index, 4096))))
            return SignatureKind.Zip;

        if (first == (byte)'S' && index + 16 <= length &&
            buffer.AsSpan(index, 16).SequenceEqual("SQLite format 3\0"u8))
            return SignatureKind.SqliteDatabase;

        if (first == 0x37 && index + 6 <= length &&
            buffer[index + 1] == 0x7A && buffer[index + 2] == 0xBC &&
            buffer[index + 3] == 0xAF && buffer[index + 4] == 0x27 && buffer[index + 5] == 0x1C)
            return SignatureKind.SevenZip;

        if (first == 0x37 && index + 4 <= length &&
            buffer[index + 1] == 0x7F && buffer[index + 2] == 0x06 &&
            buffer[index + 3] is 0x82 or 0x83)
            return SignatureKind.SqliteWal;

        if (first == 0xD9 && index + 8 <= length &&
            buffer.AsSpan(index, 8).SequenceEqual(new byte[] { 0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7 }))
            return SignatureKind.SqliteJournal;

        if (first == 0x00 &&
            index + 4 <= length &&
            buffer[index + 1] == 0x00 &&
            buffer[index + 2] == 0x01 &&
            buffer[index + 3] == 0xBA)
            return SignatureKind.MpegProgramStream;

        if (first == 0x00 &&
            index + 4 <= length &&
            buffer[index + 1] == 0x00 &&
            buffer[index + 2] == 0x01 &&
            buffer[index + 3] == 0xB3)
            return SignatureKind.MpegVideoStream;

        if (index + 12 <= length)
        {
            if (MatchesBoxType(buffer, index, (byte)'f', (byte)'t', (byte)'y', (byte)'p'))
                return SignatureKind.IsoBmff;

            bool fragmentBox =
                MatchesBoxType(buffer, index, (byte)'s', (byte)'t', (byte)'y', (byte)'p') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'f') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'d', (byte)'a', (byte)'t') ||
                MatchesBoxType(buffer, index, (byte)'m', (byte)'o', (byte)'o', (byte)'v');

            if (fragmentBox)
            {
                uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index, 4));
                if (boxSize == 0 || boxSize == 1 || boxSize >= 8)
                    return SignatureKind.IsoBmffFragment;
            }
        }

        if (first == 0x47 && HasSync(buffer, index, length, 188, 0, 5))
            return SignatureKind.MpegTs;

        if (index + 4 < length &&
            buffer[index + 4] == 0x47 &&
            HasSync(buffer, index, length, 192, 4, 5))
            return SignatureKind.MpegTs;

        if (includeEmbeddedVideoSearch && first == 0x00)
        {
            if (TryMatchAnnexBNalPair(buffer, index, length, h265: false))
                return SignatureKind.H264AnnexB;

            if (TryMatchAnnexBNalPair(buffer, index, length, h265: true))
                return SignatureKind.H265AnnexB;

            if (TryMatchLengthPrefixedNalPair(buffer, index, length, h265: false))
                return SignatureKind.H264LengthPrefixed;

            if (TryMatchLengthPrefixedNalPair(buffer, index, length, h265: true))
                return SignatureKind.H265LengthPrefixed;
        }

        return null;
    }

    private static bool ValidateWholeFileCandidateHeader(
        RawDeviceReader reader,
        long start,
        long length,
        string extension)
    {
        if (length <= 0)
            return false;

        int inspectLength = (int)Math.Min(4096L, length);
        if (inspectLength <= 0)
            return false;

        byte[] header = new byte[inspectLength];
        int read = reader.ReadBestEffort(start, header, out long unreadableBytes);
        if (read <= 0 || unreadableBytes > 0)
            return false;

        return FileHeaderValidator.LooksLike(extension, header.AsSpan(0, read), length);
    }

    private readonly record struct RawScanGeometry(long Origin, int Alignment)
    {
        public bool IsAligned(long offset) =>
            Alignment > 0 && offset >= Origin && (offset - Origin) % Alignment == 0;
    }

    private readonly record struct DeferredRawCandidate(SignatureKind Kind, long Start);

    private static SignatureKind? TryMatchAdditionalGlobalVideoAt(byte[] buffer, int index, int length)
    {
        int remaining = length - index;
        if (remaining <= 0)
            return null;

        ReadOnlySpan<byte> span = buffer.AsSpan(index, remaining);

        if (remaining >= 16 &&
            MatchesBytes(span, 0, MxfHeaderPrefix) &&
            span[14] is >= 0x01 and <= 0x04 && span[15] == 0x00)
            return SignatureKind.Mxf;

        if (remaining >= 16 &&
            (MatchesBytes(span, 0, MxfEssencePrefix) ||
             MatchesBytes(span, 0, MxfAvidEssencePrefix) ||
             MatchesBytes(span, 0, MxfCanopusEssencePrefix)))
            return SignatureKind.MxfEssence;

        if (remaining >= 16 &&
            MatchesBytes(span, 0, WtvHeaderSignature))
            return SignatureKind.Wtv;

        if (remaining >= 4 && span[..4].SequenceEqual("NSVf"u8))
            return SignatureKind.Nsv;

        if (remaining >= 8 && MatchesBytes(span, 0, RoqHeaderSignature))
            return SignatureKind.Roq;

        if (remaining >= 4 &&
            ((span[0] == (byte)'B' && span[1] == (byte)'I' && span[2] == (byte)'K') ||
             (span[0] == (byte)'K' && span[1] == (byte)'B' && span[2] == (byte)'2')))
            return SignatureKind.Bink;

        if (remaining >= 4 &&
            (span[..4].SequenceEqual("SMK2"u8) || span[..4].SequenceEqual("SMK4"u8)))
            return SignatureKind.Smacker;

        if (remaining >= 12 && span[..4].SequenceEqual("DKIF"u8) && span.Slice(8, 4).SequenceEqual("AV01"u8))
            return SignatureKind.Av1Ivf;

        if (remaining >= 6 * 80 && GlobalVideoRawAnalyzer.LooksLikeDvStart(span[..(6 * 80)]))
            return SignatureKind.Dv;

        return null;
    }

    private static bool MatchesBytes(ReadOnlySpan<byte> source, int offset, byte[] signature)
    {
        if (offset < 0 || signature.Length == 0 || offset + signature.Length > source.Length)
            return false;

        for (int i = 0; i < signature.Length; i++)
        {
            if (source[offset + i] != signature[i])
                return false;
        }

        return true;
    }

    private static bool RequiresFileBoundaryAlignment(SignatureKind kind) => kind switch
    {
        SignatureKind.IsoBmffFragment or
        SignatureKind.AsfDataObject or
        SignatureKind.MatroskaCluster or
        SignatureKind.MxfEssence or
        SignatureKind.H264LengthPrefixed or
        SignatureKind.H265LengthPrefixed or
        SignatureKind.H264AnnexB or
        SignatureKind.H265AnnexB => false,
        _ => true
    };

    private static bool TryMatchAnnexBNalPair(byte[] buffer, int index, int length, bool h265)
    {
        if (!TryGetAnnexBHeader(buffer, index, length, h265, out int prefixLength, out int firstType))
            return false;

        bool anchor = h265
            ? firstType is 19 or 20 or 21 or 32 or 33 or 34
            : firstType is 5 or 7 or 8;
        if (!anchor)
            return false;

        int searchStart = index + prefixLength + (h265 ? 2 : 1);
        int searchEnd = Math.Min(length - 4, index + 1024 * 1024);
        for (int i = searchStart; i <= searchEnd; i++)
        {
            if (buffer[i] != 0x00 || buffer[i + 1] != 0x00)
                continue;

            if (TryGetAnnexBHeader(buffer, i, length, h265, out _, out _))
                return true;
        }

        return false;
    }

    private static bool TryGetAnnexBHeader(
        byte[] buffer,
        int index,
        int length,
        bool h265,
        out int prefixLength,
        out int nalType)
    {
        prefixLength = 0;
        nalType = -1;
        if (index < 0 || index + 5 >= length)
            return false;

        if (buffer[index] == 0x00 && buffer[index + 1] == 0x00 &&
            buffer[index + 2] == 0x00 && buffer[index + 3] == 0x01)
            prefixLength = 4;
        else if (buffer[index] == 0x00 && buffer[index + 1] == 0x00 && buffer[index + 2] == 0x01)
            prefixLength = 3;
        else
            return false;

        int header = index + prefixLength;
        if (header >= length)
            return false;

        byte b0 = buffer[header];
        if ((b0 & 0x80) != 0)
            return false;

        if (!h265)
        {
            nalType = b0 & 0x1F;
            return nalType is >= 1 and <= 12;
        }

        if (header + 1 >= length || (buffer[header + 1] & 0x07) == 0)
            return false;

        nalType = (b0 >> 1) & 0x3F;
        return nalType <= 40;
    }

    private static bool TryMatchLengthPrefixedNalPair(byte[] buffer, int index, int length, bool h265)
    {
        if (index < 0 || index + 6 > length)
            return false;

        uint firstLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(index, 4));
        if (firstLength < 2)
            return false;

        int firstHeader = index + 4;
        if (!LooksLikeNalHeader(buffer, firstHeader, length, h265))
            return false;

        long nextLong = index + 4L + firstLength;
        if (nextLong > int.MaxValue || nextLong + 6 > length)
        {
            // İlk NAL mevcut tarama bloğundan daha büyük olabilir. Dosya boyutuna göre
            // keyfi NAL üst sınırı koyma; yalnız güçlü video NAL tiplerinde adayı tam
            // RawFileAnalyzer doğrulamasına gönder.
            int type = h265
                ? (buffer[firstHeader] >> 1) & 0x3F
                : buffer[firstHeader] & 0x1F;
            return h265
                ? type is 19 or 20 or 21 || (type is 32 or 33 or 34 && firstLength <= 65536)
                : type == 5 || (type is 7 or 8 && firstLength <= 65536);
        }

        int next = (int)nextLong;
        uint secondLength = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(next, 4));
        if (secondLength < 2)
            return false;

        int secondHeader = next + 4;
        return secondHeader + 2 <= length &&
               LooksLikeNalHeader(buffer, secondHeader, length, h265);
    }

    private static bool LooksLikeNalHeader(byte[] buffer, int index, int length, bool h265)
    {
        if (index >= length)
            return false;

        byte b0 = buffer[index];
        if ((b0 & 0x80) != 0)
            return false;

        if (!h265)
        {
            int type = b0 & 0x1F;
            return type is >= 1 and <= 12;
        }

        if (index + 1 >= length || (buffer[index + 1] & 0x07) == 0)
            return false;

        int h265Type = (b0 >> 1) & 0x3F;
        return h265Type <= 40;
    }

    private static bool MatchesBoxType(
        byte[] buffer,
        int index,
        byte a,
        byte b,
        byte c,
        byte d) =>
        buffer[index + 4] == a &&
        buffer[index + 5] == b &&
        buffer[index + 6] == c &&
        buffer[index + 7] == d;

    private static bool HasSync(byte[] buffer, int start, int length, int packetSize, int syncOffset, int packetCount)
    {
        for (int i = 0; i < packetCount; i++)
        {
            int index = start + syncOffset + i * packetSize;
            if (index >= length || buffer[index] != 0x47)
                return false;
        }

        return true;
    }

    private sealed class DeepScanProgressPulse : IProgress<OperationProgress>, IDisposable
    {
        private readonly IProgress<OperationProgress> _inner;
        private readonly CancellationToken _cancellationToken;
        private readonly TimeSpan _interval;
        private readonly object _sync = new();
        private readonly Timer _timer;
        private OperationProgress? _activity;
        private long _lastEmissionTimestamp;
        private bool _disposed;

        public DeepScanProgressPulse(
            IProgress<OperationProgress> inner,
            CancellationToken cancellationToken,
            TimeSpan interval)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _cancellationToken = cancellationToken;
            _interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(750) : interval;
            _lastEmissionTimestamp = Stopwatch.GetTimestamp();
            _timer = new Timer(OnTimer, null, _interval, _interval);
        }

        public void Report(OperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_disposed)
                return;

            lock (_sync)
            {
                if (_disposed)
                    return;

                _activity = value with { NewFiles = null };
                _lastEmissionTimestamp = Stopwatch.GetTimestamp();
                // Serialize timer and real updates so an older heartbeat can never arrive
                // after a newer worker update and move the UI/progress position backwards.
                _inner.Report(value);
            }
        }

        public void SetActivity(OperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_disposed)
                return;

            lock (_sync)
            {
                if (_disposed)
                    return;

                // Activity updates are intentionally not emitted immediately. The timer sends
                // them only when the worker has produced no regular progress event, preventing
                // UI flooding while still proving that a long parser/reconstruction step lives.
                _activity = value with { NewFiles = null };
            }
        }

        private void OnTimer(object? state)
        {
            if (_disposed || _cancellationToken.IsCancellationRequested)
                return;

            lock (_sync)
            {
                if (_disposed || _activity is null ||
                    Stopwatch.GetElapsedTime(_lastEmissionTimestamp) < _interval)
                    return;

                _lastEmissionTimestamp = Stopwatch.GetTimestamp();
                string detail = string.IsNullOrWhiteSpace(_activity.Detail)
                    ? "USB/SD tarama motoru çalışıyor"
                    : _activity.Detail;
                OperationProgress pulse = _activity with
                {
                    Detail = $"{detail} • motor aktif {DateTime.Now:HH:mm:ss}",
                    NewFiles = null,
                    Checkpoint = null
                };

                // Kept under the same lock as Report() to guarantee monotonic UI ordering.
                _inner.Report(pulse);
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }

            _timer.Dispose();
        }
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public InlineProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }

    private static void Report(
        IProgress<OperationProgress>? progress,
        long processed,
        long total,
        IReadOnlyCollection<RecoveryFileItem> results,
        int rebuiltVideoCount,
        string? detailOverride,
        ref int reportedResultCount,
        RecoveryScanCheckpoint? checkpoint = null,
        double? percentOverride = null,
        string? titleOverride = null,
        bool stagedPortableProgress = false)
    {
        double percent = percentOverride ?? (stagedPortableProgress
            ? PortableDeepScanPolicy.MapRawPercent(processed, total)
            : total <= 0 ? 0d : Math.Clamp(processed * 100d / total, 0d, 100d));
        int videoCount = results.Count(f => f.Category == "Video");
        string detail = detailOverride ??
            $"I/O {RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(total)} • video {videoCount:N0} • yeniden oluşturulan video {rebuiltVideoCount:N0}";

        RecoveryFileItem[]? newFiles = null;
        if (results.Count > reportedResultCount)
        {
            newFiles = RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray());
            reportedResultCount = results.Count;
        }

        progress?.Report(new OperationProgress(
            Math.Clamp(percent, 0d, 100d),
            titleOverride ?? "Derin Tarama • Ham Veri Analizi",
            detail,
            processed,
            total,
            results.Count,
            newFiles,
            checkpoint));
    }

    private static RecoveryScanCheckpoint CreateCheckpoint(
        int passNumber,
        string stage,
        long position,
        long total,
        bool metadataStageCompleted,
        bool rawScanCompleted,
        bool fragmentStageCompleted,
        bool finalValidationStarted,
        RawDeviceReader reader,
        AdaptiveDeepScanController adaptiveScan,
        IReadOnlyList<RecoveryFileItem>? orphanFragmentNodes = null,
        int fragmentNodeStartIndex = 0,
        bool resetPendingFragmentNodes = false)
    {
        long safePosition = Math.Max(0, position);
        long safeTotal = Math.Max(0, total);
        long scannedLength = safeTotal > 0 ? Math.Min(safePosition, safeTotal) : safePosition;

        return new RecoveryScanCheckpoint
        {
            PassNumber = Math.Clamp(passNumber, 1, 4),
            Stage = stage ?? string.Empty,
            ResumePosition = safePosition,
            ResumeTotal = safeTotal,
            MetadataStageCompleted = metadataStageCompleted,
            RawScanCompleted = rawScanCompleted,
            FragmentStageCompleted = fragmentStageCompleted,
            FinalValidationStarted = finalValidationStarted,
            AdaptiveBlockSize = adaptiveScan.CurrentBlockSize,
            AdaptiveSafeScanActive = adaptiveScan.SafeScanActive,
            AdaptiveBlocksObserved = adaptiveScan.BlocksObserved,
            AdaptiveCleanBlocks = adaptiveScan.CleanBlocks,
            AdaptiveDenseBlocks = adaptiveScan.DenseBlocks,
            AdaptiveErrorBlocks = adaptiveScan.ErrorBlocks,
            AdaptiveUnreadableBytes = adaptiveScan.CumulativeUnreadableBytes,
            AdaptiveBlockSizeChanges = adaptiveScan.BlockSizeChanges,
            AdaptiveReadSamples = adaptiveScan.ReadSamples,
            AdaptiveReadBytes = adaptiveScan.ReadBytes,
            AdaptiveReadMilliseconds = adaptiveScan.ActiveReadMilliseconds,
            AdaptivePeakBytesPerSecond = adaptiveScan.PeakReadBytesPerSecond,
            AdaptiveSlowReadSamples = adaptiveScan.SlowReadSamples,
            AdaptiveFastReadSamples = adaptiveScan.FastReadSamples,
            BadSectorFailureEvents = reader.HeatMap.FailureEvents,
            BadSectorRecoveredBytes = reader.HeatMap.RecoveredBytes,
            BadSectorSkippedKnownBadReads = reader.HeatMap.SkippedKnownBadReads,
            BadSectors = reader.HeatMap.SnapshotState().ToList(),
            ScannedRanges = scannedLength > 0
                ? [new RecoveryScannedRange { Offset = 0, Length = scannedLength, Kind = "RAW" }]
                : [],
            PendingFragmentNodesAreDelta = !resetPendingFragmentNodes,
            PendingFragmentNodes = resetPendingFragmentNodes
                ? []
                : BuildFragmentCheckpointDelta(orphanFragmentNodes, fragmentNodeStartIndex)
        };
    }

    private static List<RecoveryFragmentCheckpointItem> BuildFragmentCheckpointDelta(
        IReadOnlyList<RecoveryFileItem>? orphanFragmentNodes,
        int startIndex)
    {
        if (orphanFragmentNodes is null || orphanFragmentNodes.Count == 0)
            return [];

        int first = Math.Clamp(startIndex, 0, orphanFragmentNodes.Count);
        if (first >= orphanFragmentNodes.Count)
            return [];

        var delta = new List<RecoveryFragmentCheckpointItem>(orphanFragmentNodes.Count - first);
        for (int index = first; index < orphanFragmentNodes.Count; index++)
            delta.Add(RecoveryFragmentCheckpointItem.FromItem(orphanFragmentNodes[index]));

        return delta;
    }
}
