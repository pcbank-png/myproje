using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record DiskImageReport(
    string ImagePath,
    long ImageBytes,
    int BadBlocks,
    string LogPath)
{
    public long UnreadableBytes { get; init; }
    public long RecoveredSectors { get; init; }
    public int RetryPasses { get; init; }
    public bool WasResumed { get; init; }
    public bool AlreadyCompleted { get; init; }
    public string MapPath { get; init; } = string.Empty;
}

internal sealed record DiskImageOptions(
    int BlockSize,
    long CheckpointIntervalBytes,
    int SectorReadAttempts,
    int RetryPasses)
{
    public static DiskImageOptions Default { get; } = new(
        4 * 1024 * 1024,
        64L * 1024 * 1024,
        2,
        3);
}

internal interface IDiskImageReader : IDisposable
{
    long Length { get; }
    int SectorSize { get; }
    int Read(long offset, Span<byte> buffer);
}

internal sealed class DiskImageCheckpoint
{
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "Imaging";
    public string SourceIdentity { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public string ImageFileName { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public int SectorSize { get; set; }
    public long CompletedBytes { get; set; }
    public bool PrimaryPassCompleted { get; set; }
    public int RetryPassesCompleted { get; set; }
    public long RecoveredSectors { get; set; }
    public long RecoveredBytes { get; set; }
    public long FailureEvents { get; set; }
    public long UnreadableBytes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<DiskImageBadSectorState> BadSectors { get; set; } = [];
}

internal sealed class DiskImageBadSectorState
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public int FailureEvents { get; set; }
}

/// <summary>
/// Kaynak aygıtı salt-okunur açarak kesintiye dayanıklı sektör imajı üretir.
/// Geniş I/O hataları mantıksal sektör seviyesine kadar ayrıştırılır; okunamayan
/// sektörler yapılandırılmış haritada tutulur ve ayrı retry turlarında yeniden denenir.
/// </summary>
public sealed class DiskImageService
{
    private const string CompletedStatus = "Completed";
    private const int MaximumBlockSize = 64 * 1024 * 1024;

    private static readonly JsonSerializerOptions CheckpointJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public DiskImageReport CreateImage(
        StorageDeviceInfo sourceDevice,
        string imagePath,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceDevice);

        if (sourceDevice.TotalBytes <= 0)
            throw new InvalidOperationException("Kaynak aygıt kapasitesi okunamadı.");

        RawDeviceReader rawReader = RawDeviceReader.OpenDevice(
            sourceDevice,
            pauseGate,
            RecoveryMediaProfileService.Create(sourceDevice));
        using IDiskImageReader reader = new RawDiskImageReader(rawReader);

        long sourceLength = reader.Length > 0 ? reader.Length : sourceDevice.TotalBytes;
        string sourceIdentity = BuildSourceIdentity(sourceDevice, sourceLength, reader.SectorSize);
        return CreateImageCore(
            reader,
            sourceIdentity,
            sourceDevice.DisplayName,
            imagePath,
            DiskImageOptions.Default,
            progress,
            pauseGate,
            cancellationToken);
    }

    internal DiskImageReport CreateImageForTesting(
        IDiskImageReader reader,
        string sourceIdentity,
        string sourceDisplayName,
        string imagePath,
        DiskImageOptions options,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken) =>
        CreateImageCore(
            reader,
            sourceIdentity,
            sourceDisplayName,
            imagePath,
            options,
            progress,
            null,
            cancellationToken);

    private static DiskImageReport CreateImageCore(
        IDiskImageReader reader,
        string sourceIdentity,
        string sourceDisplayName,
        string imagePath,
        DiskImageOptions requestedOptions,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.Length <= 0)
            throw new InvalidOperationException("Kaynak aygıt kapasitesi okunamadı.");
        if (string.IsNullOrWhiteSpace(sourceIdentity))
            throw new InvalidOperationException("Kaynak aygıt kimliği oluşturulamadı; güvenli resume mümkün değil.");

        int sectorSize = ValidateSectorSize(reader.SectorSize);
        DiskImageOptions options = NormalizeOptions(requestedOptions, sectorSize);
        long total = reader.Length;

