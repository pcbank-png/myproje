using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record PartitionCandidate(
    int PhysicalDriveNumber,
    long OffsetBytes,
    long LengthBytes,
    string FileSystem,
    string Scheme,
    string Name,
    string Identity,
    int Confidence,
    bool IsRecoveredSignature,
    int TableIndex = 0)
{
    public string Key => $"{PhysicalDriveNumber}:{OffsetBytes}:{LengthBytes}";
}

public sealed record PartitionDiscoveryReport(
    IReadOnlyList<PartitionCandidate> Candidates,
    long ScannedBytes,
    long TotalBytes,
    string Summary);

/// <summary>
/// Read-only lost-partition discovery for MBR/GPT and raw filesystem boot sectors.
/// Nothing in this service writes a partition table or opens a device with write access.
/// </summary>
public sealed class PartitionDiscoveryService
{
    private const int SignatureStride = 512;
    private const int DefaultScanBlock = 16 * 1024 * 1024;
    private const int ScanOverlap = 4096;
    private const long MinimumPartitionBytes = 8L * 1024 * 1024;

    private static readonly byte[] NtfsSignature = Encoding.ASCII.GetBytes("NTFS    ");
    private static readonly byte[] ExFatSignature = Encoding.ASCII.GetBytes("EXFAT   ");
    private static readonly byte[] Fat32Signature = Encoding.ASCII.GetBytes("FAT32   ");
    private static readonly byte[] Fat16Signature = Encoding.ASCII.GetBytes("FAT16   ");
    private static readonly byte[] Fat12Signature = Encoding.ASCII.GetBytes("FAT12   ");
    private static readonly byte[] GptSignature = Encoding.ASCII.GetBytes("EFI PART");

