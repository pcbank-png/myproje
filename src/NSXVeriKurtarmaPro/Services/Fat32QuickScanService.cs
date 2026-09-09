using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class Fat32QuickScanService
{
    private readonly Func<RawDeviceReader>? _openReader;
    public Fat32QuickScanService() { }
    internal Fat32QuickScanService(Func<RawDeviceReader> openReader) => _openReader = openReader;

    private const int MaxResults = 500000;
    private const int MaxDirectoryClusters = 2_000_000;

    private readonly record struct DirectoryRef(uint FirstCluster, string Path, bool Deleted = false);

    private sealed record StaleFat32Entry(
        uint ContainerCluster,
        bool Deleted,
        bool IsDirectory,
        uint FirstCluster,
        uint FileSize,
        string Name,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? ModifiedAt);

    public ScanReport Scan(
        StorageDeviceInfo device,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        bool allowPortableRescue = true,
        bool includeExistingFiles = false,
        bool forceHistoricalPathRescue = false)
    {
        bool fastPrelude = PortableDeepScanPolicy.IsFastMetadataPrelude(device, allowPortableRescue);
        int directoryClusterLimit = PortableDeepScanPolicy.GetFatDirectoryClusterLimit(
            fastPrelude,
            MaxDirectoryClusters);

        using RawDeviceReader reader = _openReader?.Invoke() ?? RawDeviceReader.OpenDevice(device, pauseGate, RecoveryMediaProfileService.Create(device));
        long volumeLength = reader.VolumeLength > 0 ? reader.VolumeLength : device.TotalBytes;
        byte[] boot = ReadValidatedFat32BootSector(reader);

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        int sectorsPerCluster = boot[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        int numberOfFats = boot[16];
        uint fatSizeSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44, 4));

        if (bytesPerSector <= 0 || sectorsPerCluster <= 0 ||
            reservedSectors <= 0 || numberOfFats <= 0 ||
            fatSizeSectors == 0 || rootCluster < 2)
            throw new InvalidDataException("FAT32 geometrisi doğrulanamadı.");

        int clusterSize = checked(bytesPerSector * sectorsPerCluster);
        long fatOffset = checked(reservedSectors * (long)bytesPerSector);
        long dataOffset = checked((reservedSectors + numberOfFats * (long)fatSizeSectors) * bytesPerSector);
        long rootOffset = ClusterToOffset(rootCluster, clusterSize, dataOffset);
        if (volumeLength <= 0 || fatOffset < 0 || fatOffset >= volumeLength ||
            dataOffset < 0 || dataOffset >= volumeLength || rootOffset < 0 || rootOffset >= volumeLength)
            throw new InvalidDataException("FAT32 volume geometrisi fiziksel aygıt sınırlarıyla uyuşmuyor.");

        long fatLengthBytes = checked((long)fatSizeSectors * bytesPerSector);
        var fatTable = new Fat32TableReader(
            reader,
            fatOffset,
            fatLengthBytes,
            preload: includeExistingFiles && fastPrelude);

        var folderProgress = new HistoricalFolderProgress(progress);
        progress = folderProgress;
        var results = new List<RecoveryFileItem>();
        var directoryQueue = new Queue<DirectoryRef>();
        var visitedDirectories = new HashSet<uint>();
        directoryQueue.Enqueue(new DirectoryRef(rootCluster, "\\"));

        int visitedClusterCount = 0;
        int reportedResultCount = 0;

        while (directoryQueue.Count > 0 &&
               results.Count < MaxResults &&
               visitedClusterCount < directoryClusterLimit)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            DirectoryRef directoryRef = directoryQueue.Dequeue();
            if (directoryRef.Deleted)
                folderProgress.Add(directoryRef.Path);
            uint directoryStartCluster = directoryRef.FirstCluster;
            if (!visitedDirectories.Add(directoryStartCluster))
                continue;

            uint cluster = directoryStartCluster;
            var visitedChain = new HashSet<uint>();
            var pendingLfn = new List<string>();
            bool? pendingLfnDeleted = null;
            bool endOfDirectory = false;

            while (cluster >= 2 &&
                   cluster < 0x0FFFFFF8 &&
                   visitedChain.Add(cluster) &&
                   !endOfDirectory)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visitedClusterCount++;

                long clusterOffset = ClusterToOffset(cluster, clusterSize, dataOffset);
                byte[] clusterData = ReadBestEffortBytes(reader, clusterOffset, clusterSize);
                if (clusterData.Length < 32) break;

                for (int entryOffset = 0; entryOffset + 32 <= clusterData.Length; entryOffset += 32)
                {
                    ReadOnlySpan<byte> entry = clusterData.AsSpan(entryOffset, 32);
                    byte first = entry[0];

                    if (first == 0x00)
                    {
                        endOfDirectory = true;
                        break;
                    }

                    byte attributes = entry[11];
                    bool isLfn = attributes == 0x0F;
                    bool deleted = first == 0xE5;

                    if (isLfn)
                    {
                        if (pendingLfnDeleted != deleted)
                        {
                            pendingLfn.Clear();
                            pendingLfnDeleted = deleted;
                        }

                        string fragment = ReadLfnFragment(entry);
                        if (!string.IsNullOrEmpty(fragment))
                            pendingLfn.Add(fragment);
                        continue;
                    }

                    bool volumeLabel = (attributes & 0x08) != 0;
                    bool directory = (attributes & 0x10) != 0;

                    uint high = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20, 2));
                    uint low = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
                    uint firstCluster = (high << 16) | low;
                    uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));
                    DateTimeOffset? fileSystemCreatedAt = TryReadFatCreatedTime(entry);
                    DateTimeOffset? fileSystemModifiedAt = TryReadFatModifiedTime(entry);

                    string entryName = pendingLfn.Count > 0 && pendingLfnDeleted == deleted
                        ? string.Concat(pendingLfn.AsEnumerable().Reverse())
                        : ReadShortName(entry, deleted);

                    deleted |= directoryRef.Deleted;

                    if (directory && !volumeLabel && firstCluster >= 2)
                    {
                        // Both live and deleted FAT32 folders are traversed. During portable
                        // Deep Scan this also exposes the existing file catalog immediately,
                        // while Quick Scan retains its deleted-file-only behavior.
                        if (entryName is not "." and not "..")
                        {
                            directoryQueue.Enqueue(new DirectoryRef(
                                firstCluster,
                                CombineRecoveredPath(directoryRef.Path, FileTypeHelper.SanitizeFileName(entryName)),
                                deleted));
                        }
                    }
                    else if ((deleted || includeExistingFiles) && !directory && !volumeLabel &&
                             firstCluster >= 2 && fileSize > 0)
                    {
                        string fileName = FileTypeHelper.SanitizeFileName(entryName);
                        string extension = FileTypeHelper.Normalize(Path.GetExtension(fileName));
                        string recoveredPath = CombineRecoveredPath(directoryRef.Path, fileName);

                        long sourceOffset = ClusterToOffset(firstCluster, clusterSize, dataOffset);
                        if (sourceOffset >= 0 && sourceOffset < volumeLength)
                        {
                            byte[] header = deleted ? ReadBestEffortBytes(reader, sourceOffset, (int)Math.Min(256u, fileSize)) : [];
                            FileHeaderValidationOutcome headerOutcome = FileHeaderValidator.Validate(extension, header, fileSize);
                            bool headerOkay = headerOutcome != FileHeaderValidationOutcome.Mismatch;

                            IReadOnlyList<SourceExtent> extents = BuildDeletedFatExtents(
                                fatTable,
                                dataOffset,
                                clusterSize,
                                firstCluster,
                                fileSize,
                                volumeLength,
                                out long coveredBytes,
                                out bool chainWasUsable);

                            if (chainWasUsable && extents.Count > 0 && coveredBytes > 0)
                            {
                                long recoverableLength = Math.Min((long)fileSize, coveredBytes);
                                results.Add(new RecoveryFileItem
                                {
                                    FileName = fileName,
                                    Extension = extension,
                                    SizeBytes = recoverableLength,
                                    RecoveryState = deleted
                                        ? !headerOkay ? "Zayıf" : recoverableLength >= fileSize ? "İyi" : "Kısmi"
                                        : recoverableLength >= fileSize ? "Çok İyi" : "Kısmi",
                                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                                    SourceText = deleted
                                        ? $"FAT32 silinmiş dosya • FAT zinciri • {extents.Count:N0} parça"
                                        : $"FAT32 mevcut dosya • FAT zinciri • {extents.Count:N0} parça",
                                    SourceKind = RecoverySourceKind.Extents,
                                    IsExistingFile = !deleted,
                                    SourceOffset = sourceOffset,
                                    SourceExtents = extents,
                                    ClusterSize = clusterSize,
                                    FileSystemCreatedAt = fileSystemCreatedAt,
                                    FileSystemModifiedAt = fileSystemModifiedAt,
                                    RecoveredOriginalPath = recoveredPath
                                });
                            }
                            else if (fileSize <= volumeLength - sourceOffset)
                            {
                                results.Add(new RecoveryFileItem
                                {
                                    FileName = fileName,
                                    Extension = extension,
                                    SizeBytes = fileSize,
                                    RecoveryState = deleted ? headerOkay ? "İyi" : "Zayıf" : "Çok İyi",
                                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                                    SourceText = deleted
                                        ? $"FAT32 silinmiş dosya • ardışık küme {firstCluster:N0}"
                                        : $"FAT32 mevcut dosya • ardışık küme {firstCluster:N0}",
                                    SourceKind = RecoverySourceKind.FatContiguous,
                                    IsExistingFile = !deleted,
                                    SourceOffset = sourceOffset,
                                    ClusterSize = clusterSize,
                                    FileSystemCreatedAt = fileSystemCreatedAt,
                                    FileSystemModifiedAt = fileSystemModifiedAt,
                                    RecoveredOriginalPath = recoveredPath
                                });
                            }
                        }
                    }

                    pendingLfn.Clear();
                    pendingLfnDeleted = null;

                    if (results.Count >= MaxResults)
                        break;
                }

                if (results.Count > reportedResultCount)
                {
                    RecoveryFileItem[] newFiles = RecoveryScanPriorityService.OrderNewestFirst(
                        results.Skip(reportedResultCount).ToArray()).ToArray();
                    reportedResultCount = results.Count;
                    progress?.Report(new OperationProgress(
                        Math.Min(90d, 2d + visitedClusterCount / 32d),
                        includeExistingFiles ? "FAT32 • Canlı Dosya Kataloğu" : "FAT32 • Silinmiş Dosya Analizi",
                        includeExistingFiles
                            ? $"İlk sonuçlar canlı aktarılıyor • klasör {visitedDirectories.Count:N0} • bulunan {results.Count:N0}."
                            : $"Silinmiş kayıtlar canlı aktarılıyor • bulunan {results.Count:N0}.",
                        visitedClusterCount * (long)clusterSize,
                        volumeLength,
                        results.Count,
                        newFiles));
                }

                if (endOfDirectory || results.Count >= MaxResults)
                    break;

                cluster = fatTable.Read(cluster);
            }

            if (results.Count > reportedResultCount || visitedClusterCount % 32 == 0 || directoryQueue.Count == 0)
            {
                RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                    ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                    : null;
                reportedResultCount = results.Count;

                progress?.Report(new OperationProgress(
                    directoryQueue.Count == 0 ? 100d : Math.Min(95d, 5d + visitedClusterCount / 64d),
                    includeExistingFiles ? "FAT32 • Dosya Kataloğu + Silinmiş Kayıtlar" : "FAT32 • Silinmiş Dosya Analizi",
                    includeExistingFiles
                        ? $"Klasör kayıtları: {visitedDirectories.Count:N0} • mevcut ve silinmiş dosyalar doğrulanıyor."
                        : $"Klasör kayıtları: {visitedDirectories.Count:N0} • silinmiş dosyalar doğrulanıyor; zaman bilgisi bulunan kayıtlar öncelikli işleniyor.",
                    visitedClusterCount * (long)clusterSize,
                    volumeLength,
                    results.Count,
                    newFiles));
            }
        }

        if (forceHistoricalPathRescue ||
            (!includeExistingFiles && ShouldRunPortableStaleDirectoryRescue(device, results.Count, allowPortableRescue)))
        {
            ScanPortableStaleDirectoryRegion(
                reader,
                fatTable,
                dataOffset,
                clusterSize,
                rootCluster,
                volumeLength,
                results,
                progress,
                pauseGate,
                cancellationToken);
        }

        string summary = results.Count >= MaxResults
            ? $"FAT32 metadata aday sınırına ulaşıldı • {results.Count:N0} dosya kaydı doğrulandı."
            : includeExistingFiles
                ? $"FAT32 katalog analizi tamamlandı • {results.Count:N0} mevcut/silinmiş dosya kaydı doğrulandı; RAW carving devam edecek."
                : $"FAT32 kayıt analizi tamamlandı • {results.Count:N0} silinmiş dosya doğrulandı; parçalı veriler Derin Tarama aşamasında değerlendirilecek.";

        if (PortableQuickScanPolicy.IsPortableMountedSource(device))
        {
            summary += fastPrelude
                ? " • Deep Scan hızlı metadata ön analizi tamamlandı; bounded stale-directory tekrarı atlandı ve tam RAW tarama aşamasına bırakıldı."
                : " • USB/SD bounded stale-directory kontrolü gerektiğinde uygulandı.";
        }

        return new ScanReport(RecoveryScanPriorityService.OrderNewestFirst(results).ToList(), summary, ScanMode.Quick) { HistoricalFolders = folderProgress.Snapshot() };
    }

    internal static bool ShouldRunPortableStaleDirectoryRescue(
        StorageDeviceInfo device,
        int deletedResultCount,
        bool allowPortableRescue = true) =>
        allowPortableRescue &&
        deletedResultCount == 0 &&
        PortableQuickScanPolicy.IsPortableMountedSource(device);

    private static byte[] ReadValidatedFat32BootSector(RawDeviceReader reader)
    {
        int sectorSize = Math.Max(512, reader.SectorSize);
        byte[] primary = ReadBestEffortBytes(reader, 0, sectorSize);
        if (LooksLikeFat32BootSector(primary))
            return primary;

        int backupSector = 6;
        if (primary.Length >= 52)
        {
            ushort declaredBackup = BinaryPrimitives.ReadUInt16LittleEndian(primary.AsSpan(50, 2));
            if (declaredBackup is > 0 and <= 128)
                backupSector = declaredBackup;
        }

        byte[] backup = ReadBestEffortBytes(reader, backupSector * (long)sectorSize, sectorSize);
        if (LooksLikeFat32BootSector(backup))
            return backup;

        if (backupSector != 6)
        {
            backup = ReadBestEffortBytes(reader, 6L * sectorSize, sectorSize);
            if (LooksLikeFat32BootSector(backup))
                return backup;
        }

        throw new InvalidDataException("FAT32 birincil ve yedek önyükleme kayıtları doğrulanamadı.");
    }

    private static bool LooksLikeFat32BootSector(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 96) return false;

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        int sectorsPerCluster = boot[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
        int numberOfFats = boot[16];
        int rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
        ushort fat16Size = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
        uint fat32Size = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(36, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(44, 4));

        return bytesPerSector is >= 512 and <= 4096 && (bytesPerSector & (bytesPerSector - 1)) == 0
               && sectorsPerCluster is > 0 and <= 128 && (sectorsPerCluster & (sectorsPerCluster - 1)) == 0
               && reservedSectors > 0 && numberOfFats is > 0 and <= 4
               && rootEntryCount == 0 && fat16Size == 0 && fat32Size > 0 && rootCluster >= 2;
    }

    private static DateTimeOffset? TryReadFatCreatedTime(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 18) return null;
        ushort rawTime = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(14, 2));
        ushort rawDate = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(16, 2));
        if (rawDate == 0) return null;

        int year = 1980 + ((rawDate >> 9) & 0x7F);
        int month = (rawDate >> 5) & 0x0F;
        int day = rawDate & 0x1F;
        int hour = (rawTime >> 11) & 0x1F;
        int minute = (rawTime >> 5) & 0x3F;
        int second = (rawTime & 0x1F) * 2;
        int tenths = entry[13];
        if (tenths > 199) tenths = 0;
        second += tenths / 100;
        int milliseconds = (tenths % 100) * 10;

        try
        {
            var local = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), milliseconds, DateTimeKind.Unspecified);
            var result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return result <= DateTimeOffset.Now.AddYears(2) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? TryReadFatModifiedTime(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 26) return null;
        ushort rawTime = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(22, 2));
        ushort rawDate = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(24, 2));
        if (rawDate == 0) return null;

        int year = 1980 + ((rawDate >> 9) & 0x7F);
        int month = (rawDate >> 5) & 0x0F;
        int day = rawDate & 0x1F;
        int hour = (rawTime >> 11) & 0x1F;
        int minute = (rawTime >> 5) & 0x3F;
        int second = (rawTime & 0x1F) * 2;

        try
        {
            var local = new DateTime(year, month, day, hour, minute, Math.Min(second, 59), DateTimeKind.Unspecified);
            var result = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            return result <= DateTimeOffset.Now.AddYears(2) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<SourceExtent> BuildDeletedFatExtents(
        Fat32TableReader fatTable,
        long dataOffset,
        int clusterSize,
        uint firstCluster,
        uint fileSize,
        long volumeLength,
        out long coveredBytes,
        out bool chainWasUsable)
    {
        var extents = new List<SourceExtent>();
        coveredBytes = 0;
        chainWasUsable = false;

        if (fileSize <= clusterSize)
            return extents;

        uint firstNext = fatTable.Read(firstCluster);
        if (firstNext == 0 || firstNext == 0x0FFFFFF7 || firstNext == firstCluster)
            return extents;

        chainWasUsable = firstNext >= 2 && firstNext < 0x0FFFFFF7;
        if (!chainWasUsable)
            return extents;

        uint cluster = firstCluster;
        long remaining = fileSize;
        var visited = new HashSet<uint>();
        long currentOffset = -1;
        long currentLength = 0;

        while (remaining > 0 &&
               cluster >= 2 && cluster < 0x0FFFFFF7 &&
               visited.Add(cluster) && visited.Count <= 4_000_000)
        {
            long offset = ClusterToOffset(cluster, clusterSize, dataOffset);
            if (offset < 0 || offset >= volumeLength)
                break;

            long bytes = Math.Min((long)clusterSize, remaining);
            if (bytes > volumeLength - offset)
                bytes = volumeLength - offset;
            if (bytes <= 0)
                break;

            if (currentOffset >= 0 && currentOffset + currentLength == offset)
                currentLength += bytes;
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

            uint next = fatTable.Read(cluster);
            if (next == 0 || next == 0x0FFFFFF7 || next >= 0x0FFFFFF8)
                break;

            cluster = next;
        }

        if (currentOffset >= 0 && currentLength > 0)
            extents.Add(new SourceExtent(currentOffset, currentLength));

        return extents;
    }

    private static void ScanPortableStaleDirectoryRegion(
        RawDeviceReader reader,
        Fat32TableReader fatTable,
        long dataOffset,
        int clusterSize,
        uint rootCluster,
        long volumeLength,
        List<RecoveryFileItem> results,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        long available = Math.Max(0, volumeLength - dataOffset);
        long targetBytes = Math.Min(available, PortableQuickScanPolicy.FatDirectoryRescueMaxBytes);
        if (targetBytes < clusterSize || clusterSize < 32)
            return;

        const int blockBytes = 4 * 1024 * 1024;
        int bufferSize = Math.Max(clusterSize, blockBytes);
        bufferSize -= bufferSize % clusterSize;
        if (bufferSize < clusterSize)
            bufferSize = clusterSize;

        byte[] block = new byte[bufferSize];
        long maxCluster = Math.Max(2, available / Math.Max(1, clusterSize) + 1);
        long scanned = 0;
        long absolute = dataOffset;
        var evidence = new List<StaleFat32Entry>();
        var graphEntries = new List<FatHistoricalPathResolver.EntryEvidence>();
        var parentHints = new Dictionary<uint, uint>();

        progress?.Report(new OperationProgress(
            70,
            "FAT32 • USB/SD Özgün Yol Metadata Rescue",
            $"Normal FAT32 zincirinde silinmiş kayıt bulunamadı • ilk {RecoveryFileItem.FormatBytes(targetBytes)} veri alanında eski dizin kümeleri ve gerçek parent/child ilişkileri okunuyor; Deep Scan çalıştırılmayacak.",
            0,
            targetBytes,
            results.Count));

        while (scanned < targetBytes && evidence.Count < MaxResults)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(block.Length, targetBytes - scanned);
            int read = reader.ReadBestEffort(absolute, block.AsSpan(0, request), out _);
            if (read > 0)
            {
                int clusterBytes = read - read % clusterSize;
                for (int clusterOffset = 0; clusterOffset + clusterSize <= clusterBytes && evidence.Count < MaxResults; clusterOffset += clusterSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long clusterIndex = (scanned + clusterOffset) / clusterSize;
                    if (clusterIndex < 0 || clusterIndex > uint.MaxValue - 2L)
                        continue;
                    uint containerCluster = checked((uint)(clusterIndex + 2));
                    if (containerCluster < 2 || containerCluster > maxCluster)
                        continue;

                    ReadOnlySpan<byte> clusterData = block.AsSpan(clusterOffset, clusterSize);
                    CollectStaleFat32ClusterEvidence(
                        clusterData,
                        containerCluster,
                        maxCluster,
                        evidence,
                        graphEntries,
                        parentHints,
                        cancellationToken);
                }
            }

            scanned += request;
            absolute += request;
            if (scanned == targetBytes || scanned % (32L * 1024 * 1024) < request)
            {
                progress?.Report(new OperationProgress(
                    70d + 18d * scanned / targetBytes,
                    "FAT32 • Eski Dizin Grafiği",
                    $"Metadata alanı: {RecoveryFileItem.FormatBytes(scanned)} / {RecoveryFileItem.FormatBytes(targetBytes)} • doğrulanan kayıt {evidence.Count:N0}.",
                    scanned,
                    targetBytes,
                    results.Count));
            }
        }

        if (evidence.Count == 0)
            return;

        IReadOnlyList<uint> EnumerateDirectoryClusters(uint firstCluster)
        {
            var clusters = new List<uint>();
            var visited = new HashSet<uint>();
            uint cluster = firstCluster;
            while (cluster >= 2 && cluster <= maxCluster && cluster < 0x0FFFFFF8 &&
                   visited.Add(cluster) && clusters.Count < 32768)
            {
                clusters.Add(cluster);
                uint next = fatTable.Read(cluster);
                if (next == 0 || next == 0x0FFFFFF7 || next >= 0x0FFFFFF8 || next == cluster)
                    break;
                cluster = next;
            }
            return clusters;
        }

        Dictionary<uint, FatHistoricalPathResolver.PathState> directoryPaths = FatHistoricalPathResolver.Build(
            graphEntries,
            parentHints,
            rootCluster,
            rootIsDataCluster: true,
            allowHistoricalRootInference: true,
            enumerateDirectoryClusters: EnumerateDirectoryClusters);

        int resolvedPaths = 0;
        int unresolvedPaths = 0;
        int reported = results.Count;
        var seen = new HashSet<(long Offset, long Size)>();

        foreach (StaleFat32Entry item in evidence)
        {
            if (results.Count >= MaxResults || item.IsDirectory || item.FileSize == 0 || item.FirstCluster < 2)
                continue;

            bool parentResolved = directoryPaths.TryGetValue(item.ContainerCluster, out FatHistoricalPathResolver.PathState? parent) &&
                                  parent is not null;
            bool historicalParent = parentResolved && parent!.Historical;
            if (!item.Deleted && !historicalParent)
                continue;

            long sourceOffset = ClusterToOffset(item.FirstCluster, clusterSize, dataOffset);
            if (sourceOffset < 0 || sourceOffset >= volumeLength || item.FileSize > volumeLength - sourceOffset)
                continue;

            string fileName = FileTypeHelper.SanitizeFileName(item.Name);
            string extension = FileTypeHelper.Normalize(Path.GetExtension(fileName));
            bool supportedType = FileTypeHelper.IsSupported(extension);
            byte[] header = ReadBestEffortBytes(reader, sourceOffset, (int)Math.Min(256u, item.FileSize));
            FileHeaderValidationOutcome headerOutcome = FileHeaderValidator.Validate(extension, header, item.FileSize);

            // A fully resolved historical directory path is itself strong filesystem evidence,
            // so camera sidecar files such as CPI/MPL/BDM are preserved even when they have no
            // carving signature. Unresolved standalone records retain the old strict header gate.
            if (!historicalParent && (!supportedType || headerOutcome != FileHeaderValidationOutcome.Match))
                continue;
            if (!seen.Add((sourceOffset, item.FileSize)))
                continue;

            string? recoveredPath = parentResolved
                ? FatHistoricalPathResolver.Combine(parent!.Path, fileName)
                : null;

            IReadOnlyList<SourceExtent> extents = BuildDeletedFatExtents(
                fatTable,
                dataOffset,
                clusterSize,
                item.FirstCluster,
                item.FileSize,
                volumeLength,
                out long coveredBytes,
                out bool chainWasUsable);

            RecoveryFileItem recovered;
            if (chainWasUsable && extents.Count > 0 && coveredBytes > 0)
            {
                long recoverableLength = Math.Min((long)item.FileSize, coveredBytes);
                recovered = new RecoveryFileItem
                {
                    FileName = fileName,
                    Extension = extension,
                    SizeBytes = recoverableLength,
                    RecoveryState = headerOutcome == FileHeaderValidationOutcome.Mismatch
                        ? "Zayıf"
                        : recoverableLength >= item.FileSize ? "İyi" : "Kısmi",
                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                    SourceText = historicalParent
                        ? $"FAT32 tarihsel dizin • FAT zinciri • {extents.Count:N0} parça"
                        : $"FAT32 silinmiş dosya • FAT zinciri • {extents.Count:N0} parça",
                    SourceKind = RecoverySourceKind.Extents,
                    IsExistingFile = false,
                    SourceOffset = sourceOffset,
                    SourceExtents = extents,
                    ClusterSize = clusterSize,
                    FileSystemCreatedAt = item.CreatedAt,
                    FileSystemModifiedAt = item.ModifiedAt,
                    RecoveredOriginalPath = recoveredPath
                };
            }
            else
            {
                recovered = new RecoveryFileItem
                {
                    FileName = fileName,
                    Extension = extension,
                    SizeBytes = item.FileSize,
                    RecoveryState = headerOutcome == FileHeaderValidationOutcome.Mismatch ? "Zayıf" : "İyi",
                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                    SourceText = historicalParent
                        ? $"FAT32 tarihsel dizin • ardışık küme {item.FirstCluster:N0}"
                        : $"FAT32 silinmiş dosya • ardışık küme {item.FirstCluster:N0}",
                    SourceKind = RecoverySourceKind.FatContiguous,
                    IsExistingFile = false,
                    SourceOffset = sourceOffset,
                    ClusterSize = clusterSize,
                    FileSystemCreatedAt = item.CreatedAt,
                    FileSystemModifiedAt = item.ModifiedAt,
                    RecoveredOriginalPath = recoveredPath
                };
            }

            if (parentResolved)
                resolvedPaths++;
            else
                unresolvedPaths++;
            results.Add(recovered);
        }

        string[] historicalFolders = directoryPaths.Values
            .Where(state => state.Historical && state.Path != "\\")
            .Select(state => state.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Count(ch => ch == '\\'))
            .ThenBy(path => path, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        RecoveryFileItem[]? newFiles = results.Count > reported
            ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reported).ToArray())
            : null;
        progress?.Report(new OperationProgress(
            100,
            "FAT32 • Özgün Yol Eşleştirme",
            $"Eski FAT32 dizin grafiği tamamlandı • özgün yolu eşleşen {resolvedPaths:N0} • yolu doğrulanamayan {unresolvedPaths:N0}.",
            targetBytes,
            targetBytes,
            results.Count,
            newFiles,
            NewHistoricalFolders: historicalFolders));
    }

    private static void CollectStaleFat32ClusterEvidence(
        ReadOnlySpan<byte> clusterData,
        uint containerCluster,
        long maxCluster,
        List<StaleFat32Entry> evidence,
        List<FatHistoricalPathResolver.EntryEvidence> graphEntries,
        IDictionary<uint, uint> parentHints,
        CancellationToken cancellationToken)
    {
        var pendingLfn = new List<string>();
        bool? pendingLfnDeleted = null;
        uint dotSelf = 0;
        uint dotParent = 0;

        for (int offset = 0; offset + 32 <= clusterData.Length && evidence.Count < MaxResults; offset += 32)
        {
            if ((offset & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> entry = clusterData.Slice(offset, 32);
            byte first = entry[0];
            if (first == 0x00)
            {
                pendingLfn.Clear();
                pendingLfnDeleted = null;
                continue;
            }

            byte attributes = entry[11];
            bool deleted = first == 0xE5;
            if (attributes == 0x0F)
            {
                if (pendingLfnDeleted != deleted)
                {
                    pendingLfn.Clear();
                    pendingLfnDeleted = deleted;
                }

                string fragment = ReadLfnFragment(entry);
                if (!string.IsNullOrEmpty(fragment))
                    pendingLfn.Add(fragment);
                continue;
            }

            if (!LooksLikeFat32MetadataEntry(entry))
            {
                pendingLfn.Clear();
                pendingLfnDeleted = null;
                continue;
            }

            bool volumeLabel = (attributes & 0x08) != 0;
            bool isDirectory = (attributes & 0x10) != 0;
            uint high = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(20, 2));
            uint low = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
            uint firstCluster = (high << 16) | low;
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));

            string name = pendingLfn.Count > 0 && pendingLfnDeleted == deleted
                ? string.Concat(pendingLfn.AsEnumerable().Reverse())
                : ReadShortName(entry, deleted);
            name = FileTypeHelper.SanitizeFileName(name);

            pendingLfn.Clear();
            pendingLfnDeleted = null;

            if (volumeLabel || string.IsNullOrWhiteSpace(name))
                continue;

            if (!deleted && isDirectory && name == "." && firstCluster >= 2 && firstCluster <= maxCluster)
            {
                dotSelf = firstCluster;
                continue;
            }
            if (!deleted && isDirectory && name == ".." && firstCluster <= maxCluster)
            {
                dotParent = firstCluster;
                continue;
            }

            if (firstCluster < 2 || firstCluster > maxCluster)
                continue;
            if (!isDirectory && fileSize == 0)
                continue;

            var item = new StaleFat32Entry(
                containerCluster,
                deleted,
                isDirectory,
                firstCluster,
                fileSize,
                name,
                TryReadFatCreatedTime(entry),
                TryReadFatModifiedTime(entry));
            evidence.Add(item);
            graphEntries.Add(new FatHistoricalPathResolver.EntryEvidence(
                containerCluster,
                isDirectory,
                firstCluster,
                name,
                deleted));
        }

        if (dotSelf == containerCluster && dotSelf >= 2)
            parentHints[dotSelf] = dotParent;
    }

    private static bool LooksLikeFat32MetadataEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 32 || entry[0] is 0x00 or 0xFF)
            return false;

        byte attributes = entry[11];
        if (attributes == 0x0F || (attributes & 0xC0) != 0)
            return false;

        for (int index = 1; index < 11; index++)
        {
            byte value = entry[index];
            if (value == 0x00 || value == 0xFF || value < 0x20)
                return false;
        }

        return true;
    }

    private static bool LooksLikePortableDeletedFileEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 32 || entry[0] != 0xE5)
            return false;

        byte attributes = entry[11];
        if (attributes == 0x0F || (attributes & 0x08) != 0 || (attributes & 0x10) != 0)
            return false;

        for (int index = 1; index < 11; index++)
        {
            byte value = entry[index];
            if (value == 0x00 || value == 0xFF)
                return false;
        }
        return true;
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

    private sealed class Fat32TableReader
    {
        private const long MaxPreloadBytes = 64L * 1024 * 1024;
        private readonly RawDeviceReader _reader;
        private readonly long _fatOffset;
        private readonly long _fatLength;
        private readonly byte[]? _preloaded;

        public Fat32TableReader(RawDeviceReader reader, long fatOffset, long fatLength, bool preload)
        {
            _reader = reader;
            _fatOffset = fatOffset;
            _fatLength = Math.Max(0, fatLength);

            if (!preload || _fatLength <= 0 || _fatLength > MaxPreloadBytes || _fatLength > int.MaxValue)
                return;

            byte[] candidate = ReadBestEffortBytes(_reader, _fatOffset, (int)_fatLength);
            if (candidate.LongLength == _fatLength)
                _preloaded = candidate;
        }

        public uint Read(uint cluster)
        {
            long relative = cluster * 4L;
            if (relative < 0 || relative + 4 > _fatLength)
                return 0x0FFFFFFF;

            if (_preloaded is not null)
                return BinaryPrimitives.ReadUInt32LittleEndian(_preloaded.AsSpan((int)relative, 4)) & 0x0FFFFFFF;

            Span<byte> value = stackalloc byte[4];
            int read = _reader.ReadBestEffort(_fatOffset + relative, value, out long unreadableBytes);
            if (read != value.Length || unreadableBytes > 0)
                return 0x0FFFFFFF;

            return BinaryPrimitives.ReadUInt32LittleEndian(value) & 0x0FFFFFFF;
        }
    }

    private static long ClusterToOffset(uint cluster, int clusterSize, long dataOffset) =>
        dataOffset + (cluster - 2L) * clusterSize;

    private static string CombineRecoveredPath(string directoryPath, string name)
    {
        string cleanName = FileTypeHelper.SanitizeFileName(name);
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryPath == "\\")
            return $"\\{cleanName}";
        return $"{directoryPath.TrimEnd('\\')}\\{cleanName}";
    }

    private static string ReadShortName(ReadOnlySpan<byte> entry, bool deleted)
    {
        Span<byte> baseNameBytes = stackalloc byte[8];
        entry.Slice(0, 8).CopyTo(baseNameBytes);

        if (deleted)
            baseNameBytes[0] = (byte)'_';

        string baseName = Encoding.ASCII.GetString(baseNameBytes).TrimEnd(' ');
        string extension = Encoding.ASCII.GetString(entry.Slice(8, 3)).TrimEnd(' ');

        return string.IsNullOrWhiteSpace(extension)
            ? baseName
            : $"{baseName}.{extension}";
    }

    private static string ReadLfnFragment(ReadOnlySpan<byte> entry)
    {
        Span<char> chars = stackalloc char[13];
        int count = 0;

        ReadNameChars(entry.Slice(1, 10), chars, ref count);
        ReadNameChars(entry.Slice(14, 12), chars, ref count);
        ReadNameChars(entry.Slice(28, 4), chars, ref count);

        return new string(chars[..count]);
    }

    private static void ReadNameChars(ReadOnlySpan<byte> bytes, Span<char> chars, ref int count)
    {
        for (int i = 0; i + 1 < bytes.Length; i += 2)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i, 2));
            if (value is 0x0000 or 0xFFFF)
                break;

            if (count < chars.Length)
                chars[count++] = (char)value;
        }
    }
}
