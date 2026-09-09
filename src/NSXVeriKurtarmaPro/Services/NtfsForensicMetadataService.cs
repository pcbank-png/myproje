using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Conservative NTFS forensic enrichment used by Quick Scan.
/// It never writes to the source volume. $UsnJrnl supplies exact delete/name evidence,
/// $Bitmap is sampled to estimate cluster reuse, and $LogFile is structurally validated
/// as corroborating transaction-journal evidence.
/// </summary>
internal sealed class NtfsForensicMetadataService
{
    internal sealed record DirectoryEntry(
        long RecordIndex,
        ushort SequenceNumber,
        ulong ParentReference,
        string Name,
        bool InUse);

    internal sealed record AllocationEvidence(
        int SampledClusters,
        int FreeClusters,
        int AllocatedClusters,
        string Summary)
    {
        public bool HasEvidence => SampledClusters > 0;
        public double AllocatedRatio => SampledClusters <= 0 ? 0d : AllocatedClusters / (double)SampledClusters;
    }

    internal sealed record LogFileEvidence(
        bool Available,
        int RestartPages,
        int RecordPages,
        string Summary);

    private const int BitmapCachePageSize = 4096;
    private const int MaxBitmapCachePages = 96;
    private const int MaxLogFileProbeBytes = 2 * 1024 * 1024;

    private readonly RawDeviceReader _reader;
    private readonly IReadOnlyList<DataRun> _bitmapRuns;
    private readonly IReadOnlyList<DataRun> _mftRuns;
    private readonly int _clusterSize;
    private readonly int _recordSize;
    private readonly int _bytesPerSector;
    private readonly bool _safeMode;
    private readonly Dictionary<long, byte[]> _bitmapPageCache = new();

    private NtfsForensicMetadataService(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> bitmapRuns,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        bool safeMode,
        LogFileEvidence logFile)
    {
        _reader = reader;
        _bitmapRuns = bitmapRuns;
        _mftRuns = mftRuns;
        _clusterSize = clusterSize;
        _recordSize = recordSize;
        _bytesPerSector = bytesPerSector;
        _safeMode = safeMode;
        LogFile = logFile;
    }

    public LogFileEvidence LogFile { get; }
    public bool BitmapAvailable => _bitmapRuns.Count > 0;

    public static NtfsForensicMetadataService Create(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        bool safeMode)
    {
        ArgumentNullException.ThrowIfNull(reader);

        (IReadOnlyList<DataRun> bitmapRuns, _) = ReadSystemFileLayout(
            reader,
            mftRuns,
            clusterSize,
            recordSize,
            bytesPerSector,
            recordIndex: 6);

        LogFileEvidence logEvidence = safeMode
            ? new LogFileEvidence(false, 0, 0, "$LogFile ek okuması SMART Safe Scan nedeniyle atlandı")
            : InspectLogFile(reader, mftRuns, clusterSize, recordSize, bytesPerSector);

        return new NtfsForensicMetadataService(
            reader,
            bitmapRuns,
            mftRuns,
            clusterSize,
            recordSize,
            bytesPerSector,
            safeMode,
            logEvidence);
    }

    public AllocationEvidence ProbeAllocation(
        IReadOnlyList<DataRun>? fileRuns,
        CancellationToken cancellationToken)
    {
        if (_bitmapRuns.Count == 0 || fileRuns is null || fileRuns.Count == 0)
            return new AllocationEvidence(0, 0, 0, "$Bitmap kanıtı yok");

        int maxSamples = _safeMode ? 3 : 8;
        var samples = BuildClusterSamples(fileRuns, maxSamples);
        if (samples.Count == 0)
            return new AllocationEvidence(0, 0, 0, "$Bitmap örneklenebilir fiziksel küme yok");

        int free = 0;
        int allocated = 0;
        int sampled = 0;
        foreach (long lcn in samples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool? isAllocated = TryReadAllocationBit(lcn);
            if (!isAllocated.HasValue)
                continue;

            sampled++;
            if (isAllocated.Value)
                allocated++;
            else
                free++;
        }

        if (sampled == 0)
            return new AllocationEvidence(0, 0, 0, "$Bitmap okunamadı");

        string summary = allocated == 0
            ? $"$Bitmap • {sampled:N0}/{sampled:N0} örnek küme boş • yeniden kullanım izi yok"
            : allocated == sampled
                ? $"$Bitmap • {allocated:N0}/{sampled:N0} örnek küme yeniden tahsisli • üzerine yazılma riski yüksek"
                : $"$Bitmap • {allocated:N0}/{sampled:N0} örnek küme yeniden tahsisli • kısmi üzerine yazılma riski";

        return new AllocationEvidence(sampled, free, allocated, summary);
    }

