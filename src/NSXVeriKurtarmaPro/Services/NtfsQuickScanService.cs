using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class NtfsQuickScanService
{
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const uint AttributeList = 0x20;
    private const uint AttributeFileName = 0x30;
    private const uint AttributeData = 0x80;
    private const int RecordsPerBlock = 1024;
    private const int MaxResults = 500000;
    private const int MaxUsnPriorityCandidates = 8192;
    private const int MaxBitmapForensicCandidates = 25000;
    private const int MaxBitmapForensicCandidatesRotational = 5000;
    private const int MaxBitmapForensicCandidatesSafeScan = 256;
    private const long MinimumMftRescueScanBytes = 256L * 1024 * 1024;
    private const long MaximumMftRescueScanBytes = 1L * 1024 * 1024 * 1024;
    private const int NormalMftRescueBlockBytes = 8 * 1024 * 1024;
    private const int SafeMftRescueBlockBytes = 1 * 1024 * 1024;

    public ScanReport Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        long resumeRecordIndex = 0,
        IReadOnlyList<RecoveryFileItem>? seedResults = null,
        DeepScanTarget scope = DeepScanTarget.All,
        RecoveryScanCheckpoint? resumeCheckpoint = null,
        bool allowExtendedRescue = true,
        bool enableUsnPriority = true,
        bool includeExistingFiles = false)
    {
        bool fastPrelude = PortableDeepScanPolicy.IsFastMetadataPrelude(device, allowExtendedRescue);
        bool surfaceResumeRequested = IsQuickSurfaceCheckpoint(resumeCheckpoint);

        using RawDeviceReader reader = RawDeviceReader.OpenDevice(device, pauseGate, RecoveryMediaProfileService.Create(device));

        long volumeLength = reader.VolumeLength > 0 ? reader.VolumeLength : device.TotalBytes;
        bool legacyRotationalQuickPath = IsLegacyRotationalQuickPath(device);
        progress?.Report(new OperationProgress(
            0,
            legacyRotationalQuickPath ? "NTFS • Legacy Quick Kaynak Doğrulama" : "NTFS • Hızlı Tarama Kaynak Doğrulama",
            $"Salt-okunur kaynak açıldı • {device.DisplayName} • {RecoveryFileItem.FormatBytes(volumeLength)} • {device.BusTypeText}."));

        byte[] boot;
        bool mftLocationTrusted;
        try
        {
            boot = ReadBestAvailableNtfsBootSector(reader, volumeLength, legacyRotationalQuickPath, out mftLocationTrusted);
        }
        catch (InvalidDataException ex) when (
            IsLegacyRotationalQuickPath(device) ||
            device.PartitionScheme.Contains("Legacy Hint", StringComparison.OrdinalIgnoreCase) ||
            PortableQuickScanPolicy.IsPortableMountedSource(device))
        {
            if (fastPrelude)
            {
                return CreateDeepScanPreludeReport(
                    seedResults,
                    $"NTFS VBR doğrulanamadı ({ex.Message}); bounded MFT rescue tekrarı atlandı.");
            }

            return ScanBootlessLegacyNtfsRescue(
                device,
                reader,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                seedResults,
                scope);
        }

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        int sectorsPerCluster = boot[13];
        if (bytesPerSector <= 0 || sectorsPerCluster <= 0)
            throw new InvalidDataException("NTFS küme geometrisi okunamadı.");

        int clusterSize = checked(bytesPerSector * sectorsPerCluster);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48, 8));
        long mftMirrorLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(56, 8));
        sbyte recordCode = unchecked((sbyte)boot[64]);
        int recordSize = recordCode switch
        {
            > 0 => checked(clusterSize * recordCode),
            >= -30 and < 0 => 1 << -recordCode,
            _ => 0
        };

        // NTFS 3.x almost always uses 1024-byte FILE records. If an old VBR keeps the
        // geometry but its record-size byte is damaged, use 1024 only as a locator size;
        // every candidate still has to pass FILE/USA/attribute validation before listing.
        if (recordSize < 512 || recordSize > 64 * 1024 || (recordSize & (recordSize - 1)) != 0)
            recordSize = 1024;

        long mftOffset = -1;
        if (mftLocationTrusted && mftLcn > 0 && mftLcn <= long.MaxValue / Math.Max(1, clusterSize))
        {
            long candidateOffset = mftLcn * (long)clusterSize;
            if (candidateOffset >= 0 && candidateOffset < volumeLength)
                mftOffset = candidateOffset;
        }

        if (volumeLength <= 0)
            throw new InvalidDataException("NTFS birim uzunluğu okunamadı.");

        if (mftOffset < 0)
        {
            if (fastPrelude)
                return CreateDeepScanPreludeReport(seedResults, "NTFS $MFT konumu güvenilir değil; RAW tarama hemen başlatılacak.");

            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                -1,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                seedResults,
                "NTFS VBR mevcut ancak $MFT konumu güvenilir değil; Legacy MFT Locator devrede",
                scope: scope);
        }

        byte[] primaryMftRecordZero = ReadBestEffortBytes(reader, mftOffset, recordSize);
        bool primaryMftValid = primaryMftRecordZero.Length == recordSize &&
                               FixupFileRecord(primaryMftRecordZero.AsSpan(), bytesPerSector);

        (IReadOnlyList<DataRun> Runs, long DataSize) primaryLayout = primaryMftValid
            ? ReadMftDataRuns(primaryMftRecordZero, clusterSize)
            : (Array.Empty<DataRun>(), 0);

        (IReadOnlyList<DataRun> Runs, long DataSize) mirrorLayout = (Array.Empty<DataRun>(), 0);
        bool mirrorMftValid = false;
        if (mftMirrorLcn > 0 && mftMirrorLcn <= long.MaxValue / Math.Max(1, clusterSize))
        {
            long mirrorOffset = mftMirrorLcn * (long)clusterSize;
            byte[] mirrorRecordZero = mirrorOffset >= 0 && mirrorOffset < volumeLength
                ? ReadBestEffortBytes(reader, mirrorOffset, recordSize)
                : [];
            mirrorMftValid = mirrorRecordZero.Length == recordSize &&
                             FixupFileRecord(mirrorRecordZero.AsSpan(), bytesPerSector);
            if (mirrorMftValid)
                mirrorLayout = ReadMftDataRuns(mirrorRecordZero, clusterSize);
        }

        if (!primaryMftValid && !mirrorMftValid)
        {
            if (fastPrelude)
                return CreateDeepScanPreludeReport(seedResults, "NTFS $MFT ve $MFTMirr doğrulanamadı; bounded rescue yerine RAW tarama başlatılacak.");

            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                mftOffset,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                seedResults,
                "NTFS $MFT record 0 ve $MFTMirr okunamadı",
                scope: scope);
        }

        // Aged/damaged disks can have a readable FILE record but a damaged mapping-pairs
        // area in the primary $MFT. Prefer whichever copy exposes the larger readable run map.
        long primaryCoverage = GetRunCoverageBytes(primaryLayout.Runs, clusterSize);
        long mirrorCoverage = GetRunCoverageBytes(mirrorLayout.Runs, clusterSize);
        var selectedLayout = mirrorCoverage > primaryCoverage ? mirrorLayout : primaryLayout;
        IReadOnlyList<DataRun> mftRuns = selectedLayout.Runs;
        long mftDataSize = selectedLayout.DataSize;

        if (mftRuns.Count == 0 || mftDataSize <= 0)
        {
            if (fastPrelude)
                return CreateDeepScanPreludeReport(seedResults, "NTFS $MFT run haritası okunamadı; RAW tarama hemen başlatılacak.");

            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                mftOffset,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                seedResults,
                "NTFS $MFT mapping-pairs zinciri okunamadı",
                scope: scope);
        }

        long readableMftBytes = Math.Min(mftDataSize, GetRunCoverageBytes(mftRuns, clusterSize));
        bool mftCoverageIncomplete = readableMftBytes + recordSize < mftDataSize;
        if (readableMftBytes < recordSize)
        {
            if (fastPrelude)
                return CreateDeepScanPreludeReport(seedResults, "NTFS erişilebilir MFT alanı yetersiz; RAW tarama hemen başlatılacak.");

            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                mftOffset,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                seedResults,
                "NTFS $MFT veri alanı yalnız kısmen okunabiliyor",
                scope: scope);
        }

        var results = seedResults is { Count: > 0 }
            ? new List<RecoveryFileItem>(seedResults)
            : new List<RecoveryFileItem>();
        int reportedResultCount = results.Count;
        long totalRecords = readableMftBytes / recordSize;
        long scanRecordLimit = PortableDeepScanPolicy.GetNtfsRecordLimit(fastPrelude, totalRecords, recordSize);
        long requestedRecordIndex = surfaceResumeRequested ? scanRecordLimit : resumeRecordIndex;
        long recordIndex = Math.Clamp(requestedRecordIndex, 0, scanRecordLimit);
        int blockSize = checked(recordSize * RecordsPerBlock);
        byte[] block = new byte[blockSize];
        var priorityProcessed = new HashSet<long>();
        var directoryEntries = new Dictionary<long, NtfsForensicMetadataService.DirectoryEntry>();
        var directoryIndexRecovery = new NtfsDirectoryIndexRecoveryService();
        var usnEvidenceByRecord = new Dictionary<long, NtfsUsnJournalService.JournalDeleteEvidence>();
        NtfsForensicMetadataService forensicMetadata = NtfsForensicMetadataService.Create(
            reader,
            mftRuns,
            clusterSize,
            recordSize,
            bytesPerSector,
            device.HealthSafeScanRecommended || legacyRotationalQuickPath || fastPrelude);

        // PRO YOL V4 - canlı MFT path çözümü. Önce bellekte taranmış directory map kullanılır.
        // Parent henüz map'e girmemişse yalnız benzersiz directory MFT kayıtları cache'li ve
        // sınırlı bütçeyle okunur. Bu, 30 bin dosya için 30 bin seek değil; pratikte klasör
        // sayısı kadar küçük ek okuma demektir. Deep/Quick carving blok düzenine dokunmaz.
        int livePathRecoveredCount = 0;
        int livePathFallbackReads = 0;
        int livePathRetryCursor = 0;
        const int LivePathRetryBatchSize = 128;
        int livePathFallbackBudget = fastPrelude
            ? 64
            : device.HealthSafeScanRecommended
                ? 192
                : (legacyRotationalQuickPath || device.VisualKind is "Hdd" or "FixedDisk" or "ExternalHdd")
                    ? 768
                    : 4096;

        IReadOnlyDictionary<ulong, DateTimeOffset> usnDeleteTimes =
            new Dictionary<ulong, DateTimeOffset>();
        IReadOnlyList<NtfsUsnJournalService.JournalDeleteEvidence> usnNewest =
            Array.Empty<NtfsUsnJournalService.JournalDeleteEvidence>();

        // Yeni bir taramada USN Journal gerçek FILE_DELETE zamanını önceliklendirmek için
        // kullanılır. Hızlı Tarama'yı yıllarca birikmiş journal geçmişiyle bloke etmemek için
        // yalnız son pencere okunur; kaynak gerçekliği yine tüm erişilebilir $MFT taramasıdır.
        if (recordIndex == 0 && results.Count == 0 && !device.HealthSafeScanRecommended &&
            !legacyRotationalQuickPath && !fastPrelude && enableUsnPriority)
        {
            try
            {
                progress?.Report(new OperationProgress(
                    0,
                    "NTFS • Silinme Kayıtları Doğrulanıyor",
                    "Silinme geçmişi dosya sistemi kayıtlarıyla doğrulanıyor; ardından tüm dosya kayıtları taranacak."));

                usnDeleteTimes = NtfsUsnJournalService.ReadDeleteTimes(
                    device.RootPath,
                    cancellationToken,
                    out usnNewest);

                foreach (NtfsUsnJournalService.JournalDeleteEvidence evidence in usnNewest)
                {
                    if (!usnEvidenceByRecord.TryGetValue(evidence.RecordIndex, out NtfsUsnJournalService.JournalDeleteEvidence? existing) ||
                        existing is null ||
                        evidence.DeletedAt > existing.DeletedAt)
                    {
                        usnEvidenceByRecord[evidence.RecordIndex] = evidence;
                    }
                }

                foreach (NtfsUsnJournalService.JournalDeleteEvidence evidence in usnNewest.Take(MaxUsnPriorityCandidates))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (evidence.RecordIndex < 0 || evidence.RecordIndex >= totalRecords ||
                        priorityProcessed.Contains(evidence.RecordIndex))
                        continue;

                    RecoveryFileItem? priorityItem = TryReadDeletedRecord(
                        reader,
                        mftRuns,
                        clusterSize,
                        recordSize,
                        bytesPerSector,
                        evidence.RecordIndex,
                        usnDeleteTimes,
                        evidence);

                    if (priorityItem is not null)
                    {
                        priorityProcessed.Add(evidence.RecordIndex);
                        results.Add(priorityItem);
                    }
                    if (results.Count >= MaxResults)
                        break;
                }

                if (results.Count > reportedResultCount)
                {
                    RecoveryFileItem[] newFiles = RecoveryScanPriorityService.OrderNewestFirst(
                        results.Skip(reportedResultCount).ToArray());
                    reportedResultCount = results.Count;
                    progress?.Report(new OperationProgress(
                        0,
                        "NTFS • Silinme Kayıtları Doğrulanıyor",
                        $"Doğrulanan silinme kaydı: {usnDeleteTimes.Count:N0} • tüm dosya kayıtlarının taraması devam ediyor.",
                        0,
                        totalRecords,
                        results.Count,
                        newFiles));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // USN opsiyonel bir hız/öncelik katmanıdır. Yoksa MFT taraması devam eder.
                usnDeleteTimes = new Dictionary<ulong, DateTimeOffset>();
                usnNewest = Array.Empty<NtfsUsnJournalService.JournalDeleteEvidence>();
            }
        }
        else if (recordIndex == 0 && results.Count == 0 && fastPrelude)
        {
            progress?.Report(new OperationProgress(
                0,
                "NTFS • Deep Scan Hızlı Metadata Ön Analizi",
                "USB/SD üzerinde opsiyonel USN ve geniş stale-MFT rescue bu fazda atlandı; erişilebilir MFT kayıtları kısa ön analizden sonra tam RAW taramaya devredilecek."));
        }
        else if (recordIndex == 0 && results.Count == 0 && device.HealthSafeScanRecommended)
        {
            progress?.Report(new OperationProgress(
                0,
                "NTFS • SMART Safe Scan",
                "Disk sağlık uyarısı nedeniyle opsiyonel USN tarih önceliği atlandı; doğrudan salt-okunur $MFT kayıt taramasına geçiliyor."));
        }

        while (recordIndex < scanRecordLimit && results.Count < MaxResults)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            long logicalOffset = recordIndex * recordSize;
            int recordsThisBlock = (int)Math.Min(RecordsPerBlock, scanRecordLimit - recordIndex);
            int bytesThisBlock = checked(recordsThisBlock * recordSize);

            Array.Clear(block, 0, bytesThisBlock);
            int read = ReadVirtualMft(
                reader,
                mftRuns,
                clusterSize,
                logicalOffset,
                block.AsSpan(0, bytesThisBlock));

            if (read <= 0)
                break;

            int completeRecords = read / recordSize;
            for (int localRecord = 0; localRecord < completeRecords; localRecord++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                long currentRecordIndex = recordIndex + localRecord;
                if (priorityProcessed.Contains(currentRecordIndex))
                    continue;

                Span<byte> record = block.AsSpan(localRecord * recordSize, recordSize);
                if (!FixupFileRecord(record, bytesPerSector))
                    continue;

                // Capture NTFS directory-index metadata while the FILE record is already in
                // memory. Resident $I30 costs zero extra I/O; non-resident allocation runs are
                // only scheduled for the dedicated path-recovery phase after the MFT pass.
                directoryIndexRecovery.CaptureDirectoryRecord(record, currentRecordIndex, clusterSize);

                if (NtfsForensicMetadataService.TryParseDirectoryEntry(
                        record,
                        currentRecordIndex,
                        out NtfsForensicMetadataService.DirectoryEntry? directoryEntry) &&
                    directoryEntry is not null)
                {
                    directoryEntries[currentRecordIndex] = directoryEntry;
                }

                usnEvidenceByRecord.TryGetValue(
                    currentRecordIndex,
                    out NtfsUsnJournalService.JournalDeleteEvidence? usnEvidence);

                RecoveryFileItem? item = ParseDeletedFileRecord(
                    reader,
                    record,
                    currentRecordIndex,
                    clusterSize,
                    usnDeleteTimes,
                    includeInUse: includeExistingFiles,
                    mftRuns: mftRuns,
                    recordSize: recordSize,
                    bytesPerSector: bytesPerSector,
                    usnEvidence: usnEvidence);

                if (item is not null)
                {
                    // YOL'u scan sonuna bırakma: sonuç UI'ye gitmeden önce mevcut MFT
                    // directory map ile çöz. Çoğu dosyada bu sıfır ek disk I/O'dur.
                    if (item.SourceKind is RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList &&
                        item.NtfsParentReference != 0 &&
                        string.IsNullOrWhiteSpace(item.RecoveredOriginalPath))
                    {
                        string livePath = forensicMetadata.ResolveOriginalPath(
                            item,
                            directoryEntries,
                            cancellationToken,
                            allowDiskFallback: false,
                            indexEntries: directoryIndexRecovery.Evidence);

                        if (string.IsNullOrWhiteSpace(livePath) &&
                            livePathFallbackReads < livePathFallbackBudget)
                        {
                            int beforeDirectoryCount = directoryEntries.Count;
                            livePath = forensicMetadata.ResolveOriginalPath(
                                item,
                                directoryEntries,
                                cancellationToken,
                                allowDiskFallback: true,
                                maxDiskFallbackReads: livePathFallbackBudget - livePathFallbackReads,
                                indexEntries: directoryIndexRecovery.Evidence);
                            livePathFallbackReads += Math.Max(0, directoryEntries.Count - beforeDirectoryCount);
                        }

                        if (!string.IsNullOrWhiteSpace(livePath))
                        {
                            item.RecoveredOriginalPath = livePath;
                            livePathRecoveredCount++;
                        }
                    }

                    results.Add(item);
                }

                if (results.Count >= MaxResults)
                    break;
            }

            recordIndex += Math.Max(completeRecords, 1);

            long progressRecordInterval = fastPrelude ? RecordsPerBlock : RecordsPerBlock * 4L;
            if (recordIndex % progressRecordInterval == 0 || recordIndex >= scanRecordLimit)
            {
                // USN öncelik fazında veya parent klasörü daha sonra taranan sonuçlar için
                // küçük, yalnız-bellek retry turu. Disk I/O yapmaz; her progress tick'inde
                // en fazla 128 kayıt bakılır ve böylece YOL scan sürerken kademeli oluşur.
                int retryCount = Math.Min(LivePathRetryBatchSize, results.Count);
                for (int retry = 0; retry < retryCount && results.Count > 0; retry++)
                {
                    if (livePathRetryCursor >= results.Count)
                        livePathRetryCursor = 0;

                    RecoveryFileItem retryItem = results[livePathRetryCursor++];
                    if (retryItem.SourceKind is not (RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList) ||
                        retryItem.NtfsParentReference == 0 ||
                        !string.IsNullOrWhiteSpace(retryItem.RecoveredOriginalPath))
                    {
                        continue;
                    }

                    string retryPath = forensicMetadata.ResolveOriginalPath(
                        retryItem,
                        directoryEntries,
                        cancellationToken,
                        allowDiskFallback: false,
                        indexEntries: directoryIndexRecovery.Evidence);
                    if (!string.IsNullOrWhiteSpace(retryPath))
                    {
                        retryItem.RecoveredOriginalPath = retryPath;
                        livePathRecoveredCount++;
                    }
                }

                double percent = scanRecordLimit <= 0 ? 0 : recordIndex * 100d / scanRecordLimit;
                RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                    ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                    : null;
                reportedResultCount = results.Count;

                progress?.Report(new OperationProgress(
                    Math.Clamp(percent, 0d, 100d),
                    includeExistingFiles ? "NTFS • Dosya Kataloğu + Silinmiş Kayıtlar" : "NTFS • Silinmiş Dosya Analizi",
                    includeExistingFiles
                        ? $"Dosya kayıtları: {recordIndex:N0} / {scanRecordLimit:N0} • mevcut ve silinmiş kayıtlar doğrulanıyor • yol: {livePathRecoveredCount:N0}."
                        : $"Dosya kayıtları: {recordIndex:N0} / {scanRecordLimit:N0} • zaman bilgisi bulunan kayıtlar öncelikli işleniyor • yol: {livePathRecoveredCount:N0}.",
                    recordIndex,
                    scanRecordLimit,
                    results.Count,
                    newFiles));
            }
        }

        // PRO YOL V6 - EaseUS tarzı tarihsel path katmanı. MFT parent zincirine ek olarak
        // directory INDEX_ROOT / INDEX_ALLOCATION ($I30) ve index slack okunur. Bu özellikle
        // eski HDD'de parent MFT slotu yeniden kullanılmış klasör adlarını geri getirir.
        int i30RecoveredEvidence = 0;
        if (!device.HealthSafeScanRecommended && !fastPrelude)
        {
            try
            {
                progress?.Report(new OperationProgress(
                    98.5d,
                    "NTFS • Eski Klasör Dizini ($I30)",
                    "Silinmiş/eski klasör adları NTFS directory index ve index slack üzerinden salt-okunur çözümleniyor."));

                i30RecoveredEvidence = directoryIndexRecovery.ReadCapturedIndexAllocations(
                    reader,
                    clusterSize,
                    bytesPerSector,
                    cancellationToken,
                    progress);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // $I30 historical path recovery is enrichment only; the scan stays authoritative.
            }
        }

        int pathRecoveredCount = livePathRecoveredCount;

        // PRO YOL V6 final doğrulama: canlı fazda çözülemeyen kayıtları tamamlanan MFT +
        // $I30 evidence ile yeniden çöz. Ardından yalnız kalan parent zincirleri için forensic
        // fallback ve USN historical evidence kullanılır.
        // tamamlanan directory map ile tekrar çöz. Ardından yalnız kalan parent zincirleri
        // için kontrollü forensic fallback uygula.
        foreach (RecoveryFileItem pathItem in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pathItem.SourceKind is not (RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList))
                continue;

            string memoryPath = forensicMetadata.ResolveOriginalPath(
                pathItem,
                directoryEntries,
                cancellationToken,
                allowDiskFallback: false,
                indexEntries: directoryIndexRecovery.Evidence);
            if (!string.IsNullOrWhiteSpace(memoryPath) &&
                IsRicherRecoveredPath(memoryPath, pathItem.RecoveredOriginalPath))
            {
                bool wasEmpty = string.IsNullOrWhiteSpace(pathItem.RecoveredOriginalPath);
                pathItem.RecoveredOriginalPath = memoryPath;
                if (wasEmpty)
                    pathRecoveredCount++;
            }
        }

        int initialDirectoryCacheCount = directoryEntries.Count;
        int pathDirectoryReadBudget = fastPrelude
            ? 384
            : legacyRotationalQuickPath || device.HealthSafeScanRecommended
                ? 1536
                : 4096;

        foreach (RecoveryFileItem pathItem in RecoveryScanPriorityService.OrderNewestFirst(results))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pathItem.SourceKind is not (RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList) ||
                !string.IsNullOrWhiteSpace(pathItem.RecoveredOriginalPath) ||
                pathItem.NtfsParentReference == 0)
            {
                continue;
            }

            bool allowFallback = directoryEntries.Count - initialDirectoryCacheCount < pathDirectoryReadBudget;
            if (!allowFallback)
                break;

            string originalPath = forensicMetadata.ResolveOriginalPath(
                pathItem,
                directoryEntries,
                cancellationToken,
                allowDiskFallback: true,
                indexEntries: directoryIndexRecovery.Evidence);
            if (!string.IsNullOrWhiteSpace(originalPath))
            {
                pathItem.RecoveredOriginalPath = originalPath;
                pathRecoveredCount++;
            }
        }

        int pathFallbackDirectoryReads = Math.Max(0, directoryEntries.Count - initialDirectoryCacheCount);

        // PRO YOL V5 - Eski klasör zinciri kurtarma. Eski HDD'lerde parent MFT slotu
        // sonradan yeniden kullanılmış olsa bile $UsnJrnl exact 64-bit file reference, eski
        // klasör adı ve parent reference bilgisini tutabilir. Ana MFT/carving okuma düzenine
        // dokunmamak için bu yalnız path zenginleştirme fazında, bounded journal penceresiyle
        // çalışır. Journal okuması lineerdir; dosya başına random disk seek oluşturmaz.
        int historicalPathRecoveredCount = 0;
        int historicalPathEvidenceCount = 0;
        int historicalPathJournalRecords = 0;
        IReadOnlyDictionary<ulong, NtfsUsnJournalService.JournalPathEvidence> historicalPathHistory =
            new Dictionary<ulong, NtfsUsnJournalService.JournalPathEvidence>();
        int unresolvedPathCandidates = results.Count(item =>
            (item.SourceKind is RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList) &&
            item.NtfsParentReference != 0 &&
            string.IsNullOrWhiteSpace(item.RecoveredOriginalPath));

        if (!device.HealthSafeScanRecommended &&
            (unresolvedPathCandidates > 0 || directoryEntries.Count > 0 || directoryIndexRecovery.EvidenceCount > 0))
        {
            try
            {
                bool pathRotationalMedia = legacyRotationalQuickPath ||
                                           device.VisualKind is "Hdd" or "FixedDisk" or "ExternalHdd";
                long pathHistoryWindowBytes = fastPrelude
                    ? 32L * 1024 * 1024
                    : pathRotationalMedia
                        ? 256L * 1024 * 1024
                        : 64L * 1024 * 1024;

                progress?.Report(new OperationProgress(
                    99d,
                    "NTFS • Eski Klasör Yolları",
                    $"Tarihsel klasör kataloğu hazırlanıyor • çözülemeyen dosya {unresolvedPathCandidates:N0} • silinmiş/yeniden adlandırılmış klasör kayıtları $UsnJrnl üzerinden doğrulanıyor."));

                historicalPathHistory = NtfsUsnJournalService.ReadPathHistory(
                        device.RootPath,
                        cancellationToken,
                        pathHistoryWindowBytes,
                        out historicalPathJournalRecords);
                historicalPathEvidenceCount = historicalPathHistory.Count;

                if (historicalPathHistory.Count > 0)
                {
                    foreach (RecoveryFileItem pathItem in results)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (pathItem.SourceKind is not (RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList) ||
                            pathItem.NtfsParentReference == 0)
                        {
                            continue;
                        }

                        string historicalPath = forensicMetadata.ResolveOriginalPath(
                            pathItem,
                            directoryEntries,
                            cancellationToken,
                            allowDiskFallback: false,
                            historicalEntries: historicalPathHistory,
                            indexEntries: directoryIndexRecovery.Evidence);

                        if (!string.IsNullOrWhiteSpace(historicalPath) &&
                            IsRicherRecoveredPath(historicalPath, pathItem.RecoveredOriginalPath))
                        {
                            bool wasEmpty = string.IsNullOrWhiteSpace(pathItem.RecoveredOriginalPath);
                            pathItem.RecoveredOriginalPath = historicalPath;
                            historicalPathRecoveredCount++;
                            if (wasEmpty)
                                pathRecoveredCount++;
                        }
                    }

                    progress?.Report(new OperationProgress(
                        99.5d,
                        "NTFS • Eski Klasör Yolları",
                        $"Tarihsel yol kanıtı: {historicalPathEvidenceCount:N0} kayıt • eski/tam yol geliştirilen: {historicalPathRecoveredCount:N0}."));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Historical path recovery is enrichment only. Main scan remains authoritative.
            }
        }

        // PRO YOL V7 - YOL artık dosya sonucundan türetilmez. MFT'de bulunan tüm aktif/silinmiş
        // dizin kayıtları + $I30 + tarihsel USN parent zinciri ayrı bir klasör kataloğu üretir.
        // Böylece içinde şu an eşleşmiş dosya olmasa bile diske daha önce yazılmış klasörler
        // EaseUS benzeri sol YOL ağacında görünür. Bu aşama yalnız RAM'deki metadata'yı kullanır.
        IReadOnlyList<string> historicalFolderPaths = NtfsHistoricalPathCatalogService.Build(
            directoryEntries,
            directoryIndexRecovery.Evidence,
            historicalPathHistory);

        if (historicalFolderPaths.Count > 0)
        {
            progress?.Report(new OperationProgress(
                99.65d,
                "NTFS • Tarihsel Klasör Kataloğu",
                $"Diske yazılmış klasör geçmişi: {historicalFolderPaths.Count:N0} klasör • dosya eşleşmesinden bağımsız YOL ağacı hazırlanıyor.",
                FoundCount: results.Count,
                NewHistoricalFolders: historicalFolderPaths));
        }

        int bitmapRiskCount = 0;
        int bitmapProbedCount = 0;
        bool rotationalMedia = device.VisualKind is "Hdd" or "FixedDisk" or "ExternalHdd";
        int bitmapProbeBudget = fastPrelude
            ? 64
            : device.HealthSafeScanRecommended || legacyRotationalQuickPath
                ? MaxBitmapForensicCandidatesSafeScan
                : rotationalMedia
                    ? MaxBitmapForensicCandidatesRotational
                    : MaxBitmapForensicCandidates;
        int forensicEnrichmentBudget = fastPrelude ? 256 : legacyRotationalQuickPath ? 512 : int.MaxValue;
        int forensicEnrichedCount = 0;

        foreach (RecoveryFileItem item in RecoveryScanPriorityService.OrderNewestFirst(results))
        {
            if (forensicEnrichedCount >= forensicEnrichmentBudget)
                break;
            forensicEnrichedCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (item.SourceKind is not (RecoverySourceKind.NtfsResident or RecoverySourceKind.NtfsRunList))
                continue;

            bool hadForensicEvidence = !string.IsNullOrWhiteSpace(item.NtfsForensicEvidence);
            var evidence = new List<string>(4);
            if (item.IsExistingFile)
            {
                RecoveryConfidenceService.ApplyMetadataConfidence(item);
                continue;
            }

            if (item.DeletedAt.HasValue)
                evidence.Add("$UsnJrnl FILE_DELETE");

            if (bitmapProbedCount < bitmapProbeBudget && item.DataRuns is { Count: > 0 })
            {
                NtfsForensicMetadataService.AllocationEvidence allocation = forensicMetadata.ProbeAllocation(
                    item.DataRuns,
                    cancellationToken);
                if (allocation.HasEvidence)
                {
                    bitmapProbedCount++;
                    evidence.Add(allocation.Summary);
                    if (allocation.AllocatedRatio >= 0.25d)
                        bitmapRiskCount++;
                }
            }

            if (forensicMetadata.LogFile.Available)
                evidence.Add(forensicMetadata.LogFile.Summary);

            if (evidence.Count > 0)
            {
                item.NtfsForensicEvidence = string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase));
                if (!item.SourceText.Contains("Forensic V2", StringComparison.OrdinalIgnoreCase))
                    item.SourceText = $"{item.SourceText} • Forensic V2";
            }

            RecoveryConfidenceService.ApplyMetadataConfidence(item);
            if (!hadForensicEvidence && item.RecoveryConfidenceScore >= 0 && item.NtfsForensicEvidence is not null)
            {
                int adjusted = item.RecoveryConfidenceScore;
                if (item.NtfsForensicEvidence.Contains("yeniden tahsisli • üzerine yazılma riski yüksek", StringComparison.OrdinalIgnoreCase))
                    adjusted -= 28;
                else if (item.NtfsForensicEvidence.Contains("kısmi üzerine yazılma riski", StringComparison.OrdinalIgnoreCase))
                    adjusted -= 14;
                else if (item.NtfsForensicEvidence.Contains("yeniden kullanım izi yok", StringComparison.OrdinalIgnoreCase))
                    adjusted += 3;

                adjusted = Math.Clamp(adjusted, 0, 100);
                string grade = adjusted switch
                {
                    >= 90 => "Cok Yuksek",
                    >= 78 => "Yuksek",
                    >= 65 => "Iyi",
                    >= 55 => "Orta",
                    >= 40 => "Dusuk",
                    _ => "Zayif"
                };
                RecoveryConfidenceService.Apply(
                    item,
                    new RecoveryConfidenceResult(
                        adjusted,
                        grade,
                        $"{item.RecoveryConfidenceSummary} • {item.NtfsForensicEvidence}",
                        RejectAsFalsePositive: false));
            }
        }

        List<RecoveryFileItem> ordered = RecoveryScanPriorityService.OrderNewestFirst(results).ToList();
        string legacyQuickNote = legacyRotationalQuickPath
            ? $" • Legacy SATA hızlı yol: USN silinme önceliği/$LogFile ağır ön okumaları atlandı; tarihsel YOL journal okuması scan sonu bounded çalıştı, forensic zenginleştirme ilk {forensicEnrichedCount:N0} sonuçla sınırlandı"
            : string.Empty;
        string forensicSummary = $"Forensic V6 • yol {pathRecoveredCount:N0} • $I30 kanıt {directoryIndexRecovery.EvidenceCount:N0} (+{i30RecoveredEvidence:N0}) • tarihsel yol {historicalPathRecoveredCount:N0}/{historicalPathEvidenceCount:N0} USN kanıt (journal kayıt {historicalPathJournalRecords:N0}) • klasör cache fallback {pathFallbackDirectoryReads:N0} • $Bitmap {bitmapProbedCount:N0} örneklenmiş dosya • yeniden tahsis riski {bitmapRiskCount:N0} • {forensicMetadata.LogFile.Summary}{legacyQuickNote}";
        int existingFileCount = results.Count(item => item.IsExistingFile);
        int deletedFileCount = results.Count - existingFileCount;
        string resultSummary = includeExistingFiles
            ? $"{existingFileCount:N0} mevcut + {deletedFileCount:N0} silinmiş dosya"
            : $"{deletedFileCount:N0} silinmiş dosya";
        string summary = results.Count >= MaxResults
            ? $"NTFS kayıt tarama kapasitesine ulaşıldı • {resultSummary} doğrulandı."
            : mftCoverageIncomplete
                ? $"NTFS erişilebilir $MFT kayıtları tarandı • {resultSummary} doğrulandı; parçalı/hasarlı $MFT için RAW fallback gerekli."
                : $"NTFS kayıt analizi tamamlandı • {resultSummary} doğrulandı; mevcut silinme zamanları sonuçlara uygulandı.";
        summary = $"{summary} {forensicSummary}";
        if (fastPrelude)
        {
            summary += $" • Deep Scan hızlı metadata ön analizi: {recordIndex:N0}/{totalRecords:N0} MFT kaydı işlendi; geniş rescue ve ileri forensic tekrarları tam RAW aşamasına bırakıldı.";
        }

        if (!includeExistingFiles && mftCoverageIncomplete && results.Count < MaxResults && !fastPrelude)
        {
            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                mftOffset,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                ordered,
                "NTFS $MFT run haritası kısmi; erişilebilir MFT bölgesi hızlı kurtarma ile tamamlanıyor",
                scope: scope);
        }

        // USB/SD üzerinde yeni/yeniden biçimlendirilmiş NTFS metadata'sı çok küçük olabilir.
        // Normal $MFT taraması 0 silinmiş kayıtla anında bittiğinde, tüm diski RAW taramak yerine
        // mevcut MFT çevresinde en fazla 1 GB'lık bounded eski FILE-record penceresi taranır.
        // Bu yol yalnız mounted portable kaynaklarda çalışır; C:/D:/E: sabit disk davranışı değişmez.
        if (!includeExistingFiles && ShouldRunPortableStaleMftRescue(device,
                LegacySurfaceQuickScanService.CountMatchingScope(ordered, scope), allowExtendedRescue))
        {
            return ScanMftRescue(
                device,
                reader,
                bytesPerSector,
                clusterSize,
                recordSize,
                mftOffset,
                volumeLength,
                progress,
                pauseGate,
                cancellationToken,
                ordered,
                "USB/SD NTFS aktif $MFT içinde silinmiş kayıt bulunamadı; bounded stale-MFT metadata rescue",
                requireValidatedDataHeader: false,
                scope: scope);
        }

        // Eski dönel disklerde format/yeniden kullanım sonrası silinmiş MFT kayıtları tamamen
        // yok olabilir. Bu durumda Quick Scan'in 0 sonuçla bitmesi yerine yalnız tek geçişli
        // Quick Surface/Free-Space aşaması çalışır. Deep Scan'in 4-pass, fragment graph ve
        // reconstruction motorları kesinlikle çağrılmaz. $Bitmap sağlamsa sadece boş cluster'lar
        // okunur; böylece boşaltılmış eski SATA disklerde gerçek silinmiş içerik de Quick'te görünür.
        if (legacyRotationalQuickPath && !includeExistingFiles)
        {
            ScanReport surfaceReport = LegacySurfaceQuickScanService.Scan(
                device,
                reader,
                volumeLength,
                mftRuns,
                clusterSize,
                recordSize,
                bytesPerSector,
                progress,
                pauseGate,
                cancellationToken,
                ordered,
                scope,
                summary,
                surfaceResumeRequested ? resumeCheckpoint : null);
            return surfaceReport with { HistoricalFolders = historicalFolderPaths };
        }

        return new ScanReport(ordered, summary, ScanMode.Quick, UsedFallback: false)
        {
            HistoricalFolders = historicalFolderPaths
        };
    }

    private static bool IsQuickSurfaceCheckpoint(RecoveryScanCheckpoint? checkpoint)
    {
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.Stage))
            return false;

        return checkpoint.Stage.StartsWith("quick-surface", StringComparison.OrdinalIgnoreCase) ||
               checkpoint.Stage.StartsWith("quick-free-space", StringComparison.OrdinalIgnoreCase);
    }

    private static ScanReport CreateDeepScanPreludeReport(
        IReadOnlyList<RecoveryFileItem>? seedResults,
        string reason)
    {
        IReadOnlyList<RecoveryFileItem> preserved = seedResults ?? Array.Empty<RecoveryFileItem>();
        return new ScanReport(
            preserved,
            $"NTFS Deep Scan hızlı metadata ön analizi tamamlandı • {reason} Tam medya RAW carving ile taranacak.",
            ScanMode.Quick,
            UsedFallback: false);
    }

    internal static bool ShouldRunPortableStaleMftRescue(
        StorageDeviceInfo device,
        int deletedResultCount,
        bool allowExtendedRescue = true) =>
        allowExtendedRescue &&
        deletedResultCount == 0 &&
        PortableQuickScanPolicy.IsPortableMountedSource(device);

    private static ScanReport ScanBootlessLegacyNtfsRescue(
        StorageDeviceInfo device,
        RawDeviceReader reader,
        long volumeLength,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        IReadOnlyList<RecoveryFileItem>? seedResults,
        DeepScanTarget scope)
    {
        if (volumeLength <= 0)
            throw new InvalidDataException("Legacy NTFS Rescue için birim uzunluğu okunamadı.");

        int bytesPerSector = reader.SectorSize is 512 or 1024 or 2048 or 4096
            ? reader.SectorSize
            : 512;
        int bestRecordSize = 1024;
        int bestScore = 0;
        long bestStart = -1;

        foreach (int recordSize in new[] { 1024, 4096 })
        {
            long start = LocateLikelyMftRegion(
                reader,
                bytesPerSector,
                recordSize,
                volumeLength,
                -1,
                device.HealthSafeScanRecommended,
                progress,
                pauseGate,
                cancellationToken,
                out int score);
            if (score <= bestScore || start < 0)
                continue;
            bestScore = score;
            bestRecordSize = recordSize;
            bestStart = start;
        }

        if (bestStart < 0 || bestScore < 4)
            throw new InvalidDataException("Legacy NTFS Rescue: VBR yok ve doğrulanmış MFT FILE yoğunluğu bulunamadı. Metadata fiziksel olarak kayıp olabilir.");

        // Windows NTFS default cluster size for this legacy volume class is 4 KiB. Because
        // the VBR is absent, non-resident files are accepted only when their first recovered
        // data run also matches the file's real header; resident records remain geometry-safe.
        const int legacyClusterSize = 4096;
        progress?.Report(new OperationProgress(
            0,
            "NTFS • VBR'siz Legacy Hızlı Tarama",
            $"NTFS metadata yapısı MFT FILE yoğunluğuyla bulundu • skor {bestScore:N0}. Yalnız yapısal ve veri-header doğrulamasını geçen kayıtlar listelenecek."));

        return ScanMftRescue(
            device,
            reader,
            bytesPerSector,
            legacyClusterSize,
            bestRecordSize,
            bestStart,
            volumeLength,
            progress,
            pauseGate,
            cancellationToken,
            seedResults,
            "NTFS VBR kopyaları okunamadı; MBR + FILE yoğunluğu ile VBR'siz Legacy Metadata Rescue",
            requireValidatedDataHeader: true,
            scope: scope);
    }

    private static long LocateLikelyMftRegion(
        RawDeviceReader reader,
        int bytesPerSector,
        int recordSize,
        long volumeLength,
        long expectedMftOffset,
        bool safeScan,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        out int bestScore)
    {
        bestScore = 0;
        if (volumeLength <= recordSize || recordSize < 512)
            return -1;

        int sampleBytes = safeScan ? 128 * 1024 : 256 * 1024;
        sampleBytes = Math.Max(recordSize * 8, sampleBytes - sampleBytes % recordSize);
        var offsets = new SortedSet<long>();

        void AddOffset(long value)
        {
            if (value < 0 || value >= volumeLength)
                return;
            long aligned = value - value % recordSize;
            if (aligned >= 0 && aligned < volumeLength)
                offsets.Add(aligned);
        }

        if (expectedMftOffset >= 0)
        {
            AddOffset(expectedMftOffset);
            AddOffset(expectedMftOffset - 64L * 1024 * 1024);
            AddOffset(expectedMftOffset + 64L * 1024 * 1024);
        }

        // Classic NTFS installations place $MFT in the early part of the volume. Sample
        // that area densely without turning Quick Scan into a full RAW scan.
        long earlyLimit = Math.Min(volumeLength, 16L * 1024 * 1024 * 1024);
        long earlyStride = safeScan ? 64L * 1024 * 1024 : 32L * 1024 * 1024;
        for (long offset = 0; offset < earlyLimit; offset += earlyStride)
            AddOffset(offset);

        // Also sample the whole volume sparsely. This catches migrated/fragmented MFT runs
        // on old disks while keeping total locator I/O bounded to roughly 64-128 MiB.
        long wholeStride = Math.Max(64L * 1024 * 1024, volumeLength / 256);
        for (long offset = 0; offset < volumeLength; offset += wholeStride)
            AddOffset(offset);

        byte[] sample = new byte[sampleBytes];
        long bestOffset = -1;
        long sampledBytes = 0;
        int sampleIndex = 0;
        int totalSamples = offsets.Count;

        foreach (long offset in offsets)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(sample.Length, volumeLength - offset);
            request -= request % recordSize;
            if (request < recordSize)
                continue;

            Array.Clear(sample, 0, request);
            int read = reader.ReadBestEffort(offset, sample.AsSpan(0, request), out _);
            sampledBytes += Math.Max(0, read);
            int complete = Math.Min(request, Math.Max(0, read));
            complete -= complete % recordSize;
            int score = ScoreMftSample(sample.AsSpan(0, complete), bytesPerSector, recordSize);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }

            sampleIndex++;
            if (sampleIndex == totalSamples || sampleIndex % 32 == 0)
            {
                progress?.Report(new OperationProgress(
                    totalSamples <= 0 ? 0 : Math.Clamp(sampleIndex * 100d / totalSamples, 0d, 100d),
                    "NTFS • Legacy MFT Locator",
                    $"Eski disk metadata bölgeleri örnekleniyor • {sampleIndex:N0}/{totalSamples:N0} • okunan {RecoveryFileItem.FormatBytes(sampledBytes)} • en iyi FILE skoru {bestScore:N0}."));
            }
        }

        // A single accidental FILE signature is not enough. Four structurally plausible
        // records in one small window is already strong MFT density evidence.
        if (bestOffset < 0 || bestScore < 4)
            return -1;

        long backtrack = 512L * 1024 * 1024;
        long start = Math.Max(0, bestOffset - backtrack);
        start -= start % recordSize;
        return start;
    }

    private static int ScoreMftSample(ReadOnlySpan<byte> sample, int bytesPerSector, int recordSize)
    {
        if (sample.Length < recordSize)
            return 0;

        int score = 0;
        for (int offset = 0; offset + recordSize <= sample.Length; offset += recordSize)
        {
            ReadOnlySpan<byte> record = sample.Slice(offset, recordSize);
            if (!LooksLikeMftRecordHeader(record, bytesPerSector, recordSize))
                continue;
            score++;
        }
        return score;
    }

    private static bool LooksLikeMftRecordHeader(ReadOnlySpan<byte> record, int bytesPerSector, int recordSize)
    {
        if (record.Length < Math.Min(recordSize, 64) ||
            record[0] != (byte)'F' || record[1] != (byte)'I' ||
            record[2] != (byte)'L' || record[3] != (byte)'E')
            return false;

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        uint usedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(24, 4));
        uint allocatedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(28, 4));

        int expectedUsaCount = recordSize / Math.Max(512, bytesPerSector) + 1;
        if (usaOffset < 0x28 || usaOffset + usaCount * 2 > recordSize ||
            usaCount < 2 || Math.Abs(usaCount - expectedUsaCount) > 1)
            return false;
        if (firstAttributeOffset < 0x30 || firstAttributeOffset >= recordSize)
            return false;
        if (usedSize < firstAttributeOffset || usedSize > recordSize)
            return false;
        if (allocatedSize < usedSize || allocatedSize > recordSize)
            return false;
        return true;
    }

    private static ScanReport ScanMftRescue(
        StorageDeviceInfo device,
        RawDeviceReader reader,
        int bytesPerSector,
        int clusterSize,
        int recordSize,
        long mftOffset,
        long volumeLength,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        IReadOnlyList<RecoveryFileItem>? seedResults,
        string reason,
        bool requireValidatedDataHeader = false,
        DeepScanTarget scope = DeepScanTarget.All)
    {
        if (recordSize < 512 || recordSize > 64 * 1024 ||
            bytesPerSector < 512 || clusterSize < bytesPerSector || volumeLength <= 0)
        {
            throw new InvalidDataException($"{reason}. MFT Rescue geometrisi doğrulanamadı.");
        }

        long expectedMftOffset = mftOffset >= 0 && mftOffset < volumeLength ? mftOffset : -1;
        long locatedMftOffset = LocateLikelyMftRegion(
            reader,
            bytesPerSector,
            recordSize,
            volumeLength,
            expectedMftOffset,
            device.HealthSafeScanRecommended,
            progress,
            pauseGate,
            cancellationToken,
            out int locatorScore);
        if (locatedMftOffset >= 0)
            mftOffset = locatedMftOffset;
        else if (expectedMftOffset >= 0)
            mftOffset = expectedMftOffset;
        else
            throw new InvalidDataException($"{reason}. Legacy MFT Locator doğrulanmış FILE kayıt bölgesi bulamadı.");

        var results = seedResults is { Count: > 0 }
            ? new List<RecoveryFileItem>(seedResults)
            : new List<RecoveryFileItem>();
        var seenRecords = new HashSet<long>(results
            .Where(item => item.NtfsRecordIndex >= 0)
            .Select(item => item.NtfsRecordIndex));

        long available = volumeLength - mftOffset;
        long proportionalTarget = Math.Max(MinimumMftRescueScanBytes, volumeLength / 50);
        long rescueLength = Math.Min(available, Math.Min(MaximumMftRescueScanBytes, proportionalTarget));
        rescueLength -= rescueLength % recordSize;
        if (rescueLength < recordSize)
            throw new InvalidDataException($"{reason}. MFT Rescue için okunabilir metadata bölgesi yok.");

        int requestedBlock = device.HealthSafeScanRecommended
            ? SafeMftRescueBlockBytes
            : NormalMftRescueBlockBytes;
        int blockSize = Math.Max(recordSize, requestedBlock - requestedBlock % recordSize);
        byte[] block = new byte[blockSize];
        long scanned = 0;
        long unreadableTotal = 0;
        int reportedResultCount = results.Count;

        string locatorDetail = locatorScore > 0
            ? $" Legacy MFT Locator skoru {locatorScore:N0}; metadata başlangıcı 0x{mftOffset:X}."
            : string.Empty;
        progress?.Report(new OperationProgress(
            0,
            "NTFS • Legacy MFT Rescue Hızlı Tarama",
            $"{reason}.{locatorDetail} Derin RAW taramaya geçmeden erişilebilir MFT metadata bölgesi salt-okunur taranıyor.",
            0,
            rescueLength,
            results.Count));

        while (scanned < rescueLength && results.Count < MaxResults)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(blockSize, rescueLength - scanned);
            request -= request % recordSize;
            if (request < recordSize)
                break;

            Array.Clear(block, 0, request);
            long absoluteOffset = mftOffset + scanned;
            int read = reader.ReadBestEffort(
                absoluteOffset,
                block.AsSpan(0, request),
                out long unreadableBytes);
            unreadableTotal += unreadableBytes;

            int completeBytes = Math.Min(request, Math.Max(0, read));
            completeBytes -= completeBytes % recordSize;
            int records = completeBytes / recordSize;

            for (int index = 0; index < records && results.Count < MaxResults; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int recordOffset = index * recordSize;
                Span<byte> record = block.AsSpan(recordOffset, recordSize);
                if (record[0] != (byte)'F' || record[1] != (byte)'I' ||
                    record[2] != (byte)'L' || record[3] != (byte)'E')
                {
                    continue;
                }

                long positionalIndex = (scanned + recordOffset) / recordSize;
                uint embeddedRecordNumber = record.Length >= 48
                    ? BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(44, 4))
                    : 0;
                long recordIndex = embeddedRecordNumber > 0 ? embeddedRecordNumber : positionalIndex;
                if (!seenRecords.Add(recordIndex) || !FixupFileRecord(record, bytesPerSector))
                    continue;

                RecoveryFileItem? item = ParseDeletedFileRecord(
                    reader,
                    record,
                    recordIndex,
                    clusterSize,
                    usnDeleteTimes: null,
                    includeInUse: false,
                    mftRuns: null,
                    recordSize: recordSize,
                    bytesPerSector: bytesPerSector);

                if (item is null)
                    continue;
                if (requireValidatedDataHeader &&
                    item.SourceKind == RecoverySourceKind.NtfsRunList &&
                    string.Equals(item.RecoveryState, "Zayıf", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!item.SourceText.Contains("MFT Rescue", StringComparison.OrdinalIgnoreCase))
                    item.SourceText = $"{item.SourceText} • MFT Rescue";
                RecoveryConfidenceService.ApplyMetadataConfidence(item);
                results.Add(item);
            }

            scanned += request;
            if (scanned == rescueLength || scanned % (64L * 1024 * 1024) < request)
            {
                RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                    ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                    : null;
                reportedResultCount = results.Count;
                progress?.Report(new OperationProgress(
                    Math.Clamp(scanned * 100d / rescueLength, 0d, 100d),
                    "NTFS • Legacy MFT Rescue Hızlı Tarama",
                    $"MFT metadata: {RecoveryFileItem.FormatBytes(scanned)} / {RecoveryFileItem.FormatBytes(rescueLength)} • bulunan {results.Count:N0} • okunamayan {RecoveryFileItem.FormatBytes(unreadableTotal)}",
                    scanned,
                    rescueLength,
                    results.Count,
                    newFiles));
            }
        }

        List<RecoveryFileItem> ordered = RecoveryScanPriorityService.OrderNewestFirst(results).ToList();
        string summary = $"NTFS Legacy MFT Rescue Hızlı Tarama tamamlandı • {ordered.Count:N0} silinmiş dosya doğrulandı • en fazla 1 GB metadata penceresi • {RecoveryFileItem.FormatBytes(scanned)} tarandı • okunamayan {RecoveryFileItem.FormatBytes(unreadableTotal)}. Tam disk RAW/Derin tarama çalıştırılmadı.";

        if (IsLegacyRotationalQuickPath(device))
        {
            return LegacySurfaceQuickScanService.Scan(
                device,
                reader,
                volumeLength,
                mftRuns: null,
                clusterSize,
                recordSize,
                bytesPerSector,
                progress,
                pauseGate,
                cancellationToken,
                ordered,
                scope,
                summary);
        }

        return new ScanReport(ordered, summary, ScanMode.Quick, UsedFallback: false);
    }

    private static RecoveryFileItem? TryReadDeletedRecord(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        long recordIndex,
        IReadOnlyDictionary<ulong, DateTimeOffset> usnDeleteTimes,
        NtfsUsnJournalService.JournalDeleteEvidence? usnEvidence = null)
    {
        byte[] record = new byte[recordSize];
        int read = ReadVirtualMft(
            reader,
            mftRuns,
            clusterSize,
            recordIndex * (long)recordSize,
            record);
        if (read < recordSize || !FixupFileRecord(record.AsSpan(), bytesPerSector))
            return null;

        return ParseDeletedFileRecord(
            reader,
            record.AsSpan(),
            recordIndex,
            clusterSize,
            usnDeleteTimes,
            mftRuns: mftRuns,
            recordSize: recordSize,
            bytesPerSector: bytesPerSector,
            usnEvidence: usnEvidence);
    }

    internal static RecoveryFileItem? ParseDeletedFileRecord(
        RawDeviceReader reader,
        Span<byte> record,
        long recordIndex,
        int clusterSize,
        IReadOnlyDictionary<ulong, DateTimeOffset>? usnDeleteTimes = null,
        bool includeInUse = false,
        IReadOnlyList<DataRun>? mftRuns = null,
        int recordSize = 0,
        int bytesPerSector = 0,
        NtfsUsnJournalService.JournalDeleteEvidence? usnEvidence = null)
    {
        if (record.Length < 48 ||
            record[0] != (byte)'F' ||
            record[1] != (byte)'I' ||
            record[2] != (byte)'L' ||
            record[3] != (byte)'E')
            return null;

        ushort sequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(16, 2));
        ulong fileReference = ((ulong)sequenceNumber << 48) | ((ulong)recordIndex & 0x0000FFFFFFFFFFFFUL);
        NtfsUsnJournalService.JournalDeleteEvidence? exactUsnEvidence =
            usnEvidence?.FileReferenceNumber == fileReference ? usnEvidence : null;
        DateTimeOffset? deletedAt = null;
        if (usnDeleteTimes is not null && usnDeleteTimes.TryGetValue(fileReference, out DateTimeOffset journalDeletedAt))
            deletedAt = journalDeletedAt;
        else if (exactUsnEvidence is not null)
            deletedAt = exactUsnEvidence.DeletedAt;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2));
        bool inUse = (flags & 0x0001) != 0;
        bool isDirectory = (flags & 0x0002) != 0;
        if (isDirectory || (!includeInUse && inUse)) return null;

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        if (firstAttributeOffset < 24 || firstAttributeOffset >= record.Length)
            return null;

        string? fileName = null;
        int fileNameScore = -1;
        byte[]? residentData = null;
        IReadOnlyList<DataRun>? dataRuns = null;
        var dataSegments = new List<NtfsDataSegment>();
        byte[]? attributeListData = null;
        long realSize = 0;
        bool dataIsResident = false;
        bool unsupportedData = false;
        DateTimeOffset? fileSystemCreatedAt = null;
        DateTimeOffset? fileSystemModifiedAt = null;
        ulong parentReference = 0;
        bool nameRecoveredFromUsn = false;

        int position = firstAttributeOffset;
        while (position + 16 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position, 4));
            if (type == AttributeEnd) break;

            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 4, 4));
            if (lengthRaw < 16 || lengthRaw > int.MaxValue) break;
            int attributeLength = (int)lengthRaw;
            if (position + attributeLength > record.Length) break;

            bool nonResident = record[position + 8] != 0;
            byte nameLength = record[position + 9];
            ushort attributeFlags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 12, 2));
            ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 14, 2));

            if (type == AttributeList)
            {
                attributeListData = TryReadAttributeListValue(
                    reader,
                    record.Slice(position, attributeLength),
                    nonResident,
                    clusterSize);
            }
            else if (type == AttributeFileName && !nonResident)
            {
                uint valueLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 16, 4));
                ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 20, 2));

                if (valueLengthRaw <= int.MaxValue)
                {
                    int valueLength = (int)valueLengthRaw;
                    int valueStart = position + valueOffset;

                    if (valueLength >= 66 && valueStart >= position &&
                        valueStart + valueLength <= position + attributeLength)
                    {
                        if (!fileSystemCreatedAt.HasValue && valueLength >= 16)
                            fileSystemCreatedAt = TryReadNtfsFileTime(record.Slice(valueStart + 8, 8));

                        if (!fileSystemModifiedAt.HasValue && valueLength >= 24)
                            fileSystemModifiedAt = TryReadNtfsFileTime(record.Slice(valueStart + 16, 8));

                        byte nameChars = record[valueStart + 64];
                        byte nameNamespace = record[valueStart + 65];
                        int nameBytes = nameChars * 2;
                        if (nameChars > 0 && valueStart + 66 + nameBytes <= position + attributeLength)
                        {
                            string candidate = Encoding.Unicode.GetString(
                                record.Slice(valueStart + 66, nameBytes));

                            int score = nameNamespace switch
                            {
                                1 => 3,
                                3 => 3,
                                0 => 2,
                                2 => 1,
                                _ => 0
                            };

                            if (score > fileNameScore && IsUsableName(candidate))
                            {
                                fileName = candidate;
                                fileNameScore = score;
                                parentReference = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(valueStart, 8));
                            }
                        }
                    }
                }
            }
            else if (type == AttributeData && nameLength == 0)
            {
                if ((attributeFlags & 0x4001) != 0)
                {
                    unsupportedData = true;
                }
                else if (!nonResident)
                {
                    uint valueLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 16, 4));
                    ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 20, 2));

                    if (valueLengthRaw <= int.MaxValue)
                    {
                        int valueLength = (int)valueLengthRaw;
                        int valueStart = position + valueOffset;
                        if (valueStart >= position &&
                            valueLength >= 0 &&
                            valueStart + valueLength <= position + attributeLength)
                        {
                            residentData = record.Slice(valueStart, valueLength).ToArray();
                            realSize = valueLength;
                            dataIsResident = true;
                        }
                    }
                }
                else if (attributeLength >= 64)
                {
                    ushort runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 32, 2));
                    long candidateSize = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(position + 48, 8));

                    int runStart = position + runListOffset;
                    if (candidateSize > 0 &&
                        runStart >= position &&
                        runStart < position + attributeLength)
                    {
                        IReadOnlyList<DataRun> runs = DecodeRunList(
                            record.Slice(runStart, position + attributeLength - runStart));

                        if (runs.Count > 0)
                        {
                            long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(position + 16, 8));
                            dataSegments.Add(new NtfsDataSegment(lowestVcn, attributeId, runs, candidateSize));
                            dataRuns = runs;
                            realSize = Math.Max(realSize, candidateSize);
                            dataIsResident = false;
                        }
                    }
                }
            }

            position += attributeLength;
        }

        bool usedAttributeList = false;
        if (!dataIsResident && attributeListData is { Length: > 0 } &&
            mftRuns is { Count: > 0 } && recordSize >= 512 && bytesPerSector >= 512)
        {
            IReadOnlyList<NtfsAttributeListEntry> entries = ParseAttributeListEntries(attributeListData);
            foreach (NtfsAttributeListEntry entry in entries
                         .Where(e => e.Type == AttributeData && string.IsNullOrEmpty(e.Name))
                         .OrderBy(e => e.LowestVcn))
            {
                if (entry.RecordIndex == recordIndex || entry.RecordIndex < 0)
                    continue;

                NtfsDataSegment? segment = TryReadDataSegmentFromMftRecord(
                    reader,
                    mftRuns,
                    clusterSize,
                    recordSize,
                    bytesPerSector,
                    entry);
                if (segment is null)
                    continue;

                if (dataSegments.Any(existing =>
                        existing.LowestVcn == segment.LowestVcn &&
                        existing.AttributeId == segment.AttributeId))
                    continue;

                dataSegments.Add(segment);
                realSize = Math.Max(realSize, segment.RealSize);
                usedAttributeList = true;
            }
        }

        if (!dataIsResident && dataSegments.Count > 0)
        {
            IReadOnlyList<NtfsDataSegment> orderedSegments = dataSegments
                .OrderBy(segment => segment.LowestVcn)
                .ThenBy(segment => segment.AttributeId)
                .ToList();
            dataRuns = orderedSegments.SelectMany(segment => segment.Runs).ToList();
        }

        if (string.IsNullOrWhiteSpace(fileName) && exactUsnEvidence is not null &&
            IsUsableName(exactUsnEvidence.FileName))
        {
            fileName = exactUsnEvidence.FileName;
            parentReference = exactUsnEvidence.ParentReferenceNumber;
            nameRecoveredFromUsn = true;
        }
        else if (parentReference == 0 && exactUsnEvidence is not null)
        {
            parentReference = exactUsnEvidence.ParentReferenceNumber;
        }

        if (unsupportedData || string.IsNullOrWhiteSpace(fileName) || realSize <= 0)
            return null;

        string extension = FileTypeHelper.Normalize(Path.GetExtension(fileName));

        if (dataIsResident && residentData is not null)
        {
            bool headerOkay = FileHeaderValidator.LooksLike(extension, residentData);
            return new RecoveryFileItem
            {
                FileName = FileTypeHelper.SanitizeFileName(fileName),
                Extension = extension,
                SizeBytes = realSize,
                RecoveryState = inUse ? "Çok İyi" : headerOkay ? "Çok İyi" : "Kısmi",
                TypeGlyph = FileTypeHelper.GetGlyph(extension),
                SourceText = inUse
                    ? $"NTFS mevcut dosya • MFT #{recordIndex:N0}"
                    : nameRecoveredFromUsn
                        ? $"NTFS silinmiş dosya • MFT #{recordIndex:N0} • $UsnJrnl dosya adı geri kazanıldı"
                        : $"NTFS silinmiş dosya • MFT #{recordIndex:N0}",
                SourceKind = RecoverySourceKind.NtfsResident,
                IsExistingFile = inUse,
                ClusterSize = clusterSize,
                ResidentData = residentData,
                FileSystemCreatedAt = fileSystemCreatedAt,
                FileSystemModifiedAt = fileSystemModifiedAt,
                DeletedAt = deletedAt,
                DeletionDateSource = deletedAt.HasValue ? "NTFS USN Journal • FILE_DELETE" : null,
                NtfsRecordIndex = recordIndex,
                NtfsFileReference = fileReference,
                NtfsParentReference = parentReference
            };
        }

        if (dataRuns is null || dataRuns.Count == 0)
            return null;

        bool hasSparse = dataRuns.Any(r => r.IsSparse);
        DataRun? firstPhysicalRun = dataRuns.FirstOrDefault(r => !r.IsSparse && r.ClusterCount > 0 && r.LogicalClusterNumber > 0);
        long firstPhysicalOffset = firstPhysicalRun is null
            ? 0
            : firstPhysicalRun.LogicalClusterNumber * (long)clusterSize;
        bool firstHeaderOkay = ValidateFirstRunHeader(reader, dataRuns, clusterSize, extension);
        string state = inUse
            ? hasSparse ? "Kısmi" : "Çok İyi"
            : !firstHeaderOkay
                ? "Zayıf"
                : hasSparse
                    ? "Kısmi"
                    : "İyi";

        string sourceText = inUse
            ? usedAttributeList
                ? $"NTFS mevcut dosya • MFT #{recordIndex:N0} • $ATTRIBUTE_LIST birleştirildi"
                : $"NTFS mevcut dosya • MFT #{recordIndex:N0}"
            : usedAttributeList
                ? $"NTFS silinmiş dosya • MFT #{recordIndex:N0} • $ATTRIBUTE_LIST birleştirildi{(nameRecoveredFromUsn ? " • $UsnJrnl dosya adı geri kazanıldı" : string.Empty)}"
                : nameRecoveredFromUsn
                    ? $"NTFS silinmiş dosya • MFT #{recordIndex:N0} • $UsnJrnl dosya adı geri kazanıldı"
                    : $"NTFS silinmiş dosya • MFT #{recordIndex:N0}";

        return new RecoveryFileItem
        {
            FileName = FileTypeHelper.SanitizeFileName(fileName),
            Extension = extension,
            SizeBytes = realSize,
            RecoveryState = state,
            TypeGlyph = FileTypeHelper.GetGlyph(extension),
            SourceText = sourceText,
            SourceKind = RecoverySourceKind.NtfsRunList,
            IsExistingFile = inUse,
            SourceOffset = firstPhysicalOffset,
            ClusterSize = clusterSize,
            DataRuns = dataRuns,
            FileSystemCreatedAt = fileSystemCreatedAt,
            FileSystemModifiedAt = fileSystemModifiedAt,
            DeletedAt = deletedAt,
            DeletionDateSource = deletedAt.HasValue ? "NTFS USN Journal • FILE_DELETE" : null,
            NtfsRecordIndex = recordIndex,
            NtfsFileReference = fileReference,
            NtfsParentReference = parentReference
        };
    }


    internal sealed record NtfsAttributeListEntry(
        uint Type,
        long LowestVcn,
        long RecordIndex,
        ushort SequenceNumber,
        ushort AttributeId,
        string Name);

    private sealed record NtfsDataSegment(
        long LowestVcn,
        ushort AttributeId,
        IReadOnlyList<DataRun> Runs,
        long RealSize);

    internal static IReadOnlyList<NtfsAttributeListEntry> ParseAttributeListEntries(ReadOnlySpan<byte> value)
    {
        var entries = new List<NtfsAttributeListEntry>();
        int position = 0;

        while (position + 26 <= value.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(position, 4));
            if (type == AttributeEnd || type == 0)
                break;

            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(position + 4, 2));
            byte nameLength = value[position + 6];
            byte nameOffset = value[position + 7];
            if (length < 26 || position + length > value.Length)
                break;

            long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(value.Slice(position + 8, 8));
            ulong segmentReference = BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(position + 16, 8));
            ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(position + 24, 2));
            long recordIndex = (long)(segmentReference & 0x0000FFFFFFFFFFFFUL);
            ushort sequence = (ushort)(segmentReference >> 48);

            string name = string.Empty;
            int nameBytes = nameLength * 2;
            if (nameLength > 0 && nameOffset >= 26 && nameOffset + nameBytes <= length)
                name = Encoding.Unicode.GetString(value.Slice(position + nameOffset, nameBytes));

            entries.Add(new NtfsAttributeListEntry(type, lowestVcn, recordIndex, sequence, attributeId, name));
            position += length;
        }

        return entries;
    }

    private static byte[]? TryReadAttributeListValue(
        RawDeviceReader reader,
        ReadOnlySpan<byte> attribute,
        bool nonResident,
        int clusterSize)
    {
        try
        {
            if (!nonResident)
            {
                if (attribute.Length < 24)
                    return null;

                uint valueLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(attribute.Slice(16, 4));
                ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(20, 2));
                if (valueLengthRaw == 0 || valueLengthRaw > 8 * 1024 * 1024 || valueLengthRaw > int.MaxValue)
                    return null;

                int valueLength = (int)valueLengthRaw;
                return valueOffset >= 24 && valueOffset + valueLength <= attribute.Length
                    ? attribute.Slice(valueOffset, valueLength).ToArray()
                    : null;
            }

            if (attribute.Length < 64)
                return null;

            ushort runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(attribute.Slice(32, 2));
            long dataSize = BinaryPrimitives.ReadInt64LittleEndian(attribute.Slice(48, 8));
            if (dataSize <= 0 || dataSize > 8L * 1024 * 1024 || runListOffset < 64 || runListOffset >= attribute.Length)
                return null;

            IReadOnlyList<DataRun> runs = DecodeRunList(attribute.Slice(runListOffset));
            return ReadRunsBestEffort(reader, runs, clusterSize, checked((int)dataSize));
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadRunsBestEffort(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> runs,
        int clusterSize,
        int wantedBytes)
    {
        if (wantedBytes <= 0)
            return null;

        byte[] output = new byte[wantedBytes];
        int written = 0;
        foreach (DataRun run in runs)
        {
            if (written >= wantedBytes)
                break;

            long runBytesLong = Math.Min((long)wantedBytes - written, run.ClusterCount * (long)clusterSize);
            if (runBytesLong <= 0 || runBytesLong > int.MaxValue)
                break;
            int runBytes = (int)runBytesLong;

            if (run.IsSparse)
            {
                written += runBytes;
                continue;
            }

            long offset = run.LogicalClusterNumber * (long)clusterSize;
            int read = reader.ReadBestEffort(offset, output.AsSpan(written, runBytes), out _);
            if (read <= 0)
                break;
            written += read;
            if (read < runBytes)
                break;
        }

        if (written <= 0)
            return null;
        if (written == output.Length)
            return output;

        Array.Resize(ref output, written);
        return output;
    }

    private static NtfsDataSegment? TryReadDataSegmentFromMftRecord(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        NtfsAttributeListEntry entry)
    {
        if (entry.RecordIndex < 0 || entry.RecordIndex > long.MaxValue / recordSize)
            return null;

        byte[] extensionRecord = new byte[recordSize];
        int read = ReadVirtualMft(
            reader,
            mftRuns,
            clusterSize,
            entry.RecordIndex * (long)recordSize,
            extensionRecord);
        if (read < recordSize || !FixupFileRecord(extensionRecord.AsSpan(), bytesPerSector))
            return null;

        ushort recordSequence = BinaryPrimitives.ReadUInt16LittleEndian(extensionRecord.AsSpan(16, 2));
        if (entry.SequenceNumber != 0 && recordSequence != 0 && entry.SequenceNumber != recordSequence)
            return null;

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(extensionRecord.AsSpan(20, 2));
        int position = firstAttributeOffset;
        while (position + 64 <= extensionRecord.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(extensionRecord.AsSpan(position, 4));
            if (type == AttributeEnd)
                break;

            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(extensionRecord.AsSpan(position + 4, 4));
            if (lengthRaw < 16 || lengthRaw > int.MaxValue)
                break;
            int length = (int)lengthRaw;
            if (position + length > extensionRecord.Length)
                break;

            bool nonResident = extensionRecord[position + 8] != 0;
            byte nameLength = extensionRecord[position + 9];
            ushort attributeId = BinaryPrimitives.ReadUInt16LittleEndian(extensionRecord.AsSpan(position + 14, 2));
            if (type == AttributeData && nonResident && nameLength == 0 && attributeId == entry.AttributeId && length >= 64)
            {
                long lowestVcn = BinaryPrimitives.ReadInt64LittleEndian(extensionRecord.AsSpan(position + 16, 8));
                ushort runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(extensionRecord.AsSpan(position + 32, 2));
                long realSize = BinaryPrimitives.ReadInt64LittleEndian(extensionRecord.AsSpan(position + 48, 8));
                int runStart = position + runListOffset;
                if (lowestVcn == entry.LowestVcn && runStart >= position && runStart < position + length)
                {
                    IReadOnlyList<DataRun> runs = DecodeRunList(
                        extensionRecord.AsSpan(runStart, position + length - runStart));
                    if (runs.Count > 0)
                        return new NtfsDataSegment(lowestVcn, attributeId, runs, Math.Max(0, realSize));
                }
            }

            position += length;
        }

        return null;
    }


    private static DateTimeOffset? TryReadNtfsFileTime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
            return null;

        long raw = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        if (raw <= 0)
            return null;

        try
        {
            DateTimeOffset candidate = DateTimeOffset.FromFileTime(raw);
            return candidate.Year >= 1970 && candidate <= DateTimeOffset.UtcNow.AddYears(2)
                ? candidate
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool ValidateFirstRunHeader(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> runs,
        int clusterSize,
        string extension)
    {
        DataRun? first = runs.FirstOrDefault(r => !r.IsSparse && r.ClusterCount > 0 && r.LogicalClusterNumber > 0);
        if (first is null) return false;

        byte[] header = new byte[64];
        try
        {
            int read = reader.ReadBestEffort(
                first.LogicalClusterNumber * (long)clusterSize,
                header,
                out long unreadableBytes);
            return read > 0 && unreadableBytes == 0 &&
                   FileHeaderValidator.LooksLike(extension, header.AsSpan(0, read));
        }
        catch
        {
            return false;
        }
    }

    private static byte[] ReadBestAvailableNtfsBootSector(
        RawDeviceReader reader,
        long volumeLength,
        bool legacyRotationalQuickPath,
        out bool mftLocationTrusted)
    {
        mftLocationTrusted = false;
        int sectorSize = Math.Max(512, reader.SectorSize);

        byte[] primary = ReadBestEffortBytes(reader, 0, sectorSize);
        if (LooksLikeNtfsBootSectorGeometry(primary, volumeLength, out bool primaryMftTrusted))
        {
            mftLocationTrusted = primaryMftTrusted;
            return primary;
        }

        if (volumeLength > sectorSize)
        {
            long backupOffset = Math.Max(0, volumeLength - sectorSize);
            byte[] backup = ReadBestEffortBytes(reader, backupOffset, sectorSize);
            if (LooksLikeNtfsBootSectorGeometry(backup, volumeLength, out bool backupMftTrusted))
            {
                mftLocationTrusted = backupMftTrusted;
                return backup;
            }
        }

        // On aged rotational media a multi-megabyte best-effort edge probe can spend minutes
        // recursively retrying bad sectors before Quick Scan ever reports progress. Primary +
        // canonical backup VBR were already tried above. For the legacy path, move immediately
        // to the bounded MFT locator/rescue instead of hammering the first/last 16 MiB.
        if (legacyRotationalQuickPath)
            throw new InvalidDataException("NTFS primary/yedek VBR doğrulanamadı; Legacy MFT Locator başlatılıyor.");

        // Non-legacy media may still have a slightly displaced backup VBR, so retain the narrow
        // edge search for those faster/healthier sources.
        const long EdgeWindowBytes = 16L * 1024 * 1024;
        foreach (long windowStart in new[]
                 {
                     0L,
                     Math.Max(0, volumeLength - EdgeWindowBytes)
                 }.Distinct())
        {
            long windowLength = Math.Min(EdgeWindowBytes, volumeLength - windowStart);
            byte[]? found = FindNtfsBootSectorInWindow(
                reader,
                windowStart,
                windowLength,
                sectorSize,
                volumeLength,
                out bool trusted);
            if (found is not null)
            {
                mftLocationTrusted = trusted;
                return found;
            }
        }

        throw new InvalidDataException("NTFS birincil/yedek VBR ve legacy kenar kopyaları doğrulanamadı.");
    }

    private static byte[]? FindNtfsBootSectorInWindow(
        RawDeviceReader reader,
        long windowStart,
        long windowLength,
        int sectorSize,
        long volumeLength,
        out bool mftLocationTrusted)
    {
        mftLocationTrusted = false;
        if (windowLength < 512)
            return null;

        const int ChunkBytes = 1024 * 1024;
        byte[] buffer = new byte[ChunkBytes];
        long end = Math.Min(volumeLength, windowStart + windowLength);
        for (long position = windowStart; position < end; position += ChunkBytes)
        {
            int request = (int)Math.Min(buffer.Length, end - position);
            Array.Clear(buffer, 0, request);
            int read = reader.ReadBestEffort(position, buffer.AsSpan(0, request), out _);
            int complete = Math.Max(0, read);

            for (int relative = 0; relative + 512 <= complete; relative += sectorSize)
            {
                ReadOnlySpan<byte> candidate = buffer.AsSpan(relative, Math.Min(sectorSize, complete - relative));
                if (!LooksLikeNtfsBootSectorGeometry(candidate, volumeLength, out bool trusted))
                    continue;

                mftLocationTrusted = trusted;
                return candidate.ToArray();
            }
        }
        return null;
    }

    private static bool LooksLikeNtfsBootSectorGeometry(
        ReadOnlySpan<byte> boot,
        long volumeLength,
        out bool mftLocationTrusted)
    {
        mftLocationTrusted = false;
        if (boot.Length < 80 || !boot.Slice(3, 8).SequenceEqual("NTFS    "u8))
            return false;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        int sectorsPerCluster = boot[13];
        if (bytesPerSector is not (512 or 1024 or 2048 or 4096) ||
            sectorsPerCluster <= 0 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 ||
            sectorsPerCluster > 128)
            return false;

        ulong totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.Slice(40, 8));
        if (totalSectors == 0 || totalSectors > (ulong)(long.MaxValue / bytesPerSector))
            return false;

        long declaredLength = (long)totalSectors * bytesPerSector;
        long tolerance = Math.Max(64L * 1024 * 1024, Math.Max(1, volumeLength) / 100); // max(64 MiB, 1%)
        bool lengthCompatible = volumeLength <= 0 || Math.Abs(declaredLength - volumeLength) <= tolerance;

        // A real NTFS VBR must still carry the classic end marker. Length mismatch alone is
        // not fatal on legacy SATA/CHS/HPA media; it only makes the stored $MFT location
        // untrusted so the bounded locator is used instead.
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            return false;

        sbyte recordCode = unchecked((sbyte)boot[64]);
        int clusterSize = bytesPerSector * sectorsPerCluster;
        int recordSize;
        try
        {
            recordSize = recordCode switch
            {
                > 0 => checked(clusterSize * recordCode),
                >= -30 and < 0 => 1 << -recordCode,
                _ => 0
            };
        }
        catch (OverflowException)
        {
            recordSize = 0;
        }
        if (recordSize != 0 && (recordSize < 512 || recordSize > 64 * 1024))
            return false;

        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.Slice(48, 8));
        if (mftLcn > 0 && mftLcn <= long.MaxValue / Math.Max(1, clusterSize))
        {
            long mftOffset = mftLcn * (long)clusterSize;
            mftLocationTrusted = lengthCompatible && mftOffset >= 0 && (volumeLength <= 0 || mftOffset < volumeLength);
        }
        return true;
    }

    internal static bool IsLegacyRotationalQuickPath(StorageDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        // TRIM is a strong negative signal for the old-HDD path. Keep SSD/NVMe media out even
        // when a controller exposes them with a generic SCSI/RAID bus label.
        if (device.TrimEnabled == true)
            return false;

        // Prefer the physical media query over UI labels. Older Windows/AHCI/RAID drivers can
        // expose a perfectly ordinary SATA HDD as SCSI, RAID, Unknown or with a generic visual
        // kind. This was the important gap in the previous legacy Quick routing.
        if (device.PhysicalDriveNumber is int physicalDriveNumber &&
            PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo physical))
        {
            if (physical.Media.MediaKind == StorageMediaKind.SolidState || physical.Media.TrimEnabled == true)
                return false;

            if (physical.Media.MediaKind == StorageMediaKind.Rotational)
                return true;
        }

        string bus = (device.BusTypeText ?? string.Empty).Trim().ToUpperInvariant();
        string visual = (device.VisualKind ?? string.Empty).Trim();
        string kind = device.Kind ?? string.Empty;
        bool rotationalVisual = visual.Contains("Hdd", StringComparison.OrdinalIgnoreCase) ||
                                visual.Contains("FixedDisk", StringComparison.OrdinalIgnoreCase) ||
                                visual.Contains("ExternalHdd", StringComparison.OrdinalIgnoreCase) ||
                                kind.Contains("HDD", StringComparison.OrdinalIgnoreCase) ||
                                kind.Contains("Sabit Disk", StringComparison.OrdinalIgnoreCase);
        if (!rotationalVisual)
            return false;

        // A physical SATA disk is not guaranteed to be reported as BusType=SATA. Old AHCI,
        // RAID and vendor miniport drivers commonly surface it as SCSI/RAID/Unknown. USB is also
        // accepted only when the media itself was already classified visually as a rotational HDD
        // (for SATA-to-USB bridges). Explicit solid-state buses are excluded.
        return bus is not ("NVME" or "UFS" or "SCM" or "SD" or "MMC" or "VIRTUAL" or "FILEBACKEDVIRTUAL");
    }

    private static long GetRunCoverageBytes(IReadOnlyList<DataRun> runs, int clusterSize)
    {
        long clusters = 0;
        foreach (DataRun run in runs)
        {
            if (run.ClusterCount <= 0)
                continue;

            if (clusters > long.MaxValue - run.ClusterCount)
                return long.MaxValue;
            clusters += run.ClusterCount;
        }

        if (clusters > long.MaxValue / Math.Max(1, clusterSize))
            return long.MaxValue;
        return clusters * clusterSize;
    }

    internal static (IReadOnlyList<DataRun> Runs, long DataSize) ReadMftDataRuns(
        byte[] record,
        int clusterSize)
    {
        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(20, 2));
        int position = firstAttributeOffset;

        while (position + 64 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position, 4));
            if (type == AttributeEnd) break;

            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(position + 4, 4));
            if (lengthRaw < 16 || lengthRaw > int.MaxValue) break;
            int attributeLength = (int)lengthRaw;
            if (position + attributeLength > record.Length) break;

            bool nonResident = record[position + 8] != 0;
            byte nameLength = record[position + 9];

            if (type == AttributeData && nonResident && nameLength == 0)
            {
                ushort runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(position + 32, 2));
                long dataSize = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(position + 48, 8));
                int runStart = position + runListOffset;

                if (dataSize > 0 &&
                    runStart >= position &&
                    runStart < position + attributeLength)
                {
                    IReadOnlyList<DataRun> runs = DecodeRunList(
                        record.AsSpan(runStart, position + attributeLength - runStart));

                    return (runs, dataSize);
                }
            }

            position += attributeLength;
        }

        return (Array.Empty<DataRun>(), 0);
    }

    internal static IReadOnlyList<DataRun> DecodeRunList(ReadOnlySpan<byte> runList)
    {
        var runs = new List<DataRun>();
        int position = 0;
        long currentLcn = 0;

        while (position < runList.Length)
        {
            byte header = runList[position++];
            if (header == 0) break;

            int lengthBytes = header & 0x0F;
            int offsetBytes = (header >> 4) & 0x0F;
            if (lengthBytes <= 0 || lengthBytes > 8 || offsetBytes > 8)
                break;

            if (position + lengthBytes + offsetBytes > runList.Length)
                break;

            ulong clusterCountUnsigned = 0;
            for (int i = 0; i < lengthBytes; i++)
                clusterCountUnsigned |= (ulong)runList[position + i] << (8 * i);
            position += lengthBytes;

            if (clusterCountUnsigned == 0 || clusterCountUnsigned > (ulong)long.MaxValue)
                break;

            long clusterCount = (long)clusterCountUnsigned;
            bool sparse = offsetBytes == 0;

            if (sparse)
            {
                runs.Add(new DataRun(0, clusterCount, true));
                continue;
            }

            long relativeLcn = 0;
            for (int i = 0; i < offsetBytes; i++)
                relativeLcn |= (long)runList[position + i] << (8 * i);

            if ((runList[position + offsetBytes - 1] & 0x80) != 0 && offsetBytes < 8)
                relativeLcn |= -1L << (offsetBytes * 8);

            position += offsetBytes;
            if (relativeLcn > 0 && currentLcn > long.MaxValue - relativeLcn)
                break;
            currentLcn += relativeLcn;

            if (currentLcn < 0 || clusterCount > long.MaxValue - currentLcn)
                break;

            runs.Add(new DataRun(currentLcn, clusterCount, false));
        }

        return runs;
    }

    internal static int ReadVirtualMft(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> runs,
        int clusterSize,
        long logicalOffset,
        Span<byte> destination)
    {
        if (clusterSize <= 0) throw new ArgumentOutOfRangeException(nameof(clusterSize));
        if (logicalOffset < 0) throw new ArgumentOutOfRangeException(nameof(logicalOffset));
        long logicalCluster = logicalOffset / clusterSize;
        int clusterOffset = (int)(logicalOffset % clusterSize);
        int written = 0;

        while (written < destination.Length)
        {
            DataRun? selectedRun = null;
            long selectedVcnStart = 0;
            long runningVcn = 0;

            foreach (DataRun run in runs)
            {
                if (run.ClusterCount <= 0 || run.ClusterCount > long.MaxValue - runningVcn)
                    return written;
                if (logicalCluster >= runningVcn && logicalCluster < runningVcn + run.ClusterCount)
                {
                    selectedRun = run;
                    selectedVcnStart = runningVcn;
                    break;
                }
                runningVcn += run.ClusterCount;
            }

            if (selectedRun is null)
                break;

            long clusterIndexInRun = logicalCluster - selectedVcnStart;
            long clustersAvailable = selectedRun.ClusterCount - clusterIndexInRun;
            long bytesAvailable = clustersAvailable > long.MaxValue / clusterSize
                ? long.MaxValue - clusterOffset
                : clustersAvailable * clusterSize - clusterOffset;
            int toRead = (int)Math.Min(destination.Length - written, bytesAvailable);

            int read;
            if (selectedRun.IsSparse)
            {
                // A hole must retain its logical position; later FILE records are still readable.
                destination.Slice(written, toRead).Clear();
                read = toRead;
            }
            else
            {
                long lcn = selectedRun.LogicalClusterNumber;
                if (lcn < 0 || clusterIndexInRun > long.MaxValue - lcn) break;
                lcn += clusterIndexInRun;
                if (lcn > (long.MaxValue - clusterOffset) / clusterSize) break;
                long physicalOffset = lcn * clusterSize + clusterOffset;
                if (physicalOffset >= reader.VolumeLength) break;
                read = reader.ReadBestEffort(physicalOffset, destination.Slice(written, toRead), out _);
            }
            if (read <= 0) break;

            written += read;
            if (logicalOffset > long.MaxValue - written) break;
            long advancedLogical = logicalOffset + written;
            logicalCluster = advancedLogical / clusterSize;
            clusterOffset = (int)(advancedLogical % clusterSize);

            if (read < toRead) break;
        }

        return written;
    }

    internal static bool FixupFileRecord(Span<byte> record, int bytesPerSector)
    {
        if (record.Length < 8 ||
            record[0] != (byte)'F' ||
            record[1] != (byte)'I' ||
            record[2] != (byte)'L' ||
            record[3] != (byte)'E')
            return false;

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));

        if (bytesPerSector < 2 || record.Length % bytesPerSector != 0 ||
            usaCount != record.Length / bytesPerSector + 1 || usaCount < 2 || usaOffset < 8 ||
            usaOffset + usaCount * 2 > record.Length ||
            usaOffset + usaCount * 2 > bytesPerSector - 2)
            return false;

        ushort updateSequenceNumber =
            BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));

        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector - 2;
            if (sectorEnd < 0 || sectorEnd + 2 > record.Length)
                return false;

            ushort current = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(sectorEnd, 2));
            if (current != updateSequenceNumber)
                return false;

        }

        // Validate every trailer before mutating the record; a torn second sector must not
        // leave the first sector already restored when a caller retries another source.
        for (int i = 1; i < usaCount; i++)
        {
            int sectorEnd = i * bytesPerSector - 2;
            ushort replacement =
                BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset + i * 2, 2));

            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(sectorEnd, 2), replacement);
        }

        return true;
    }

    internal static byte[] ReadBestEffortBytes(RawDeviceReader reader, long offset, int count)
    {
        if (count <= 0) return [];

        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(offset, data, out _);
        if (read == data.Length) return data;
        if (read <= 0) return [];

        Array.Resize(ref data, read);
        return data;
    }

    private static bool IsRicherRecoveredPath(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        static int Depth(string value) => value.Count(ch => ch == '\\');
        int candidateDepth = Depth(candidate);
        int currentDepth = Depth(current);
        if (candidateDepth != currentDepth)
            return candidateDepth > currentDepth;

        // Same depth: prefer the path carrying more actual directory-name information.
        return candidate.Length > current.Length + 3;
    }

    internal static bool IsUsableName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.StartsWith("$", StringComparison.Ordinal)) return false;
        if (fileName is "." or "..") return false;
        return fileName.IndexOf('\0') < 0;
    }
}