        string fullImagePath = Path.GetFullPath(imagePath);
        string? directory = Path.GetDirectoryName(fullImagePath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("İmaj hedef klasörü geçersiz.");
        Directory.CreateDirectory(directory);

        string mapPath = fullImagePath + ".nsxmap.json";
        string logPath = fullImagePath + ".nsxlog.txt";
        bool imageExists = File.Exists(fullImagePath);
        bool mapExists = File.Exists(mapPath);
        if (imageExists != mapExists)
        {
            throw new InvalidDataException(
                imageExists
                    ? "Mevcut imajın NSX bad-sector/checkpoint haritası bulunamadı. Güvenli olmayan kör devam veya üzerine yazma engellendi; yeni bir dosya adı seçin."
                    : "Checkpoint haritasına ait yarım imaj bulunamadı. Güvenli resume engellendi; dosya çiftini doğrulayın veya yeni bir ad seçin.");
        }

        DiskImageCheckpoint checkpoint;
        bool wasResumed = false;
        if (imageExists)
        {
            checkpoint = LoadCheckpoint(mapPath);
            ValidateCheckpoint(checkpoint, sourceIdentity, total, sectorSize);

            long existingLength = new FileInfo(fullImagePath).Length;
            if (string.Equals(checkpoint.Status, CompletedStatus, StringComparison.OrdinalIgnoreCase))
            {
                if (checkpoint.CompletedBytes != total || existingLength != total)
                    throw new InvalidDataException("Tamamlanmış imaj veya checkpoint uzunluğu değişmiş. Dosya bütünlüğü belirsiz olduğu için işlem durduruldu.");

                WriteFinalLog(logPath, checkpoint, fullImagePath, alreadyCompleted: true);
                return BuildReport(fullImagePath, logPath, mapPath, checkpoint, wasResumed: true, alreadyCompleted: true);
            }

            wasResumed = checkpoint.CompletedBytes > 0 || checkpoint.PrimaryPassCompleted;
        }
        else
        {
            checkpoint = new DiskImageCheckpoint
            {
                SourceIdentity = sourceIdentity,
                SourceDisplayName = sourceDisplayName,
                ImageFileName = Path.GetFileName(fullImagePath) ?? string.Empty,
                TotalBytes = total,
                SectorSize = sectorSize
            };
        }

        using var output = new FileStream(
            fullImagePath,
            imageExists ? FileMode.Open : FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            options.BlockSize,
            FileOptions.None);

        long processed = PrepareOutputForResume(output, checkpoint, total, sectorSize, imageExists);
        var heatMap = RestoreHeatMap(checkpoint, processed, sectorSize);
        checkpoint.CompletedBytes = processed;
        checkpoint.PrimaryPassCompleted = checkpoint.PrimaryPassCompleted && processed == total;
        if (!checkpoint.PrimaryPassCompleted)
            checkpoint.RetryPassesCompleted = 0;

        PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Imaging", processed);

        byte[] buffer = new byte[options.BlockSize];
        long lastCheckpoint = processed;
        bool primaryCompleted = checkpoint.PrimaryPassCompleted;

        try
        {
            if (!primaryCompleted)
            {
                output.Position = processed;
                while (processed < total)
                {
                    pauseGate?.Wait(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    int request = (int)Math.Min(buffer.Length, total - processed);
                    Span<byte> target = buffer.AsSpan(0, request);
                    target.Clear();
                    ReadRecovering(
                        reader,
                        processed,
                        target,
                        sectorSize,
                        options.SectorReadAttempts,
                        heatMap,
                        pauseGate,
                        cancellationToken);

                    output.Write(target);
                    processed += request;

                    if (processed - lastCheckpoint >= options.CheckpointIntervalBytes || processed >= total)
                    {
                        PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Imaging", processed);
                        ReportPrimaryProgress(progress, processed, total, heatMap);
                        lastCheckpoint = processed;
                    }
                }

                primaryCompleted = true;
                checkpoint.PrimaryPassCompleted = true;
                checkpoint.CompletedBytes = total;
                PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Retrying", total);
            }

            int nextRetryPass = Math.Max(1, checkpoint.RetryPassesCompleted + 1);
            for (int pass = nextRetryPass; pass <= options.RetryPasses && heatMap.UnreadableBytes > 0; pass++)
            {
                long recoveredThisPass = RetryBadSectors(
                    reader,
                    output,
                    mapPath,
                    checkpoint,
                    heatMap,
                    pass,
                    total,
                    sectorSize,
                    options.CheckpointIntervalBytes,
                    progress,
                    pauseGate,
                    cancellationToken);

                checkpoint.RetryPassesCompleted = pass;
                PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Retrying", total);

                // Bozuk medyayı gereksiz yere yormamak için iki tam retry turunda
                // hiçbir sektör kazanılamazsa üçüncü turu çalıştırma.
                if (recoveredThisPass == 0 && pass >= 2)
                    break;
            }

            output.SetLength(total);
            checkpoint.PrimaryPassCompleted = true;
            checkpoint.CompletedBytes = total;
            PersistCheckpoint(output, mapPath, checkpoint, heatMap, CompletedStatus, total);
            WriteFinalLog(logPath, checkpoint, fullImagePath, alreadyCompleted: false);
            ReportCompletedProgress(progress, checkpoint, total);

            return BuildReport(fullImagePath, logPath, mapPath, checkpoint, wasResumed, alreadyCompleted: false);
        }
        catch (OperationCanceledException)
        {
            checkpoint.PrimaryPassCompleted = primaryCompleted;
            TryPersistCheckpoint(output, mapPath, checkpoint, heatMap, "Paused", processed);
            throw;
        }
        catch
        {
            checkpoint.PrimaryPassCompleted = primaryCompleted;
            TryPersistCheckpoint(output, mapPath, checkpoint, heatMap, "Interrupted", processed);
            throw;
        }
    }

    private static long PrepareOutputForResume(
        FileStream output,
        DiskImageCheckpoint checkpoint,
        long total,
        int sectorSize,
        bool imageExists)
    {
        if (!imageExists)
        {
            output.SetLength(0);
            return 0;
        }

        if (output.Length > total)
            throw new InvalidDataException("Yarım imaj kaynak aygıt kapasitesinden büyük. Dosya değiştirilmiş olabileceği için resume durduruldu.");

        long durableLength = Math.Min(output.Length, Math.Clamp(checkpoint.CompletedBytes, 0, total));
        long resumeOffset = durableLength == total
            ? total
            : AlignDown(durableLength, sectorSize);

        if (output.Length != resumeOffset)
            output.SetLength(resumeOffset);
        output.Position = resumeOffset;
        return resumeOffset;
    }

    private static BadSectorHeatMap RestoreHeatMap(
        DiskImageCheckpoint checkpoint,
        long resumeOffset,
        int sectorSize)
    {
        var state = new RecoveryScanCheckpoint
        {
            BadSectorFailureEvents = checkpoint.FailureEvents,
            BadSectorRecoveredBytes = checkpoint.RecoveredBytes,
            BadSectors = checkpoint.BadSectors
                .Where(item => item.Offset >= 0 && item.Length > 0 && item.Offset < resumeOffset)
                .Select(item => new RecoveryBadSectorSnapshot
                {
                    Offset = item.Offset,
                    Length = Math.Min(item.Length, resumeOffset - item.Offset),
                    FailureEvents = Math.Max(1, item.FailureEvents)
                })
                .Where(item => item.Length > 0)
                .ToList()
        };

        var heatMap = new BadSectorHeatMap(sectorSize);
        heatMap.RestoreState(state);
        return heatMap;
    }

    private static void ReadRecovering(
        IDiskImageReader reader,
        long offset,
        Span<byte> destination,
        int sectorSize,
        int sectorReadAttempts,
        BadSectorHeatMap heatMap,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        if (destination.Length == 0)
            return;

        pauseGate?.Wait(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        long sectorRemainder = offset % sectorSize;
        if (sectorRemainder != 0 && destination.Length > sectorSize - sectorRemainder)
        {
            int prefixLength = (int)(sectorSize - sectorRemainder);
            ReadRecovering(
                reader,
                offset,
                destination[..prefixLength],
                sectorSize,
                sectorReadAttempts,
                heatMap,
                pauseGate,
                cancellationToken);
            ReadRecovering(
                reader,
                offset + prefixLength,
                destination[prefixLength..],
                sectorSize,
                sectorReadAttempts,
                heatMap,
                pauseGate,
                cancellationToken);
            return;
        }

        if (destination.Length <= sectorSize)
        {
            if (TryReadExactly(reader, offset, destination, sectorReadAttempts, pauseGate, cancellationToken))
                return;

            destination.Clear();
            heatMap.RecordFailure(offset, destination.Length);
            return;
        }

        try
        {
            int read = reader.Read(offset, destination);
            if (read == destination.Length)
                return;

            if (read > 0)
            {
                ReadRecovering(
                    reader,
                    offset + read,
                    destination[read..],
                    sectorSize,
                    sectorReadAttempts,
                    heatMap,
                    pauseGate,
                    cancellationToken);
                return;
            }
        }
        catch (IOException)
        {
            // Hatalı geniş blok sektör seviyesine kadar bölünür.
        }

        int split = destination.Length / 2;
        split -= split % sectorSize;
        if (split <= 0 || split >= destination.Length)
            split = Math.Min(sectorSize, destination.Length - 1);

        ReadRecovering(
            reader,
            offset,
            destination[..split],
            sectorSize,
            sectorReadAttempts,
            heatMap,
            pauseGate,
            cancellationToken);
        ReadRecovering(
            reader,
            offset + split,
            destination[split..],
            sectorSize,
            sectorReadAttempts,
            heatMap,
            pauseGate,
            cancellationToken);
    }

    private static bool TryReadExactly(
        IDiskImageReader reader,
        long offset,
        Span<byte> destination,
        int attempts,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            destination.Clear();
            int totalRead = 0;
            try
            {
                while (totalRead < destination.Length)
                {
                    int read = reader.Read(offset + totalRead, destination[totalRead..]);
                    if (read <= 0)
                        break;
                    totalRead += read;
                }

                if (totalRead == destination.Length)
                    return true;
            }
            catch (IOException)
            {
            }
        }

        destination.Clear();
        return false;
    }

    private static long RetryBadSectors(
        IDiskImageReader reader,
        FileStream output,
        string mapPath,
        DiskImageCheckpoint checkpoint,
        BadSectorHeatMap heatMap,
        int pass,
        long total,
        int sectorSize,
        long checkpointInterval,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BadSectorRange> ranges = heatMap.Snapshot(int.MaxValue);
        bool reverse = pass % 2 == 0;
        long recoveredSectors = 0;
        long attemptedBytes = 0;
        long bytesSinceCheckpoint = 0;
        byte[] sectorBuffer = new byte[sectorSize];

        foreach (long sectorOffset in EnumerateSectorOffsets(ranges, total, sectorSize, reverse))
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            int length = (int)Math.Min(sectorSize, total - sectorOffset);
            if (length <= 0)
                continue;

            Span<byte> target = sectorBuffer.AsSpan(0, length);
            bool recovered = TryReadExactly(reader, sectorOffset, target, 1, pauseGate, cancellationToken);
            attemptedBytes += length;
            bytesSinceCheckpoint += length;

            if (recovered)
            {
                output.Position = sectorOffset;
                output.Write(target);
                heatMap.MarkRecovered(sectorOffset, length);
                checkpoint.RecoveredSectors++;
                checkpoint.RecoveredBytes += length;
                recoveredSectors++;
            }

            if (bytesSinceCheckpoint >= checkpointInterval)
            {
                PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Retrying", total);
                ReportRetryProgress(progress, pass, attemptedBytes, ranges, heatMap, total, sectorSize);
                bytesSinceCheckpoint = 0;
            }
        }

        PersistCheckpoint(output, mapPath, checkpoint, heatMap, "Retrying", total);
        ReportRetryProgress(progress, pass, attemptedBytes, ranges, heatMap, total, sectorSize);
        return recoveredSectors;
    }