    public static bool TryParseDirectoryEntry(
        ReadOnlySpan<byte> fixedRecord,
        long recordIndex,
        out DirectoryEntry? entry)
    {
        entry = null;
        if (fixedRecord.Length < 48 ||
            fixedRecord[0] != (byte)'F' ||
            fixedRecord[1] != (byte)'I' ||
            fixedRecord[2] != (byte)'L' ||
            fixedRecord[3] != (byte)'E')
            return false;

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(22, 2));
        if ((flags & 0x0002) == 0)
            return false;

        if (!TryReadBestFileName(fixedRecord, out string name, out ulong parentReference))
            return false;

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(16, 2));
        entry = new DirectoryEntry(
            recordIndex,
            sequence,
            parentReference,
            name,
            InUse: (flags & 0x0001) != 0);
        return true;
    }

    public string ResolveOriginalPath(
        RecoveryFileItem item,
        IDictionary<long, DirectoryEntry> directories,
        CancellationToken cancellationToken,
        bool allowDiskFallback = true,
        int maxDiskFallbackReads = int.MaxValue,
        IReadOnlyDictionary<ulong, NtfsUsnJournalService.JournalPathEvidence>? historicalEntries = null,
        IReadOnlyDictionary<ulong, NtfsDirectoryIndexRecoveryService.IndexPathEvidence>? indexEntries = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(directories);

        string fileName = FileTypeHelper.SanitizeFileName(item.FileName);
        ulong initialParentReference = item.NtfsParentReference;

        // $I30 keeps a second copy of file name + parent reference. On old HDDs the FILE_NAME
        // attribute can be stale/reused while the directory index still carries the historical
        // exact 64-bit reference. Prefer it when available, then walk the parent chain.
        if (item.NtfsFileReference != 0 &&
            TryGetIndexEntry(item.NtfsFileReference, indexEntries, requireDirectory: false, out NtfsDirectoryIndexRecoveryService.IndexPathEvidence? indexedFile) &&
            indexedFile is not null)
        {
            if (NtfsQuickScanService.IsUsableName(indexedFile.FileName))
                fileName = indexedFile.FileName;
            if (indexedFile.ParentReferenceNumber != 0)
                initialParentReference = indexedFile.ParentReferenceNumber;
        }

        if (initialParentReference == 0)
            return string.Empty;

        var names = new List<string>(16);
        var visited = new HashSet<ulong>();
        ulong currentReference = initialParentReference;
        bool parentChainResolved = false;
        int diskFallbackReads = 0;

        for (int depth = 0; depth < 64; depth++)
        {
            long recordIndex = (long)(currentReference & 0x0000FFFFFFFFFFFFUL);
            if (recordIndex == 5)
            {
                parentChainResolved = true;
                break;
            }
            if (recordIndex < 0 || !visited.Add(currentReference))
                break;

            cancellationToken.ThrowIfCancellationRequested();
            if (!directories.TryGetValue(recordIndex, out DirectoryEntry? directory) || directory is null)
            {
                // $I30 is the strongest historical directory-name source after the live MFT:
                // the index entry carries the exact child reference plus its old parent reference.
                if (TryGetIndexEntry(currentReference, indexEntries, requireDirectory: true, out NtfsDirectoryIndexRecoveryService.IndexPathEvidence? indexedMissing) &&
                    indexedMissing is not null)
                {
                    parentChainResolved = true;
                    names.Add(indexedMissing.FileName);
                    if (indexedMissing.ParentReferenceNumber == 0 || indexedMissing.ParentReferenceNumber == currentReference)
                        break;
                    currentReference = indexedMissing.ParentReferenceNumber;
                    continue;
                }

                // If the directory index is also gone, exact 64-bit USN evidence is the next
                // conservative historical source. Sequence number is part of the key.
                if (TryGetHistoricalEntry(currentReference, historicalEntries, out NtfsUsnJournalService.JournalPathEvidence? historicalMissing))
                {
                    parentChainResolved = true;
                    if (!string.IsNullOrWhiteSpace(historicalMissing.FileName) && historicalMissing.FileName is not "." and not "..")
                        names.Add(historicalMissing.FileName);

                    if (historicalMissing.ParentReferenceNumber == 0 || historicalMissing.ParentReferenceNumber == currentReference)
                        break;
                    currentReference = historicalMissing.ParentReferenceNumber;
                    continue;
                }

                // Tam MFT taramasında dizin kayıtlarının büyük bölümü zaten bellekte.
                // Eksik parent kaydı gerekiyorsa yalnız benzersiz dizin MFT kayıtlarını
                // cache'li ve bütçeli biçimde okuruz. Böylece YOL canlı oluşurken dosya
                // başına random seek yapılmaz; aynı klasördeki binlerce dosya tek cache
                // kaydını paylaşır.
                if (!allowDiskFallback ||
                    diskFallbackReads >= Math.Max(0, maxDiskFallbackReads) ||
                    !TryReadDirectoryEntry(recordIndex, out directory) || directory is null)
                {
                    break;
                }

                diskFallbackReads++;
                directories[recordIndex] = directory;
            }

            ushort expectedSequence = (ushort)(currentReference >> 48);
            if (expectedSequence != 0 && directory.SequenceNumber != 0 &&
                expectedSequence != directory.SequenceNumber)
            {
                // Slot reused: never attach the current folder name to an old reference. First
                // consult historical $I30 slack/allocation evidence, then USN.
                if (TryGetIndexEntry(currentReference, indexEntries, requireDirectory: true, out NtfsDirectoryIndexRecoveryService.IndexPathEvidence? indexedReused) &&
                    indexedReused is not null)
                {
                    parentChainResolved = true;
                    names.Add(indexedReused.FileName);
                    if (indexedReused.ParentReferenceNumber == 0 || indexedReused.ParentReferenceNumber == currentReference)
                        break;
                    currentReference = indexedReused.ParentReferenceNumber;
                    continue;
                }

                if (TryGetHistoricalEntry(currentReference, historicalEntries, out NtfsUsnJournalService.JournalPathEvidence? historicalReused))
                {
                    parentChainResolved = true;
                    if (!string.IsNullOrWhiteSpace(historicalReused.FileName) && historicalReused.FileName is not "." and not "..")
                        names.Add(historicalReused.FileName);

                    if (historicalReused.ParentReferenceNumber == 0 || historicalReused.ParentReferenceNumber == currentReference)
                        break;
                    currentReference = historicalReused.ParentReferenceNumber;
                    continue;
                }

                // Sequence mismatch on an active directory means the MFT slot was reused;
                // attaching that live folder would fabricate a path, so fail closed.
                // A deleted directory record, however, can legitimately survive with a stale
                // sequence in old-media forensic recovery.
                if (item.IsExistingFile || directory.InUse)
                    break;
            }

            parentChainResolved = true;
            if (!string.IsNullOrWhiteSpace(directory.Name) &&
                directory.Name is not "." and not "..")
            {
                names.Add(directory.Name);
            }

            if (directory.ParentReference == 0 || directory.ParentReference == currentReference)
                break;
            currentReference = directory.ParentReference;
        }

        if (!parentChainResolved)
            return string.Empty;

        names.Reverse();
        string directoryPath = names.Count == 0 ? string.Empty : string.Join("\\", names);
        return string.IsNullOrWhiteSpace(directoryPath)
            ? $"\\{fileName}"
            : $"\\{directoryPath}\\{fileName}";
    }

    private static bool TryGetIndexEntry(
        ulong reference,
        IReadOnlyDictionary<ulong, NtfsDirectoryIndexRecoveryService.IndexPathEvidence>? indexEntries,
        bool requireDirectory,
        out NtfsDirectoryIndexRecoveryService.IndexPathEvidence? evidence)
    {
        evidence = null;
        return indexEntries is not null &&
               reference != 0 &&
               indexEntries.TryGetValue(reference, out evidence) &&
               evidence is not null &&
               (!requireDirectory || evidence.IsDirectory) &&
               !string.IsNullOrWhiteSpace(evidence.FileName);
    }

    private static bool TryGetHistoricalEntry(
        ulong reference,
        IReadOnlyDictionary<ulong, NtfsUsnJournalService.JournalPathEvidence>? historicalEntries,
        out NtfsUsnJournalService.JournalPathEvidence? evidence)
    {
        evidence = null;
        return historicalEntries is not null &&
               reference != 0 &&
               historicalEntries.TryGetValue(reference, out evidence) &&
               evidence is not null &&
               !string.IsNullOrWhiteSpace(evidence.FileName);
    }

    private bool TryReadDirectoryEntry(long recordIndex, out DirectoryEntry? entry)
    {
        entry = null;
        if (recordIndex < 0 || recordIndex > long.MaxValue / Math.Max(1, _recordSize))
            return false;

        try
        {
            byte[] record = new byte[_recordSize];
            int read = NtfsQuickScanService.ReadVirtualMft(
                _reader,
                _mftRuns,
                _clusterSize,
                recordIndex * (long)_recordSize,
                record);
            if (read < _recordSize || !NtfsQuickScanService.FixupFileRecord(record.AsSpan(), _bytesPerSector))
                return false;

            return TryParseDirectoryEntry(record, recordIndex, out entry);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadBestFileName(
        ReadOnlySpan<byte> record,
        out string name,
        out ulong parentReference)
    {
        name = string.Empty;
        parentReference = 0;
        if (record.Length < 48)
            return false;

        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(20, 2));
        if (firstAttributeOffset < 24 || firstAttributeOffset >= record.Length)
            return false;

        int bestScore = -1;
        int position = firstAttributeOffset;
        while (position + 24 <= record.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position, 4));
            if (type == 0xFFFFFFFF)
                break;

            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 4, 4));
            if (lengthRaw < 24 || lengthRaw > int.MaxValue)
                break;
            int length = (int)lengthRaw;
            if (position + length > record.Length)
                break;

            bool nonResident = record[position + 8] != 0;
            if (type == 0x30 && !nonResident)
            {
                uint valueLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 16, 4));
                ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 20, 2));
                if (valueLengthRaw <= int.MaxValue)
                {
                    int valueLength = (int)valueLengthRaw;
                    int valueStart = position + valueOffset;
                    if (valueLength >= 66 && valueStart >= position &&
                        valueStart + valueLength <= position + length)
                    {
                        byte nameChars = record[valueStart + 64];
                        byte nameNamespace = record[valueStart + 65];
                        int nameBytes = nameChars * 2;
                        if (nameChars > 0 && valueStart + 66 + nameBytes <= position + length)
                        {
                            string candidate = Encoding.Unicode.GetString(record.Slice(valueStart + 66, nameBytes));
                            int score = nameNamespace switch
                            {
                                1 or 3 => 3,
                                0 => 2,
                                2 => 1,
                                _ => 0
                            };
                            if (score > bestScore && NtfsQuickScanService.IsUsableName(candidate))
                            {
                                bestScore = score;
                                name = candidate;
                                parentReference = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(valueStart, 8));
                            }
                        }
                    }
                }
            }

            position += length;
        }

        return bestScore >= 0;
    }

    private bool? TryReadAllocationBit(long lcn)
    {
        if (lcn < 0)
            return null;

        try
        {
            long byteIndex = lcn >> 3;
            int bitIndex = (int)(lcn & 7);
            long pageIndex = byteIndex / BitmapCachePageSize;
            int offsetInPage = (int)(byteIndex % BitmapCachePageSize);

            if (!_bitmapPageCache.TryGetValue(pageIndex, out byte[]? page) || page is null)
            {
                page = new byte[BitmapCachePageSize];
                int read = NtfsQuickScanService.ReadVirtualMft(
                    _reader,
                    _bitmapRuns,
                    _clusterSize,
                    pageIndex * BitmapCachePageSize,
                    page);
                if (read <= offsetInPage)
                    return null;
                if (read < page.Length)
                    page = page.AsSpan(0, read).ToArray();

                if (_bitmapPageCache.Count >= MaxBitmapCachePages)
                    _bitmapPageCache.Clear();
                _bitmapPageCache[pageIndex] = page;
            }

            if (offsetInPage >= page.Length)
                return null;

            return (page[offsetInPage] & (1 << bitIndex)) != 0;
        }
        catch
        {
            // $Bitmap enrichment is advisory. An unreadable bitmap page must never stop Quick Scan.
            return null;
        }
    }

    private static List<long> BuildClusterSamples(IReadOnlyList<DataRun> runs, int maxSamples)
    {
        var samples = new List<long>(maxSamples);
        var unique = new HashSet<long>();

        foreach (DataRun run in runs)
        {
            if (samples.Count >= maxSamples)
                break;
            if (run.IsSparse || run.ClusterCount <= 0 || run.LogicalClusterNumber < 0)
                continue;

            long count = run.ClusterCount;
            long[] candidates = count switch
            {
                1 => [run.LogicalClusterNumber],
                2 => [run.LogicalClusterNumber, run.LogicalClusterNumber + 1],
                _ =>
                [
                    run.LogicalClusterNumber,
                    run.LogicalClusterNumber + count / 4,
                    run.LogicalClusterNumber + count / 2,
                    run.LogicalClusterNumber + (count * 3) / 4,
                    run.LogicalClusterNumber + count - 1
                ]
            };

            foreach (long candidate in candidates)
            {
                if (samples.Count >= maxSamples)
                    break;
                if (candidate >= 0 && unique.Add(candidate))
                    samples.Add(candidate);
            }
        }

        return samples;
    }

    private static LogFileEvidence InspectLogFile(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector)
    {
        try
        {
            (IReadOnlyList<DataRun> logRuns, long logSize) = ReadSystemFileLayout(
                reader,
                mftRuns,
                clusterSize,
                recordSize,
                bytesPerSector,
                recordIndex: 2);

            if (logRuns.Count == 0 || logSize <= 0)
                return new LogFileEvidence(false, 0, 0, "$LogFile veri zinciri doğrulanamadı");

            int wanted = (int)Math.Min(MaxLogFileProbeBytes, logSize);
            if (wanted < 4096)
                return new LogFileEvidence(false, 0, 0, "$LogFile doğrulama alanı çok küçük");

            byte[] buffer = new byte[wanted];
            int read = NtfsQuickScanService.ReadVirtualMft(reader, logRuns, clusterSize, 0, buffer);
            if (read <= 0)
                return new LogFileEvidence(false, 0, 0, "$LogFile okunamadı");

            int restartPages = 0;
            int recordPages = 0;
            for (int offset = 0; offset + 4 <= read; offset += 512)
            {
                ReadOnlySpan<byte> signature = buffer.AsSpan(offset, 4);
                if (signature.SequenceEqual("RSTR"u8))
                    restartPages++;
                else if (signature.SequenceEqual("RCRD"u8))
                    recordPages++;
            }

            bool available = restartPages > 0 || recordPages > 0;
            string summary = available
                ? $"$LogFile doğrulandı • RSTR {restartPages:N0} • RCRD {recordPages:N0}"
                : "$LogFile okundu ancak geçerli RSTR/RCRD sayfa imzası doğrulanamadı";

            return new LogFileEvidence(available, restartPages, recordPages, summary);
        }
        catch
        {
            return new LogFileEvidence(false, 0, 0, "$LogFile ek kanıtı okunamadı; ana MFT taraması devam etti");
        }
    }

    internal static (IReadOnlyList<DataRun> Runs, long DataSize) ReadSystemFileLayout(
        RawDeviceReader reader,
        IReadOnlyList<DataRun> mftRuns,
        int clusterSize,
        int recordSize,
        int bytesPerSector,
        long recordIndex)
    {
        try
        {
            if (recordIndex < 0 || recordIndex > long.MaxValue / Math.Max(1, recordSize))
                return (Array.Empty<DataRun>(), 0);

            byte[] record = new byte[recordSize];
            int read = NtfsQuickScanService.ReadVirtualMft(
                reader,
                mftRuns,
                clusterSize,
                recordIndex * (long)recordSize,
                record);
            if (read < recordSize || !NtfsQuickScanService.FixupFileRecord(record.AsSpan(), bytesPerSector))
                return (Array.Empty<DataRun>(), 0);

            return NtfsQuickScanService.ReadMftDataRuns(record, clusterSize);
        }
        catch
        {
            return (Array.Empty<DataRun>(), 0);
        }
    }
}
