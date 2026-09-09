using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// NTFS $I30 directory index recovery layer.
///
/// MFT parent chains are the first and cheapest path source, but old/deleted folders can lose
/// their FILE_NAME parent records after MFT slot reuse. NTFS directory indexes keep a second
/// copy of child file references, names and parent references inside INDEX_ROOT / INDEX_ALLOCATION
/// records. Deleted entries may also survive in index slack. This service harvests that metadata
/// without changing any file-carving decisions.
///
/// For Deep Scan it can also inspect already-read raw blocks for orphaned INDX records, so the
/// full-disk path pass adds no second raw read of the media.
/// </summary>
internal sealed class NtfsDirectoryIndexRecoveryService
{
    internal sealed record IndexPathEvidence(
        ulong FileReferenceNumber,
        ulong ParentReferenceNumber,
        string FileName,
        bool IsDirectory,
        long RealSize,
        DateTimeOffset? ModifiedAt,
        int Confidence,
        string Source);

    private sealed record IndexAllocationLayout(
        ulong OwnerReference,
        IReadOnlyList<DataRun> Runs,
        long DataSize,
        int IndexBlockSize);

    private const uint AttributeEnd = 0xFFFFFFFF;
    private const uint AttributeIndexRoot = 0x90;
    private const uint AttributeIndexAllocation = 0xA0;
    private const int MinIndexEntryLength = 16 + 66;
    private const int MaxIndexRecordBytes = 64 * 1024;
    private const int DefaultIndexBlockSize = 4096;
    private const long MaximumReferencedIndexBytes = 512L * 1024 * 1024;

    private readonly Dictionary<ulong, IndexPathEvidence> _evidence = new();
    private readonly List<IndexAllocationLayout> _allocations = new();

    public IReadOnlyDictionary<ulong, IndexPathEvidence> Evidence => _evidence;
    public int EvidenceCount => _evidence.Count;

