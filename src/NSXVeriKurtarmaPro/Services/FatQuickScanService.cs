using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// FAT12/FAT16 Quick Scan path used by Windows volumes reported simply as "FAT".
/// It is intentionally metadata-first: fixed root directory + reachable/deleted directory
/// chains are parsed first. On portable USB/SD media only, a bounded stale-directory window
/// is inspected when normal metadata contains no deleted entries. It never invokes Deep Scan.
/// </summary>
public sealed class FatQuickScanService
{
    private readonly Func<RawDeviceReader>? _openReader;
    public FatQuickScanService() { }
    internal FatQuickScanService(Func<RawDeviceReader> openReader) => _openReader = openReader;

    private const int MaxResults = 500000;
    private const int MaxDirectoryClusters = 2_000_000;
    private const int RescueBlockBytes = 4 * 1024 * 1024;

    private readonly record struct DirectoryRef(uint FirstCluster, string Path, bool Deleted = false);

    private enum FatVariant
    {
        Fat12,
        Fat16,
        Fat32
    }

    private readonly record struct FatGeometry(
        FatVariant Variant,
        int BytesPerSector,
        int SectorsPerCluster,
        int ReservedSectors,
        int NumberOfFats,
        int RootEntryCount,
        uint FatSizeSectors,
        long TotalSectors,
        long RootDirectoryOffset,
        long RootDirectoryBytes,
        long FatOffset,
        long DataOffset,
        long ClusterCount,
        int ClusterSize,
        long VolumeLength)
    {
        public string Name => Variant == FatVariant.Fat12 ? "FAT12" : "FAT16";
        public uint EndOfChain => Variant == FatVariant.Fat12 ? 0x0FF8u : 0xFFF8u;
        public uint BadCluster => Variant == FatVariant.Fat12 ? 0x0FF7u : 0xFFF7u;
        public uint MaxDataCluster => checked((uint)Math.Min(uint.MaxValue, ClusterCount + 1));
    }

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
        FatGeometry geometry = ReadGeometry(reader, volumeLength);

        var folderProgress = new HistoricalFolderProgress(progress);
        progress = folderProgress;
        var results = new List<RecoveryFileItem>();
        var seenCandidates = new HashSet<(long Offset, long Size)>();
        var directoryQueue = new Queue<DirectoryRef>();
        var visitedDirectories = new HashSet<uint>();

        progress?.Report(new OperationProgress(
            0,
            $"{geometry.Name} • Silinmiş Dosya Analizi",
            $"{device.DisplayName} • sabit kök dizin ve FAT zincirleri salt-okunur taranıyor.",
            0,
            geometry.VolumeLength,
            0));

        byte[] rootDirectory = ReadBestEffortBytes(
            reader,
            geometry.RootDirectoryOffset,
            checked((int)Math.Min(geometry.RootDirectoryBytes, int.MaxValue)));
        ScanDirectoryEntries(
            reader,
            rootDirectory,
            geometry,
            results,
            seenCandidates,
            directoryQueue,
            "\\",
            includeExistingFiles,
            cancellationToken);

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
            uint directoryStart = directoryRef.FirstCluster;
            if (!visitedDirectories.Add(directoryStart))
                continue;

            uint cluster = directoryStart;
            var visitedChain = new HashSet<uint>();
            while (IsDataCluster(cluster, geometry) &&
                   visitedChain.Add(cluster) &&
                   results.Count < MaxResults)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visitedClusterCount++;

                long offset = ClusterToOffset(cluster, geometry);
                byte[] data = ReadBestEffortBytes(reader, offset, geometry.ClusterSize);
                if (data.Length < 32)
                    break;

                bool end = ScanDirectoryEntries(
                    reader,
                    data,
                    geometry,
                    results,
                    seenCandidates,
                    directoryQueue,
                    directoryRef.Path,
                    includeExistingFiles,
                    cancellationToken,
                    directoryRef.Deleted);

