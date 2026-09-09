using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class ExFatQuickScanService
{
    private readonly Func<RawDeviceReader>? _openReader;
    public ExFatQuickScanService() { }
    internal ExFatQuickScanService(Func<RawDeviceReader> openReader) => _openReader = openReader;

    private const int MaxResults = 500000;
    private const long MaxDirectoryBytes = 128L * 1024 * 1024;

    private sealed record DirectoryRef(uint FirstCluster, ulong DataLength, bool NoFatChain, string Path, bool Deleted = false);

    private sealed record StaleEntryEvidence(
        uint ContainerCluster,
        bool Deleted,
        bool IsDirectory,
        uint FirstCluster,
        ulong DataLength,
        bool NoFatChain,
        string Name,
        bool AllocationKnown,
        bool IsAllocated,
        byte[]? EntrySet);

    private sealed record DirectoryPathState(string Path, bool Historical, int Confidence);

    internal sealed record ExFatAllocationAssessment(int CheckedClusters, int AllocatedClusters, bool Sampled)
    {
        public double AllocatedRatio => CheckedClusters <= 0 ? 0d : AllocatedClusters * 100d / CheckedClusters;
        public bool HasEvidence => CheckedClusters > 0;
    }

    private sealed class ExFatAllocationBitmap
    {
        private const int CachePageSize = 64 * 1024;
        private const int MaxCachedPages = 128;
        private readonly RawDeviceReader _reader;
        private readonly IReadOnlyList<SourceExtent> _extents;
        private readonly long _length;
        private readonly Dictionary<long, byte[]> _cache = new();
        private readonly Queue<long> _cacheOrder = new();

        public ExFatAllocationBitmap(RawDeviceReader reader, IReadOnlyList<SourceExtent> extents, long length)
        {
            _reader = reader;
            _extents = extents;
            _length = length;
        }

        public bool TryIsAllocated(uint cluster, out bool allocated)
        {
            allocated = false;
            if (cluster < 2)
                return false;

            ulong bitIndex = cluster - 2UL;
            long byteIndex = checked((long)(bitIndex >> 3));
            if (byteIndex < 0 || byteIndex >= _length || !TryMapLogicalOffset(byteIndex, out long physicalOffset))
                return false;

            long pageStart = physicalOffset / CachePageSize * CachePageSize;
            if (!_cache.TryGetValue(pageStart, out byte[]? page))
            {
                page = new byte[CachePageSize];
                int read = _reader.ReadBestEffort(pageStart, page, out _);
                if (read <= 0)
                    return false;
                if (read < page.Length)
                    Array.Resize(ref page, read);

                _cache[pageStart] = page;
                _cacheOrder.Enqueue(pageStart);
                while (_cacheOrder.Count > MaxCachedPages)
                {
                    long expired = _cacheOrder.Dequeue();
                    _cache.Remove(expired);
                }
            }

            int pageOffset = checked((int)(physicalOffset - pageStart));
            if (pageOffset < 0 || pageOffset >= page.Length)
                return false;

            allocated = (page[pageOffset] & (1 << (int)(bitIndex & 7))) != 0;
            return true;
        }

        private bool TryMapLogicalOffset(long logicalOffset, out long physicalOffset)
        {
            long cursor = 0;
            foreach (SourceExtent extent in _extents)
            {
                if (logicalOffset >= cursor && logicalOffset < cursor + extent.Length)
                {
                    physicalOffset = extent.Offset + (logicalOffset - cursor);
                    return true;
                }
                cursor += extent.Length;
            }

            physicalOffset = 0;
            return false;
        }
    }

    public ScanReport Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        bool deepScanPrelude = false,
        bool includeExistingFiles = false,
        bool forceHistoricalPathRescue = false)
    {
        bool fastPrelude = deepScanPrelude && PortableQuickScanPolicy.IsPortableRecoverySource(device);
        long directoryByteLimit = PortableDeepScanPolicy.GetExFatDirectoryByteLimit(
            fastPrelude,
            MaxDirectoryBytes);
        int directoryLimit = PortableDeepScanPolicy.GetExFatDirectoryLimit(fastPrelude);
        long totalDirectoryBytes = 0;

        using RawDeviceReader reader = _openReader?.Invoke() ?? RawDeviceReader.OpenDevice(device, pauseGate, RecoveryMediaProfileService.Create(device));
        long volumeLength = reader.VolumeLength > 0 ? reader.VolumeLength : device.TotalBytes;
        byte[] boot = ReadValidatedExFatBootSector(reader);

        int bytesPerSectorShift = boot[108];
        int sectorsPerClusterShift = boot[109];
        if (bytesPerSectorShift is < 9 or > 12 || sectorsPerClusterShift > 25)
            throw new InvalidDataException("exFAT sektör geometrisi geçersiz.");

        int bytesPerSector = 1 << bytesPerSectorShift;
        long sectorsPerCluster = 1L << sectorsPerClusterShift;
        long clusterSizeLong = bytesPerSector * sectorsPerCluster;
        if (clusterSizeLong <= 0 || clusterSizeLong > 32L * 1024 * 1024)
            throw new InvalidDataException("exFAT küme boyutu desteklenmiyor.");

        int clusterSize = (int)clusterSizeLong;
        uint fatOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80, 4));
        uint clusterHeapOffsetSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88, 4));
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96, 4));

        if (rootCluster < 2 || clusterCount == 0)
            throw new InvalidDataException("exFAT kök dizin bilgisi okunamadı.");

        long fatOffset = checked(fatOffsetSectors * (long)bytesPerSector);
        long clusterHeapOffset = checked(clusterHeapOffsetSectors * (long)bytesPerSector);
        long rootOffset = ClusterToOffset(rootCluster, clusterHeapOffset, clusterSize);
        if (volumeLength <= 0 || fatOffset < 0 || fatOffset >= volumeLength ||
            clusterHeapOffset < 0 || clusterHeapOffset >= volumeLength ||
            rootOffset < 0 || rootOffset >= volumeLength)
            throw new InvalidDataException("exFAT volume geometrisi fiziksel aygıt sınırlarıyla uyuşmuyor.");

        var folderProgress = new HistoricalFolderProgress(progress);
        progress = folderProgress;
        var results = new List<RecoveryFileItem>();
        var queue = new Queue<DirectoryRef>();
        var visited = new HashSet<uint>();
        int reportedResultCount = 0;
        ExFatAllocationBitmap? allocationBitmap = null;
        queue.Enqueue(new DirectoryRef(rootCluster, 0, false, "\\"));

        while (queue.Count > 0 &&
               results.Count < MaxResults &&
               visited.Count < directoryLimit &&
               (!fastPrelude || totalDirectoryBytes < PortableDeepScanPolicy.ExFatPreludeTotalDirectoryBytes))
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            DirectoryRef directory = queue.Dequeue();
            if (directory.Deleted)
                folderProgress.Add(directory.Path);
            if (!visited.Add(directory.FirstCluster))
                continue;

            byte[] directoryData = ReadDirectoryData(
                reader,
                directory,
                fatOffset,
                clusterHeapOffset,
                clusterSize,
                clusterCount,
                directoryByteLimit,
                cancellationToken);

            totalDirectoryBytes += directoryData.LongLength;

            if (directory.FirstCluster == rootCluster && allocationBitmap is null)
            {
                allocationBitmap = TryCreateAllocationBitmap(
                    reader,
                    directoryData,
                    fatOffset,
                    clusterHeapOffset,
                    clusterSize,
                    clusterCount);
            }

            ParseDirectory(
                reader,
                directoryData,
                device,
                volumeLength,
                queue,
                results,
                fatOffset,
                clusterHeapOffset,
                clusterSize,
                clusterCount,
                allocationBitmap,
                includeExistingFiles,
                directory.Path,
                directory.Deleted);

            RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                : null;
            reportedResultCount = results.Count;

            progress?.Report(new OperationProgress(
                queue.Count == 0 ? 100d : Math.Min(95d, 5d + visited.Count),
                includeExistingFiles ? "exFAT • Dosya Kataloğu + Silinmiş Kayıtlar" : "exFAT • Silinmiş Dosya Analizi",
                includeExistingFiles
                    ? $"Klasör kayıtları: {visited.Count:N0} • mevcut ve silinmiş dosyalar doğrulanıyor."
                    : $"Klasör kayıtları: {visited.Count:N0} • silinmiş dosyalar doğrulanıyor; zaman bilgisi bulunan kayıtlar öncelikli işleniyor.",
                visited.Count,
                visited.Count + queue.Count,
                results.Count,
                newFiles));
        }

        // Deep Scan can request the exact same historical-directory resolver used by
        // Quick Scan without changing Quick Scan's default behavior. This is metadata-only.
        bool staleRescue = forceHistoricalPathRescue ||
                           (!includeExistingFiles && !deepScanPrelude && results.Count == 0);
        if (staleRescue)
            ScanStaleDirectorySets(reader, device, volumeLength, fatOffset, clusterHeapOffset,
                clusterSize, clusterCount, rootCluster, allocationBitmap, results, progress, pauseGate, cancellationToken);

        string bitmapSummary = allocationBitmap is null
            ? "Allocation Bitmap okunamadı"
            : "Allocation Bitmap ile yeniden tahsis kontrolü aktif";
        string summary = results.Count >= MaxResults
            ? $"exFAT kayıt tarama kapasitesine ulaşıldı • {results.Count:N0} dosya kaydı doğrulandı • {bitmapSummary}."
            : includeExistingFiles
                ? $"exFAT katalog analizi tamamlandı • {results.Count:N0} mevcut/silinmiş dosya doğrulandı • RAW carving devam edecek."
                : $"exFAT metadata analizi tamamlandı • {results.Count:N0} silinmiş dosya doğrulandı • {bitmapSummary}.";

        if (fastPrelude)
            summary += " • Deep Scan hızlı metadata ön analizi tamamlandı; geniş dizin taraması tam RAW aşamasına bırakıldı.";

        if (staleRescue)
            summary += " • exFAT eski dizin kayıtları ilk 512 MB içinde denetlendi; dosya kaydı kalmamış/biçimlendirilmiş kartlarda Derin Tarama kullanın.";
        return new ScanReport(RecoveryScanPriorityService.OrderNewestFirst(results).ToList(), summary, ScanMode.Quick) { HistoricalFolders = folderProgress.Snapshot() };
    }

    // Metadata rescue only: never infer a file from random payload without a complete,
    // checksummed file/stream/name set. InUse bits are restored for deleted-set checksums.
    internal static bool IsValidStaleEntrySet(ReadOnlySpan<byte> data, out int setBytes)
    {
        setBytes = 0;
        if (data.Length < 96 || data[0] is not (0x85 or 0x05)) return false;
        int secondaries = data[1];
        if (secondaries is < 2 or > 18 || data.Length < (secondaries + 1) * 32) return false;
        int length = (secondaries + 1) * 32;
        if ((data[32] & 0x7F) != 0x40 || (data[33] & 0xFC) != 0 || (data[33] & 1) == 0) return false;
        int nameLength = data[35];
        if (nameLength == 0 || secondaries != 1 + (nameLength + 14) / 15) return false;
        if ((BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2)) & ~0x37) != 0) return false;
        ulong validLength = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(40, 8));
        ulong fileLength = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(56, 8));
        if (validLength > fileLength || fileLength == 0 || fileLength > long.MaxValue) return false;
        for (int i = 64; i < length; i += 32)
            if ((data[i] & 0x7F) != 0x41) return false;
        for (int i = 0; i < nameLength; i++)
        {
            char ch = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(66 + (i / 15) * 32 + (i % 15) * 2, 2));
            if (ch < 32 || ch == 0xFFFF || "\"*/:<>?\\|".Contains(ch)) return false;
        }
        ushort actual = 0, restored = 0;
        for (int i = 0; i < length; i++)
        {
            if (i is 2 or 3) continue;
            actual = unchecked((ushort)(((actual << 15) | (actual >> 1)) + data[i]));
            byte value = i % 32 == 0 ? (byte)(data[i] | 0x80) : data[i];
            restored = unchecked((ushort)(((restored << 15) | (restored >> 1)) + value));
        }
        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
        if (actual != expected && restored != expected) return false;
        setBytes = length;
        return true;
    }

    private static void ScanStaleDirectorySets(
        RawDeviceReader reader, StorageDeviceInfo device, long volumeLength, long fatOffset,
        long heapOffset, int clusterSize, uint clusterCount, uint rootCluster, ExFatAllocationBitmap? bitmap,
        List<RecoveryFileItem> results,
        IProgress<OperationProgress>? progress, OperationPauseGate? pauseGate, CancellationToken token)
    {
        const int blockSize = 4 * 1024 * 1024;
        const int overlap = 608;
        long end = Math.Min(volumeLength, heapOffset + PortableQuickScanPolicy.FatDirectoryRescueMaxBytes);
        byte[] buffer = new byte[blockSize + overlap];
        var evidence = new List<StaleEntryEvidence>();
        var seen = new HashSet<(long, long)>();
        for (long offset = heapOffset; offset < end && evidence.Count < MaxResults; offset += blockSize)
        {
            pauseGate?.Wait(token);
            token.ThrowIfCancellationRequested();
            int read = reader.ReadBestEffort(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, end - offset)), out _);
            for (int i = 0; i < Math.Min(blockSize, read) && evidence.Count < MaxResults; i += 32)
            {
                if ((i & 0xFFFF) == 0) token.ThrowIfCancellationRequested();
                ReadOnlySpan<byte> candidate = buffer.AsSpan(i, read - i);
                if (!IsValidStaleEntrySet(candidate, out int length))
                    continue;

                if (TryCreateStaleEntryEvidence(
                        candidate[..length],
                        offset + i,
                        heapOffset,
                        clusterSize,
                        clusterCount,
                        bitmap,
                        out StaleEntryEvidence? item) &&
                    item is not null)
                {
                    evidence.Add(item);
                }
            }

            progress?.Report(new OperationProgress(
                95d * Math.Min(end - heapOffset, offset - heapOffset + blockSize) / Math.Max(1, end - heapOffset),
                "exFAT • Eski Dizin Analizi", "Eski dosya ve klasör kayıtları sağlama toplamı ile denetleniyor; özgün yol zinciri hazırlanıyor.",
                Math.Min(end - heapOffset, offset - heapOffset + blockSize), end - heapOffset, results.Count));
        }

        if (evidence.Count == 0)
            return;

        Dictionary<uint, DirectoryPathState> directoryPaths = BuildHistoricalDirectoryPathMap(
            reader,
            fatOffset,
            clusterSize,
            clusterCount,
            rootCluster,
            evidence,
            token);

        int resolvedPaths = 0;
        int unresolvedPaths = 0;
        foreach (StaleEntryEvidence item in evidence)
        {
            if (results.Count >= MaxResults)
                break;
            if (item.IsDirectory || item.EntrySet is null)
                continue;

            bool parentResolved = directoryPaths.TryGetValue(item.ContainerCluster, out DirectoryPathState? parentState) &&
                                  parentState is not null;
            bool historicalByParent = parentResolved && parentState!.Historical;

            // Deleted entry sets are explicit forensic evidence. Active entry sets are only
            // recoverable when they belong to a deleted/orphaned directory, or when the
            // allocation bitmap cannot prove that their data is still owned by a live file.
            bool shouldRecover = item.Deleted ||
                                 historicalByParent ||
                                 (!parentResolved && (!item.AllocationKnown || !item.IsAllocated));
            if (!shouldRecover)
                continue;

            var found = new List<RecoveryFileItem>(1);
            ParseDirectory(
                reader,
                item.EntrySet,
                device,
                volumeLength,
                new Queue<DirectoryRef>(),
                found,
                fatOffset,
                heapOffset,
                clusterSize,
                clusterCount,
                bitmap,
                false,
                parentResolved ? parentState!.Path : "\\",
                deletedDirectory: true);

            foreach (RecoveryFileItem file in found)
            {
                if (!seen.Add((file.SourceOffset, file.SizeBytes)))
                    continue;

                if (!parentResolved)
                {
                    // Do not invent a synthetic folder. An unresolved stale record belongs in
                    // the UI's "Yolu Bulunamayan Dosyalar" bucket until real metadata proves it.
                    file.RecoveredOriginalPath = null;
                    unresolvedPaths++;
                }
                else
                {
                    resolvedPaths++;
                }

                results.Add(file);
                if (results.Count >= MaxResults)
                    break;
            }
        }

        string[] historicalFolders = directoryPaths.Values
            .Where(state => state.Historical && state.Path != "\\")
            .Select(state => state.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(ch => ch == '\\'))
            .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        progress?.Report(new OperationProgress(
            100,
            "exFAT • Özgün Yol Eşleştirme",
            $"Eski dizin zinciri tamamlandı • özgün yolu eşleşen {resolvedPaths:N0} • yolu doğrulanamayan {unresolvedPaths:N0}.",
            end - heapOffset,
            end - heapOffset,
            results.Count,
            results.Count > 0 ? RecoveryScanPriorityService.OrderNewestFirst(results).ToArray() : null,
            NewHistoricalFolders: historicalFolders));
    }

    private static bool TryCreateStaleEntryEvidence(
        ReadOnlySpan<byte> entrySet,
        long entryOffset,
        long heapOffset,
        int clusterSize,
        uint clusterCount,
        ExFatAllocationBitmap? bitmap,
        out StaleEntryEvidence? evidence)
    {
        evidence = null;
        if (entrySet.Length < 96 || clusterSize <= 0 || entryOffset < heapOffset)
            return false;

        bool deleted = entrySet[0] == 0x05;
        ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(entrySet.Slice(4, 2));
        bool isDirectory = (attributes & 0x0010) != 0;
        bool noFatChain = (entrySet[33] & 0x02) != 0;
        byte nameLength = entrySet[35];
        uint firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(entrySet.Slice(52, 4));
        ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(entrySet.Slice(56, 8));
        if (firstCluster < 2 || firstCluster >= clusterCount + 2 || dataLength == 0)
            return false;

        long clusterIndex = (entryOffset - heapOffset) / clusterSize;
        if (clusterIndex < 0 || clusterIndex > uint.MaxValue - 2L)
            return false;
        uint containerCluster = checked((uint)(clusterIndex + 2));
        if (containerCluster < 2 || containerCluster >= clusterCount + 2)
            return false;

        var nameBuilder = new StringBuilder(nameLength);
        for (int position = 64; position + 32 <= entrySet.Length; position += 32)
            AppendExFatName(entrySet.Slice(position, 32), nameBuilder);

        string name = nameBuilder.ToString();
        if (nameLength > 0 && name.Length > nameLength)
            name = name[..nameLength];
        name = FileTypeHelper.SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(name))
            return false;

        bool allocated = false;
        bool allocationKnown = bitmap is not null && bitmap.TryIsAllocated(firstCluster, out allocated);
        bool isAllocated = allocationKnown && allocated;

        // Keep the complete checksummed entry set for every file as metadata evidence. Whether
        // an active set is exposed is decided only after its parent directory is classified as
        // current or historical. This is essential after a quick format: old entry sets often
        // keep their active bit even though the whole directory tree is no longer live.
        byte[]? bytes = !isDirectory ? entrySet.ToArray() : null;

        evidence = new StaleEntryEvidence(
            containerCluster,
            deleted,
            isDirectory,
            firstCluster,
            dataLength,
            noFatChain,
            name,
            allocationKnown,
            isAllocated,
            bytes);
        return isDirectory || bytes is not null;
    }

    private static Dictionary<uint, DirectoryPathState> BuildHistoricalDirectoryPathMap(
        RawDeviceReader reader,
        long fatOffset,
        int clusterSize,
        uint clusterCount,
        uint rootCluster,
        IReadOnlyList<StaleEntryEvidence> evidence,
        CancellationToken token)
    {
        var paths = new Dictionary<uint, DirectoryPathState>();
        var rootState = new DirectoryPathState("\\", Historical: false, Confidence: 10000);
        RegisterDirectoryClusters(
            reader,
            fatOffset,
            clusterSize,
            clusterCount,
            rootCluster,
            dataLength: 0,
            noFatChain: false,
            rootState,
            paths);

        // A reformatted exFAT card can have a completely new RootDirectoryCluster while the old
        // root directory and its checksummed entry sets are still physically present. In that
        // case anchoring only to the current root makes every old file look pathless. Build the
        // historical directory graph first and identify only strong orphan-root components:
        // a candidate must not belong to any known child-directory extent and must own a child
        // directory whose own directory cluster still contains validated entry-set evidence.
        // This gives us the old volume root without inventing a synthetic folder name.
        var containerClusters = new HashSet<uint>(evidence.Select(item => item.ContainerCluster));
        var childDirectoryClusters = new HashSet<uint>();
        var strongHistoricalRootParents = new HashSet<uint>();
        foreach (StaleEntryEvidence item in evidence)
        {
            if (!item.IsDirectory)
                continue;

            bool childContainsValidatedEntries = false;
            foreach (uint cluster in EnumerateDirectoryClusters(
                         reader,
                         fatOffset,
                         clusterSize,
                         clusterCount,
                         item.FirstCluster,
                         item.DataLength,
                         item.NoFatChain))
            {
                childDirectoryClusters.Add(cluster);
                if (containerClusters.Contains(cluster))
                    childContainsValidatedEntries = true;
            }

            if (childContainsValidatedEntries)
                strongHistoricalRootParents.Add(item.ContainerCluster);
        }

        foreach (uint candidateRoot in containerClusters)
        {
            if (candidateRoot == rootCluster || childDirectoryClusters.Contains(candidateRoot) || paths.ContainsKey(candidateRoot))
                continue;
            if (!strongHistoricalRootParents.Contains(candidateRoot))
                continue;

            paths[candidateRoot] = new DirectoryPathState("\\", Historical: true, Confidence: 7000);
        }

        // Resolve from both the current root and proven historical roots outward. Deleted
        // directory entries mark the path as historical; active children below a historical
        // parent remain historical as well. This is how old AVCHD -> BDMV -> STREAM/CLIPINF
        // trees survive a format without ever assigning a guessed folder.
        for (int pass = 0; pass < 64; pass++)
        {
            token.ThrowIfCancellationRequested();
            bool changed = false;

            foreach (StaleEntryEvidence item in evidence)
            {
                if (!item.IsDirectory || item.FirstCluster == item.ContainerCluster)
                    continue;
                if (!paths.TryGetValue(item.ContainerCluster, out DirectoryPathState? parent) || parent is null)
                    continue;

                bool historical = parent.Historical || item.Deleted;
                int confidence = Math.Max(1, parent.Confidence - (item.Deleted ? 20 : 1));
                var candidate = new DirectoryPathState(
                    CombineRecoveredPath(parent.Path, item.Name),
                    historical,
                    confidence);

                if (paths.TryGetValue(item.FirstCluster, out DirectoryPathState? existing) &&
                    existing is not null &&
                    !IsBetterDirectoryPath(candidate, existing))
                {
                    continue;
                }

                changed |= RegisterDirectoryClusters(
                    reader,
                    fatOffset,
                    clusterSize,
                    clusterCount,
                    item.FirstCluster,
                    item.DataLength,
                    item.NoFatChain,
                    candidate,
                    paths);
            }

            if (!changed)
                break;
        }

        return paths;
    }

    private static IReadOnlyList<uint> EnumerateDirectoryClusters(
        RawDeviceReader reader,
        long fatOffset,
        int clusterSize,
        uint clusterCount,
        uint firstCluster,
        ulong dataLength,
        bool noFatChain)
    {
        var clusters = new List<uint>();
        if (clusterSize <= 0 || firstCluster < 2 || firstCluster >= clusterCount + 2)
            return clusters;

        long maxClustersByLength = dataLength > 0
            ? Math.Max(1L, (long)Math.Min((ulong)int.MaxValue, (dataLength + (ulong)clusterSize - 1) / (ulong)clusterSize))
            : 1L;
        int maxClusters = checked((int)Math.Min(maxClustersByLength, 32768L));
        uint cluster = firstCluster;
        var visited = noFatChain ? null : new HashSet<uint>();

        for (int index = 0; index < maxClusters; index++)
        {
            if (cluster < 2 || cluster >= clusterCount + 2)
                break;
            if (visited is not null && !visited.Add(cluster))
                break;

            clusters.Add(cluster);
            if (noFatChain)
            {
                if (cluster == uint.MaxValue)
                    break;
                cluster++;
                continue;
            }

            uint next = ReadFatEntry(reader, fatOffset, cluster);
            if (next >= 0xFFFFFFF8 || next == 0 || next == 0xFFFFFFFF || next == cluster)
                break;
            cluster = next;
        }

        return clusters;
    }

    private static bool RegisterDirectoryClusters(
        RawDeviceReader reader,
        long fatOffset,
        int clusterSize,
        uint clusterCount,
        uint firstCluster,
        ulong dataLength,
        bool noFatChain,
        DirectoryPathState state,
        IDictionary<uint, DirectoryPathState> paths)
    {
        if (clusterSize <= 0 || firstCluster < 2 || firstCluster >= clusterCount + 2)
            return false;

        long maxClustersByLength = dataLength > 0
            ? Math.Max(1L, (long)Math.Min((ulong)int.MaxValue, (dataLength + (ulong)clusterSize - 1) / (ulong)clusterSize))
            : Math.Max(1L, MaxDirectoryBytes / clusterSize);
        int maxClusters = checked((int)Math.Min(maxClustersByLength, 32768L));

        bool changed = false;
        uint cluster = firstCluster;
        var visited = noFatChain ? null : new HashSet<uint>();
        for (int index = 0; index < maxClusters; index++)
        {
            if (cluster < 2 || cluster >= clusterCount + 2)
                break;
            if (visited is not null && !visited.Add(cluster))
                break;

            if (!paths.TryGetValue(cluster, out DirectoryPathState? existing) || existing is null || IsBetterDirectoryPath(state, existing))
            {
                paths[cluster] = state;
                changed = true;
            }

            if (noFatChain)
            {
                if (cluster == uint.MaxValue)
                    break;
                cluster++;
                continue;
            }

            uint next = ReadFatEntry(reader, fatOffset, cluster);
            if (next >= 0xFFFFFFF8 || next == 0 || next == 0xFFFFFFFF || next == cluster)
                break;
            cluster = next;
        }

        return changed;
    }

    private static bool IsBetterDirectoryPath(DirectoryPathState candidate, DirectoryPathState existing)
    {
        if (candidate.Confidence != existing.Confidence)
            return candidate.Confidence > existing.Confidence;
        if (candidate.Historical != existing.Historical)
            return !candidate.Historical;

        int candidateDepth = candidate.Path.Count(ch => ch == '\\');
        int existingDepth = existing.Path.Count(ch => ch == '\\');
        if (candidateDepth != existingDepth)
            return candidateDepth < existingDepth;

        return string.Compare(candidate.Path, existing.Path, StringComparison.OrdinalIgnoreCase) < 0;
    }

    private static byte[] ReadValidatedExFatBootSector(RawDeviceReader reader)
    {
        int sectorSize = Math.Max(512, reader.SectorSize);
        byte[] primary = ReadBestEffortBytes(reader, 0, sectorSize);
        if (LooksLikeExFatBootSector(primary))
            return primary;

        // exFAT keeps a duplicate boot region starting at sector 12.
        byte[] backup = ReadBestEffortBytes(reader, 12L * sectorSize, sectorSize);
        if (LooksLikeExFatBootSector(backup))
            return backup;

        throw new InvalidDataException("exFAT birincil ve yedek önyükleme kayıtları doğrulanamadı.");
    }

    private static bool LooksLikeExFatBootSector(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 120 || !boot.Slice(3, 8).SequenceEqual("EXFAT   "u8))
            return false;

        int bytesPerSectorShift = boot[108];
        int sectorsPerClusterShift = boot[109];
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(92, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(96, 4));

        return bytesPerSectorShift is >= 9 and <= 12
               && sectorsPerClusterShift is >= 0 and <= 25
               && clusterCount > 0 && rootCluster >= 2 && rootCluster < clusterCount + 2;
    }

    private static void ParseDirectory(
        RawDeviceReader reader,
        byte[] data,
        StorageDeviceInfo device,
        long volumeLength,
        Queue<DirectoryRef> directories,
        List<RecoveryFileItem> results,
        long fatOffset,
        long clusterHeapOffset,
        int clusterSize,
        uint clusterCount,
        ExFatAllocationBitmap? allocationBitmap,
        bool includeExistingFiles,
        string directoryPath,
        bool deletedDirectory = false)
    {
        int position = 0;

        while (position + 32 <= data.Length && results.Count < MaxResults)
        {
            ReadOnlySpan<byte> primary = data.AsSpan(position, 32);
            byte type = primary[0];

            if (type == 0x00)
                break;

            bool activeFile = type == 0x85;
            bool deletedFile = type == 0x05;

            if (!activeFile && !deletedFile)
            {
                position += 32;
                continue;
            }

            if (deletedDirectory)
            {
                activeFile = false;
                deletedFile = true;
            }

            int secondaryCount = primary[1];
            int setBytes = checked((secondaryCount + 1) * 32);
            if (secondaryCount <= 0 || position + setBytes > data.Length)
            {
                position += 32;
                continue;
            }

            ushort attributes = BinaryPrimitives.ReadUInt16LittleEndian(primary.Slice(4, 2));
            bool isDirectory = (attributes & 0x0010) != 0;
            DateTimeOffset? fileSystemCreatedAt = TryReadExFatCreatedTime(primary);
            DateTimeOffset? fileSystemModifiedAt = TryReadExFatModifiedTime(primary);

            ReadOnlySpan<byte> streamEntry = default;
            var nameBuilder = new StringBuilder();

            for (int i = 1; i <= secondaryCount; i++)
            {
                ReadOnlySpan<byte> secondary = data.AsSpan(position + i * 32, 32);
                byte secondaryType = secondary[0];

                if (secondaryType is 0xC0 or 0x40)
                {
                    streamEntry = secondary;
                }
                else if (secondaryType is 0xC1 or 0x41)
                {
                    AppendExFatName(secondary, nameBuilder);
                }
            }

            if (!streamEntry.IsEmpty)
            {
                byte generalFlags = streamEntry[1];
                bool noFatChain = (generalFlags & 0x02) != 0;
                byte nameLength = streamEntry[3];
                uint firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(streamEntry.Slice(20, 4));
                ulong dataLength = BinaryPrimitives.ReadUInt64LittleEndian(streamEntry.Slice(24, 8));

                string fileName = nameBuilder.ToString();
                if (nameLength > 0 && fileName.Length > nameLength)
                    fileName = fileName[..nameLength];

                if ((activeFile || deletedFile) && isDirectory &&
                    firstCluster >= 2 && firstCluster < clusterCount + 2)
                {
                    // exFAT keeps enough stream metadata in many deleted directory sets to
                    // walk the orphaned folder tree. Include those directories in Quick Scan.
                    directories.Enqueue(new DirectoryRef(
                        firstCluster,
                        dataLength,
                        noFatChain,
                        CombineRecoveredPath(directoryPath, fileName),
                        deletedFile));
                }
                else if ((deletedFile || includeExistingFiles) && !isDirectory &&
                         firstCluster >= 2 && firstCluster < clusterCount + 2 &&
                         dataLength > 0 && dataLength <= (ulong)long.MaxValue)
                {
                    fileName = FileTypeHelper.SanitizeFileName(fileName);
                    string extension = FileTypeHelper.Normalize(Path.GetExtension(fileName));
                    string recoveredPath = CombineRecoveredPath(directoryPath, fileName);

                    long sourceOffset = ClusterToOffset(firstCluster, clusterHeapOffset, clusterSize);
                    long fileLength = (long)dataLength;

                    if (sourceOffset >= 0 && sourceOffset < volumeLength)
                    {
                        byte[] header = deletedFile ? ReadBestEffortBytes(reader, sourceOffset, (int)Math.Min(128L, fileLength)) : [];
                        bool headerOkay = FileHeaderValidator.LooksLike(extension, header);

                        if (noFatChain)
                        {
                            if (fileLength <= volumeLength - sourceOffset)
                            {
                                var contiguousExtents = new[] { new SourceExtent(sourceOffset, fileLength) };
                                ExFatAllocationAssessment allocation = AssessAllocation(
                                    allocationBitmap,
                                    contiguousExtents,
                                    clusterHeapOffset,
                                    clusterSize);
                                string state = activeFile ? "Çok İyi" : DetermineExFatState(headerOkay, chainComplete: true, allocation);

                                results.Add(new RecoveryFileItem
                                {
                                    FileName = fileName,
                                    Extension = extension,
                                    SizeBytes = fileLength,
                                    RecoveryState = state,
                                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                                    SourceText = activeFile
                                        ? $"exFAT mevcut dosya • contiguous • küme {firstCluster:N0}"
                                        : $"exFAT silinmiş dosya • contiguous • küme {firstCluster:N0}{FormatAllocationEvidence(allocation)}",
                                    SourceKind = RecoverySourceKind.ExFatContiguous,
                                    IsExistingFile = activeFile,
                                    SourceOffset = sourceOffset,
                                    ClusterSize = clusterSize,
                                    FileSystemCreatedAt = fileSystemCreatedAt,
                                    FileSystemModifiedAt = fileSystemModifiedAt,
                                    RecoveredOriginalPath = recoveredPath
                                });
                            }
                        }
                        else
                        {
                            IReadOnlyList<SourceExtent> extents = BuildFatExtents(
                                reader,
                                fatOffset,
                                clusterHeapOffset,
                                clusterSize,
                                clusterCount,
                                firstCluster,
                                fileLength,
                                out long coveredBytes);

                            if (extents.Count > 0 && coveredBytes > 0)
                            {
                                long recoverableLength = Math.Min(fileLength, coveredBytes);
                                bool chainComplete = coveredBytes >= fileLength;
                                ExFatAllocationAssessment allocation = AssessAllocation(
                                    allocationBitmap,
                                    extents,
                                    clusterHeapOffset,
                                    clusterSize);
                                string state = activeFile
                                    ? chainComplete ? "Çok İyi" : "Kısmi"
                                    : DetermineExFatState(headerOkay, chainComplete, allocation);

                                results.Add(new RecoveryFileItem
                                {
                                    FileName = fileName,
                                    Extension = extension,
                                    SizeBytes = recoverableLength,
                                    RecoveryState = state,
                                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                                    SourceText = activeFile
                                        ? $"exFAT mevcut dosya • FAT zinciri • {extents.Count:N0} parça"
                                        : $"exFAT silinmiş dosya • FAT zinciri • {extents.Count:N0} parça{FormatAllocationEvidence(allocation)}",
                                    SourceKind = RecoverySourceKind.Extents,
                                    IsExistingFile = activeFile,
                                    SourceOffset = sourceOffset,
                                    SourceExtents = extents,
                                    ClusterSize = clusterSize,
                                    FileSystemCreatedAt = fileSystemCreatedAt,
                                    FileSystemModifiedAt = fileSystemModifiedAt,
                                    RecoveredOriginalPath = recoveredPath
                                });
                            }
                        }
                    }
                }
            }

            position += setBytes;
        }
    }


    private static string CombineRecoveredPath(string directoryPath, string name)
    {
        string cleanName = FileTypeHelper.SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryPath == "\\")
            return $"\\{cleanName}";
        return $"{directoryPath.TrimEnd('\\')}\\{cleanName}";
    }

    private static ExFatAllocationBitmap? TryCreateAllocationBitmap(
        RawDeviceReader reader,
        ReadOnlySpan<byte> rootDirectory,
        long fatOffset,
        long clusterHeapOffset,
        int clusterSize,
        uint clusterCount)
    {
        for (int position = 0; position + 32 <= rootDirectory.Length; position += 32)
        {
            ReadOnlySpan<byte> entry = rootDirectory.Slice(position, 32);
            if (entry[0] == 0x00)
                break;
            if (entry[0] != 0x81 || (entry[1] & 0x01) != 0)
                continue;

            uint firstCluster = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4));
            ulong dataLengthRaw = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(24, 8));
            if (firstCluster < 2 || firstCluster >= clusterCount + 2 ||
                dataLengthRaw == 0 || dataLengthRaw > (ulong)long.MaxValue)
                continue;

            long dataLength = (long)dataLengthRaw;
            IReadOnlyList<SourceExtent> extents = BuildFatExtents(
                reader,
                fatOffset,
                clusterHeapOffset,
                clusterSize,
                clusterCount,
                firstCluster,
                dataLength,
                out long coveredBytes);

            if (extents.Count > 0 && coveredBytes >= Math.Min(dataLength, clusterSize))
                return new ExFatAllocationBitmap(reader, extents, Math.Min(dataLength, coveredBytes));
        }

        return null;
    }

    internal static bool IsClusterAllocatedInBitmap(ReadOnlySpan<byte> bitmap, uint cluster)
    {
        if (cluster < 2)
            return false;

        ulong bitIndex = cluster - 2UL;
        ulong byteIndex = bitIndex >> 3;
        if (byteIndex >= (ulong)bitmap.Length)
            return false;

        return (bitmap[(int)byteIndex] & (1 << (int)(bitIndex & 7))) != 0;
    }

    private static ExFatAllocationAssessment AssessAllocation(
        ExFatAllocationBitmap? bitmap,
        IReadOnlyList<SourceExtent> extents,
        long clusterHeapOffset,
        int clusterSize)
    {
        if (bitmap is null || extents.Count == 0 || clusterSize <= 0)
            return new ExFatAllocationAssessment(0, 0, false);

        const int maxSamples = 4096;
        long totalClusters = 0;
        var clusterRanges = new List<(uint FirstCluster, long Count)>();
        foreach (SourceExtent extent in extents)
        {
            if (extent.Offset < clusterHeapOffset || extent.Length <= 0)
                continue;

            long relative = extent.Offset - clusterHeapOffset;
            if (relative % clusterSize != 0)
                continue;

            long count = (extent.Length + clusterSize - 1) / clusterSize;
            long firstLong = relative / clusterSize + 2;
            if (count <= 0 || firstLong < 2 || firstLong > uint.MaxValue)
                continue;

            clusterRanges.Add(((uint)firstLong, count));
            totalClusters += count;
        }

        if (totalClusters <= 0)
            return new ExFatAllocationAssessment(0, 0, false);

        int wantedSamples = (int)Math.Min(maxSamples, totalClusters);
        int checkedClusters = 0;
        int allocatedClusters = 0;
        int rangeIndex = 0;
        long rangeStart = 0;

        for (int sampleIndex = 0; sampleIndex < wantedSamples; sampleIndex++)
        {
            long target = wantedSamples == 1
                ? 0
                : sampleIndex * (totalClusters - 1) / (wantedSamples - 1);

            while (rangeIndex < clusterRanges.Count &&
                   target >= rangeStart + clusterRanges[rangeIndex].Count)
            {
                rangeStart += clusterRanges[rangeIndex].Count;
                rangeIndex++;
            }
            if (rangeIndex >= clusterRanges.Count)
                break;

            (uint firstCluster, long count) = clusterRanges[rangeIndex];
            long within = Math.Clamp(target - rangeStart, 0, count - 1);
            ulong candidate = (ulong)firstCluster + (ulong)within;
            if (candidate > uint.MaxValue)
                continue;

            if (bitmap.TryIsAllocated((uint)candidate, out bool allocated))
            {
                checkedClusters++;
                if (allocated)
                    allocatedClusters++;
            }
        }

        return new ExFatAllocationAssessment(
            checkedClusters,
            allocatedClusters,
            totalClusters > wantedSamples);
    }

    private static string DetermineExFatState(
        bool headerOkay,
        bool chainComplete,
        ExFatAllocationAssessment allocation)
    {
        if (!headerOkay)
            return "Zayıf";
        if (!chainComplete)
            return "Kısmi";
        if (!allocation.HasEvidence)
            return "İyi";
        if (allocation.AllocatedClusters == 0)
            return "Çok İyi";
        return allocation.AllocatedRatio <= 10d ? "Kısmi" : "Zayıf";
    }

    private static string FormatAllocationEvidence(ExFatAllocationAssessment allocation)
    {
        if (!allocation.HasEvidence)
            return "";
        if (allocation.AllocatedClusters == 0)
            return allocation.Sampled
                ? $" • bitmap {allocation.CheckedClusters:N0} örnek boş"
                : " • bitmap boş";

        string sampleText = allocation.Sampled ? " örnek" : "";
        return $" • bitmap{sampleText} %{allocation.AllocatedRatio:0.#} yeniden tahsis";
    }


    private static DateTimeOffset? TryReadExFatCreatedTime(ReadOnlySpan<byte> primary) =>
        TryReadExFatTimestamp(primary, timeOffset: 8, dateOffset: 10, tenMsOffset: 20, utcOffset: 22);

    private static DateTimeOffset? TryReadExFatModifiedTime(ReadOnlySpan<byte> primary) =>
        TryReadExFatTimestamp(primary, timeOffset: 12, dateOffset: 14, tenMsOffset: 21, utcOffset: 23);

    private static DateTimeOffset? TryReadExFatTimestamp(
        ReadOnlySpan<byte> primary,
        int timeOffset,
        int dateOffset,
        int tenMsOffset,
        int utcOffset)
    {
        if (primary.Length <= Math.Max(utcOffset, tenMsOffset) || primary.Length < dateOffset + 2)
            return null;

        ushort rawTime = BinaryPrimitives.ReadUInt16LittleEndian(primary.Slice(timeOffset, 2));
        ushort rawDate = BinaryPrimitives.ReadUInt16LittleEndian(primary.Slice(dateOffset, 2));
        if (rawDate == 0) return null;

        int year = 1980 + ((rawDate >> 9) & 0x7F);
        int month = (rawDate >> 5) & 0x0F;
        int day = rawDate & 0x1F;
        int hour = (rawTime >> 11) & 0x1F;
        int minute = (rawTime >> 5) & 0x3F;
        int second = (rawTime & 0x1F) * 2;
        int tenMs = primary[tenMsOffset];
        if (tenMs > 199) tenMs = 0;
        second += tenMs / 100;
        int milliseconds = (tenMs % 100) * 10;

        try
        {
            var local = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), milliseconds, DateTimeKind.Unspecified);
            TimeSpan offset = TimeZoneInfo.Local.GetUtcOffset(local);
            byte rawOffset = primary[utcOffset];
            if ((rawOffset & 0x80) != 0)
            {
                int quarters = rawOffset & 0x7F;
                if ((quarters & 0x40) != 0) quarters -= 0x80;
                TimeSpan candidateOffset = TimeSpan.FromMinutes(quarters * 15);
                if (candidateOffset >= TimeSpan.FromHours(-14) && candidateOffset <= TimeSpan.FromHours(14))
                    offset = candidateOffset;
            }

            var result = new DateTimeOffset(local, offset);
            return result <= DateTimeOffset.Now.AddYears(2) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SourceExtent> BuildFatExtents(
        RawDeviceReader reader,
        long fatOffset,
        long clusterHeapOffset,
        int clusterSize,
        uint clusterCount,
        uint firstCluster,
        long fileLength,
        out long coveredBytes)
    {
        var extents = new List<SourceExtent>();
        var visited = new HashSet<uint>();
        uint cluster = firstCluster;
        long remaining = fileLength;
        coveredBytes = 0;

        long currentOffset = -1;
        long currentLength = 0;

        while (remaining > 0 &&
               cluster >= 2 && cluster < clusterCount + 2 &&
               visited.Add(cluster) &&
               visited.Count <= 4_000_000)
        {
            long offset = ClusterToOffset(cluster, clusterHeapOffset, clusterSize);
            long bytes = Math.Min((long)clusterSize, remaining);
            if (offset < 0 || offset >= reader.VolumeLength) break;
            bytes = Math.Min(bytes, reader.VolumeLength - offset);

            if (currentOffset >= 0 && currentOffset + currentLength == offset)
            {
                currentLength += bytes;
            }
            else
            {
                if (currentOffset >= 0 && currentLength > 0)
                    extents.Add(new SourceExtent(currentOffset, currentLength));

                currentOffset = offset;
                currentLength = bytes;
            }

            coveredBytes += bytes;
            remaining -= bytes;
            if (remaining <= 0)
                break;

            uint next = ReadFatEntry(reader, fatOffset, cluster);
            if (next >= 0xFFFFFFF8 || next == 0 || next == 0xFFFFFFFF)
                break;

            cluster = next;
        }

        if (currentOffset >= 0 && currentLength > 0)
            extents.Add(new SourceExtent(currentOffset, currentLength));

        return extents;
    }

    private static byte[] ReadDirectoryData(
        RawDeviceReader reader,
        DirectoryRef directory,
        long fatOffset,
        long clusterHeapOffset,
        int clusterSize,
        uint clusterCount,
        long maxDirectoryBytes,
        CancellationToken cancellationToken)
    {
        if (directory.NoFatChain && directory.DataLength > 0)
        {
            long length = (long)Math.Min(directory.DataLength, (ulong)maxDirectoryBytes);
            long offset = ClusterToOffset(directory.FirstCluster, clusterHeapOffset, clusterSize);
            return ReadBestEffortBytes(reader, offset, checked((int)length));
        }

        using var memory = new MemoryStream();
        uint cluster = directory.FirstCluster;
        var visited = new HashSet<uint>();

        while (cluster >= 2 &&
               cluster < clusterCount + 2 &&
               visited.Add(cluster) &&
               memory.Length < maxDirectoryBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long offset = ClusterToOffset(cluster, clusterHeapOffset, clusterSize);
            byte[] block = ReadBestEffortBytes(reader, offset, clusterSize);
            if (block.Length == 0) break;

            memory.Write(block, 0, block.Length);

            uint next = ReadFatEntry(reader, fatOffset, cluster);
            if (next >= 0xFFFFFFF8 || next == 0)
                break;

            cluster = next;
        }

        return memory.ToArray();
    }

    private static byte[] ReadBestEffortBytes(RawDeviceReader reader, long offset, int count)
    {
        if (count <= 0) return [];

        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(offset, data, out _);
        if (read == data.Length) return data;
        if (read <= 0) return [];

        Array.Resize(ref data, read);
        return data;
    }

    private static uint ReadFatEntry(RawDeviceReader reader, long fatOffset, uint cluster)
    {
        Span<byte> value = stackalloc byte[4];
        int read = reader.ReadBestEffort(fatOffset + cluster * 4L, value, out long unreadableBytes);
        if (read != value.Length || unreadableBytes > 0)
            return 0xFFFFFFFF;

        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private static long ClusterToOffset(uint cluster, long clusterHeapOffset, int clusterSize) =>
        clusterHeapOffset + (cluster - 2L) * clusterSize;

    private static void AppendExFatName(ReadOnlySpan<byte> entry, StringBuilder builder)
    {
        for (int i = 2; i + 1 < 32; i += 2)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(i, 2));
            if (value is 0x0000 or 0xFFFF)
                break;

            builder.Append((char)value);
        }
    }
}