    internal IReadOnlyList<PartitionCandidate> DiscoverPartitionTableCandidates(PhysicalDriveInfo disk)
    {
        try
        {
            using RawDeviceReader reader = RawDeviceReader.OpenPhysicalDrive(disk.Number, 0, disk.LengthBytes);
            var result = new Dictionary<long, PartitionCandidate>();
            foreach (PartitionCandidate candidate in ReadMbrCandidates(reader, disk))
                AddBest(result, candidate);
            foreach (PartitionCandidate candidate in ReadGptCandidates(reader, disk))
                AddBest(result, candidate);

            // Windows "biçimlendir" dediginde MBR/GPT bazen hic okunamazken dosya sisteminin
            // VBR'i (veya backup VBR'i) hala saglamdir. Tum diski taramadan, yaygin partition
            // baslangiclari + FAT/exFAT backup konumlari + NTFS tail backup konumlarini birkac
            // kucuk salt-okunur probe ile kontrol et. Bu, USB/SD superfloppy ve bozuk partition
            // table vakalarinda metadata taramasini saniyeler yerine aninda baslatabilen bootstrap'tir.
            foreach (PartitionCandidate candidate in DiscoverFastBootCandidates(reader, disk))
                AddBest(result, candidate);

            return result.Values.OrderBy(item => item.OffsetBytes).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IEnumerable<PartitionCandidate> DiscoverFastBootCandidates(
        RawDeviceReader reader,
        PhysicalDriveInfo disk)
    {
        if (disk.LengthBytes < MinimumPartitionBytes)
            yield break;

        int logicalSector = disk.LogicalSectorSize is >= 512 and <= 65536
            ? disk.LogicalSectorSize
            : 512;
        int[] sectorSizes = [logicalSector, 512, 4096, 1024, 2048];
        sectorSizes = sectorSizes
            .Where(value => value is >= 512 and <= 65536 && (value & (value - 1)) == 0)
            .Distinct()
            .ToArray();

        const long MiB = 1024L * 1024;
        long[] commonStarts =
        [
            0,
            63L * 512,      // legacy CHS-aligned removable media
            1L * MiB,       // modern Windows default
            2L * MiB,
            4L * MiB,
            8L * MiB
        ];

        var probeOffsets = new HashSet<long>();
        foreach (long start in commonStarts)
        {
            probeOffsets.Add(start);
            foreach (int sectorSize in sectorSizes)
            {
                probeOffsets.Add(start + 6L * sectorSize);   // FAT32 backup VBR
                probeOffsets.Add(start + 12L * sectorSize);  // exFAT backup boot
            }
        }

        // NTFS keeps a backup VBR at the end of the volume. Real disks commonly leave a tiny
        // alignment/GPT tail, so probe a handful of likely end positions instead of scanning
        // gigabytes just to discover the filesystem.
        foreach (int sectorSize in sectorSizes)
        {
            long[] tailGaps =
            [
                0,
                33L * logicalSector,
                34L * logicalSector,
                1L * MiB,
                2L * MiB
            ];

            foreach (long gap in tailGaps)
                probeOffsets.Add(disk.LengthBytes - gap - sectorSize);
        }

        foreach (long bootOffset in probeOffsets.Where(offset => offset >= 0).OrderBy(offset => offset))
        {
            if (bootOffset + 512 > disk.LengthBytes)
                continue;

            FileSystemProbe probe = ProbeFileSystem(reader, bootOffset, disk.LengthBytes);
            if (!probe.IsValid || probe.LengthBytes < MinimumPartitionBytes)
                continue;

            long normalizedOffset = NormalizePrimaryOffsetFromBackup(bootOffset, probe, disk.LengthBytes);
            if (normalizedOffset < 0 || normalizedOffset > disk.LengthBytes - probe.LengthBytes)
                continue;

            bool recoveredFromBackup = normalizedOffset != bootOffset;
            int confidence = recoveredFromBackup ? 90 : 93;
            yield return new PartitionCandidate(
                disk.Number,
                normalizedOffset,
                probe.LengthBytes,
                probe.FileSystem,
                recoveredFromBackup ? "Fast Backup VBR" : "Fast VBR Probe",
                $"Otomatik {probe.FileSystem} Bölümü",
                $"FAST-VBR-{disk.Number}-{normalizedOffset:X}",
                confidence,
                true);
        }
    }

    public PartitionDiscoveryReport ScanForLostPartitions(
        StorageDeviceInfo physicalDevice,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        if (!physicalDevice.IsWholePhysicalDisk || physicalDevice.PhysicalDriveNumber is not int physicalDriveNumber)
            throw new ArgumentException("Kayıp bölüm taraması için tüm fiziksel disk kaynağı seçilmelidir.", nameof(physicalDevice));

        RecoveryMediaProfile mediaProfile = RecoveryMediaProfileService.Create(physicalDevice);
        using RawDeviceReader reader = RawDeviceReader.OpenDevice(physicalDevice, pauseGate, mediaProfile);
        long total = reader.VolumeLength > 0 ? reader.VolumeLength : physicalDevice.TotalBytes;
        var candidates = new Dictionary<long, PartitionCandidate>();

        if (PhysicalDriveAccessService.TryGetInfo(physicalDriveNumber, out PhysicalDriveInfo disk))
        {
            foreach (PartitionCandidate candidate in ReadMbrCandidates(reader, disk))
                AddBest(candidates, candidate);
            foreach (PartitionCandidate candidate in ReadGptCandidates(reader, disk))
                AddBest(candidates, candidate);
        }

        int blockSize = Math.Max(DefaultScanBlock, Math.Min(mediaProfile.ScanBlockSize, 32 * 1024 * 1024));
        byte[] rented = ArrayPool<byte>.Shared.Rent(blockSize + ScanOverlap);
        long position = 0;
        long lastReported = -1;

        try
        {
            while (position < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pauseGate?.Wait();

                int request = (int)Math.Min(blockSize + ScanOverlap, total - position);
                Span<byte> buffer = rented.AsSpan(0, request);
                int read = reader.ReadBestEffort(position, buffer, out _);
                if (read <= 0)
                    break;

                ScanSignatureOccurrences(reader, physicalDriveNumber, total, position, buffer[..read], NtfsSignature, 3, "NTFS", candidates);
                ScanSignatureOccurrences(reader, physicalDriveNumber, total, position, buffer[..read], ExFatSignature, 3, "exFAT", candidates);
                ScanSignatureOccurrences(reader, physicalDriveNumber, total, position, buffer[..read], Fat32Signature, 82, "FAT32", candidates);
                ScanSignatureOccurrences(reader, physicalDriveNumber, total, position, buffer[..read], Fat16Signature, 54, "FAT16", candidates);
                ScanSignatureOccurrences(reader, physicalDriveNumber, total, position, buffer[..read], Fat12Signature, 54, "FAT12", candidates);

                long processed = Math.Min(total, position + Math.Min(blockSize, read));
                long reportBucket = processed / (64L * 1024 * 1024);
                if (reportBucket != lastReported)
                {
                    lastReported = reportBucket;
                    progress?.Report(new OperationProgress(
                        total <= 0 ? 0 : Math.Clamp(processed * 100d / total, 0d, 100d),
                        "Kayıp Bölüm Tarama • PhysicalDrive",
                        $"NTFS/FAT12/FAT16/FAT32/exFAT boot sector adayları salt-okunur aranıyor • {RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(total)} • {candidates.Count:N0} aday",
                        processed,
                        total,
                        candidates.Count));
                }

                if (read <= ScanOverlap)
                    break;
                position += Math.Min(blockSize, read - ScanOverlap);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        List<PartitionCandidate> normalized = NormalizeCandidates(candidates.Values, total);
        progress?.Report(new OperationProgress(
            100,
            "Kayıp Bölüm Tarama • Tamamlandı",
            $"{normalized.Count:N0} doğrulanmış bölüm adayı bulundu. Partition tablosuna hiçbir veri yazılmadı.",
            total,
            total,
            normalized.Count));

        return new PartitionDiscoveryReport(
            normalized,
            total,
            total,
            $"PhysicalDrive{physicalDriveNumber} • {normalized.Count:N0} doğrulanmış NTFS/FAT12/FAT16/FAT32/exFAT bölüm adayı bulundu.");
    }

    private static IEnumerable<PartitionCandidate> ReadMbrCandidates(RawDeviceReader reader, PhysicalDriveInfo disk)
    {
        byte[] sector = reader.ReadBytes(0, Math.Max(512, disk.LogicalSectorSize));
        if (sector.Length < 512 || sector[510] != 0x55 || sector[511] != 0xAA)
            yield break;

        int index = 0;
        for (int i = 0; i < 4; i++)
        {
            int entryOffset = 446 + i * 16;
            byte type = sector[entryOffset + 4];
            uint firstLba = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(entryOffset + 8, 4));
            uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(entryOffset + 12, 4));
            if (type == 0 || sectorCount == 0 || type == 0xEE)
                continue;

            if (type is 0x05 or 0x0F or 0x85)
            {
                List<PartitionCandidate> logicalPartitions = ReadExtendedMbrChain(reader, disk, firstLba, index);
                foreach (PartitionCandidate logical in logicalPartitions)
                    yield return logical;
                index += logicalPartitions.Count;
                continue;
            }

            index++;
            long offset = checked((long)firstLba * disk.LogicalSectorSize);
            long length = checked((long)sectorCount * disk.LogicalSectorSize);
            PartitionCandidate? candidate = CreateTableCandidate(reader, disk, offset, length, "MBR", index, $"MBR-{type:X2}");
            if (candidate is not null)
            {
                yield return candidate;
                continue;
            }

            // Very old SATA/ATA disks can retain a perfectly usable legacy MBR entry while
            // the primary NTFS/FAT32 VBR is damaged or the reported end-of-disk geometry is
            // a few legacy track sectors shorter than the table claims. Quick Scan must not
            // lose the entire metadata path merely because the VBR probe failed. Expose a
            // low-confidence, read-only table hint; the filesystem scanner still performs
            // its own strict metadata validation/rescue before returning any file result.
            PartitionCandidate? legacyHint = CreateLegacyMbrHintCandidate(disk, offset, length, type, index);
            if (legacyHint is not null)
                yield return legacyHint;
        }
    }


    private static PartitionCandidate? CreateLegacyMbrHintCandidate(
        PhysicalDriveInfo disk,
        long offset,
        long tableLength,
        byte partitionType,
        int tableIndex)
    {
        string fileSystem = partitionType switch
        {
            0x01 => "FAT12",
            0x04 or 0x06 or 0x0E => "FAT16",
            0x07 => "NTFS", // legacy NTFS; exFAT also uses 0x07, but a broken exFAT VBR is not accepted by its scanner
            0x0B or 0x0C => "FAT32",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(fileSystem) || offset < 0 || offset >= disk.LengthBytes)
            return null;

        long available = disk.LengthBytes - offset;
        if (available < MinimumPartitionBytes)
            return null;

        // Clamp only the view; never write or "repair" the partition table. This also handles
        // old CHS/LBA capacity rounding and HPA-like one-track tail mismatches safely.
        long safeLength = Math.Min(Math.Max(MinimumPartitionBytes, tableLength), available);
        int sector = disk.LogicalSectorSize is >= 512 and <= 65536 ? disk.LogicalSectorSize : 512;
        safeLength -= safeLength % sector;
        if (safeLength < MinimumPartitionBytes)
            return null;

        return new PartitionCandidate(
            disk.Number,
            offset,
            safeLength,
            fileSystem,
            "MBR Legacy Hint",
            $"Legacy Bölüm {tableIndex}",
            $"MBR-LEGACY-{partitionType:X2}-{tableIndex}",
            58,
            true,
            tableIndex);
    }

    private static List<PartitionCandidate> ReadExtendedMbrChain(
        RawDeviceReader reader,
        PhysicalDriveInfo disk,
        uint extendedBaseLba,
        int startIndex)
    {
        var result = new List<PartitionCandidate>();
        long extendedBase = checked((long)extendedBaseLba * disk.LogicalSectorSize);
        long ebrOffset = extendedBase;
        var visited = new HashSet<long>();

        for (int depth = 0; depth < 128 && ebrOffset > 0 && ebrOffset < disk.LengthBytes; depth++)
        {
            if (!visited.Add(ebrOffset))
                break;

            byte[] ebr = reader.ReadBytes(ebrOffset, 512);
            if (ebr.Length < 512 || ebr[510] != 0x55 || ebr[511] != 0xAA)
                break;

            byte type = ebr[450];
            uint relativeLba = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(454, 4));
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(458, 4));
            if (type != 0 && count > 0)
            {
                long offset = checked(ebrOffset + (long)relativeLba * disk.LogicalSectorSize);
                long length = checked((long)count * disk.LogicalSectorSize);
                int logicalIndex = startIndex + result.Count + 1;
                PartitionCandidate? candidate = CreateTableCandidate(
                    reader,
                    disk,
                    offset,
                    length,
                    "MBR/EBR",
                    logicalIndex,
                    $"EBR-{type:X2}");
                if (candidate is not null)
                {
                    result.Add(candidate);
                }
                else
                {
                    PartitionCandidate? legacyHint = CreateLegacyMbrHintCandidate(
                        disk,
                        offset,
                        length,
                        type,
                        logicalIndex);
                    if (legacyHint is not null)
                        result.Add(legacyHint with { Scheme = "MBR/EBR Legacy Hint", Identity = $"EBR-LEGACY-{type:X2}-{logicalIndex}" });
                }
            }

            byte nextType = ebr[466];
            uint nextRelativeLba = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(470, 4));
            if (nextType == 0 || nextRelativeLba == 0)
                break;

            ebrOffset = checked(extendedBase + (long)nextRelativeLba * disk.LogicalSectorSize);
        }