    private static IEnumerable<long> EnumerateSectorOffsets(
        IReadOnlyList<BadSectorRange> ranges,
        long total,
        int sectorSize,
        bool reverse)
    {
        if (!reverse)
        {
            foreach (BadSectorRange range in ranges)
            {
                long end = Math.Min(total, SafeAdd(range.Offset, range.Length));
                for (long offset = range.Offset; offset < end; offset += sectorSize)
                    yield return offset;
            }
            yield break;
        }

        for (int index = ranges.Count - 1; index >= 0; index--)
        {
            BadSectorRange range = ranges[index];
            long end = Math.Min(total, SafeAdd(range.Offset, range.Length));
            if (end <= range.Offset)
                continue;

            long offset = range.Offset + AlignDown(end - range.Offset - 1, sectorSize);
            while (offset >= range.Offset)
            {
                yield return offset;
                if (offset - range.Offset < sectorSize)
                    break;
                offset -= sectorSize;
            }
        }
    }

    private static void PersistCheckpoint(
        FileStream output,
        string mapPath,
        DiskImageCheckpoint checkpoint,
        BadSectorHeatMap heatMap,
        string status,
        long completedBytes)
    {
        output.Flush(flushToDisk: true);
        UpdateCheckpoint(checkpoint, heatMap, status, completedBytes);
        WriteCheckpointAtomic(mapPath, checkpoint);
    }