    /// <summary>
    /// Called while the MFT record is already in memory. Resident INDEX_ROOT is parsed immediately
    /// and INDEX_ALLOCATION runlists are captured for a bounded, HDD-friendly metadata pass later.
    /// </summary>
    public void CaptureDirectoryRecord(
        ReadOnlySpan<byte> fixedRecord,
        long recordIndex,
        int clusterSize)
    {
        if (fixedRecord.Length < 48 ||
            fixedRecord[0] != (byte)'F' || fixedRecord[1] != (byte)'I' ||
            fixedRecord[2] != (byte)'L' || fixedRecord[3] != (byte)'E')
        {
            return;
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(22, 2));
        if ((flags & 0x0002) == 0)
            return;

        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(16, 2));
        ulong ownerReference = ((ulong)sequence << 48) | ((ulong)recordIndex & 0x0000FFFFFFFFFFFFUL);
        ushort firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(20, 2));
        if (firstAttributeOffset < 24 || firstAttributeOffset >= fixedRecord.Length)
            return;

        int indexBlockSize = DefaultIndexBlockSize;
        IReadOnlyList<DataRun>? allocationRuns = null;
        long allocationDataSize = 0;

        int position = firstAttributeOffset;
        while (position + 24 <= fixedRecord.Length)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.Slice(position, 4));
            if (type == AttributeEnd)
                break;

            uint attributeLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.Slice(position + 4, 4));
            if (attributeLengthRaw < 24 || attributeLengthRaw > int.MaxValue)
                break;
            int attributeLength = (int)attributeLengthRaw;
            if (position + attributeLength > fixedRecord.Length)
                break;

            bool nonResident = fixedRecord[position + 8] != 0;
            byte nameLength = fixedRecord[position + 9];
            string attributeName = ReadAttributeName(fixedRecord, position, attributeLength, nameLength);
            bool isI30 = string.IsNullOrWhiteSpace(attributeName) ||
                         string.Equals(attributeName, "$I30", StringComparison.OrdinalIgnoreCase);

            if (type == AttributeIndexRoot && !nonResident && isI30)
            {
                uint valueLengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(fixedRecord.Slice(position + 16, 4));
                ushort valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(position + 20, 2));
                if (valueLengthRaw <= int.MaxValue)
                {
                    int valueLength = (int)valueLengthRaw;
                    int valueStart = position + valueOffset;
                    if (valueLength >= 32 && valueStart >= position &&
                        valueStart + valueLength <= position + attributeLength)
                    {
                        ReadOnlySpan<byte> value = fixedRecord.Slice(valueStart, valueLength);
                        uint candidateBlockSize = BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(8, 4));
                        if (candidateBlockSize is >= 512 and <= MaxIndexRecordBytes &&
                            (candidateBlockSize & (candidateBlockSize - 1)) == 0)
                        {
                            indexBlockSize = (int)candidateBlockSize;
                        }

                        ParseIndexRoot(value, ownerReference);
                    }
                }
            }
            else if (type == AttributeIndexAllocation && nonResident && isI30 && attributeLength >= 64)
            {
                ushort runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(fixedRecord.Slice(position + 32, 2));
                long allocatedSize = BinaryPrimitives.ReadInt64LittleEndian(fixedRecord.Slice(position + 40, 8));
                long dataSize = BinaryPrimitives.ReadInt64LittleEndian(fixedRecord.Slice(position + 48, 8));
                long forensicScanSize = Math.Max(dataSize, allocatedSize);
                int runStart = position + runListOffset;
                if (forensicScanSize > 0 && runStart >= position && runStart < position + attributeLength)
                {
                    IReadOnlyList<DataRun> runs = NtfsQuickScanService.DecodeRunList(
                        fixedRecord.Slice(runStart, position + attributeLength - runStart));
                    if (runs.Count > 0)
                    {
                        allocationRuns = runs;
                        allocationDataSize = forensicScanSize;
                    }
                }
            }

            position += attributeLength;
        }

        if (allocationRuns is { Count: > 0 } && allocationDataSize > 0 && clusterSize > 0)
        {
            _allocations.Add(new IndexAllocationLayout(
                ownerReference,
                allocationRuns,
                allocationDataSize,
                indexBlockSize));
        }
    }

    /// <summary>
    /// Reads only $I30 INDEX_ALLOCATION extents referenced by surviving directory MFT records.
    /// Physical extents are sorted before reading to reduce seek churn on rotational disks.
    /// </summary>
    public int ReadCapturedIndexAllocations(
        RawDeviceReader reader,
        int clusterSize,
        int bytesPerSector,
        CancellationToken cancellationToken,
        IProgress<OperationProgress>? progress = null)
    {
        if (_allocations.Count == 0 || clusterSize <= 0)
            return 0;

        var segments = new List<(long Offset, long Length, int IndexBlockSize)>();
        long scheduled = 0;

        foreach (IndexAllocationLayout layout in _allocations)
        {
            long logicalRemaining = Math.Min(layout.DataSize, MaximumReferencedIndexBytes - scheduled);
            if (logicalRemaining <= 0)
                break;

            foreach (DataRun run in layout.Runs)
            {
                if (logicalRemaining <= 0 || scheduled >= MaximumReferencedIndexBytes)
                    break;

                long runBytes;
                try
                {
                    runBytes = checked(run.ClusterCount * (long)clusterSize);
                }
                catch (OverflowException)
                {
                    break;
                }

                long take = Math.Min(runBytes, logicalRemaining);
                logicalRemaining -= take;
                if (run.IsSparse || run.LogicalClusterNumber <= 0 || take <= 0)
                    continue;

                long physicalOffset;
                try
                {
                    physicalOffset = checked(run.LogicalClusterNumber * (long)clusterSize);
                }
                catch (OverflowException)
                {
                    continue;
                }

                long bounded = Math.Min(take, MaximumReferencedIndexBytes - scheduled);
                if (bounded <= 0)
                    break;

                segments.Add((physicalOffset, bounded, layout.IndexBlockSize));
                scheduled += bounded;
            }
        }

        if (segments.Count == 0)
            return 0;

        segments.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        int before = _evidence.Count;
        long processed = 0;
        const int chunkBytes = 2 * 1024 * 1024;
        byte[] buffer = new byte[chunkBytes + MaxIndexRecordBytes];

        foreach ((long segmentOffset, long segmentLength, int indexBlockSize) in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long local = 0;
            while (local < segmentLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int request = (int)Math.Min(chunkBytes, segmentLength - local);
                int read = reader.ReadBestEffort(segmentOffset + local, buffer.AsSpan(0, request), out _);
                if (read <= 0)
                    break;

                InspectRawBuffer(
                    buffer.AsSpan(0, read),
                    segmentOffset + local,
                    bytesPerSector,
                    indexBlockSize,
                    cancellationToken);

                local += read;
                processed += read;
                if ((processed & ((16L * 1024 * 1024) - 1)) < read)
                {
                    double percent = scheduled <= 0 ? 0 : Math.Clamp(processed * 100d / scheduled, 0d, 100d);
                    progress?.Report(new OperationProgress(
                        percent,
                        "NTFS • Eski Klasör Dizini ($I30)",
                        $"Dizin indeksleri salt-okunur çözümleniyor • {RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(scheduled)} • yol kanıtı {_evidence.Count:N0}."));
                }
            }
        }

        return _evidence.Count - before;
    }

    /// <summary>
    /// Deep Scan hook: inspect an already-read disk block for INDX records. No extra disk I/O.
    /// </summary>
    public int InspectRawBuffer(
        ReadOnlySpan<byte> buffer,
        long absoluteOffset,
        int bytesPerSector,
        CancellationToken cancellationToken)
    {
        return InspectRawBuffer(buffer, absoluteOffset, bytesPerSector, 0, cancellationToken);
    }

    public int ApplyIndexOnlyPaths(IEnumerable<RecoveryFileItem> items)
    {
        var materialized = items as IList<RecoveryFileItem> ?? items.ToList();
        int applied = 0;

        foreach (RecoveryFileItem item in materialized)
        {
            if (item.NtfsFileReference == 0 && item.NtfsParentReference == 0)
                continue;

            string? path = TryResolvePath(item);
            if (string.IsNullOrWhiteSpace(path))
                continue;

            if (IsRicher(path, item.RecoveredOriginalPath))
            {
                item.RecoveredOriginalPath = path;
                applied++;
            }
        }

        // If an old FILE MFT record is completely gone, a raw-carved file has no reference to
        // walk. $I30 still stores original name + real size. Use that only when extension+exact
        // size resolves to one unique historical path across the whole index evidence set. This
        // conservative bridge recovers additional EaseUS-style paths without fabricating a
        // directory when multiple files could match.
        Dictionary<(string Extension, long Size), string?> uniqueSizePaths = BuildUniqueSizePathMap();
        foreach (RecoveryFileItem item in materialized)
        {
            if (item.SourceKind != RecoverySourceKind.RawContiguous ||
                item.SizeBytes <= 0 ||
                !string.IsNullOrWhiteSpace(item.RecoveredOriginalPath))
            {
                continue;
            }

            string extension = FileTypeHelper.Normalize(item.Extension);
            if (string.IsNullOrWhiteSpace(extension))
                continue;

            if (!uniqueSizePaths.TryGetValue((extension, item.SizeBytes), out string? historicalPath) ||
                string.IsNullOrWhiteSpace(historicalPath))
            {
                continue;
            }

            item.RecoveredOriginalPath = historicalPath;
            if (!item.SourceText.Contains("$I30 benzersiz boyut/yol", StringComparison.OrdinalIgnoreCase))
                item.SourceText = $"{item.SourceText} • $I30 benzersiz boyut/yol eşleşmesi";
            applied++;
        }

        return applied;
    }

    private Dictionary<(string Extension, long Size), string?> BuildUniqueSizePathMap()
    {
        var map = new Dictionary<(string Extension, long Size), string?>();
        foreach (IndexPathEvidence evidence in _evidence.Values)
        {
            if (evidence.IsDirectory || evidence.RealSize <= 0 || evidence.Confidence < 70)
                continue;

            string extension = FileTypeHelper.Normalize(Path.GetExtension(evidence.FileName));
            if (string.IsNullOrWhiteSpace(extension) || !FileTypeHelper.IsSupported(extension))
                continue;

            string? path = TryResolveEvidencePath(evidence);
            if (string.IsNullOrWhiteSpace(path) || path.Count(ch => ch == '\\') < 2)
                continue;

            var key = (extension, evidence.RealSize);
            if (!map.TryGetValue(key, out string? existing))
            {
                map[key] = path;
            }
            else if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
            {
                map[key] = null;
            }
        }

        return map;
    }

    private string? TryResolveEvidencePath(IndexPathEvidence fileEvidence)
    {
        if (fileEvidence.ParentReferenceNumber == 0 || !NtfsQuickScanService.IsUsableName(fileEvidence.FileName))
            return null;

        var names = new List<string>(16);
        var visited = new HashSet<ulong>();
        ulong current = fileEvidence.ParentReferenceNumber;
        bool reachedRoot = false;

        for (int depth = 0; depth < 64 && current != 0; depth++)
        {
            long recordIndex = (long)(current & 0x0000FFFFFFFFFFFFUL);
            if (recordIndex == 5)
            {
                reachedRoot = true;
                break;
            }

            if (!visited.Add(current) || !_evidence.TryGetValue(current, out IndexPathEvidence? parent) || parent is null ||
                !parent.IsDirectory || !NtfsQuickScanService.IsUsableName(parent.FileName))
            {
                break;
            }

            names.Add(parent.FileName);
            if (parent.ParentReferenceNumber == 0 || parent.ParentReferenceNumber == current)
                break;
            current = parent.ParentReferenceNumber;
        }

        if (names.Count == 0 && !reachedRoot)
            return null;

        names.Reverse();
        string prefix = names.Count == 0 ? string.Empty : string.Join("\\", names);
        return string.IsNullOrWhiteSpace(prefix)
            ? $"\\{fileEvidence.FileName}"
            : $"\\{prefix}\\{fileEvidence.FileName}";
    }

    public string? TryResolvePath(RecoveryFileItem item)
    {
        string fileName = FileTypeHelper.SanitizeFileName(item.FileName);
        ulong parentReference = item.NtfsParentReference;

        if (item.NtfsFileReference != 0 &&
            _evidence.TryGetValue(item.NtfsFileReference, out IndexPathEvidence? fileEvidence) &&
            fileEvidence is not null)
        {
            if (NtfsQuickScanService.IsUsableName(fileEvidence.FileName))
                fileName = fileEvidence.FileName;
            if (fileEvidence.ParentReferenceNumber != 0)
                parentReference = fileEvidence.ParentReferenceNumber;
        }

        if (parentReference == 0)
            return null;

        var names = new List<string>(16);
        var visited = new HashSet<ulong>();
        ulong current = parentReference;
        bool reachedRoot = false;

        for (int depth = 0; depth < 64 && current != 0; depth++)
        {
            long recordIndex = (long)(current & 0x0000FFFFFFFFFFFFUL);
            if (recordIndex == 5)
            {
                reachedRoot = true;
                break;
            }

            if (!visited.Add(current) || !_evidence.TryGetValue(current, out IndexPathEvidence? parent) || parent is null)
                break;
            if (!parent.IsDirectory || !NtfsQuickScanService.IsUsableName(parent.FileName))
                break;

            names.Add(parent.FileName);
            if (parent.ParentReferenceNumber == 0 || parent.ParentReferenceNumber == current)
                break;
            current = parent.ParentReferenceNumber;
        }

        if (names.Count == 0 && !reachedRoot)
            return null;

        names.Reverse();
        string prefix = names.Count == 0 ? string.Empty : string.Join("\\", names);
        return string.IsNullOrWhiteSpace(prefix) ? $"\\{fileName}" : $"\\{prefix}\\{fileName}";
    }

    private int InspectRawBuffer(
        ReadOnlySpan<byte> buffer,
        long absoluteOffset,
        int bytesPerSector,
        int preferredIndexBlockSize,
        CancellationToken cancellationToken)
    {
        int before = _evidence.Count;
        int cursor = 0;
        while (cursor + 32 <= buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int relative = buffer.Slice(cursor).IndexOf("INDX"u8);
            if (relative < 0)
                break;

            int index = cursor + relative;
            if (index + 32 > buffer.Length)
                break;

            if ((absoluteOffset + index) % Math.Max(512, bytesPerSector) == 0)
            {
                TryParseIndxAt(buffer, index, bytesPerSector, preferredIndexBlockSize);
            }

            cursor = index + 4;
        }

        return _evidence.Count - before;
    }

    private void TryParseIndxAt(
        ReadOnlySpan<byte> source,
        int index,
        int bytesPerSector,
        int preferredIndexBlockSize)
    {
        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(index + 4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(index + 6, 2));
        if (usaOffset < 8 || usaCount < 2)
            return;

        Span<int> candidateSizes = stackalloc int[4];
        int candidateCount = 0;
        AddCandidateSize(candidateSizes, ref candidateCount, preferredIndexBlockSize);
        if (bytesPerSector > 0)
            AddCandidateSize(candidateSizes, ref candidateCount, (usaCount - 1) * bytesPerSector);
        AddCandidateSize(candidateSizes, ref candidateCount, (usaCount - 1) * 512);
        AddCandidateSize(candidateSizes, ref candidateCount, DefaultIndexBlockSize);

        for (int c = 0; c < candidateCount; c++)
        {
            int recordSize = candidateSizes[c];
            if (recordSize < 1024 || recordSize > MaxIndexRecordBytes || index + recordSize > source.Length)
                continue;

            byte[] fixedRecord = source.Slice(index, recordSize).ToArray();
            bool strictFixup = TryFixupIndx(fixedRecord, bytesPerSector > 0 ? bytesPerSector : 512);
            if (!strictFixup && bytesPerSector != 512)
                strictFixup = TryFixupIndx(fixedRecord, 512);

            if (!strictFixup)
            {
                // Slack can outlive a damaged update-sequence trailer. We still parse with a
                // lower confidence, but every entry must pass strong FILE_NAME validation.
                fixedRecord = source.Slice(index, recordSize).ToArray();
            }

            if (fixedRecord.Length < 40 ||
                fixedRecord[0] != (byte)'I' || fixedRecord[1] != (byte)'N' ||
                fixedRecord[2] != (byte)'D' || fixedRecord[3] != (byte)'X')
            {
                continue;
            }

            int headerStart = 24;
            ParseIndexHeader(fixedRecord, headerStart, strictFixup ? 92 : 72, "I30 INDEX_ALLOCATION");
            return;
        }
    }

    private void ParseIndexRoot(ReadOnlySpan<byte> value, ulong ownerReference)
    {
        if (value.Length < 32)
            return;

        int headerStart = 16;
        ParseIndexHeader(value, headerStart, 98, "I30 INDEX_ROOT", ownerReference);
    }

    private void ParseIndexHeader(
        ReadOnlySpan<byte> data,
        int headerStart,
        int baseConfidence,
        string source,
        ulong ownerReference = 0)
    {
        if (headerStart < 0 || headerStart + 16 > data.Length)
            return;

        uint entryOffsetRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(headerStart, 4));
        uint usedSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(headerStart + 4, 4));
        uint allocatedSizeRaw = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(headerStart + 8, 4));
        if (entryOffsetRaw > int.MaxValue || usedSizeRaw > int.MaxValue || allocatedSizeRaw > int.MaxValue)
            return;

        int entryStart = headerStart + (int)entryOffsetRaw;
        int usedEnd = headerStart + (int)usedSizeRaw;
        int allocatedEnd = headerStart + (int)allocatedSizeRaw;
        if (entryStart < headerStart + 16 || entryStart >= data.Length)
            return;

        usedEnd = Math.Clamp(usedEnd, entryStart, data.Length);
        allocatedEnd = Math.Clamp(allocatedEnd, usedEnd, data.Length);

        var liveOffsets = new HashSet<int>();
        int cursor = entryStart;
        int guard = 0;
        while (cursor + 16 <= usedEnd && guard++ < 65536)
        {
            if (!TryParseIndexEntry(data, cursor, usedEnd, ownerReference, out IndexPathEvidence? evidence, out int entryLength, out ushort flags))
                break;

            liveOffsets.Add(cursor);
            if (evidence is not null)
                AddEvidence(evidence with { Confidence = Math.Min(100, baseConfidence), Source = source });

            if (entryLength <= 0 || (flags & 0x0002) != 0)
                break;
            cursor += entryLength;
        }

        // Recover deleted entries from the unused/slack portion. NTFS index entries are aligned;
        // scanning on 8-byte boundaries plus FILE_NAME validation keeps false positives low.
        int slackStart = entryStart;
        int slackEnd = allocatedEnd;
        for (int offset = slackStart; offset + MinIndexEntryLength <= slackEnd; offset += 8)
        {
            if (liveOffsets.Contains(offset))
                continue;

            if (!TryParseIndexEntry(data, offset, slackEnd, ownerReference, out IndexPathEvidence? evidence, out _, out _))
                continue;
            if (evidence is null)
                continue;

            int confidence = Math.Max(60, baseConfidence - 24);
            AddEvidence(evidence with { Confidence = confidence, Source = source + " slack" });
        }
    }

    private static bool TryParseIndexEntry(
        ReadOnlySpan<byte> data,
        int offset,
        int limit,
        ulong ownerReference,
        out IndexPathEvidence? evidence,
        out int entryLength,
        out ushort flags)
    {
        evidence = null;
        entryLength = 0;
        flags = 0;
        if (offset < 0 || offset + 16 > limit || limit > data.Length)
            return false;

        ulong fileReference = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
        entryLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 8, 2));
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 10, 2));
        flags = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 12, 2));

        if (fileReference == 0 || entryLength < 16 || (entryLength & 7) != 0 ||
            offset + entryLength > limit || keyLength < 66 || keyLength > entryLength - 16)
        {
            return false;
        }

        int keyStart = offset + 16;
        if (keyStart + keyLength > limit)
            return false;

        ReadOnlySpan<byte> key = data.Slice(keyStart, keyLength);
        ulong parentReference = BinaryPrimitives.ReadUInt64LittleEndian(key.Slice(0, 8));
        if (parentReference == 0)
            return false;

        byte nameChars = key[64];
        byte nameNamespace = key[65];
        int nameBytes = nameChars * 2;
        if (nameChars == 0 || nameChars > 255 || nameNamespace > 3 || 66 + nameBytes > key.Length)
            return false;

        string name;
        try
        {
            name = Encoding.Unicode.GetString(key.Slice(66, nameBytes)).TrimEnd('\0');
        }
        catch
        {
            return false;
        }

        if (!NtfsQuickScanService.IsUsableName(name) || name is "." or "..")
            return false;

        // A normal directory index stores a FILE_NAME whose parent is the directory itself.
        // When the owner is known, require matching record number to reject slack garbage while
        // still tolerating an old sequence number after deletion.
        if (ownerReference != 0)
        {
            long ownerRecord = (long)(ownerReference & 0x0000FFFFFFFFFFFFUL);
            long parentRecord = (long)(parentReference & 0x0000FFFFFFFFFFFFUL);
            if (ownerRecord != parentRecord)
                return false;
        }

        long realSize = 0;
        if (key.Length >= 56)
            realSize = Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(key.Slice(48, 8)));

        uint fileAttributes = key.Length >= 60
            ? BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(56, 4))
            : 0;
        bool isDirectory = (fileAttributes & 0x10000000u) != 0;
        DateTimeOffset? modifiedAt = key.Length >= 24 ? TryReadFileTime(key.Slice(16, 8)) : null;

        evidence = new IndexPathEvidence(
            fileReference,
            parentReference,
            name,
            isDirectory,
            realSize,
            modifiedAt,
            0,
            string.Empty);
        return true;
    }

    private void AddEvidence(IndexPathEvidence candidate)
    {
        if (candidate.FileReferenceNumber == 0 || candidate.ParentReferenceNumber == 0 ||
            !NtfsQuickScanService.IsUsableName(candidate.FileName))
        {
            return;
        }

        if (!_evidence.TryGetValue(candidate.FileReferenceNumber, out IndexPathEvidence? existing) ||
            existing is null || IsBetter(candidate, existing))
        {
            _evidence[candidate.FileReferenceNumber] = candidate;
        }
    }

    private static bool IsBetter(IndexPathEvidence candidate, IndexPathEvidence existing)
    {
        if (candidate.Confidence != existing.Confidence)
            return candidate.Confidence > existing.Confidence;

        if (candidate.IsDirectory != existing.IsDirectory)
            return candidate.IsDirectory;

        if (candidate.ModifiedAt.HasValue && existing.ModifiedAt.HasValue && candidate.ModifiedAt != existing.ModifiedAt)
            return candidate.ModifiedAt > existing.ModifiedAt;

        if (candidate.ModifiedAt.HasValue != existing.ModifiedAt.HasValue)
            return candidate.ModifiedAt.HasValue;

        return candidate.FileName.Length > existing.FileName.Length;
    }

    private static bool IsRicher(string candidate, string? current)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return false;
        if (string.IsNullOrWhiteSpace(current))
            return true;

        int candidateDepth = candidate.Count(ch => ch == '\\');
        int currentDepth = current.Count(ch => ch == '\\');
        if (candidateDepth != currentDepth)
            return candidateDepth > currentDepth;

        return candidate.Length > current.Length;
    }

    private static string ReadAttributeName(ReadOnlySpan<byte> record, int position, int attributeLength, byte nameLength)
    {
        if (nameLength == 0 || position + 12 > record.Length)
            return string.Empty;

        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(position + 10, 2));
        int start = position + nameOffset;
        int bytes = nameLength * 2;
        if (start < position || start + bytes > position + attributeLength || start + bytes > record.Length)
            return string.Empty;

        try
        {
            return Encoding.Unicode.GetString(record.Slice(start, bytes));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryFixupIndx(byte[] record, int bytesPerSector)
    {
        if (record.Length < 16 || bytesPerSector < 512 || record.Length % bytesPerSector != 0)
            return false;
        if (record[0] != (byte)'I' || record[1] != (byte)'N' || record[2] != (byte)'D' || record[3] != (byte)'X')
            return false;

        ushort usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4, 2));
        ushort usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6, 2));
        if (usaCount < 2 || usaOffset < 8 || usaOffset + usaCount * 2 > record.Length)
            return false;
        if (usaCount != record.Length / bytesPerSector + 1)
            return false;

        ushort updateSequence = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset, 2));
        for (int sector = 1; sector < usaCount; sector++)
        {
            int trailer = sector * bytesPerSector - 2;
            if (trailer < 0 || trailer + 2 > record.Length)
                return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(trailer, 2)) != updateSequence)
                return false;
        }

        for (int sector = 1; sector < usaCount; sector++)
        {
            int trailer = sector * bytesPerSector - 2;
            ushort replacement = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(usaOffset + sector * 2, 2));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(trailer, 2), replacement);
        }

        return true;
    }

    private static void AddCandidateSize(Span<int> candidates, ref int count, int size)
    {
        if (size < 1024 || size > MaxIndexRecordBytes || (size & (size - 1)) != 0)
            return;
        for (int i = 0; i < count; i++)
        {
            if (candidates[i] == size)
                return;
        }
        if (count < candidates.Length)
            candidates[count++] = size;
    }

    private static DateTimeOffset? TryReadFileTime(ReadOnlySpan<byte> value)
    {
        if (value.Length < 8)
            return null;
        long raw = BinaryPrimitives.ReadInt64LittleEndian(value);
        if (raw <= 0)
            return null;
        try
        {
            DateTimeOffset timestamp = DateTimeOffset.FromFileTime(raw).ToUniversalTime();
            return timestamp.Year is >= 1970 and <= 2200 ? timestamp : null;
        }
        catch
        {
            return null;
        }
    }
}
