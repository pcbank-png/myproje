using NSXVeriKurtarmaPro.Models;
using System.Security.Cryptography;

namespace NSXVeriKurtarmaPro.Services;

public sealed record RecoveryBatchReport(
    int Succeeded,
    int Failed,
    long WrittenBytes,
    string Destination,
    int Partial = 0,
    long UnreadableBytes = 0,
    int Verified = 0,
    int NeedsReview = 0,
    int VerificationFailed = 0);

public sealed record RecoveryDestinationCapacityInfo(
    long SelectedBytes,
    long RequiredBytes,
    long AvailableBytes,
    long TotalBytes,
    string TargetRoot,
    bool IsKnown)
{
    public bool ExceedsTotalCapacity => IsKnown && SelectedBytes > TotalBytes;
    public bool ExceedsAvailableSpace => IsKnown && RequiredBytes > AvailableBytes;
    public bool CanFit => !IsKnown || (!ExceedsTotalCapacity && !ExceedsAvailableSpace);
}

public sealed class RecoveryService
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const long MinimumFreeSpaceReserveBytes = 8L * 1024 * 1024;
    private const long MaximumFreeSpaceReserveBytes = 256L * 1024 * 1024;
    private const string RecoveryFolderName = "NSX Kurtarılan Dosyalar";

    public RecoveryDestinationCapacityInfo InspectDestinationCapacity(
        IReadOnlyList<RecoveryFileItem> items,
        string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("Kurtarma hedef klasörü belirtilmedi.", nameof(destinationDirectory));

        long selectedBytes = 0L;
        foreach (RecoveryFileItem item in items)
        {
            long itemBytes = Math.Max(0L, GetRecoveryWorkBytes(item));
            selectedBytes = selectedBytes > long.MaxValue - itemBytes
                ? long.MaxValue
                : selectedBytes + itemBytes;
        }

        long requiredBytes = CalculateRequiredCapacity(selectedBytes, includeReserve: true);
        try
        {
            string fullPath = Path.GetFullPath(destinationDirectory);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return new RecoveryDestinationCapacityInfo(selectedBytes, requiredBytes, -1L, -1L, fullPath, IsKnown: false);

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new RecoveryDestinationCapacityInfo(selectedBytes, requiredBytes, -1L, -1L, root, IsKnown: false);

            return new RecoveryDestinationCapacityInfo(
                selectedBytes,
                requiredBytes,
                Math.Max(0L, drive.AvailableFreeSpace),
                Math.Max(0L, drive.TotalSize),
                root,
                IsKnown: true);
        }
        catch
        {
            // UNC veya özel provider hedeflerinde kapasite bilgisi alınamıyorsa
            // asıl yazma katmanındaki güvenli kapasite kontrolü devrede kalır.
            return new RecoveryDestinationCapacityInfo(selectedBytes, requiredBytes, -1L, -1L, destinationDirectory, IsKnown: false);
        }
    }

    public RecoveryBatchReport Recover(
        StorageDeviceInfo sourceDevice,
        IReadOnlyList<RecoveryFileItem> items,
        string destinationDirectory,
        IProgress<OperationProgress>? progress,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken)
    {
        destinationDirectory = ResolveRecoveryDestination(destinationDirectory);

        if (items.Count == 0)
            return new RecoveryBatchReport(0, 0, 0, destinationDirectory);

        Directory.CreateDirectory(destinationDirectory);

        long[] workBytes = items.Select(GetRecoveryWorkBytes).ToArray();
        long totalBytes = workBytes.Sum();
        EnsureDestinationCapacity(destinationDirectory, totalBytes, includeReserve: true);
        long completedWorkBytes = 0;
        long writtenBytes = 0;
        int succeeded = 0;
        int failed = 0;
        int completedItems = 0;
        int partial = 0;
        long unreadableBytes = 0;
        long lastReportedBytes = -1;
        TimeSpan lastReportedAt = TimeSpan.Zero;
        var progressClock = System.Diagnostics.Stopwatch.StartNew();

        void ReportProgress(long processedBytes, string detail, bool force = false)
        {
            processedBytes = Math.Clamp(processedBytes, 0L, Math.Max(0L, totalBytes));
            TimeSpan elapsed = progressClock.Elapsed;
            long deltaBytes = lastReportedBytes < 0 ? processedBytes : processedBytes - lastReportedBytes;
            bool shouldReport = force ||
                                lastReportedBytes < 0 ||
                                deltaBytes >= 2L * 1024 * 1024 ||
                                (elapsed - lastReportedAt).TotalMilliseconds >= 180;
            if (!shouldReport)
                return;

            double percent = totalBytes > 0
                ? processedBytes * 100d / totalBytes
                : completedItems * 100d / Math.Max(1, items.Count);
            if (completedItems < items.Count)
                percent = Math.Min(percent, 99.5d);

            progress?.Report(new OperationProgress(
                Math.Clamp(percent, 0d, 100d),
                "Dosyalar kurtarılıyor",
                detail,
                processedBytes,
                totalBytes,
                succeeded));

            lastReportedBytes = processedBytes;
            lastReportedAt = elapsed;
        }

        ReportProgress(0, $"{items.Count:N0} seçili dosya hazırlanıyor", force: true);

        bool needsRawReader = items.Any(item =>
            !item.IsRepaired ||
            string.IsNullOrWhiteSpace(item.RepairedFilePath) ||
            !File.Exists(item.RepairedFilePath));
        using RawDeviceReader? reader = needsRawReader
            ? RawDeviceReader.OpenDevice(sourceDevice, pauseGate, RecoveryMediaProfileService.Create(sourceDevice))
            : null;

        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            RecoveryFileItem item = items[itemIndex];
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            long itemWorkBytes = Math.Max(0L, workBytes[itemIndex]);
            long itemProcessedBytes = 0;
            void AdvanceItemProgress(long bytes)
            {
                if (bytes <= 0)
                    return;

                itemProcessedBytes = itemWorkBytes > 0
                    ? Math.Min(itemWorkBytes, itemProcessedBytes + bytes)
                    : itemProcessedBytes + bytes;
                ReportProgress(
                    completedWorkBytes + Math.Min(itemWorkBytes, itemProcessedBytes),
                    $"{itemIndex + 1:N0}/{items.Count:N0} • {item.FileName}");
            }

            string? repairedFilePath = item.RepairedFilePath;
            bool useRepairedFile = item.IsRepaired &&
                                   !string.IsNullOrWhiteSpace(repairedFilePath) &&
                                   File.Exists(repairedFilePath);
            string destinationFileName = FileTypeHelper.SanitizeFileName(item.FileName);
            string verificationExtension = item.Extension;
            if (useRepairedFile && repairedFilePath is not null)
            {
                string repairedExtension = Path.GetExtension(repairedFilePath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(repairedExtension))
                {
                    destinationFileName = Path.ChangeExtension(destinationFileName, repairedExtension);
                    verificationExtension = repairedExtension;
                }
            }

            string finalDestinationPath = GetUniquePath(destinationDirectory, destinationFileName);
            string workingPath = CreateRecoveryWorkingPath(destinationDirectory, destinationFileName);
            string? repairWorkingPath = null;
            string? preservedRawPath = null;
            try
            {
                EnsureDestinationCapacity(destinationDirectory, itemWorkBytes, includeReserve: false);

                using IncrementalHash outputHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var output = new FileStream(
                    workingPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    BufferSize,
                    FileOptions.SequentialScan);

                long before = output.Position;
                long itemUnreadableBytes = 0;

                if (useRepairedFile)
                {
                    string repairedSourcePath = repairedFilePath
                        ?? throw new InvalidOperationException("Onarılmış dosya yolu bulunamadı.");
                    using var repairedInput = new FileStream(
                        repairedSourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        BufferSize,
                        FileOptions.SequentialScan);
                    CopyStreamWithProgress(repairedInput, output, outputHash, pauseGate, cancellationToken, AdvanceItemProgress);
                }
                else
                {
                    RawDeviceReader sourceReader = reader
                        ?? throw new InvalidOperationException("Kaynak aygıt okuyucusu başlatılamadı.");

                    if (item.PrefixData is { Length: > 0 })
                    {
                        pauseGate?.Wait(cancellationToken);
                        WriteHashed(output, outputHash, item.PrefixData);
                        AdvanceItemProgress(item.PrefixData.Length);
                    }

                    switch (item.SourceKind)
                    {
                        case RecoverySourceKind.NtfsResident:
                        {
                            long positionBefore = output.Position;
                            pauseGate?.Wait(cancellationToken);
                            WriteResident(item, output, outputHash);
                            AdvanceItemProgress(output.Position - positionBefore);
                            break;
                        }

                        case RecoverySourceKind.NtfsRunList:
                            itemUnreadableBytes += CopyNtfsRuns(sourceReader, item, output, outputHash, pauseGate, cancellationToken, AdvanceItemProgress);
                            break;

                        case RecoverySourceKind.RawContiguous:
                        case RecoverySourceKind.FatContiguous:
                        case RecoverySourceKind.ExFatContiguous:
                            if (item.TransformKind == RecoveryTransformKind.None)
                                itemUnreadableBytes += CopyContiguous(sourceReader, item.SourceOffset, item.SizeBytes, output, outputHash, cancellationToken, AdvanceItemProgress);
                            else
                                itemUnreadableBytes += CopyLengthPrefixedNalToAnnexB(sourceReader, item, output, outputHash, cancellationToken, AdvanceItemProgress);
                            break;

                        case RecoverySourceKind.Extents:
                            itemUnreadableBytes += CopyExtents(sourceReader, item, output, outputHash, cancellationToken, AdvanceItemProgress);
                            break;

                        default:
                            throw new NotSupportedException("Kurtarma kaynağı desteklenmiyor.");
                    }

                    if (item.SuffixData is { Length: > 0 })
                    {
                        pauseGate?.Wait(cancellationToken);
                        WriteHashed(output, outputHash, item.SuffixData);
                        AdvanceItemProgress(item.SuffixData.Length);
                    }
                }

                pauseGate?.Wait(cancellationToken);
                ReportProgress(
                    completedWorkBytes + itemWorkBytes,
                    $"{itemIndex + 1:N0}/{items.Count:N0} • {item.FileName} kaydediliyor",
                    force: true);

                output.Flush(true);
                long actual = output.Length - before;
                _ = outputHash.GetHashAndReset();

                if (actual <= 0)
                    throw new IOException("Kurtarılan dosya boş oluştu.");

                output.Position = 0;
                byte[] verificationHeader = new byte[(int)Math.Min(2048L, output.Length)];
                int verificationRead = output.Read(verificationHeader, 0, verificationHeader.Length);
                FileHeaderValidationOutcome headerOutcome = verificationRead > 0
                    ? FileHeaderValidator.Validate(
                        verificationExtension,
                        verificationHeader.AsSpan(0, verificationRead),
                        output.Length)
                    : FileHeaderValidationOutcome.Mismatch;

                if (headerOutcome == FileHeaderValidationOutcome.Mismatch && itemUnreadableBytes <= 0)
                    throw new IOException("Kurtarılan dosyanın başlangıç yapısı doğrulanamadı.");

                output.Dispose();

                if (!useRepairedFile && item.Category == "Video")
                    item.SourceUnreadableBytes = itemUnreadableBytes;

                bool autoRepairEligible = !useRepairedFile &&
                                          item.Category == "Video" &&
                                          ShouldAutoRepairVideo(item, verificationExtension);

                if (autoRepairEligible)
                {
                    // Adli güvenlik ilkesi: ham çıkarım onarım başlamadan önce kalıcı hedefe commit edilir.
                    // Bundan sonraki hiçbir onarım hatası bu ham adayı silemez.
                    preservedRawPath = CommitRawCandidateBeforeAutoRepair(workingPath, finalDestinationPath);
                    ApplyRecoveredTimestamp(item, preservedRawPath);
                    long rawBytes = new FileInfo(preservedRawPath).Length;
                    writtenBytes = checked(writtenBytes + rawBytes);

                    // Onarım ham dosyayı asla değiştirmez; ayrı bir geçici türev üretir.
                    EnsureDestinationCapacity(destinationDirectory, itemWorkBytes, includeReserve: false);
                    ReportProgress(
                        completedWorkBytes + itemWorkBytes,
                        $"{itemIndex + 1:N0}/{items.Count:N0} • {item.FileName} video onarımı uygulanıyor",
                        force: true);

                    AutoRepairCandidate? repairedCandidate = TryAutoRepairRecoveredVideo(item, preservedRawPath);
                    if (repairedCandidate is not null)
                    {
                        repairWorkingPath = repairedCandidate.OutputPath;
                        string repairedExtension = repairedCandidate.OutputExtension;
                        pauseGate?.Wait(cancellationToken);
                        if (File.Exists(repairWorkingPath) && new FileInfo(repairWorkingPath).Length > 0)
                        {
                            string repairedFileName = BuildAutoRepairDerivativeFileName(destinationFileName, repairedExtension);
                            string repairedFinalPath = GetUniquePath(destinationDirectory, repairedFileName);
                            string committedRepairPath = CommitRecoveredOutput(repairWorkingPath, repairedFinalPath);
                            repairWorkingPath = null;
                            ApplyRecoveredTimestamp(item, committedRepairPath);
                            writtenBytes = checked(writtenBytes + repairedCandidate.Bytes);
                        }
                        else
                        {
                            // Boş türev temizlenebilir; ham çıkarım kalıcı hedefte korunur.
                            AppLog.Info($"UYARI • Otomatik video onarımı boş çıktı üretti; ham aday korundu: {item.FileName}");
                            TryDeleteFailedOutput(repairWorkingPath);
                            repairWorkingPath = null;
                        }
                    }
                }
                else
                {
                    finalDestinationPath = CommitRecoveredOutput(workingPath, finalDestinationPath);
                    ApplyRecoveredTimestamp(item, finalDestinationPath);
                    writtenBytes = checked(writtenBytes + actual);
                }

                if (itemUnreadableBytes > 0)
                {
                    partial++;
                    unreadableBytes += itemUnreadableBytes;
                }

                succeeded++;
            }
            catch (OperationCanceledException)
            {
                // Yalnız geçici dosyalar temizlenir. preservedRawPath kalıcı kanıttır ve asla silinmez.
                TryDeleteFailedOutput(repairWorkingPath ?? string.Empty);
                TryDeleteFailedOutput(workingPath);
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                AppLog.Error($"Dosya kurtarılamadı: {item.FileName}", ex);
                // Otomatik onarım başladıysa ham aday önceden commit edilmiştir; hata yolunda korunur.
                TryDeleteFailedOutput(repairWorkingPath ?? string.Empty);
                TryDeleteFailedOutput(workingPath);
            }

            completedWorkBytes += itemWorkBytes;
            completedItems++;
            ReportProgress(
                completedWorkBytes,
                $"İşlenen {completedItems:N0}/{items.Count:N0} • Kurtarılan {succeeded:N0} • Kısmi {partial:N0} • Başarısız {failed:N0}",
                force: true);
        }

        progress?.Report(new OperationProgress(
            100d,
            "Kurtarma tamamlandı",
            $"Kurtarılan {succeeded:N0} • Kısmi {partial:N0} • Başarısız {failed:N0}",
            totalBytes,
            totalBytes,
            succeeded));

        return new RecoveryBatchReport(
            succeeded,
            failed,
            writtenBytes,
            destinationDirectory,
            partial,
            unreadableBytes,
            Verified: 0,
            NeedsReview: 0,
            VerificationFailed: 0);
    }

    private static long GetRecoveryWorkBytes(RecoveryFileItem item)
    {
        try
        {
            if (item.IsRepaired &&
                !string.IsNullOrWhiteSpace(item.RepairedFilePath) &&
                File.Exists(item.RepairedFilePath))
            {
                return Math.Max(0L, new FileInfo(item.RepairedFilePath).Length);
            }
        }
        catch
        {
            // Kaynak boyutu okunamazsa ham aday boyutu kullanılır.
        }

        long prefix = item.PrefixData?.LongLength ?? 0L;
        long suffix = item.SuffixData?.LongLength ?? 0L;
        return checked(Math.Max(0L, item.SizeBytes) + prefix + suffix);
    }

    private static void CopyStreamWithProgress(
        Stream input,
        FileStream output,
        IncrementalHash outputHash,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        Action<long>? advanceProgress)
    {
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            WriteHashed(output, outputHash, buffer.AsSpan(0, read));
            advanceProgress?.Invoke(read);
        }
    }

    private static void WriteHashed(
        FileStream output,
        IncrementalHash outputHash,
        ReadOnlySpan<byte> data)
    {
        output.Write(data);
        outputHash.AppendData(data);
    }

    private static void ApplyRecoveredTimestamp(RecoveryFileItem item, string path)
    {
        if (!item.CapturedAt.HasValue || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            DateTime local = item.CapturedAt.Value.LocalDateTime;
            File.SetCreationTime(path, local);
            File.SetLastWriteTime(path, local);
        }
        catch
        {
            // Tarih koruma ikincil metadata işlemidir; başarılı kurtarmayı geçersiz kılmaz.
        }
    }

    private static bool ShouldAutoRepairVideo(RecoveryFileItem item, string extension)
    {
        if (!MediaRepairService.CanRepairVideo(extension))
            return false;

        return item.RecoveryState is
            "Yeniden İnşa" or
            "Parçalı Video" or
            "Video Parçası" or
            "Video Akışı" or
            "Kısmi" or
            "Zayıf";
    }

    private sealed record AutoRepairCandidate(long Bytes, string OutputPath, string OutputExtension);

    private static AutoRepairCandidate? TryAutoRepairRecoveredVideo(RecoveryFileItem item, string recoveredPath)
    {
        string directory = Path.GetDirectoryName(recoveredPath) ?? Path.GetTempPath();
        string sourceExtension = FileTypeHelper.Normalize(item.Extension);
        string outputExtension = sourceExtension switch
        {
            "TOD" => ".ts",
            "MOD" => ".mpg",
            _ => Path.GetExtension(recoveredPath) ?? string.Empty
        };
        if (string.IsNullOrWhiteSpace(outputExtension))
            outputExtension = ".bin";

        string repairedPath = Path.Combine(directory, $".nsx_repair_{Guid.NewGuid():N}{outputExtension}");

        try
        {
            var repairService = new MediaRepairService();
            MediaRepairResult result = repairService.Repair(item, recoveredPath, repairedPath);
            if (!result.Success || result.OutputBytes <= 0 || !File.Exists(repairedPath))
            {
                TryDeleteFailedOutput(repairedPath);
                return null;
            }

            // Kritik güvenlik kuralı: aynı uzantıda bile repairedPath hiçbir zaman recoveredPath üzerine taşınmaz.
            // recoveredPath adli/ham aday olarak değişmeden kalır; onarılmış çıktı ayrı türevdir.
            return new AutoRepairCandidate(new FileInfo(repairedPath).Length, repairedPath, outputExtension);
        }
        catch
        {
            TryDeleteFailedOutput(repairedPath);
            return null;
        }
    }

    internal static string BuildAutoRepairDerivativeFileName(string originalFileName, string outputExtension)
    {
        string safeOriginalName = FileTypeHelper.SanitizeFileName(originalFileName);
        string baseName = Path.GetFileNameWithoutExtension(safeOriginalName) ?? string.Empty;
        string extension = string.IsNullOrWhiteSpace(outputExtension)
            ? Path.GetExtension(safeOriginalName) ?? string.Empty
            : outputExtension;
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".bin";
        if (!extension.StartsWith(".", StringComparison.Ordinal))
            extension = "." + extension;

        return $"{baseName}_onarilmis{extension}";
    }

    private static string ResolveRecoveryDestination(string selectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(selectedDirectory))
            throw new ArgumentException("Kurtarma hedef klasörü belirtilmedi.", nameof(selectedDirectory));

        string fullPath = Path.GetFullPath(selectedDirectory);
        string trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string selectedFolderName = Path.GetFileName(trimmedPath) ?? string.Empty;

        if (string.Equals(selectedFolderName, RecoveryFolderName, StringComparison.OrdinalIgnoreCase))
            return fullPath;

        return Path.Combine(fullPath, RecoveryFolderName);
    }

    public string CreateTemporaryPreviewFile(
        StorageDeviceInfo sourceDevice,
        RecoveryFileItem item,
        string previewDirectory,
        CancellationToken cancellationToken) =>
        CreateTemporaryPreviewFileCore(
            sourceDevice,
            item,
            previewDirectory,
            Math.Max(0, item.SizeBytes),
            includeSuffix: true,
            durableFlush: true,
            cancellationToken);

    public string CreateTemporaryVideoPreviewFile(
        StorageDeviceInfo sourceDevice,
        RecoveryFileItem item,
        string previewDirectory,
        long maximumSourceBytes,
        CancellationToken cancellationToken)
    {
        if (maximumSourceBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumSourceBytes));

        long previewBytes = Math.Min(Math.Max(0, item.SizeBytes), maximumSourceBytes);
        return CreateTemporaryPreviewFileCore(
            sourceDevice,
            item,
            previewDirectory,
            previewBytes,
            includeSuffix: previewBytes >= item.SizeBytes,
            durableFlush: false,
            cancellationToken);
    }

    private string CreateTemporaryPreviewFileCore(
        StorageDeviceInfo sourceDevice,
        RecoveryFileItem item,
        string previewDirectory,
        long previewContentBytes,
        bool includeSuffix,
        bool durableFlush,
        CancellationToken cancellationToken)
    {
        if (previewContentBytes <= 0)
            return string.Empty;

        Directory.CreateDirectory(previewDirectory);

        string extension = FileTypeHelper.Normalize(item.Extension).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(extension))
            extension = "bin";

        string previewPath = Path.Combine(previewDirectory, $"nsx_preview_{Guid.NewGuid():N}.{extension}");

        try
        {
            using RawDeviceReader reader = RawDeviceReader.OpenDevice(sourceDevice, mediaProfile: RecoveryMediaProfileService.Create(sourceDevice));
            using var output = new FileStream(
                previewPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                BufferSize,
                FileOptions.SequentialScan);

            if (cancellationToken.IsCancellationRequested)
            {
                output.Dispose();
                TryDeleteFailedOutput(previewPath);
                return string.Empty;
            }

            if (item.PrefixData is { Length: > 0 })
                output.Write(item.PrefixData, 0, item.PrefixData.Length);

            bool completed = item.SourceKind switch
            {
                RecoverySourceKind.NtfsResident => WriteResidentPreview(item, previewContentBytes, output, cancellationToken),
                RecoverySourceKind.NtfsRunList => CopyNtfsRunsPreview(reader, item, previewContentBytes, output, cancellationToken),
                RecoverySourceKind.RawContiguous or RecoverySourceKind.FatContiguous or RecoverySourceKind.ExFatContiguous
                    => item.TransformKind == RecoveryTransformKind.None
                        ? CopyContiguousPreview(reader, item.SourceOffset, previewContentBytes, output, cancellationToken)
                        : CopyLengthPrefixedNalToAnnexBPreview(
                            reader,
                            item,
                            previewContentBytes,
                            requireCompleteInput: previewContentBytes >= item.SizeBytes,
                            output,
                            cancellationToken),
                RecoverySourceKind.Extents => CopyExtentsPreview(reader, item, previewContentBytes, output, cancellationToken),
                _ => throw new NotSupportedException("Önizleme kaynağı desteklenmiyor.")
            };

            if (completed && includeSuffix && item.SuffixData is { Length: > 0 })
                output.Write(item.SuffixData, 0, item.SuffixData.Length);

            if (!completed || cancellationToken.IsCancellationRequested)
            {
                output.Dispose();
                TryDeleteFailedOutput(previewPath);
                return string.Empty;
            }

            if (durableFlush)
                output.Flush(true);
            else
                output.Flush();
            if (cancellationToken.IsCancellationRequested)
            {
                output.Dispose();
                TryDeleteFailedOutput(previewPath);
                return string.Empty;
            }

            if (output.Length <= 0)
                throw new IOException("Önizleme dosyası oluşturulamadı.");

            return previewPath;
        }
        catch
        {
            TryDeleteFailedOutput(previewPath);
            throw;
        }
    }

    private static bool WriteResidentPreview(
        RecoveryFileItem item,
        long previewContentBytes,
        FileStream output,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return false;

        if (item.ResidentData is null)
            throw new InvalidDataException("Yerleşik NTFS verisi bulunamadı.");

        int count = checked((int)Math.Min(previewContentBytes, item.ResidentData.LongLength));
        output.Write(item.ResidentData, 0, count);
        return !cancellationToken.IsCancellationRequested;
    }

    private static bool CopyNtfsRunsPreview(
        RawDeviceReader reader,
        RecoveryFileItem item,
        long previewContentBytes,
        FileStream output,
        CancellationToken cancellationToken)
    {
        if (item.DataRuns is null || item.DataRuns.Count == 0 || item.ClusterSize <= 0)
            throw new InvalidDataException("NTFS küme zinciri bulunamadı.");

        long remaining = previewContentBytes;
        byte[] buffer = new byte[BufferSize];

        foreach (DataRun run in item.DataRuns)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            if (remaining <= 0)
                break;

            long runBytes = checked(run.ClusterCount * (long)item.ClusterSize);
            long bytesToWrite = Math.Min(runBytes, remaining);

            if (run.IsSparse)
            {
                Array.Clear(buffer);
                long sparseRemaining = bytesToWrite;
                while (sparseRemaining > 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        return false;

                    int count = (int)Math.Min(buffer.Length, sparseRemaining);
                    output.Write(buffer, 0, count);
                    sparseRemaining -= count;
                }
            }
            else
            {
                long sourceOffset = checked(run.LogicalClusterNumber * (long)item.ClusterSize);
                if (!CopyContiguousPreview(reader, sourceOffset, bytesToWrite, output, cancellationToken))
                    return false;
            }

            remaining -= bytesToWrite;
        }

        if (remaining > 0)
            throw new IOException("NTFS küme zinciri dosyanın tamamını karşılamadı.");

        return true;
    }

    private static bool CopyContiguousPreview(
        RawDeviceReader reader,
        long sourceOffset,
        long length,
        FileStream output,
        CancellationToken cancellationToken)
    {
        if (sourceOffset < 0 || length <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        byte[] buffer = new byte[BufferSize];
        long remaining = length;
        long offset = sourceOffset;

        while (remaining > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            int request = (int)Math.Min(buffer.Length, remaining);
            int read = reader.Read(offset, buffer, request);
            if (read <= 0)
                throw new EndOfStreamException("Kaynak aygıt beklenenden önce sona erdi.");

            output.Write(buffer, 0, read);
            offset += read;
            remaining -= read;
        }

        return true;
    }

    private static bool CopyExtentsPreview(
        RawDeviceReader reader,
        RecoveryFileItem item,
        long previewContentBytes,
        FileStream output,
        CancellationToken cancellationToken)
    {
        if (item.SourceExtents is null || item.SourceExtents.Count == 0)
            throw new InvalidDataException("Kurtarma parça zinciri bulunamadı.");

        long remaining = previewContentBytes;
        foreach (SourceExtent extent in item.SourceExtents)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            if (remaining <= 0)
                break;

            long length = Math.Min(extent.Length, remaining);
            if (extent.Offset < 0 || length <= 0)
                continue;

            if (!CopyContiguousPreview(reader, extent.Offset, length, output, cancellationToken))
                return false;

            remaining -= length;
        }

        if (remaining > 0)
            throw new IOException("Parçalı kaynak dosyanın tamamını karşılamadı.");

        return true;
    }

    private static bool CopyLengthPrefixedNalToAnnexBPreview(
        RawDeviceReader reader,
        RecoveryFileItem item,
        long previewContentBytes,
        bool requireCompleteInput,
        FileStream output,
        CancellationToken cancellationToken)
    {
        long remaining = previewContentBytes;
        long offset = item.SourceOffset;
        Span<byte> lengthBytes = stackalloc byte[4];
        byte[] buffer = new byte[BufferSize];
        ReadOnlySpan<byte> startCode = [0x00, 0x00, 0x00, 0x01];

        while (remaining >= 5)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;
            if (!reader.ReadExact(offset, lengthBytes))
                break;

            uint nalLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (nalLength == 0 || nalLength + 4L > remaining)
                break;

            output.Write(startCode);
            offset += 4;
            remaining -= 4;

            long nalRemaining = nalLength;
            while (nalRemaining > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                int request = (int)Math.Min(buffer.Length, nalRemaining);
                int read = reader.Read(offset, buffer, request);
                if (read <= 0)
                    throw new EndOfStreamException("Video NAL akışı beklenenden önce sona erdi.");

                output.Write(buffer, 0, read);
                offset += read;
                remaining -= read;
                nalRemaining -= read;
            }
        }

        if (output.Length == 0)
            throw new InvalidDataException("Video NAL akışı dönüştürülemedi.");
        if (requireCompleteInput && remaining != 0)
            throw new IOException("Video NAL akışı eksik dönüştürüldü.");

        return true;
    }

    private static void WriteResident(
        RecoveryFileItem item,
        FileStream output,
        IncrementalHash? outputHash = null)
    {
        if (item.ResidentData is null)
            throw new InvalidDataException("NTFS yerleşik veri bulunamadı.");

        int length = (int)Math.Min(item.ResidentData.Length, Math.Max(0L, item.SizeBytes));
        if (outputHash is null)
            output.Write(item.ResidentData, 0, length);
        else
            WriteHashed(output, outputHash, item.ResidentData.AsSpan(0, length));
    }

    private static long CopyNtfsRuns(
        RawDeviceReader reader,
        RecoveryFileItem item,
        FileStream output,
        IncrementalHash outputHash,
        OperationPauseGate? pauseGate,
        CancellationToken cancellationToken,
        Action<long>? advanceProgress)
    {
        if (item.DataRuns is null || item.DataRuns.Count == 0 || item.ClusterSize <= 0)
            throw new InvalidDataException("NTFS küme zinciri bulunamadı.");

        long remaining = item.SizeBytes;
        long unreadableBytes = 0;
        byte[] buffer = new byte[BufferSize];

        foreach (DataRun run in item.DataRuns)
        {
            pauseGate?.Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0) break;

            long runBytes = checked(run.ClusterCount * (long)item.ClusterSize);
            long bytesToWrite = Math.Min(runBytes, remaining);

            if (run.IsSparse)
            {
                Array.Clear(buffer);
                long sparseRemaining = bytesToWrite;
                while (sparseRemaining > 0)
                {
                    pauseGate?.Wait(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, sparseRemaining);
                    WriteHashed(output, outputHash, buffer.AsSpan(0, count));
                    advanceProgress?.Invoke(count);
                    sparseRemaining -= count;
                }
            }
            else
            {
                long sourceOffset = checked(run.LogicalClusterNumber * (long)item.ClusterSize);
                unreadableBytes += CopyContiguous(reader, sourceOffset, bytesToWrite, output, outputHash, cancellationToken, advanceProgress);
            }

            remaining -= bytesToWrite;
        }

        if (remaining > 0)
            throw new IOException("NTFS küme zinciri dosyanın tamamını karşılamadı.");

        return unreadableBytes;
    }

    private static long CopyContiguous(
        RawDeviceReader reader,
        long sourceOffset,
        long length,
        FileStream output,
        IncrementalHash outputHash,
        CancellationToken cancellationToken,
        Action<long>? advanceProgress)
    {
        if (sourceOffset < 0 || length <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceOffset));

        byte[] buffer = new byte[BufferSize];
        long remaining = length;
        long offset = sourceOffset;
        long unreadableBytes = 0;

        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int request = (int)Math.Min(buffer.Length, remaining);
            int read = reader.ReadBestEffort(offset, buffer.AsSpan(0, request), out long blockUnreadable);
            if (read <= 0)
                throw new EndOfStreamException("Kaynak aygıt beklenenden önce sona erdi.");

            WriteHashed(output, outputHash, buffer.AsSpan(0, read));
            advanceProgress?.Invoke(read);
            unreadableBytes += blockUnreadable;
            offset += read;
            remaining -= read;
        }

        return unreadableBytes;
    }


    private static long CopyExtents(
        RawDeviceReader reader,
        RecoveryFileItem item,
        FileStream output,
        IncrementalHash outputHash,
        CancellationToken cancellationToken,
        Action<long>? advanceProgress)
    {
        if (item.SourceExtents is null || item.SourceExtents.Count == 0)
            throw new InvalidDataException("Kurtarma parça zinciri bulunamadı.");

        long remaining = item.SizeBytes;
        long unreadableBytes = 0;
        foreach (SourceExtent extent in item.SourceExtents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0) break;

            long length = Math.Min(extent.Length, remaining);
            if (extent.Offset < 0 || length <= 0)
                continue;

            unreadableBytes += CopyContiguous(reader, extent.Offset, length, output, outputHash, cancellationToken, advanceProgress);
            remaining -= length;
        }

        if (remaining > 0)
            throw new IOException("Parçalı kaynak dosyanın tamamını karşılamadı.");

        return unreadableBytes;
    }

    private static long CopyLengthPrefixedNalToAnnexB(
        RawDeviceReader reader,
        RecoveryFileItem item,
        FileStream output,
        IncrementalHash outputHash,
        CancellationToken cancellationToken,
        Action<long>? advanceProgress)
    {
        long remaining = item.SizeBytes;
        long offset = item.SourceOffset;
        Span<byte> lengthBytes = stackalloc byte[4];
        byte[] buffer = new byte[BufferSize];
        ReadOnlySpan<byte> startCode = [0x00, 0x00, 0x00, 0x01];
        long unreadableBytes = 0;

        while (remaining >= 5)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.ReadExact(offset, lengthBytes))
                break;

            uint nalLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
            if (nalLength == 0 || nalLength + 4L > remaining)
                break;

            WriteHashed(output, outputHash, startCode);
            offset += 4;
            remaining -= 4;
            advanceProgress?.Invoke(4);

            long nalRemaining = nalLength;
            while (nalRemaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int request = (int)Math.Min(buffer.Length, nalRemaining);
                int read = reader.ReadBestEffort(
                    offset,
                    buffer.AsSpan(0, request),
                    out long blockUnreadable);
                if (read <= 0)
                    throw new EndOfStreamException("Video NAL akışı beklenenden önce sona erdi.");

                WriteHashed(output, outputHash, buffer.AsSpan(0, read));
                advanceProgress?.Invoke(read);
                unreadableBytes += blockUnreadable;
                offset += read;
                remaining -= read;
                nalRemaining -= read;
            }
        }

        if (output.Length == 0)
            throw new InvalidDataException("Video NAL akışı dönüştürülemedi.");
        if (remaining != 0)
            throw new IOException("Video NAL akışı eksik dönüştürüldü.");

        return unreadableBytes;
    }

    private static void EnsureDestinationCapacity(string destinationDirectory, long requiredBytes, bool includeReserve)
    {
        if (requiredBytes <= 0)
            return;

        long? available = TryGetAvailableFreeSpace(destinationDirectory);
        if (!available.HasValue)
            return;

        long requiredWithReserve = CalculateRequiredCapacity(requiredBytes, includeReserve);

        if (available.Value < requiredWithReserve)
        {
            throw new IOException(
                $"Kurtarma hedefinde yeterli boş alan yok. Gerekli: {RecoveryFileItem.FormatBytes(requiredWithReserve)} • Kullanılabilir: {RecoveryFileItem.FormatBytes(available.Value)}.");
        }
    }

    private static long CalculateRequiredCapacity(long requiredBytes, bool includeReserve)
    {
        if (requiredBytes <= 0)
            return 0L;

        long reserve = includeReserve
            ? Math.Min(MaximumFreeSpaceReserveBytes, Math.Max(MinimumFreeSpaceReserveBytes, requiredBytes / 100L))
            : MinimumFreeSpaceReserveBytes;
        return requiredBytes > long.MaxValue - reserve
            ? long.MaxValue
            : requiredBytes + reserve;
    }

    private static long? TryGetAvailableFreeSpace(string destinationDirectory)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(destinationDirectory));
            if (string.IsNullOrWhiteSpace(root))
                return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? Math.Max(0L, drive.AvailableFreeSpace) : null;
        }
        catch
        {
            // UNC/özel provider hedeflerinde kapasite sorgusu desteklenmeyebilir; yazma hatası doğal olarak yakalanır.
            return null;
        }
    }

    private static string CreateRecoveryWorkingPath(string directory, string fileName)
    {
        string extension = Path.GetExtension(fileName) ?? string.Empty;
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string candidate = Path.Combine(directory, $".nsxpartial_{Guid.NewGuid():N}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException("Güvenli geçici kurtarma dosyası oluşturulamadı.");
    }

    internal static string CommitRawCandidateBeforeAutoRepair(string workingPath, string preferredFinalPath)
    {
        return CommitRecoveredOutput(workingPath, preferredFinalPath);
    }

    private static string CommitRecoveredOutput(string workingPath, string preferredFinalPath)
    {
        if (string.IsNullOrWhiteSpace(workingPath) || !File.Exists(workingPath))
            throw new IOException("Doğrulanmış geçici kurtarma çıktısı bulunamadı.");

        string directory = Path.GetDirectoryName(preferredFinalPath)
            ?? throw new IOException("Kurtarma hedef klasörü çözümlenemedi.");
        string fileName = Path.GetFileName(preferredFinalPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
        string extension = Path.GetExtension(fileName) ?? string.Empty;

        for (int attempt = 0; attempt < 100000; attempt++)
        {
            string candidate = attempt == 0
                ? preferredFinalPath
                : Path.Combine(directory, $"{baseName}_{attempt + 1}{extension}");
            try
            {
                File.Move(workingPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate))
            {
                // Aynı ada eşzamanlı başka çıktı geldiyse sıradaki güvenli adı dene.
            }
        }

        string fallback = Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}{extension}");
        File.Move(workingPath, fallback);
        return fallback;
    }

    private static void TryDeleteFailedOutput(string destinationPath)
    {
        try
        {
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);
        }
        catch
        {
            // Asıl kurtarma/iptal hatasını gölgelememek için temizleme hatası yutulur.
        }
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (!File.Exists(path)) return path;

        string baseName = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
        string extension = Path.GetExtension(fileName) ?? string.Empty;

        for (int i = 2; i < 100000; i++)
        {
            path = Path.Combine(directory, $"{baseName}_{i}{extension}");
            if (!File.Exists(path)) return path;
        }

        return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}{extension}");
    }
}