    private static void TryPersistCheckpoint(
        FileStream output,
        string mapPath,
        DiskImageCheckpoint checkpoint,
        BadSectorHeatMap heatMap,
        string status,
        long completedBytes)
    {
        try
        {
            PersistCheckpoint(output, mapPath, checkpoint, heatMap, status, completedBytes);
        }
        catch (Exception ex)
        {
            AppLog.Error("Disk image checkpoint could not be persisted.", ex);
        }
    }

    private static void UpdateCheckpoint(
        DiskImageCheckpoint checkpoint,
        BadSectorHeatMap heatMap,
        string status,
        long completedBytes)
    {
        checkpoint.Status = status;
        checkpoint.CompletedBytes = Math.Clamp(completedBytes, 0, checkpoint.TotalBytes);
        checkpoint.FailureEvents = heatMap.FailureEvents;
        checkpoint.UnreadableBytes = heatMap.UnreadableBytes;
        checkpoint.UpdatedAtUtc = DateTimeOffset.UtcNow;
        checkpoint.BadSectors = heatMap.Snapshot(int.MaxValue)
            .Select(range => new DiskImageBadSectorState
            {
                Offset = range.Offset,
                Length = range.Length,
                FailureEvents = range.FailureEvents
            })
            .ToList();
    }

    private static void WriteCheckpointAtomic(string mapPath, DiskImageCheckpoint checkpoint)
    {
        string tempPath = mapPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, checkpoint, CheckpointJsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, mapPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static DiskImageCheckpoint LoadCheckpoint(string mapPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(mapPath);
            DiskImageCheckpoint? checkpoint = JsonSerializer.Deserialize<DiskImageCheckpoint>(stream, CheckpointJsonOptions);
            if (checkpoint is null)
                throw new InvalidDataException("Disk imajı checkpoint içeriği boş.");
            checkpoint.BadSectors ??= [];
            return checkpoint;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Disk imajı checkpoint haritası okunamadı veya bozuk. Kör resume engellendi.", ex);
        }
    }