                if (results.Count > reportedResultCount)
                {
                    RecoveryFileItem[] newFiles = RecoveryScanPriorityService.OrderNewestFirst(
                        results.Skip(reportedResultCount).ToArray()).ToArray();
                    reportedResultCount = results.Count;
                    progress?.Report(new OperationProgress(
                        Math.Min(70d, 2d + visitedClusterCount / 32d),
                        includeExistingFiles ? $"{geometry.Name} • Canlı Dosya Kataloğu" : $"{geometry.Name} • Silinmiş Dosya Analizi",
                        includeExistingFiles
                            ? $"İlk sonuçlar canlı aktarılıyor • klasör {visitedDirectories.Count:N0} • bulunan {results.Count:N0}."
                            : $"Silinmiş kayıtlar canlı aktarılıyor • bulunan {results.Count:N0}.",
                        Math.Min(geometry.VolumeLength, visitedClusterCount * (long)geometry.ClusterSize),
                        geometry.VolumeLength,
                        results.Count,
                        newFiles));
                }

                if (end)
                    break;

                uint next = ReadFatEntry(reader, geometry, cluster);
                if (!IsDataCluster(next, geometry))
                    break;
                cluster = next;
            }

            if (results.Count > reportedResultCount || visitedClusterCount % 32 == 0 || directoryQueue.Count == 0)
            {
                RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                    ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                    : null;
                reportedResultCount = results.Count;
                progress?.Report(new OperationProgress(
                    Math.Min(70d, 5d + visitedClusterCount / 64d),
                    includeExistingFiles ? $"{geometry.Name} • Dosya Kataloğu + Silinmiş Kayıtlar" : $"{geometry.Name} • Silinmiş Dosya Analizi",
                    includeExistingFiles
                        ? $"Dizin zincirleri: {visitedDirectories.Count:N0} • mevcut/silinmiş dosya: {results.Count:N0}."
                        : $"Dizin zincirleri: {visitedDirectories.Count:N0} • silinmiş dosya: {results.Count:N0}.",
                    Math.Min(geometry.VolumeLength, visitedClusterCount * (long)geometry.ClusterSize),
                    geometry.VolumeLength,
                    results.Count,
                    newFiles));
            }
        }

        if (forceHistoricalPathRescue ||
            (!includeExistingFiles && ShouldRunPortableStaleDirectoryRescue(device, results.Count, allowPortableRescue)))
        {
            ScanBoundedStaleDirectoryRegion(
                reader,
                geometry,
                results,
                seenCandidates,
                progress,
                pauseGate,
                cancellationToken);
        }

        List<RecoveryFileItem> ordered = RecoveryScanPriorityService.OrderNewestFirst(results).ToList();
        string summary = includeExistingFiles
            ? $"{geometry.Name} katalog analizi tamamlandı • {ordered.Count:N0} mevcut/silinmiş dosya doğrulandı"
            : $"{geometry.Name} kayıt analizi tamamlandı • {ordered.Count:N0} silinmiş dosya doğrulandı";
        summary +=
                         (PortableQuickScanPolicy.IsPortableMountedSource(device)
                             ? fastPrelude
                                 ? " • Deep Scan hızlı metadata ön analizi tamamlandı; bounded stale-directory tekrarı atlandı ve tam RAW tarama aşamasına bırakıldı."
                                 : " • USB/SD bounded stale-directory kontrolü gerektiğinde uygulandı."
                             : ".");
        return new ScanReport(ordered, summary, ScanMode.Quick, UsedFallback: false) { HistoricalFolders = folderProgress.Snapshot() };
    }

    internal static bool ShouldRunPortableStaleDirectoryRescue(
        StorageDeviceInfo device,
        int deletedResultCount,
        bool allowPortableRescue = true) =>
        allowPortableRescue &&
        deletedResultCount == 0 &&
        PortableQuickScanPolicy.IsPortableMountedSource(device);

    private static FatGeometry ReadGeometry(RawDeviceReader reader, long volumeLength)
    {
        int sectorSize = Math.Max(512, reader.SectorSize);
        byte[] boot = ReadBestEffortBytes(reader, 0, sectorSize);
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            throw new InvalidDataException("FAT önyükleme kaydı doğrulanamadı.");

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        int sectorsPerCluster = boot[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        int numberOfFats = boot[16];
        int rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17, 2));
        ushort total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        ushort fat16Size = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22, 2));
        uint total32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));

        if (bytesPerSector is < 512 or > 4096 || (bytesPerSector & (bytesPerSector - 1)) != 0 ||
            sectorsPerCluster is <= 0 or > 128 || (sectorsPerCluster & (sectorsPerCluster - 1)) != 0 ||
            reservedSectors <= 0 || numberOfFats is <= 0 or > 4 || rootEntryCount <= 0 || fat16Size == 0)
        {
            throw new InvalidDataException("FAT12/FAT16 geometrisi doğrulanamadı.");
        }

        long totalSectors = total16 != 0 ? total16 : total32;
        if (totalSectors <= 0)
            throw new InvalidDataException("FAT birim sektör sayısı okunamadı.");

        long rootDirectorySectors = ((long)rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
        long dataSectors = totalSectors - (reservedSectors + numberOfFats * (long)fat16Size + rootDirectorySectors);
        if (dataSectors <= 0)
            throw new InvalidDataException("FAT veri alanı geometrisi geçersiz.");

        long clusterCount = dataSectors / sectorsPerCluster;
        FatVariant variant = ClassifyFatVariant(clusterCount);
        if (variant == FatVariant.Fat32)
            throw new InvalidDataException("Birim FAT32 olarak görünüyor; FAT32 metadata motoru kullanılmalıdır.");

        int clusterSize = checked(bytesPerSector * sectorsPerCluster);
        long fatOffset = checked(reservedSectors * (long)bytesPerSector);
        long rootDirectoryOffset = checked((reservedSectors + numberOfFats * (long)fat16Size) * bytesPerSector);
        long rootDirectoryBytes = checked(rootDirectorySectors * bytesPerSector);
        long dataOffset = checked((reservedSectors + numberOfFats * (long)fat16Size + rootDirectorySectors) * bytesPerSector);
        long declaredLength = checked(totalSectors * bytesPerSector);
        long safeVolumeLength = volumeLength > 0 ? Math.Min(volumeLength, declaredLength) : declaredLength;

        if (safeVolumeLength <= 0 || rootDirectoryOffset < 0 || rootDirectoryOffset >= safeVolumeLength ||
            dataOffset < 0 || dataOffset >= safeVolumeLength || rootDirectoryBytes <= 0 ||
            rootDirectoryOffset + rootDirectoryBytes > safeVolumeLength)
        {
            throw new InvalidDataException("FAT geometrisi aygıt sınırlarıyla uyuşmuyor.");
        }

        return new FatGeometry(
            variant,
            bytesPerSector,
            sectorsPerCluster,
            reservedSectors,
            numberOfFats,
            rootEntryCount,
            fat16Size,
            totalSectors,
            rootDirectoryOffset,
            rootDirectoryBytes,
            fatOffset,
            dataOffset,
            clusterCount,
            clusterSize,
            safeVolumeLength);
    }

    private static FatVariant ClassifyFatVariant(long clusterCount) =>
        clusterCount < 4085 ? FatVariant.Fat12 : clusterCount < 65525 ? FatVariant.Fat16 : FatVariant.Fat32;

    internal static string ClassifyFatVariantForRegression(ReadOnlySpan<byte> boot)
    {
        if (boot.Length < 64)
            return "INVALID";

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(11, 2));
        int sectorsPerCluster = boot[13];
        int reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(14, 2));
        int numberOfFats = boot[16];
        int rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(17, 2));
        ushort total16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(19, 2));
        ushort fat16Size = BinaryPrimitives.ReadUInt16LittleEndian(boot.Slice(22, 2));
        uint total32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.Slice(32, 4));
        if (bytesPerSector <= 0 || sectorsPerCluster <= 0 || reservedSectors <= 0 ||
            numberOfFats <= 0 || rootEntryCount <= 0 || fat16Size == 0)
            return "INVALID";

        long totalSectors = total16 != 0 ? total16 : total32;
        long rootDirectorySectors = ((long)rootEntryCount * 32 + bytesPerSector - 1) / bytesPerSector;
        long dataSectors = totalSectors - (reservedSectors + numberOfFats * (long)fat16Size + rootDirectorySectors);
        if (dataSectors <= 0)
            return "INVALID";

        long clusterCount = dataSectors / sectorsPerCluster;
        return ClassifyFatVariant(clusterCount) switch
        {
            FatVariant.Fat12 => "FAT12",
            FatVariant.Fat16 => "FAT16",
            _ => "FAT32"
        };
    }

    private static bool ScanDirectoryEntries(
        RawDeviceReader reader,
        ReadOnlySpan<byte> data,
        FatGeometry geometry,
        List<RecoveryFileItem> results,
        HashSet<(long Offset, long Size)> seenCandidates,
        Queue<DirectoryRef> directoryQueue,
        string directoryPath,
        bool includeExistingFiles,
        CancellationToken cancellationToken,
        bool deletedDirectory = false)
    {
        var pendingLfn = new List<string>();
        bool? pendingLfnDeleted = null;
        for (int entryOffset = 0; entryOffset + 32 <= data.Length && results.Count < MaxResults; entryOffset += 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> entry = data.Slice(entryOffset, 32);
            byte first = entry[0];
            if (first == 0x00)
                return true;

            byte attributes = entry[11];
            bool lfn = attributes == 0x0F;
            bool deleted = first == 0xE5;
            if (lfn)
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
            uint firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));
            string entryName = pendingLfn.Count > 0 && pendingLfnDeleted == deleted
                ? string.Concat(pendingLfn.AsEnumerable().Reverse())
                : ReadShortName(entry, deleted);

            deleted |= deletedDirectory;

            if (directory && !volumeLabel && IsDataCluster(firstCluster, geometry))
            {
                if (entryName is not "." and not "..")
                {
                    directoryQueue.Enqueue(new DirectoryRef(
                        firstCluster,
                        CombineRecoveredPath(directoryPath, entryName), deleted));
                }
            }
            else if ((deleted || includeExistingFiles) && !directory && !volumeLabel &&
                     fileSize > 0 && IsDataCluster(firstCluster, geometry))
            {
                TryAddFile(
                    reader,
                    geometry,
                    entryName,
                    firstCluster,
                    fileSize,
                    deleted,
                    CombineRecoveredPath(directoryPath, entryName),
                    results,
                    seenCandidates);
            }

            pendingLfn.Clear();
            pendingLfnDeleted = null;
        }

        return false;
    }

    private static void ScanBoundedStaleDirectoryRegion(
        RawDeviceReader reader,
        FatGeometry geometry,
        List<RecoveryFileItem> results,
        HashSet<(long Offset, long Size)> seenCandidates,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        long available = Math.Max(0, geometry.VolumeLength - geometry.DataOffset);
        long targetBytes = Math.Min(available, PortableQuickScanPolicy.FatDirectoryRescueMaxBytes);
        if (targetBytes < 32)
            return;

        byte[] buffer = new byte[RescueBlockBytes];
        long scanned = 0;
        long absolute = geometry.DataOffset;
        int reportedResultCount = results.Count;

        progress?.Report(new OperationProgress(
            70,
            $"{geometry.Name} • USB/SD Metadata Rescue",
            $"Normal dizin metadata'sında silinmiş kayıt bulunamadı • ilk {RecoveryFileItem.FormatBytes(targetBytes)} veri alanında yalnız 32-byte FAT dizin kayıtları doğrulanacak; Deep Scan çalıştırılmayacak.",
            0,
            targetBytes,
            results.Count));

        while (scanned < targetBytes && results.Count < MaxResults)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(buffer.Length, targetBytes - scanned);
            int read = reader.ReadBestEffort(absolute, buffer.AsSpan(0, request), out _);
            if (read <= 0)
            {
                scanned += request;
                absolute += request;
                continue;
            }

            int usable = read - read % 32;
            for (int offset = 0; offset + 32 <= usable && results.Count < MaxResults; offset += 32)
            {
                ReadOnlySpan<byte> entry = buffer.AsSpan(offset, 32);
                if (!LooksLikeDeletedFileEntry(entry))
                    continue;

                uint firstCluster = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(26, 2));
                uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4));
                if (!IsDataCluster(firstCluster, geometry) || fileSize == 0 || fileSize > geometry.VolumeLength)
                    continue;

                string fileName = ReadShortName(entry, deleted: true);
                // This record was found outside a reachable FAT12/FAT16 directory chain.
                // The fixed root directory does not provide enough evidence to invent a parent
                // folder here, so keep the path unresolved instead of falsely placing it at \.
                TryAddFile(
                    reader,
                    geometry,
                    fileName,
                    firstCluster,
                    fileSize,
                    deleted: true,
                    recoveredPath: null,
                    results,
                    seenCandidates);
            }

            scanned += request;
            absolute += request;
            if (scanned == targetBytes || scanned % (32L * 1024 * 1024) < request)
            {
                RecoveryFileItem[]? newFiles = results.Count > reportedResultCount
                    ? RecoveryScanPriorityService.OrderNewestFirst(results.Skip(reportedResultCount).ToArray())
                    : null;
                reportedResultCount = results.Count;
                progress?.Report(new OperationProgress(
                    70d + 30d * scanned / targetBytes,
                    $"{geometry.Name} • USB/SD Metadata Rescue",
                    $"Dizin kayıt alanı: {RecoveryFileItem.FormatBytes(scanned)} / {RecoveryFileItem.FormatBytes(targetBytes)} • doğrulanan {results.Count:N0}.",
                    scanned,
                    targetBytes,
                    results.Count,
                    newFiles));
            }
        }
    }

    private static bool LooksLikeDeletedFileEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < 32 || entry[0] != 0xE5)
            return false;

        byte attributes = entry[11];
        if (attributes == 0x0F || (attributes & 0x08) != 0 || (attributes & 0x10) != 0)
            return false;

        for (int i = 1; i < 11; i++)
        {
            byte value = entry[i];
            if (value == 0x00 || value == 0xFF)
                return false;
        }
        return true;
    }

    private static void TryAddFile(
        RawDeviceReader reader,
        FatGeometry geometry,
        string fileName,
        uint firstCluster,
        uint fileSize,
        bool deleted,
        string? recoveredPath,
        List<RecoveryFileItem> results,
        HashSet<(long Offset, long Size)> seenCandidates)
    {
        fileName = FileTypeHelper.SanitizeFileName(fileName);
        string extension = FileTypeHelper.Normalize(Path.GetExtension(fileName));
        bool hasVerifiedDirectoryPath = !string.IsNullOrWhiteSpace(recoveredPath);
        if (deleted && !hasVerifiedDirectoryPath && !FileTypeHelper.IsSupported(extension))
            return;

        long sourceOffset = ClusterToOffset(firstCluster, geometry);
        if (sourceOffset < 0 || sourceOffset >= geometry.VolumeLength || fileSize > geometry.VolumeLength - sourceOffset)
            return;

        byte[] header = deleted && !hasVerifiedDirectoryPath ? ReadBestEffortBytes(reader, sourceOffset, (int)Math.Min(256u, fileSize)) : [];
        FileHeaderValidationOutcome headerOutcome = FileHeaderValidator.Validate(extension, header, fileSize);

        // Gerçek FAT parent/child yolu okunabildiyse uzantısı için özel RAW validator
        // bulunmaması dosyayı gizleme sebebi değildir. Yol kanıtı olmayan stale kayıtlar
        // ise false-positive üretmemek için eski sıkı header kuralını korur.
        if (deleted && !hasVerifiedDirectoryPath && headerOutcome != FileHeaderValidationOutcome.Match)
            return;

        if (!seenCandidates.Add((sourceOffset, fileSize)))
            return;

        IReadOnlyList<SourceExtent> extents = BuildDeletedFatExtents(
            reader,
            geometry,
            firstCluster,
            fileSize,
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
                    ? recoverableLength >= fileSize ? "İyi" : "Kısmi"
                    : recoverableLength >= fileSize ? "Çok İyi" : "Kısmi",
                TypeGlyph = FileTypeHelper.GetGlyph(extension),
                SourceText = deleted
                    ? $"{geometry.Name} silinmiş dosya • FAT zinciri • {extents.Count:N0} parça"
                    : $"{geometry.Name} mevcut dosya • FAT zinciri • {extents.Count:N0} parça",
                SourceKind = RecoverySourceKind.Extents,
                IsExistingFile = !deleted,
                SourceOffset = sourceOffset,
                SourceExtents = extents,
                ClusterSize = geometry.ClusterSize,
                RecoveredOriginalPath = recoveredPath
            });
            return;
        }

        results.Add(new RecoveryFileItem
        {
            FileName = fileName,
            Extension = extension,
            SizeBytes = fileSize,
            RecoveryState = deleted ? "İyi" : "Çok İyi",
            TypeGlyph = FileTypeHelper.GetGlyph(extension),
            SourceText = deleted
                ? $"{geometry.Name} silinmiş dosya • ardışık küme {firstCluster:N0}"
                : $"{geometry.Name} mevcut dosya • ardışık küme {firstCluster:N0}",
            SourceKind = RecoverySourceKind.FatContiguous,
            IsExistingFile = !deleted,
            SourceOffset = sourceOffset,
            ClusterSize = geometry.ClusterSize,
            RecoveredOriginalPath = recoveredPath
        });
    }

    private static IReadOnlyList<SourceExtent> BuildDeletedFatExtents(
        RawDeviceReader reader,
        FatGeometry geometry,
        uint firstCluster,
        uint fileSize,
        out long coveredBytes,
        out bool chainWasUsable)
    {
        var extents = new List<SourceExtent>();
        coveredBytes = 0;
        chainWasUsable = false;
        if (fileSize <= geometry.ClusterSize)
            return extents;

        uint firstNext = ReadFatEntry(reader, geometry, firstCluster);
        if (!IsDataCluster(firstNext, geometry) || firstNext == firstCluster)
            return extents;

        chainWasUsable = true;
        uint cluster = firstCluster;
        long remaining = fileSize;
        var visited = new HashSet<uint>();
        long currentOffset = -1;
        long currentLength = 0;

        while (remaining > 0 && IsDataCluster(cluster, geometry) && visited.Add(cluster) && visited.Count <= 4_000_000)
        {
            long offset = ClusterToOffset(cluster, geometry);
            if (offset < 0 || offset >= geometry.VolumeLength)
                break;

            long bytes = Math.Min((long)geometry.ClusterSize, remaining);
            if (bytes > geometry.VolumeLength - offset)
                bytes = geometry.VolumeLength - offset;
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

            uint next = ReadFatEntry(reader, geometry, cluster);
            if (!IsDataCluster(next, geometry))
                break;
            cluster = next;
        }

        if (currentOffset >= 0 && currentLength > 0)
            extents.Add(new SourceExtent(currentOffset, currentLength));
        return extents;
    }

    private static uint ReadFatEntry(RawDeviceReader reader, FatGeometry geometry, uint cluster)
    {
        if (geometry.Variant == FatVariant.Fat16)
        {
            Span<byte> bytes = stackalloc byte[2];
            int read = reader.ReadBestEffort(geometry.FatOffset + cluster * 2L, bytes, out long unreadable);
            if (read != 2 || unreadable > 0)
                return geometry.EndOfChain;
            return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        }

        long fat12Offset = geometry.FatOffset + cluster + cluster / 2L;
        Span<byte> pair = stackalloc byte[2];
        int pairRead = reader.ReadBestEffort(fat12Offset, pair, out long pairUnreadable);
        if (pairRead != 2 || pairUnreadable > 0)
            return geometry.EndOfChain;

        ushort packed = BinaryPrimitives.ReadUInt16LittleEndian(pair);
        return (cluster & 1) == 0 ? (uint)(packed & 0x0FFF) : (uint)(packed >> 4);
    }

    private static bool IsDataCluster(uint cluster, FatGeometry geometry) =>
        cluster >= 2 && cluster <= geometry.MaxDataCluster && cluster < geometry.BadCluster && cluster < geometry.EndOfChain;

    private static long ClusterToOffset(uint cluster, FatGeometry geometry) =>
        geometry.DataOffset + (cluster - 2L) * geometry.ClusterSize;

    private static byte[] ReadBestEffortBytes(RawDeviceReader reader, long offset, int count)
    {
        if (count <= 0)
            return [];
        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(offset, data, out _);
        if (read == data.Length)
            return data;
        if (read <= 0)
            return [];
        Array.Resize(ref data, read);
        return data;
    }

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
            baseNameBytes[0] = (byte)'#';
        string baseName = Encoding.ASCII.GetString(baseNameBytes).TrimEnd(' ');
        string extension = Encoding.ASCII.GetString(entry.Slice(8, 3)).TrimEnd(' ');
        return string.IsNullOrWhiteSpace(extension) ? baseName : $"{baseName}.{extension}";
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