        return result;
    }

    private static IEnumerable<PartitionCandidate> ReadGptCandidates(RawDeviceReader reader, PhysicalDriveInfo disk)
    {
        int sectorSize = disk.LogicalSectorSize is >= 512 and <= 65536 ? disk.LogicalSectorSize : 512;
        byte[] header = reader.ReadBytes(sectorSize, sectorSize);
        if (!HasPrefix(header, GptSignature))
        {
            long backupOffset = Math.Max(0, disk.LengthBytes - sectorSize);
            header = reader.ReadBytes(backupOffset, sectorSize);
            if (!HasPrefix(header, GptSignature))
                yield break;
        }

        if (header.Length < 92)
            yield break;

        ulong entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72, 8));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));
        if (entriesLba == 0 || entryCount == 0 || entryCount > 16384 || entrySize < 128 || entrySize > 4096)
            yield break;

        if (entriesLba > (ulong)(long.MaxValue / sectorSize) || entryCount > long.MaxValue / entrySize)
            yield break;

        long tableOffset = (long)entriesLba * sectorSize;
        long tableLength = (long)entryCount * entrySize;
        if (tableOffset < 0 || tableLength <= 0 || tableOffset > disk.LengthBytes - tableLength || tableLength > 64L * 1024 * 1024)
            yield break;

        byte[] table = reader.ReadBytes(tableOffset, checked((int)tableLength));
        if (table.Length < tableLength)
            yield break;

        for (int i = 0; i < entryCount; i++)
        {
            int offset = checked((int)(i * entrySize));
            ReadOnlySpan<byte> entry = table.AsSpan(offset, (int)entrySize);
            bool emptyType = true;
            for (int b = 0; b < 16; b++)
            {
                if (entry[b] == 0)
                    continue;
                emptyType = false;
                break;
            }
            if (emptyType)
                continue;

            ulong firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            ulong lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
            if (firstLba == 0 || lastLba < firstLba || lastLba > (ulong)(long.MaxValue / sectorSize))
                continue;

            long partitionOffset = checked((long)firstLba * sectorSize);
            long length = checked(((long)(lastLba - firstLba) + 1) * sectorSize);
            if (partitionOffset < 0 || length < MinimumPartitionBytes || partitionOffset + length > disk.LengthBytes)
                continue;

            Guid typeGuid = new(entry[..16]);
            Guid uniqueGuid = new(entry.Slice(16, 16));
            string name = ReadGptName(entry);
            PartitionCandidate? candidate = CreateTableCandidate(
                reader,
                disk,
                partitionOffset,
                length,
                "GPT",
                i + 1,
                uniqueGuid == Guid.Empty ? typeGuid.ToString("D") : uniqueGuid.ToString("D"),
                name);
            if (candidate is not null)
                yield return candidate;
        }
    }

    private static PartitionCandidate? CreateTableCandidate(
        RawDeviceReader reader,
        PhysicalDriveInfo disk,
        long offset,
        long tableLength,
        string scheme,
        int tableIndex,
        string identity,
        string? partitionName = null)
    {
        if (offset < 0 || tableLength < MinimumPartitionBytes || offset + tableLength > disk.LengthBytes)
            return null;

        FileSystemProbe probe = ProbeTablePartitionFileSystem(reader, disk, offset, tableLength, out bool recoveredFromBackupBoot);
        if (!probe.IsValid)
            return null;

        long length = probe.LengthBytes > 0 && probe.LengthBytes <= tableLength + Math.Max(4096, probe.BytesPerSector * 16L)
            ? Math.Min(tableLength, probe.LengthBytes)
            : tableLength;
        int confidence = recoveredFromBackupBoot ? 92 : probe.BackupBootValidated ? 100 : 96;
        string safeName = string.IsNullOrWhiteSpace(partitionName)
            ? $"Bölüm {tableIndex}"
            : partitionName.Trim();

        return new PartitionCandidate(
            disk.Number,
            offset,
            length,
            probe.FileSystem,
            scheme,
            safeName,
            identity,
            confidence,
            false,
            tableIndex);
    }

    private static void ScanSignatureOccurrences(
        RawDeviceReader reader,
        int physicalDriveNumber,
        long diskLength,
        long blockOffset,
        ReadOnlySpan<byte> block,
        byte[] signature,
        int signatureOffset,
        string expectedFileSystem,
        Dictionary<long, PartitionCandidate> candidates)
    {
        int searchStart = 0;
        while (searchStart <= block.Length - signature.Length)
        {
            int relative = block[searchStart..].IndexOf(signature);
            if (relative < 0)
                break;

            int signatureIndex = searchStart + relative;
            long candidateOffset = blockOffset + signatureIndex - signatureOffset;
            searchStart = signatureIndex + 1;

            if (candidateOffset < 0 || candidateOffset % SignatureStride != 0 || candidateOffset >= diskLength)
                continue;
            if (candidates.ContainsKey(candidateOffset))
                continue;

            FileSystemProbe probe = ProbeFileSystem(reader, candidateOffset, diskLength);
            if (!probe.IsValid || !string.Equals(probe.FileSystem, expectedFileSystem, StringComparison.OrdinalIgnoreCase))
                continue;

            long normalizedOffset = NormalizePrimaryOffsetFromBackup(candidateOffset, probe, diskLength);
            bool recoveredFromBackupBoot = normalizedOffset != candidateOffset;
            candidateOffset = normalizedOffset;

            if (probe.LengthBytes < MinimumPartitionBytes || candidateOffset < 0 || candidateOffset > diskLength - probe.LengthBytes)
                continue;

            int confidence = recoveredFromBackupBoot ? 88 : probe.BackupBootValidated ? 94 : 86;
            AddBest(candidates, new PartitionCandidate(
                physicalDriveNumber,
                candidateOffset,
                probe.LengthBytes,
                probe.FileSystem,
                "RAW Signature",
                $"Kayıp {probe.FileSystem} Bölümü",
                $"RAW-{physicalDriveNumber}-{candidateOffset:X}",
                confidence,
                true));
        }
    }

    private static FileSystemProbe ProbeTablePartitionFileSystem(
        RawDeviceReader reader,
        PhysicalDriveInfo disk,
        long partitionOffset,
        long tableLength,
        out bool recoveredFromBackupBoot)
    {
        recoveredFromBackupBoot = false;

        FileSystemProbe primary = ProbeFileSystem(reader, partitionOffset, disk.LengthBytes);
        if (primary.IsValid)
            return primary;

        // Eski HDD'lerde partition tablosu saglam kalirken primary VBR bozulmus olabilir.
        // Quick Scan'in bolumu tamamen kaybetmemesi icin tablo sinirlari icindeki bilinen
        // backup VBR konumlarini salt-okunur deneriz. Bu tum diski taramaz.
        int[] sectorSizes = new[] { disk.LogicalSectorSize, 512, 4096, 1024, 2048 }
            .Where(value => value is >= 512 and <= 65536 && (value & (value - 1)) == 0)
            .Distinct()
            .ToArray();

        foreach (int sectorSize in sectorSizes)
        {
            long[] backupOffsets =
            [
                partitionOffset + tableLength - sectorSize, // NTFS backup VBR
                partitionOffset + 12L * sectorSize,         // exFAT backup boot region
                partitionOffset + 6L * sectorSize           // common FAT32 backup VBR
            ];

            foreach (long backupOffset in backupOffsets.Distinct())
            {
                if (backupOffset <= partitionOffset || backupOffset < 0 || backupOffset + 512 > disk.LengthBytes)
                    continue;

                FileSystemProbe backup = ProbeFileSystem(reader, backupOffset, disk.LengthBytes);
                if (!backup.IsValid)
                    continue;

                long normalized = NormalizePrimaryOffsetFromBackup(backupOffset, backup, disk.LengthBytes);
                long tolerance = Math.Max(4096L, sectorSize * 16L);
                if (Math.Abs(normalized - partitionOffset) > tolerance)
                    continue;

                if (backup.LengthBytes > tableLength + tolerance ||
                    backup.LengthBytes + tolerance < tableLength)
                {
                    // Partition table length and filesystem length should describe the same view.
                    // Allow only a tiny geometry tolerance; otherwise this is likely a false hit.
                    continue;
                }

                recoveredFromBackupBoot = true;
                return backup;
            }
        }

        return FileSystemProbe.Invalid;
    }

    private static FileSystemProbe ProbeFileSystem(RawDeviceReader reader, long offset, long diskLength)
    {
        if (offset < 0 || offset + 512 > diskLength)
            return FileSystemProbe.Invalid;

        byte[] boot = reader.ReadBytes(offset, 4096);
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            return FileSystemProbe.Invalid;

        if (boot.AsSpan(3, 8).SequenceEqual(NtfsSignature))
            return ProbeNtfs(reader, offset, diskLength, boot);
        if (boot.AsSpan(3, 8).SequenceEqual(ExFatSignature))
            return ProbeExFat(reader, offset, diskLength, boot);
        if (boot.Length >= 90 && boot.AsSpan(82, 8).SequenceEqual(Fat32Signature))
            return ProbeFat32(reader, offset, diskLength, boot);
        if (boot.Length >= 62 && (
            boot.AsSpan(54, 8).SequenceEqual(Fat16Signature) ||
            boot.AsSpan(54, 8).SequenceEqual(Fat12Signature)))
        {
            return ProbeFat12Or16(offset, diskLength, boot);
        }

        // Some formatters leave the FAT type label blank/stale. The BPB geometry is the
        // authoritative discriminator, so use it as a strict fallback rather than rejecting
        // a perfectly valid FAT12/FAT16 partition.
        FileSystemProbe legacyFat = ProbeFat12Or16(offset, diskLength, boot);
        return legacyFat.IsValid ? legacyFat : FileSystemProbe.Invalid;
    }

    private static FileSystemProbe ProbeNtfs(RawDeviceReader reader, long offset, long diskLength, byte[] boot)
    {
        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        byte sectorsPerCluster = boot[13];
        ulong totalSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(40, 8));
        ulong mftLcn = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(48, 8));
        if (!ValidSectorSize(bytesPerSector) || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128 || totalSectors == 0)
            return FileSystemProbe.Invalid;

        long length;
        try { length = checked((long)totalSectors * bytesPerSector); }
        catch (OverflowException) { return FileSystemProbe.Invalid; }
        if (length < MinimumPartitionBytes ||
            !FitsPrimaryOrBackup(offset, length, bytesPerSector, diskLength, length - bytesPerSector))
            return FileSystemProbe.Invalid;

        long clusterCount = checked((long)(totalSectors / sectorsPerCluster));
        if (mftLcn == 0 || mftLcn >= (ulong)Math.Max(1, clusterCount))
            return FileSystemProbe.Invalid;

        bool backup = ValidateNtfsBackup(reader, offset, length, bytesPerSector);
        return new FileSystemProbe(true, "NTFS", length, bytesPerSector, backup, 0);
    }

    private static FileSystemProbe ProbeExFat(RawDeviceReader reader, long offset, long diskLength, byte[] boot)
    {
        int bytesPerSectorShift = boot[108];
        int sectorsPerClusterShift = boot[109];
        if (bytesPerSectorShift is < 9 or > 12 || sectorsPerClusterShift > 25)
            return FileSystemProbe.Invalid;

        int bytesPerSector = 1 << bytesPerSectorShift;
        ulong volumeLengthSectors = BinaryPrimitives.ReadUInt64LittleEndian(boot.AsSpan(72, 8));
        uint fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(80, 4));
        uint clusterHeapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(88, 4));
        uint clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(92, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96, 4));
        if (volumeLengthSectors == 0 || fatOffset == 0 || clusterHeapOffset <= fatOffset || clusterCount < 1 || rootCluster < 2 || (ulong)rootCluster > (ulong)clusterCount + 1)
            return FileSystemProbe.Invalid;

        long length;
        try { length = checked((long)volumeLengthSectors * bytesPerSector); }
        catch (OverflowException) { return FileSystemProbe.Invalid; }
        if (length < MinimumPartitionBytes ||
            !FitsPrimaryOrBackup(offset, length, bytesPerSector, diskLength, 12L * bytesPerSector))
            return FileSystemProbe.Invalid;

        bool backup = ValidateBootSignature(reader, offset + 12L * bytesPerSector, "exFAT");
        return new FileSystemProbe(true, "exFAT", length, bytesPerSector, backup, 12);
    }

    private static FileSystemProbe ProbeFat32(RawDeviceReader reader, long offset, long diskLength, byte[] boot)
    {
        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        byte sectorsPerCluster = boot[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        byte fatCount = boot[16];
        uint totalSectors = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));
        uint fatSize = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(36, 4));
        uint rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(44, 4));
        ushort backupSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(50, 2));
        if (!ValidSectorSize(bytesPerSector) || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128 || reservedSectors == 0 || fatCount is 0 or > 4 || totalSectors == 0 || fatSize == 0 || rootCluster < 2)
            return FileSystemProbe.Invalid;

        long dataSectors = (long)totalSectors - reservedSectors - (long)fatCount * fatSize;
        if (dataSectors <= 0 || dataSectors / sectorsPerCluster < 65525)
            return FileSystemProbe.Invalid;

        long length = checked((long)totalSectors * bytesPerSector);
        long backupDelta = backupSector > 0 && backupSector < reservedSectors
            ? (long)backupSector * bytesPerSector
            : 0;
        if (length < MinimumPartitionBytes ||
            !FitsPrimaryOrBackup(offset, length, bytesPerSector, diskLength, backupDelta))
            return FileSystemProbe.Invalid;

        bool backup = backupSector > 0 && backupSector < reservedSectors &&
                      ValidateBootSignature(reader, offset + (long)backupSector * bytesPerSector, "FAT32");
        return new FileSystemProbe(true, "FAT32", length, bytesPerSector, backup, backupSector);
    }

    private static FileSystemProbe ProbeFat12Or16(long offset, long diskLength, byte[] boot)
    {
        if (boot.Length < 64)
            return FileSystemProbe.Invalid;

        ushort bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(11, 2));
        byte sectorsPerCluster = boot[13];
        ushort reservedSectors = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(14, 2));
        byte fatCount = boot[16];
        ushort rootEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(17, 2));
        ushort totalSectors16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(19, 2));
        ushort fatSize16 = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(22, 2));
        uint totalSectors32 = BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(32, 4));

        if (!ValidSectorSize(bytesPerSector) || !IsPowerOfTwo(sectorsPerCluster) || sectorsPerCluster > 128 ||
            reservedSectors == 0 || fatCount is 0 or > 4 || rootEntryCount == 0 || fatSize16 == 0)
        {
            return FileSystemProbe.Invalid;
        }

        long totalSectors = totalSectors16 != 0 ? totalSectors16 : totalSectors32;
        if (totalSectors <= 0)
            return FileSystemProbe.Invalid;

        long rootDirSectors = ((long)rootEntryCount * 32 + (bytesPerSector - 1)) / bytesPerSector;
        long dataSectors = totalSectors - (reservedSectors + (long)fatCount * fatSize16 + rootDirSectors);
        if (dataSectors <= 0)
            return FileSystemProbe.Invalid;

        long clusterCount = dataSectors / sectorsPerCluster;
        string fileSystem = clusterCount switch
        {
            < 4085 => "FAT12",
            < 65525 => "FAT16",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(fileSystem))
            return FileSystemProbe.Invalid;

        long length;
        try { length = checked(totalSectors * bytesPerSector); }
        catch (OverflowException) { return FileSystemProbe.Invalid; }
        if (length < MinimumPartitionBytes || offset < 0 || offset > diskLength - length)
            return FileSystemProbe.Invalid;

        return new FileSystemProbe(true, fileSystem, length, bytesPerSector, false, 0);
    }

    private static bool FitsPrimaryOrBackup(
        long bootOffset,
        long volumeLength,
        int bytesPerSector,
        long diskLength,
        long backupDelta)
    {
        if (bootOffset < 0 || volumeLength <= 0 || diskLength <= 0)
            return false;

        // Normal primary VBR case. Avoid unchecked addition overflow.
        if (bootOffset <= diskLength && volumeLength <= diskLength - bootOffset)
            return true;

        // Raw scan can hit the filesystem's backup VBR after the primary was destroyed.
        // Permit it only when the derived primary start is inside the disk and has a
        // strong modern (1 MiB) or legacy CHS (63-sector) alignment.
        if (backupDelta <= 0 || backupDelta >= volumeLength || bootOffset < backupDelta)
            return false;

        long derivedPrimary = bootOffset - backupDelta;
        if (!IsStrongPartitionAlignment(derivedPrimary))
            return false;

        return volumeLength <= diskLength - derivedPrimary;
    }

    private static long NormalizePrimaryOffsetFromBackup(long bootOffset, FileSystemProbe probe, long diskLength)
    {
        if (IsStrongPartitionAlignment(bootOffset))
            return bootOffset;

        long derived = probe.FileSystem switch
        {
            "NTFS" => bootOffset - Math.Max(0, probe.LengthBytes - probe.BytesPerSector),
            "exFAT" => bootOffset - 12L * probe.BytesPerSector,
            "FAT32" when probe.BackupSectorIndex > 0 => bootOffset - (long)probe.BackupSectorIndex * probe.BytesPerSector,
            _ => -1
        };

        if (derived < 0 || derived > diskLength - probe.LengthBytes)
            return bootOffset;

        return IsStrongPartitionAlignment(derived) ? derived : bootOffset;
    }

    private static bool IsStrongPartitionAlignment(long offset)
    {
        if (offset == 0)
            return true;
        const long oneMiB = 1024L * 1024;
        const long legacyTrack = 63L * 512;
        return offset % oneMiB == 0 || offset % legacyTrack == 0;
    }

    private static bool ValidateNtfsBackup(RawDeviceReader reader, long offset, long length, int bytesPerSector)
    {
        long backupOffset = offset + length - bytesPerSector;
        if (backupOffset <= offset)
            return false;
        byte[] backup = reader.ReadBytes(backupOffset, Math.Max(512, bytesPerSector));
        return backup.Length >= 512 && backup[510] == 0x55 && backup[511] == 0xAA &&
               backup.Length >= 11 && backup.AsSpan(3, 8).SequenceEqual(NtfsSignature);
    }

    private static bool ValidateBootSignature(RawDeviceReader reader, long offset, string fileSystem)
    {
        if (offset < 0)
            return false;
        byte[] boot = reader.ReadBytes(offset, 512);
        if (boot.Length < 512 || boot[510] != 0x55 || boot[511] != 0xAA)
            return false;
        return fileSystem switch
        {
            "exFAT" => boot.AsSpan(3, 8).SequenceEqual(ExFatSignature),
            "FAT32" => boot.AsSpan(82, 8).SequenceEqual(Fat32Signature),
            _ => false
        };
    }

    private static List<PartitionCandidate> NormalizeCandidates(IEnumerable<PartitionCandidate> source, long diskLength)
    {
        var ordered = source
            .Where(item =>
                item.OffsetBytes >= 0 &&
                item.OffsetBytes <= diskLength &&
                item.LengthBytes >= MinimumPartitionBytes &&
                item.LengthBytes <= diskLength - item.OffsetBytes)
            .OrderBy(item => item.OffsetBytes)
            .ThenByDescending(item => item.Confidence)
            .ToList();

        var result = new List<PartitionCandidate>();
        foreach (PartitionCandidate candidate in ordered)
        {
            int duplicateIndex = result.FindIndex(existing =>
                string.Equals(existing.FileSystem, candidate.FileSystem, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(existing.OffsetBytes - candidate.OffsetBytes) < 1024 * 1024 &&
                Math.Abs(existing.LengthBytes - candidate.LengthBytes) <= Math.Max(4096, candidate.LengthBytes / 10000));

            if (duplicateIndex < 0)
            {
                result.Add(candidate);
                continue;
            }

            if (candidate.Confidence > result[duplicateIndex].Confidence)
                result[duplicateIndex] = candidate;
        }

        return result.OrderBy(item => item.OffsetBytes).ToList();
    }

    private static void AddBest(Dictionary<long, PartitionCandidate> result, PartitionCandidate candidate)
    {
        if (!result.TryGetValue(candidate.OffsetBytes, out PartitionCandidate? existing) ||
            existing is null ||
            candidate.Confidence > existing.Confidence)
        {
            result[candidate.OffsetBytes] = candidate;
        }
    }

    private static bool HasPrefix(byte[] value, byte[] prefix) =>
        value.Length >= prefix.Length && value.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool ValidSectorSize(int value) => value is 512 or 1024 or 2048 or 4096;

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static string ReadGptName(ReadOnlySpan<byte> entry)
    {
        if (entry.Length <= 56)
            return string.Empty;
        int byteCount = Math.Min(72, entry.Length - 56);
        string name = Encoding.Unicode.GetString(entry.Slice(56, byteCount));
        int terminator = name.IndexOf('\0');
        return (terminator >= 0 ? name[..terminator] : name).Trim();
    }

    private readonly record struct FileSystemProbe(
        bool IsValid,
        string FileSystem,
        long LengthBytes,
        int BytesPerSector,
        bool BackupBootValidated,
        int BackupSectorIndex)
    {
        public static FileSystemProbe Invalid => new(false, string.Empty, 0, 512, false, 0);
    }
}