    private static void ValidateCheckpoint(
        DiskImageCheckpoint checkpoint,
        string sourceIdentity,
        long total,
        int sectorSize)
    {
        if (checkpoint.Version != 1)
            throw new InvalidDataException($"Desteklenmeyen disk imajı checkpoint sürümü: {checkpoint.Version}.");
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(checkpoint.SourceIdentity ?? string.Empty),
                Encoding.UTF8.GetBytes(sourceIdentity)))
        {
            throw new InvalidDataException("Checkpoint başka bir kaynak aygıta ait. Yanlış diske resume edilmesini önlemek için işlem durduruldu.");
        }
        if (checkpoint.TotalBytes != total || checkpoint.SectorSize != sectorSize)
            throw new InvalidDataException("Kaynak kapasitesi veya mantıksal sektör boyutu checkpoint ile eşleşmiyor. Güvenli resume mümkün değil.");
        if (checkpoint.CompletedBytes < 0 || checkpoint.CompletedBytes > total)
            throw new InvalidDataException("Checkpoint ilerleme konumu geçersiz.");
        long paddedTotal = AlignDown(SafeAdd(total, sectorSize - 1L), sectorSize);
        if (checkpoint.BadSectors.Any(item =>
                item.Offset < 0 ||
                item.Length <= 0 ||
                item.Offset >= total ||
                item.Length > long.MaxValue - item.Offset ||
                item.Offset + item.Length > paddedTotal))
            throw new InvalidDataException("Checkpoint bad-sector haritasında geçersiz aralık bulundu.");
    }

    private static DiskImageOptions NormalizeOptions(DiskImageOptions options, int sectorSize)
    {
        ArgumentNullException.ThrowIfNull(options);
        int blockSize = Math.Clamp(options.BlockSize, sectorSize, MaximumBlockSize);
        blockSize -= blockSize % sectorSize;
        if (blockSize < sectorSize)
            blockSize = sectorSize;

        long checkpointInterval = Math.Max(blockSize, options.CheckpointIntervalBytes);
        return options with
        {
            BlockSize = blockSize,
            CheckpointIntervalBytes = checkpointInterval,
            SectorReadAttempts = Math.Clamp(options.SectorReadAttempts, 1, 8),
            RetryPasses = Math.Clamp(options.RetryPasses, 0, 8)
        };
    }

    private static int ValidateSectorSize(int sectorSize)
    {
        if (sectorSize is < 512 or > 65536 || (sectorSize & (sectorSize - 1)) != 0)
            throw new InvalidDataException($"Geçersiz mantıksal sektör boyutu: {sectorSize}.");
        return sectorSize;
    }

    private static string BuildSourceIdentity(StorageDeviceInfo device, long sourceLength, int sectorSize)
    {
        string serial = NormalizeIdentityPart(device.HardwareSerialNumber);
        string stableDevice = string.IsNullOrWhiteSpace(serial)
            ? $"physical={device.PhysicalDriveNumber?.ToString() ?? "unknown"}|root={NormalizeIdentityPart(device.RootPath)}"
            : $"serial={serial}";
        string canonical =
            $"NSX-IMAGE-V1|{stableDevice}|bytes={sourceLength}|sector={sectorSize}|whole={device.IsWholePhysicalDisk}|" +
            $"partition={device.IsPartitionSource}|offset={device.PartitionOffsetBytes}|length={device.PartitionLengthBytes}|" +
            $"partition-id={NormalizeIdentityPart(device.PartitionIdentity)}|fs={NormalizeIdentityPart(device.FileSystem)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string NormalizeIdentityPart(string? value) =>
        (value ?? string.Empty).Trim().Replace("|", "_", StringComparison.Ordinal).ToUpperInvariant();

    private static void ReportPrimaryProgress(
        IProgress<OperationProgress>? progress,
        long processed,
        long total,
        BadSectorHeatMap heatMap)
    {
        double percent = processed * 100d / total;
        progress?.Report(new OperationProgress(
            Math.Clamp(percent, 0d, 100d),
            "Block Image • Sector Copy",
            $"I/O {RecoveryFileItem.FormatBytes(processed)} / {RecoveryFileItem.FormatBytes(total)} • " +
            $"okunamayan {RecoveryFileItem.FormatBytes(heatMap.UnreadableBytes)}",
            processed,
            total));
    }

    private static void ReportRetryProgress(
        IProgress<OperationProgress>? progress,
        int pass,
        long attemptedBytes,
        IReadOnlyList<BadSectorRange> initialRanges,
        BadSectorHeatMap heatMap,
        long total,
        int sectorSize)
    {
        long initialBytes = initialRanges.Sum(range => range.Length);
        long initialSectors = DivideRoundUp(initialBytes, sectorSize);
        long attemptedSectors = DivideRoundUp(attemptedBytes, sectorSize);
        progress?.Report(new OperationProgress(
            100d,
            $"Bad Sector Retry • Tur {pass}",
            $"Denenen sektör {Math.Min(attemptedSectors, initialSectors):N0}/{initialSectors:N0} • " +
            $"kalan {RecoveryFileItem.FormatBytes(heatMap.UnreadableBytes)}",
            total,
            total));
    }

    private static void ReportCompletedProgress(
        IProgress<OperationProgress>? progress,
        DiskImageCheckpoint checkpoint,
        long total)
    {
        progress?.Report(new OperationProgress(
            100d,
            "Disk İmajı Tamamlandı",
            checkpoint.UnreadableBytes == 0
                ? $"Tüm sektörler okundu • retry ile kurtarılan {checkpoint.RecoveredSectors:N0} sektör"
                : $"Kalan okunamayan {RecoveryFileItem.FormatBytes(checkpoint.UnreadableBytes)} • bad-sector map kaydedildi",
            total,
            total));
    }

    private static DiskImageReport BuildReport(
        string imagePath,
        string logPath,
        string mapPath,
        DiskImageCheckpoint checkpoint,
        bool wasResumed,
        bool alreadyCompleted)
    {
        long badSectorCount = DivideRoundUp(checkpoint.UnreadableBytes, checkpoint.SectorSize);
        return new DiskImageReport(
            imagePath,
            checkpoint.TotalBytes,
            (int)Math.Min(int.MaxValue, badSectorCount),
            logPath)
        {
            UnreadableBytes = checkpoint.UnreadableBytes,
            RecoveredSectors = checkpoint.RecoveredSectors,
            RetryPasses = checkpoint.RetryPassesCompleted,
            WasResumed = wasResumed,
            AlreadyCompleted = alreadyCompleted,
            MapPath = mapPath
        };
    }

    private static void WriteFinalLog(
        string logPath,
        DiskImageCheckpoint checkpoint,
        string imagePath,
        bool alreadyCompleted)
    {
        string contents =
            "NSX Veri Kurtarma Pro Disk İmajı\r\n" +
            $"Kaynak: {checkpoint.SourceDisplayName}\r\n" +
            $"İmaj: {imagePath}\r\n" +
            $"Toplam bayt: {checkpoint.TotalBytes}\r\n" +
            $"Mantıksal sektör: {checkpoint.SectorSize} bayt\r\n" +
            $"Retry turu: {checkpoint.RetryPassesCompleted}\r\n" +
            $"Retry ile kurtarılan sektör: {checkpoint.RecoveredSectors}\r\n" +
            $"Kalan okunamayan bayt: {checkpoint.UnreadableBytes}\r\n" +
            $"Kalan bad-sector aralığı: {checkpoint.BadSectors.Count}\r\n" +
            $"Checkpoint durumu: {checkpoint.Status}\r\n" +
            $"Bad-sector map: {imagePath}.nsxmap.json\r\n" +
            $"Güncelleme (UTC): {checkpoint.UpdatedAtUtc:O}\r\n" +
            (alreadyCompleted ? "Not: Tamamlanmış imaj yeniden yazılmadan doğrulandı.\r\n" : string.Empty) +
            "Not: Okunamayan sektörler imaj içinde 00 ile doldurulmuştur; kesin konumlar JSON bad-sector map içindedir.\r\n";

        WriteTextAtomic(logPath, contents);
    }

    private static void WriteTextAtomic(string path, string contents)
    {
        string tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static long DivideRoundUp(long value, long divisor) =>
        value <= 0 ? 0 : 1 + (value - 1) / divisor;

    private static long AlignDown(long value, int alignment) => value - value % alignment;

    private static long SafeAdd(long left, long right) =>
        right > long.MaxValue - left ? long.MaxValue : left + right;

    private sealed class RawDiskImageReader : IDiskImageReader
    {
        private readonly RawDeviceReader _reader;

        public RawDiskImageReader(RawDeviceReader reader) => _reader = reader;

        public long Length => _reader.VolumeLength;
        public int SectorSize => _reader.SectorSize;
        public int Read(long offset, Span<byte> buffer) => _reader.Read(offset, buffer);
        public void Dispose() => _reader.Dispose();
    }
}
