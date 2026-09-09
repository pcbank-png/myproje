using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Last-resort Quick Scan path for old rotational NTFS media whose deleted MFT
/// records have already been reused or are no longer structurally recoverable.
/// It prefers $Bitmap-backed unallocated extents; when $Bitmap itself is lost,
/// it performs one sequential header/structure pass without Deep Scan fragment
/// reconstruction, graph passes, or bad-sector retry passes.
/// Source media is opened read-only by the caller.
/// </summary>
internal static class LegacySurfaceQuickScanService
{
    private const int MaxResults = 250000;
    private const int NormalBlockBytes = 64 * 1024 * 1024;
    private const int SafeBlockBytes = 1 * 1024 * 1024;
    private const int MatchOverlapBytes = 256 * 1024;
    private const int BitmapReadBytes = 128 * 1024;
    private const int MinimumMetadataScopeResults = 2048;
    private const int MaxBitmapExtents = 1_000_000;

    private readonly record struct QuickExtent(long Offset, long Length)
    {
        public long End => Offset + Length;
    }

    private sealed record BitmapExtentMap(bool Available, IReadOnlyList<QuickExtent> Extents, long TotalFreeBytes);

    internal static int CountMatchingScope(IEnumerable<RecoveryFileItem> items, DeepScanTarget scope)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Count(item => MatchesScope(item.Extension, scope));
    }

    public static ScanReport Scan(
        StorageDeviceInfo device,
        RawDeviceReader reader,
        long volumeLength,
        IReadOnlyList<DataRun>? mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        IReadOnlyList<RecoveryFileItem>? seedResults,
        DeepScanTarget scope,
        string metadataSummary,
        RecoveryScanCheckpoint? resumeCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(reader);

        var results = seedResults is { Count: > 0 }
            ? new List<RecoveryFileItem>(seedResults)
            : new List<RecoveryFileItem>();
        NtfsPathCorrelationService pathCorrelation = NtfsPathCorrelationService.Build(results);
        int pathCorrelatedCount = pathCorrelation.ApplyTo(results);

        int scopeSeedCount = CountMatchingScope(results, scope);
        bool resumeSurfaceRequested = IsSurfaceCheckpoint(resumeCheckpoint);
        if (scopeSeedCount >= MinimumMetadataScopeResults && !resumeSurfaceRequested)
            return new ScanReport(results, metadataSummary, ScanMode.Quick, UsedFallback: false);

        volumeLength = reader.VolumeLength > 0 ? reader.VolumeLength : volumeLength;
        if (volumeLength <= 0)
            return new ScanReport(results, metadataSummary, ScanMode.Quick, UsedFallback: false);

        string fileSystem = string.IsNullOrWhiteSpace(device.FileSystem)
            ? "RAW"
            : device.FileSystem.Trim().ToUpperInvariant();

        BitmapExtentMap bitmapMap;
        try
        {
            bitmapMap = BuildBitmapExtentMap(
                reader,
                mftRuns,
                clusterSize,
                recordSize,
                bytesPerSector,
                volumeLength,
                cancellationToken);
        }
        catch (InvalidDataException)
        {
            bitmapMap = new BitmapExtentMap(false, [], 0);
        }

        bool freeSpaceOnly = bitmapMap.Available;
        IReadOnlyList<QuickExtent> extents;
        long targetBytes;
        string phaseTitle;
        string phaseDetail;

        if (freeSpaceOnly)
        {
            extents = bitmapMap.Extents;
            targetBytes = bitmapMap.TotalFreeBytes;
            phaseTitle = $"{fileSystem} \u2022 Hızlı Boş Alan Taraması \u2022 Pass 1/1";
            phaseDetail = $"$Bitmap dogrulandi \u2022 yalniz bos/unallocated alan taranacak \u2022 {RecoveryFileItem.FormatBytes(targetBytes)}.";

            if (targetBytes < Math.Max(4096, clusterSize) || extents.Count == 0)
            {
                string noFreeSummary = $"{metadataSummary} $Bitmap dogrulandi ancak taranabilir bos cluster bulunmadi.";
                return new ScanReport(results, noFreeSummary, ScanMode.Quick, UsedFallback: false);
            }
        }
        else
        {
            extents = [new QuickExtent(0, volumeLength)];
            targetBytes = volumeLength;
            phaseTitle = $"{fileSystem} \u2022 Hızlı Yüzey Taraması \u2022 Pass 1/1";
            phaseDetail = "$Bitmap kullanilamiyor \u2022 tek gecisli buyuk bloklu header + structure taramasi baslatildi. Deep Scan 4-pass, fragment graph ve reconstruction calistirilmayacak.";
        }

        string checkpointStage = freeSpaceOnly ? "quick-free-space" : "quick-surface";
        long resumeBytes = ResolveResumeBytes(resumeCheckpoint, checkpointStage, targetBytes);

        progress?.Report(new OperationProgress(
            targetBytes <= 0 ? 0 : Math.Clamp(resumeBytes * 100d / targetBytes, 0d, 100d),
            phaseTitle,
            phaseDetail,
            resumeBytes,
            targetBytes,
            results.Count,
            Checkpoint: CreateSurfaceCheckpoint(checkpointStage, resumeBytes, targetBytes)));

        int blockSize = device.HealthSafeScanRecommended ? SafeBlockBytes : NormalBlockBytes;
        int alignment = Math.Max(512, reader.SectorSize);
        byte[] buffer = new byte[blockSize + MatchOverlapBytes];
        var exactStarts = new HashSet<long>(results
            .Where(item => item.SourceKind == RecoverySourceKind.RawContiguous && item.SourceOffset >= 0)
            .Select(item => item.SourceOffset));

        int sequence = Math.Max(1, results.Count + 1);
        int falsePositiveRejected = 0;
        long processed = 0;
        long resumeRemaining = resumeBytes;
        long unreadable = 0;
        long lastProgress = resumeBytes;
        int rawAdded = 0;
        int reportedResultCount = results.Count;

        foreach (QuickExtent extent in extents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= MaxResults)
                break;

            long extentStart = Math.Clamp(extent.Offset, 0, volumeLength);
            long extentEnd = Math.Clamp(extent.End, extentStart, volumeLength);
            long extentLength = Math.Max(0, extentEnd - extentStart);
            if (extentLength <= 0)
                continue;

            if (resumeRemaining >= extentLength)
            {
                processed += extentLength;
                resumeRemaining -= extentLength;
                continue;
            }

            long position = extentStart;
            if (resumeRemaining > 0)
            {
                position += resumeRemaining;
                processed += resumeRemaining;
                resumeRemaining = 0;
            }

            if (extentEnd - position < alignment)
            {
                processed += Math.Max(0, extentEnd - position);
                continue;
            }

            for (; position < extentEnd && results.Count < MaxResults;)
            {
                pauseGate?.Wait(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                int mainBytes = (int)Math.Min(blockSize, extentEnd - position);
                if (mainBytes <= 0)
                    break;

                long readableEnd = freeSpaceOnly
                    ? Math.Min(volumeLength, extentEnd + MatchOverlapBytes)
                    : volumeLength;
                int request = (int)Math.Min(buffer.Length, readableEnd - position);
                // ReadBestEffort only exposes the returned byte count; the scanner never
                // inspects bytes beyond that range, so clearing a 64 MB buffer on every block
                // would waste memory bandwidth without adding correctness.
                int read = reader.ReadBestEffort(position, buffer.AsSpan(0, request), out long blockUnreadable);
                unreadable += blockUnreadable;
                if (read <= 0)
                {
                    processed += mainBytes;
                    position += mainBytes;
                    continue;
                }

                int scanLength = Math.Min(mainBytes, read);
                int searchStart = 0;
                while (searchStart < scanLength && results.Count < MaxResults)
                {
                    int index = DeepScanService.FindNextQuickCandidate(
                        buffer,
                        position,
                        searchStart,
                        scanLength,
                        alignment);
                    if (index < 0)
                        break;

                    searchStart = index + 1;
                    SignatureKind? kind = DeepScanService.TryMatchQuickAt(buffer, index, read);
                    if (kind is null || !DeepScanService.QuickSignatureMayMatchScope(kind.Value, scope))
                        continue;

                    long candidateStart = position + index;
                    if (!exactStarts.Add(candidateStart))
                        continue;

                    RawFileAnalysis? analysis;
                    try
                    {
                        analysis = RawFileAnalyzer.Analyze(
                            reader,
                            kind.Value,
                            candidateStart,
                            volumeLength,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
                    {
                        analysis = null;
                    }

                    if (analysis is null || analysis.Length <= 0 || analysis.Length > volumeLength - candidateStart)
                        continue;

                    // Quick mode does not synthesize missing leading video container bytes.
                    // That belongs to Deep/Fragment Reconstruction. Safe suffix repair (JPEG EOI,
                    // ZIP central directory) remains allowed because the analyzer validated it.
                    if (analysis.PrependStandardMp4Header)
                        continue;

                    string extension = FileTypeHelper.Normalize(analysis.Extension);
                    if (!FileTypeHelper.IsSupported(extension) || !MatchesScope(extension, scope))
                        continue;

                    if (freeSpaceOnly && candidateStart + analysis.Length > extentEnd)
                        continue;

                    if (DeepScanService.QuickRequiresFileBoundaryAlignment(kind.Value) &&
                        !DeepScanService.QuickValidateWholeFileCandidateHeader(
                            reader,
                            candidateStart,
                            analysis.Length,
                            extension))
                    {
                        continue;
                    }

                    RecoveryConfidenceResult confidence = RecoveryConfidenceService.EvaluateRawCandidate(
                        reader,
                        candidateStart,
                        analysis.Length,
                        extension,
                        analysis.RecoveryState,
                        hasSyntheticPrefix: false,
                        hasSyntheticSuffix: analysis.AppendData is { Length: > 0 });
                    if (confidence.RejectAsFalsePositive)
                    {
                        falsePositiveRejected++;
                        continue;
                    }

                    string sourceText = freeSpaceOnly
                        ? $"{fileSystem} Free Space Quick Recovery"
                        : $"{fileSystem} Legacy Surface Quick Recovery";
                    string fileName = $"Hizli_Kurtarilan_{sequence:000000}_{candidateStart:X}.{extension.ToLowerInvariant()}";
                    var recovered = new RecoveryFileItem
                    {
                        FileName = fileName,
                        Extension = extension,
                        SizeBytes = analysis.Length,
                        RecoveryState = analysis.RecoveryState,
                        TypeGlyph = FileTypeHelper.GetGlyph(extension),
                        SourceText = sourceText,
                        SourceKind = RecoverySourceKind.RawContiguous,
                        SourceOffset = candidateStart,
                        SuffixData = analysis.AppendData,
                        TransformKind = analysis.TransformKind,
                        RecoveryConfidenceScore = confidence.Score,
                        RecoveryConfidenceGrade = confidence.Grade,
                        RecoveryConfidenceSummary = confidence.Summary
                    };
                    if (pathCorrelation.TryApply(recovered))
                        pathCorrelatedCount++;
                    results.Add(recovered);
                    rawAdded++;
                    sequence++;
                }

                processed += mainBytes;
                position += mainBytes;

                if (processed == targetBytes || processed - lastProgress >= 128L * 1024 * 1024)
                {
                    lastProgress = processed;
                    RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                        ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                        : null;
                    reportedResultCount = results.Count;
                    progress?.Report(new OperationProgress(
                        targetBytes <= 0 ? 0 : Math.Clamp(processed * 100d / targetBytes, 0d, 100d),
                        phaseTitle,
                        $"Quick alan: {RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(targetBytes)} \u2022 yeni {rawAdded:N0} \u2022 toplam {results.Count:N0} \u2022 false-positive {falsePositiveRejected:N0} \u2022 okunamayan {RecoveryFileItem.FormatBytes(unreadable)}",
                        processed,
                        targetBytes,
                        results.Count,
                        newFiles,
                        CreateSurfaceCheckpoint(checkpointStage, processed, targetBytes)));
                }
            }
        }

        List<RecoveryFileItem> ordered = RecoveryScanPriorityService.OrderNewestFirst(results).ToList();
        string modeText = freeSpaceOnly ? "$Bitmap Free Space Quick" : "Legacy Surface Quick";
        string summary = $"{metadataSummary} {modeText} tamamlandi \u2022 {rawAdded:N0} ek dosya dogrulandi \u2022 MFT yol eslesmesi {pathCorrelatedCount:N0} \u2022 {RecoveryFileItem.FormatBytes(processed)} tarandi \u2022 false-positive elenen {falsePositiveRejected:N0} \u2022 okunamayan {RecoveryFileItem.FormatBytes(unreadable)}.";
        return new ScanReport(ordered, summary, ScanMode.Quick, UsedFallback: false);
    }

    public static ScanReport ScanDeviceSurface(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        IReadOnlyList<RecoveryFileItem>? seedResults,
        DeepScanTarget scope,
        string metadataSummary,
        RecoveryScanCheckpoint? resumeCheckpoint = null)
    {
        using RawDeviceReader reader = RawDeviceReader.OpenDevice(
            device,
            pauseGate,
            RecoveryMediaProfileService.Create(device));
        long volumeLength = reader.VolumeLength > 0 ? reader.VolumeLength : device.TotalBytes;
        int bytesPerSector = reader.SectorSize is 512 or 1024 or 2048 or 4096 ? reader.SectorSize : 512;
        return Scan(
            device,
            reader,
            volumeLength,
            mftRuns: null,
            clusterSize: 4096,
            recordSize: 1024,
            bytesPerSector: bytesPerSector,
            progress: progress,
            pauseGate: pauseGate,
            cancellationToken: cancellationToken,
            seedResults: seedResults,
            scope: scope,
            metadataSummary: metadataSummary,
            resumeCheckpoint: resumeCheckpoint);
    }

    private static bool IsSurfaceCheckpoint(RecoveryScanCheckpoint? checkpoint)
    {
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Stage))
            return false;

        return checkpoint.Stage.StartsWith("quick-surface", StringComparison.OrdinalIgnoreCase) ||
               checkpoint.Stage.StartsWith("quick-free-space", StringComparison.OrdinalIgnoreCase);
    }

    private static long ResolveResumeBytes(
        RecoveryScanCheckpoint? checkpoint,
        string expectedStage,
        long expectedTotal)
    {
        if (!IsSurfaceCheckpoint(checkpoint) || checkpoint is null || expectedTotal <= 0)
            return 0;

        bool legacyAutoStage = string.Equals(checkpoint.Stage, "quick-surface-auto", StringComparison.OrdinalIgnoreCase);
        bool exactStage = string.Equals(checkpoint.Stage, expectedStage, StringComparison.OrdinalIgnoreCase);
        if (!legacyAutoStage && !exactStage)
            return 0;

        // The saved byte cursor is valid only against the same free-space/surface extent map.
        // If the mounted source changed, replay from the beginning rather than skipping data.
        if (checkpoint.ResumeTotal > 0 && checkpoint.ResumeTotal != expectedTotal)
            return 0;

        return Math.Clamp(checkpoint.ResumePosition, 0, expectedTotal);
    }

    private static RecoveryScanCheckpoint CreateSurfaceCheckpoint(
        string stage,
        long position,
        long total) => new()
    {
        PassNumber = 1,
        Stage = stage,
        ResumePosition = Math.Max(0, position),
        ResumeTotal = Math.Max(0, total),
        MetadataStageCompleted = true,
        RawScanCompleted = total > 0 && position >= total,
        FragmentStageCompleted = false,
        FinalValidationStarted = false,
        ScannedRanges = []
    };

    private static BitmapExtentMap BuildBitmapExtentMap(
        RawDeviceReader reader,
        IReadOnlyList<DataRun>? mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        if (mftRuns is null || mftRuns.Count == 0 ||
            clusterSize < 512 || recordSize < 512 || bytesPerSector < 512)
        {
            return new BitmapExtentMap(false, [], 0);
        }

        (IReadOnlyList<DataRun> bitmapRuns, long bitmapSize) = NtfsForensicMetadataService.ReadSystemFileLayout(
            reader,
            mftRuns,
            clusterSize,
            recordSize,
            bytesPerSector,
            recordIndex: 6);
        if (bitmapRuns.Count == 0 || bitmapSize <= 0)
            return new BitmapExtentMap(false, [], 0);

        long clusterCount = volumeLength / clusterSize;
        if (clusterCount <= 0)
            return new BitmapExtentMap(false, [], 0);

        long requiredBitmapBytes = (clusterCount + 7) / 8;
        if (bitmapSize < requiredBitmapBytes)
            return new BitmapExtentMap(false, [], 0);

        long readableBitmapBytes = requiredBitmapBytes;

        var extents = new List<QuickExtent>();
        byte[] buffer = new byte[BitmapReadBytes];
        long logicalOffset = 0;
        long clusterIndex = 0;
        long freeStartCluster = -1;
        long totalFreeClusters = 0;

        void CloseFreeRun(long endClusterExclusive)
        {
            if (freeStartCluster < 0 || endClusterExclusive <= freeStartCluster)
                return;

            long count = endClusterExclusive - freeStartCluster;
            long offset = freeStartCluster * (long)clusterSize;
            long length = Math.Min(volumeLength - offset, count * (long)clusterSize);
            if (length > 0)
            {
                if (extents.Count >= MaxBitmapExtents)
                    throw new InvalidDataException("$Bitmap serbest alan haritasi asiri parcali; tek-gecis surface fallback kullanilacak.");
                extents.Add(new QuickExtent(offset, length));
            }
            freeStartCluster = -1;
        }

        while (logicalOffset < readableBitmapBytes && clusterIndex < clusterCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int wanted = (int)Math.Min(buffer.Length, readableBitmapBytes - logicalOffset);
            Array.Clear(buffer, 0, wanted);
            int read = NtfsQuickScanService.ReadVirtualMft(
                reader,
                bitmapRuns,
                clusterSize,
                logicalOffset,
                buffer.AsSpan(0, wanted));
            if (read <= 0)
                return new BitmapExtentMap(false, [], 0);

            for (int byteIndex = 0; byteIndex < read && clusterIndex < clusterCount; byteIndex++)
            {
                byte allocation = buffer[byteIndex];
                for (int bit = 0; bit < 8 && clusterIndex < clusterCount; bit++, clusterIndex++)
                {
                    bool allocated = (allocation & (1 << bit)) != 0;
                    if (!allocated)
                    {
                        totalFreeClusters++;
                        if (freeStartCluster < 0)
                            freeStartCluster = clusterIndex;
                    }
                    else
                    {
                        CloseFreeRun(clusterIndex);
                    }
                }
            }

            logicalOffset += read;
            if (read < wanted)
                return new BitmapExtentMap(false, [], 0);
        }

        CloseFreeRun(clusterIndex);
        long totalFreeBytes = totalFreeClusters > long.MaxValue / clusterSize
            ? long.MaxValue
            : totalFreeClusters * (long)clusterSize;
        return new BitmapExtentMap(true, extents, totalFreeBytes);
    }

    private static bool MatchesScope(string extension, DeepScanTarget scope)
    {
        if (scope == DeepScanTarget.All)
            return true;

        string normalized = FileTypeHelper.Normalize(extension);
        if (scope == DeepScanTarget.Photo)
            return FileTypeHelper.IsPhoto(normalized);
        if (scope == DeepScanTarget.Video)
            return FileTypeHelper.IsVideo(normalized);
        if (scope == DeepScanTarget.Document)
            return FileTypeHelper.GetCategory(normalized) == "Belge";

        string category = FileTypeHelper.GetCategory(normalized);
        return (FileTypeHelper.IsPhoto(normalized) && scope.Includes(DeepScanTarget.Photo)) ||
               (FileTypeHelper.IsVideo(normalized) && scope.Includes(DeepScanTarget.Video)) ||
               (category == "Belge" && scope.Includes(DeepScanTarget.Document));
    }
}
