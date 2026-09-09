using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class NtfsFolderScanService
{
    private const uint AttributeEnd = 0xFFFFFFFF;
    private const uint AttributeFileName = 0x30;
    private const int RecordsPerBlock = 1024;
    private const int MaxCandidates = 250000;

    private readonly record struct FileReference(long RecordNumber, ushort SequenceNumber);
    private sealed record DirectoryEntry(FileReference Parent, ushort SequenceNumber, string Name);

    public ScanReport Scan(
        StorageDeviceInfo device,
        string folderPath,
        DeepScanTarget scope,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        if (scope == DeepScanTarget.None)
            throw new InvalidOperationException("Klasör taraması için en az bir dosya türü seçin.");

        string volumeRoot = NormalizeRoot(device.RootPath);
        string selectedFolder = Path.GetFullPath(folderPath);
        string selectedRoot = NormalizeRoot(Path.GetPathRoot(selectedFolder) ?? string.Empty);

        if (!string.Equals(volumeRoot, selectedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Seçilen klasör, seçili aygıtın üzerinde değil.");

        if (!Directory.Exists(selectedFolder))
            throw new DirectoryNotFoundException("Seçilen klasör artık erişilebilir değil.");

        string targetRelative = NormalizeRelative(Path.GetRelativePath(volumeRoot, selectedFolder));

        using RawDeviceReader reader = RawDeviceReader.OpenDevice(device, pauseGate, RecoveryMediaProfileService.Create(device));

        byte[] boot = reader.ReadBytes(0, 512);
        if (boot.Length < 80 || Encoding.ASCII.GetString(boot, 3, 8) != "NTFS    ")
            throw new InvalidDataException("Klasör odaklı kurtarma için NTFS önyükleme kaydı doğrulanamadı.");

        int bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        int sectorsPerCluster = boot[13];
        if (bytesPerSector <= 0 || sectorsPerCluster <= 0)
            throw new InvalidDataException("NTFS küme geometrisi okunamadı.");

        int clusterSize = checked(bytesPerSector * sectorsPerCluster);
        long mftLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(48, 8));
        long mftMirrorLcn = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(56, 8));
        sbyte recordCode = unchecked((sbyte)boot[64]);
        int recordSize = recordCode > 0
            ? checked(clusterSize * recordCode)
            : 1 << -recordCode;

        if (mftLcn <= 0 || recordSize < 512 || recordSize > 64 * 1024)
            throw new InvalidDataException("NTFS MFT geometrisi geçersiz.");

        byte[] mftRecordZero = NtfsQuickScanService.ReadBestEffortBytes(reader, mftLcn * (long)clusterSize, recordSize);
        bool primaryMftValid = mftRecordZero.Length == recordSize &&
                               NtfsQuickScanService.FixupFileRecord(mftRecordZero.AsSpan(), bytesPerSector);

        if (!primaryMftValid && mftMirrorLcn > 0)
        {
            byte[] mirrorRecordZero = NtfsQuickScanService.ReadBestEffortBytes(reader, mftMirrorLcn * (long)clusterSize, recordSize);
            if (mirrorRecordZero.Length == recordSize &&
                NtfsQuickScanService.FixupFileRecord(mirrorRecordZero.AsSpan(), bytesPerSector))
            {
                mftRecordZero = mirrorRecordZero;
            }
            else
            {
                throw new InvalidDataException("NTFS $MFT ve $MFTMirr kayıtları okunamadı.");
            }
        }
        else if (!primaryMftValid)
        {
            throw new InvalidDataException("NTFS $MFT kaydı okunamadı.");
        }

        (IReadOnlyList<DataRun> mftRuns, long mftDataSize) =
            NtfsQuickScanService.ReadMftDataRuns(mftRecordZero, clusterSize);

        if (mftRuns.Count == 0 || mftDataSize <= 0)
            throw new InvalidDataException("NTFS $MFT veri zinciri bulunamadı.");

        long totalRecords = mftDataSize / recordSize;
        if (totalRecords <= 0)
            return new ScanReport([], $"Klasör {Path.GetFileName(selectedFolder)} • MFT kaydı bulunamadı.", ScanMode.Quick);

        int blockSize = checked(recordSize * RecordsPerBlock);
        byte[] block = new byte[blockSize];

        // 1. geçiş: önce klasör ağacını eksiksiz çıkar. Böylece seçili klasörün ve
        // silinmiş alt klasörlerin MFT parent ilişkileri dosya taramasından önce hazır olur.
        var directories = new Dictionary<long, DirectoryEntry>
        {
            [5] = new DirectoryEntry(new FileReference(5, 0), 0, string.Empty)
        };

        long recordIndex = 0;
        while (recordIndex < totalRecords)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            long logicalOffset = recordIndex * recordSize;
            int recordsThisBlock = (int)Math.Min(RecordsPerBlock, totalRecords - recordIndex);
            int bytesThisBlock = checked(recordsThisBlock * recordSize);

            Array.Clear(block, 0, bytesThisBlock);
            int read = NtfsQuickScanService.ReadVirtualMft(
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

                Span<byte> record = block.AsSpan(localRecord * recordSize, recordSize);
                long currentRecordIndex = recordIndex + localRecord;

                if (!NtfsQuickScanService.FixupFileRecord(record, bytesPerSector))
                    continue;

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2));
                bool isDirectory = (flags & 0x0002) != 0;
                if (!isDirectory)
                    continue;

                if (!TryReadPreferredFileName(record, out string? name, out FileReference parent))
                    continue;

                ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(16, 2));
                if (currentRecordIndex == 5 || NtfsQuickScanService.IsUsableName(name!))
                {
                    directories[currentRecordIndex] = new DirectoryEntry(
                        parent,
                        sequence,
                        currentRecordIndex == 5 ? string.Empty : name!);
                }
            }

            recordIndex += Math.Max(completeRecords, 1);

            if (recordIndex % (RecordsPerBlock * 4L) == 0 || recordIndex >= totalRecords)
            {
                double percent = recordIndex * 50d / totalRecords;
                progress?.Report(new OperationProgress(
                    Math.Clamp(percent, 0d, 50d),
                    "NTFS Klasör • Klasör Ağacı",
                    $"{Path.GetFileName(selectedFolder)} • klasör zincirleri hazırlanıyor • kayıt {recordIndex:N0} / {totalRecords:N0}.",
                    recordIndex,
                    totalRecords * 2,
                    0));
            }
        }

        var pathCache = new Dictionary<long, string?> { [5] = string.Empty };
        var targetDirectories = new Dictionary<long, string>();

        foreach ((long directoryRecord, DirectoryEntry entry) in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? resolved = directoryRecord == 5
                ? string.Empty
                : ResolveDirectoryPath(
                    new FileReference(directoryRecord, entry.SequenceNumber),
                    directories,
                    pathCache);

            if (resolved is not null && IsWithinTarget(resolved, targetRelative))
                targetDirectories[directoryRecord] = resolved;
        }

        if (targetDirectories.Count == 0)
        {
            progress?.Report(new OperationProgress(
                100,
                "NTFS Klasör • Tamamlandı",
                "Seçili klasörün MFT klasör zinciri eşleştirilemedi.",
                totalRecords * 2,
                totalRecords * 2,
                0));

            return new ScanReport(
                [],
                $"Klasör {Path.GetFileName(selectedFolder)} • eşleşen MFT klasör zinciri bulunamadı.",
                ScanMode.Quick);
        }

        // 2. geçiş: yalnızca seçili klasör ve alt klasörlerinin dosya kayıtlarını işle.
        // Aktif dosyalar da dahil edilir; bu sayede Klasör Tara boş klasördeki silinmiş
        // kayıtları ararken normal dosyaları da Pro sonuç görünümünde gösterebilir.
        var results = new List<RecoveryFileItem>();
        var pendingBatch = new List<RecoveryFileItem>(64);
        int activeCount = 0;
        int deletedCount = 0;
        recordIndex = 0;

        while (recordIndex < totalRecords && results.Count < MaxCandidates)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            long logicalOffset = recordIndex * recordSize;
            int recordsThisBlock = (int)Math.Min(RecordsPerBlock, totalRecords - recordIndex);
            int bytesThisBlock = checked(recordsThisBlock * recordSize);

            Array.Clear(block, 0, bytesThisBlock);
            int read = NtfsQuickScanService.ReadVirtualMft(
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

                Span<byte> record = block.AsSpan(localRecord * recordSize, recordSize);
                long currentRecordIndex = recordIndex + localRecord;

                if (!NtfsQuickScanService.FixupFileRecord(record, bytesPerSector))
                    continue;

                if (!TryReadPreferredFileName(record, out _, out FileReference parent))
                    continue;

                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(22, 2));
                bool inUse = (flags & 0x0001) != 0;
                bool isDirectory = (flags & 0x0002) != 0;
                if (isDirectory)
                    continue;

                if (!targetDirectories.TryGetValue(parent.RecordNumber, out string? parentPath))
                    continue;

                if (directories.TryGetValue(parent.RecordNumber, out DirectoryEntry? parentEntry) &&
                    parent.SequenceNumber != 0 && parentEntry.SequenceNumber != 0 &&
                    parent.SequenceNumber != parentEntry.SequenceNumber)
                {
                    continue;
                }

                RecoveryFileItem? item = NtfsQuickScanService.ParseDeletedFileRecord(
                    reader,
                    record,
                    currentRecordIndex,
                    clusterSize,
                    includeInUse: true);

                if (item is null || !ScopeIncludes(scope, item.Extension))
                    continue;

                string sourceText = string.IsNullOrWhiteSpace(parentPath)
                    ? "Klasör • \\"
                    : $"Klasör • {parentPath}";

                RecoveryFileItem result = CloneWithSourceText(item, sourceText);
                results.Add(result);
                pendingBatch.Add(result);

                if (inUse)
                    activeCount++;
                else
                    deletedCount++;

                if (results.Count >= MaxCandidates)
                    break;
            }

            recordIndex += Math.Max(completeRecords, 1);

            if (recordIndex % (RecordsPerBlock * 4L) == 0 || recordIndex >= totalRecords || pendingBatch.Count >= 64)
            {
                RecoveryFileItem[]? newFiles = pendingBatch.Count > 0 ? pendingBatch.ToArray() : null;
                pendingBatch.Clear();

                double percent = 50d + recordIndex * 50d / totalRecords;
                progress?.Report(new OperationProgress(
                    Math.Clamp(percent, 50d, 100d),
                    "NTFS Klasör • Dosya Kayıtları",
                    $"{Path.GetFileName(selectedFolder)} • {results.Count:N0} dosya eşleştirildi • kayıt {recordIndex:N0} / {totalRecords:N0}.",
                    totalRecords + recordIndex,
                    totalRecords * 2,
                    results.Count,
                    newFiles));
            }
        }

        if (pendingBatch.Count > 0)
        {
            progress?.Report(new OperationProgress(
                99.9,
                "NTFS Klasör • Sonuçlar Hazırlanıyor",
                $"{results.Count:N0} dosya sonuç listesine ekleniyor.",
                totalRecords * 2 - 1,
                totalRecords * 2,
                results.Count,
                pendingBatch.ToArray()));
        }

        progress?.Report(new OperationProgress(
            100,
            "NTFS Klasör • Tamamlandı",
            $"Eşleşen dosya: {results.Count:N0} • mevcut {activeCount:N0} • silinmiş kayıt {deletedCount:N0}.",
            totalRecords * 2,
            totalRecords * 2,
            results.Count));

        string summary = results.Count == 0
            ? $"Klasör {Path.GetFileName(selectedFolder)} • seçili türlerde dosya bulunamadı."
            : $"Klasör {Path.GetFileName(selectedFolder)} • {results.Count:N0} dosya eşleştirildi • mevcut {activeCount:N0} • silinmiş kayıt {deletedCount:N0}.";

        return new ScanReport(results, summary, ScanMode.Quick);
    }

    private static bool TryReadPreferredFileName(
        ReadOnlySpan<byte> record,
        out string? fileName,
        out FileReference parent)
    {
        fileName = null;
        parent = default;

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
            if (type == AttributeEnd)
                break;

            uint lengthRaw = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(position + 4, 4));
            if (lengthRaw < 16 || lengthRaw > int.MaxValue)
                break;

            int attributeLength = (int)lengthRaw;
            if (position + attributeLength > record.Length)
                break;

            bool nonResident = record[position + 8] != 0;
            if (type == AttributeFileName && !nonResident && attributeLength >= 24)
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
                        byte nameChars = record[valueStart + 64];
                        byte nameNamespace = record[valueStart + 65];
                        int nameBytes = nameChars * 2;

                        if (nameChars > 0 && valueStart + 66 + nameBytes <= position + attributeLength)
                        {
                            string candidate = Encoding.Unicode.GetString(record.Slice(valueStart + 66, nameBytes));
                            int score = nameNamespace switch
                            {
                                1 => 3,
                                3 => 3,
                                0 => 2,
                                2 => 1,
                                _ => 0
                            };

                            if (score > bestScore && (NtfsQuickScanService.IsUsableName(candidate) || candidate == "."))
                            {
                                ulong rawReference = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(valueStart, 8));
                                parent = new FileReference(
                                    (long)(rawReference & 0x0000FFFFFFFFFFFFUL),
                                    (ushort)(rawReference >> 48));
                                fileName = candidate;
                                bestScore = score;
                            }
                        }
                    }
                }
            }

            position += attributeLength;
        }

        return fileName is not null;
    }

    private static string? ResolveDirectoryPath(
        FileReference reference,
        IReadOnlyDictionary<long, DirectoryEntry> directories,
        IDictionary<long, string?> cache)
    {
        if (reference.RecordNumber == 5)
            return string.Empty;

        if (directories.TryGetValue(reference.RecordNumber, out DirectoryEntry? referencedEntry) &&
            reference.SequenceNumber != 0 && referencedEntry.SequenceNumber != 0 &&
            reference.SequenceNumber != referencedEntry.SequenceNumber)
            return null;

        if (cache.TryGetValue(reference.RecordNumber, out string? cached))
            return cached;

        var names = new List<string>();
        var visited = new HashSet<long>();
        FileReference current = reference;

        for (int depth = 0; depth < 256; depth++)
        {
            if (current.RecordNumber == 5)
            {
                names.Reverse();
                string resolved = string.Join("\\", names);
                cache[reference.RecordNumber] = resolved;
                return resolved;
            }

            if (!visited.Add(current.RecordNumber) ||
                !directories.TryGetValue(current.RecordNumber, out DirectoryEntry? entry))
            {
                cache[reference.RecordNumber] = null;
                return null;
            }

            if (current.SequenceNumber != 0 && entry.SequenceNumber != 0 &&
                current.SequenceNumber != entry.SequenceNumber)
            {
                cache[reference.RecordNumber] = null;
                return null;
            }

            if (!string.IsNullOrWhiteSpace(entry.Name))
                names.Add(entry.Name);

            current = entry.Parent;
        }

        cache[reference.RecordNumber] = null;
        return null;
    }

    private static bool IsWithinTarget(string parentRelativePath, string targetRelativePath)
    {
        string parent = NormalizeRelative(parentRelativePath);
        string target = NormalizeRelative(targetRelativePath);

        if (string.IsNullOrWhiteSpace(target))
            return true;

        if (string.Equals(parent, target, StringComparison.OrdinalIgnoreCase))
            return true;

        return parent.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ScopeIncludes(DeepScanTarget scope, string extension)
    {
        if (scope == DeepScanTarget.Video)
            return FileTypeHelper.IsVideo(extension);

        if (scope == DeepScanTarget.Photo)
            return FileTypeHelper.IsPhoto(extension);

        string category = FileTypeHelper.GetCategory(extension);
        return category switch
        {
            "Fotoğraf" => scope.Includes(DeepScanTarget.Photo),
            "Video" => scope.Includes(DeepScanTarget.Video),
            _ => scope.Includes(DeepScanTarget.Document)
        };
    }

    private static RecoveryFileItem CloneWithSourceText(RecoveryFileItem item, string sourceText) => new()
    {
        FileName = item.FileName,
        Extension = item.Extension,
        SizeBytes = item.SizeBytes,
        RecoveryState = item.RecoveryState,
        TypeGlyph = item.TypeGlyph,
        SourceText = sourceText,
        SourceKind = item.SourceKind,
        IsExistingFile = item.IsExistingFile,
        SourceOffset = item.SourceOffset,
        ClusterSize = item.ClusterSize,
        ResidentData = item.ResidentData,
        DataRuns = item.DataRuns,
        PrefixData = item.PrefixData,
        SuffixData = item.SuffixData,
        SourceExtents = item.SourceExtents,
        TransformKind = item.TransformKind,
        RecoveryConfidenceScore = item.RecoveryConfidenceScore,
        RecoveryConfidenceGrade = item.RecoveryConfidenceGrade,
        RecoveryConfidenceSummary = item.RecoveryConfidenceSummary,
        FileSystemCreatedAt = item.FileSystemCreatedAt,
        FileSystemModifiedAt = item.FileSystemModifiedAt,
        DeletedAt = item.DeletedAt,
        DeletionDateSource = item.DeletionDateSource,
        NtfsRecordIndex = item.NtfsRecordIndex,
        NtfsFileReference = item.NtfsFileReference,
        NtfsParentReference = item.NtfsParentReference,
        RecoveredOriginalPath = item.RecoveredOriginalPath,
        NtfsForensicEvidence = item.NtfsForensicEvidence,
        CapturedAt = item.CapturedAt,
        CaptureDateSource = item.CaptureDateSource,
        IsChecked = item.IsChecked
    };

    private static string NormalizeRoot(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string full = Path.GetFullPath(value);
        string? root = Path.GetPathRoot(full);
        return (root ?? full).TrimEnd('\\') + "\\";
    }

    private static string NormalizeRelative(string value) =>
        (value ?? string.Empty)
            .Replace('/', '\\')
            .Trim()
            .Trim('\\', '.');
}
