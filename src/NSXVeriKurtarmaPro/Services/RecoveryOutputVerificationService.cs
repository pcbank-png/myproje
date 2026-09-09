using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public enum RecoveryOutputVerificationLevel
{
    Verified,
    Partial,
    NeedsReview,
    Failed
}

public sealed record RecoveryOutputVerificationResult(
    RecoveryOutputVerificationLevel Level,
    int Score,
    string Grade,
    string Summary,
    long OutputBytes,
    long SourceUnreadableBytes,
    bool HeaderValidationSupported,
    bool HeaderValidated,
    bool LengthValidated,
    bool ReadbackValidated,
    bool FormatValidationAttempted,
    bool StructureValidated,
    bool DecodeValidated);

/// <summary>
/// Kurtarma tamamlandıktan sonra hedef diskteki gerçek çıktı dosyasını yeniden açar ve
/// formatına göre ikinci bir doğrulama zincirinden geçirir. Bu katman kaynak aygıta yazmaz;
/// yalnız hedef çıktıyı okur. Kurtarma sırasında kaynak akıştan alınan SHA-256 ile tam dosya
/// read-back değeri karşılaştırılır. Başlık/format doğrulayıcısı bulunmayan türler yalnız boyut
/// ve okunabilirlik kanıtıyla doğrulanmış sayılmaz; Kısmi Kanıt/Doğrulanamadı olarak ayrılır.
/// </summary>
public static class RecoveryOutputVerificationService
{
    private const int HeaderInspectBytes = 64 * 1024;
    private const int TailInspectBytes = 2 * 1024 * 1024;
    private const int ReadbackBufferBytes = 1024 * 1024;
    private const long MaxFullZipInflateBytes = 256L * 1024 * 1024;

    private sealed record FormatProbe(
        bool Attempted,
        bool Success,
        bool StructureValidated,
        bool DecodeValidated,
        int Score,
        string Summary);

    public static RecoveryOutputVerificationResult Verify(
        RecoveryFileItem item,
        string outputPath,
        string extension,
        long sourceUnreadableBytes,
        long? expectedOutputBytes,
        byte[] expectedContentSha256,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(expectedContentSha256);
        if (expectedContentSha256.Length != 32)
            throw new ArgumentException("SHA-256 karşılaştırma değeri tam olarak 32 bayt olmalıdır.", nameof(expectedContentSha256));

        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath))
            return Failed("Hedef dosya son doğrulama için bulunamadı.", 0, sourceUnreadableBytes);

        cancellationToken.ThrowIfCancellationRequested();

        string ext = FileTypeHelper.Normalize(extension);
        FileInfo info;
        try
        {
            info = new FileInfo(outputPath);
            if (info.Length <= 0)
                return Failed("Hedef dosya boş oluştu.", 0, sourceUnreadableBytes);
        }
        catch (Exception ex)
        {
            return Failed($"Hedef dosya bilgisi okunamadı: {ex.Message}", 0, sourceUnreadableBytes);
        }

        long length = info.Length;
        progress?.Invoke("Son doğrulama • hedef dosya yeniden açılıyor...");

        byte[] header;
        try
        {
            header = ReadWindow(outputPath, 0, (int)Math.Min(HeaderInspectBytes, length), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed($"Hedef dosya yeniden okunamadı: {ex.Message}", length, sourceUnreadableBytes);
        }

        FileHeaderValidationOutcome headerOutcome = header.Length > 0
            ? FileHeaderValidator.Validate(ext, header, length)
            : FileHeaderValidationOutcome.Mismatch;
        bool headerValidationSupported = headerOutcome != FileHeaderValidationOutcome.NotSupported;
        bool headerValidated = headerOutcome == FileHeaderValidationOutcome.Match;
        if (headerOutcome == FileHeaderValidationOutcome.Mismatch && sourceUnreadableBytes <= 0)
        {
            return Failed(
                "Çıktı dosyasının başlangıç yapısı son okumada doğrulanamadı.",
                length,
                sourceUnreadableBytes,
                headerValidationSupported: true);
        }

        int score = headerValidated ? 25 : 0;
        var evidence = new List<string>(10)
        {
            headerOutcome switch
            {
                FileHeaderValidationOutcome.Match => "başlık imzası doğrulandı",
                FileHeaderValidationOutcome.Mismatch => "başlık, okunamayan kaynak alanı nedeniyle doğrulanamadı",
                _ => string.IsNullOrWhiteSpace(ext)
                    ? "uzantı bilinmiyor; başlık doğrulayıcısı yok"
                    : $"{ext} için başlık doğrulayıcısı yok"
            }
        };

        bool lengthValidated = false;
        if (expectedOutputBytes.HasValue && expectedOutputBytes.Value > 0)
        {
            lengthValidated = length == expectedOutputBytes.Value;
            if (lengthValidated)
            {
                score += 15;
                evidence.Add("çıktı boyutu tam");
            }
            else
            {
                evidence.Add($"boyut farkı {RecoveryFileItem.FormatBytes(Math.Abs(length - expectedOutputBytes.Value))}");
                if (sourceUnreadableBytes <= 0)
                {
                    return Failed(
                        $"Çıktı boyutu beklenen veri zinciriyle eşleşmedi ({RecoveryFileItem.FormatBytes(length)} / {RecoveryFileItem.FormatBytes(expectedOutputBytes.Value)}).",
                        length,
                        sourceUnreadableBytes,
                        headerValidationSupported,
                        headerValidated);
                }
            }
        }
        else
        {
            // Repair/container dönüşümü yapılmış çıktıda birebir kaynak boyutu kanıt sayılmaz.
            evidence.Add("dönüşümlü çıktı; kaynak boyutu karşılaştırması uygulanmadı");
        }

        progress?.Invoke("Son doğrulama • tam dosya SHA-256 read-back karşılaştırılıyor...");
        bool readbackValidated;
        try
        {
            readbackValidated = ValidateFullReadback(
                outputPath,
                length,
                expectedContentSha256,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(
                $"Hedef disk SHA-256 read-back doğrulaması başarısız: {ex.Message}",
                length,
                sourceUnreadableBytes,
                headerValidationSupported,
                headerValidated,
                lengthValidated);
        }

        if (!readbackValidated)
        {
            return Failed(
                "Hedefte yeniden okunan içerik, kurtarma sırasında kaynak veriden hesaplanan SHA-256 ile eşleşmedi.",
                length,
                sourceUnreadableBytes,
                headerValidationSupported,
                headerValidated,
                lengthValidated);
        }

        score += 20;
        evidence.Add("tam dosya SHA-256 read-back eşleşti");

        FormatProbe probe = VerifyFormat(item, outputPath, ext, length, cancellationToken, progress);
        bool structureValidated = probe.Success && probe.StructureValidated;
        bool decodeValidated = probe.DecodeValidated;
        bool strongFormatEvidence = probe.Success && (structureValidated || decodeValidated);

        if (probe.Attempted)
        {
            evidence.Add(probe.Summary);
            if (probe.Success)
                score += probe.Score;
            else
                score -= Math.Max(12, probe.Score);
        }
        else
        {
            evidence.Add(string.IsNullOrWhiteSpace(ext)
                ? "içerik/format doğrulayıcısı yok"
                : $"{ext} için içerik/format doğrulayıcısı yok");
        }

        score = Math.Clamp(score, 0, 100);

        if (!probe.Success && probe.Attempted)
        {
            int reviewScore = Math.Min(score, sourceUnreadableBytes > 0 ? 54 : 64);
            string reviewSummary = string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase));
            return new RecoveryOutputVerificationResult(
                RecoveryOutputVerificationLevel.NeedsReview,
                reviewScore,
                "İncelenmeli",
                reviewSummary,
                length,
                Math.Max(0, sourceUnreadableBytes),
                headerValidationSupported,
                headerValidated,
                lengthValidated,
                readbackValidated,
                probe.Attempted,
                structureValidated,
                decodeValidated);
        }

        if (sourceUnreadableBytes > 0)
        {
            evidence.Add($"kaynakta okunamayan {RecoveryFileItem.FormatBytes(sourceUnreadableBytes)}");
            if (!strongFormatEvidence)
            {
                score = Math.Min(score, 59);
                return new RecoveryOutputVerificationResult(
                    RecoveryOutputVerificationLevel.NeedsReview,
                    score,
                    headerValidated ? "Kısmi Kanıt" : "Doğrulanamadı",
                    string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase)),
                    length,
                    sourceUnreadableBytes,
                    headerValidationSupported,
                    headerValidated,
                    lengthValidated,
                    readbackValidated,
                    probe.Attempted,
                    structureValidated,
                    decodeValidated);
            }

            score = Math.Min(score, 79);
            return new RecoveryOutputVerificationResult(
                RecoveryOutputVerificationLevel.Partial,
                score,
                "Kısmi",
                string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase)),
                length,
                sourceUnreadableBytes,
                headerValidationSupported,
                headerValidated,
                lengthValidated,
                readbackValidated,
                probe.Attempted,
                structureValidated,
                decodeValidated);
        }

        if (!strongFormatEvidence)
        {
            score = Math.Min(score, 69);
            return new RecoveryOutputVerificationResult(
                RecoveryOutputVerificationLevel.NeedsReview,
                score,
                headerValidated ? "Kısmi Kanıt" : "Doğrulanamadı",
                string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase)),
                length,
                0,
                headerValidationSupported,
                headerValidated,
                lengthValidated,
                readbackValidated,
                probe.Attempted,
                structureValidated,
                decodeValidated);
        }

        score = Math.Max(score, 85);
        string grade = score >= 95 ? "Mükemmel" : score >= 88 ? "Çok İyi" : "İyi";
        return new RecoveryOutputVerificationResult(
            RecoveryOutputVerificationLevel.Verified,
            score,
            grade,
            string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase)),
            length,
            0,
            headerValidationSupported,
            headerValidated,
            lengthValidated,
            readbackValidated,
            probe.Attempted,
            structureValidated,
            decodeValidated);
    }

    private static FormatProbe VerifyFormat(
        RecoveryFileItem item,
        string path,
        string extension,
        long length,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        string category = FileTypeHelper.GetCategory(extension);
        if (string.Equals(category, "Fotoğraf", StringComparison.OrdinalIgnoreCase) &&
            GlobalImageCodec.CanDecode(extension))
        {
            progress?.Invoke("Son doğrulama • görüntü gerçek codec ile açılıyor...");
            try
            {
                GlobalImageCodec.ValidateFile(path, extension);
                return new FormatProbe(true, true, true, true, 40, "görüntü decode doğrulandı");
            }
            catch (Exception ex)
            {
                return new FormatProbe(true, false, false, false, 30, $"görüntü decode başarısız: {TrimMessage(ex.Message)}");
            }
        }

        if (string.Equals(category, "Video", StringComparison.OrdinalIgnoreCase))
            return VerifyVideo(item, path, extension, progress);

        return extension switch
        {
            "ZIP" or "DOCX" or "XLSX" or "PPTX" => VerifyZip(path, extension, cancellationToken, progress),
            "PDF" => VerifyPdf(path, length, cancellationToken),
            "SQLITE" or "DB" or "DB3" => VerifySqliteDatabase(path, length, cancellationToken),
            "WAL" => VerifySqliteWal(path, length, cancellationToken),
            "JOURNAL" => VerifySqliteJournal(path, length, cancellationToken),
            "RAR" => VerifyRar(path, length, cancellationToken),
            _ => new FormatProbe(false, false, false, false, 0, string.Empty)
        };
    }

    private static FormatProbe VerifyVideo(
        RecoveryFileItem item,
        string path,
        string extension,
        Action<string>? progress)
    {
        progress?.Invoke("Son doğrulama • video gerçek decoder ile örnekleniyor...");
        VideoDecodeHealthResult decode = VideoDecodeValidationService.Validate(path, progress);
        if (!decode.Success)
            return new FormatProbe(true, false, false, false, 28, $"video decoder doğrulaması başarısız: {TrimMessage(decode.Summary)}");

        AudioVideoSyncForensicResult audio = AudioVideoSyncForensicService.Analyze(
            path,
            extension,
            decode,
            progress);
        ForensicVideoHealthResult health = ForensicVideoHealthService.Evaluate(
            item,
            path,
            decode,
            audio,
            progress);

        bool success = health.Success || (decode.Success && health.Score >= 60);
        int contribution = Math.Clamp(18 + (health.Score * 22 / 100), 18, 40);
        string summary = $"video decode %{decode.Score} • forensic sağlık %{health.Score} ({health.Grade})";
        if (audio.AudioTrackPresent)
            summary += $" • A/V %{audio.Score}";
        return new FormatProbe(true, success, success, true, success ? contribution : 28, summary);
    }

    private static FormatProbe VerifyZip(
        string path,
        string extension,
        CancellationToken cancellationToken,
        Action<string>? progress)
    {
        progress?.Invoke("Son doğrulama • ZIP/Office merkezi dizini ve içerikler kontrol ediliyor...");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0)
                return new FormatProbe(true, false, false, false, 28, "ZIP merkezi dizini boş");

            bool contentTypes = false;
            bool officeRoot = extension == "ZIP";
            long totalInflated = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = entry.FullName.Replace('\\', '/');
                if (string.Equals(name, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                    contentTypes = true;
                if (extension == "DOCX" && string.Equals(name, "word/document.xml", StringComparison.OrdinalIgnoreCase))
                    officeRoot = true;
                else if (extension == "XLSX" && string.Equals(name, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    officeRoot = true;
                else if (extension == "PPTX" && string.Equals(name, "ppt/presentation.xml", StringComparison.OrdinalIgnoreCase))
                    officeRoot = true;

                if (entry.Length > 0 && totalInflated <= MaxFullZipInflateBytes)
                {
                    long remaining = MaxFullZipInflateBytes - totalInflated;
                    totalInflated = entry.Length > remaining
                        ? MaxFullZipInflateBytes + 1
                        : totalInflated + entry.Length;
                }
            }

            bool fullInflate = totalInflated <= MaxFullZipInflateBytes;
            byte[] buffer = new byte[128 * 1024];
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using Stream input = entry.Open();
                long remainingBudget = fullInflate ? long.MaxValue : 128 * 1024;
                while (remainingBudget > 0)
                {
                    int request = (int)Math.Min(buffer.Length, remainingBudget);
                    int read = input.Read(buffer, 0, request);
                    if (read <= 0)
                        break;
                    remainingBudget -= read;
                }
            }

            bool officeValid = extension == "ZIP" || (contentTypes && officeRoot);
            if (!officeValid)
                return new FormatProbe(true, false, false, false, 28, $"{extension} Office kök yapısı doğrulanamadı");

            return new FormatProbe(
                true,
                true,
                true,
                false,
                fullInflate ? 38 : 34,
                fullInflate
                    ? $"ZIP merkezi dizin + {archive.Entries.Count:N0} giriş okunabilir"
                    : $"ZIP merkezi dizin + {archive.Entries.Count:N0} giriş örneklemeli okunabilir");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FormatProbe(true, false, false, false, 30, $"ZIP/Office doğrulaması başarısız: {TrimMessage(ex.Message)}");
        }
    }

    private static FormatProbe VerifyPdf(string path, long length, CancellationToken cancellationToken)
    {
        byte[] tail = ReadWindow(path, Math.Max(0, length - TailInspectBytes), (int)Math.Min(TailInspectBytes, length), cancellationToken);
        bool eof = tail.AsSpan().IndexOf("%%EOF"u8) >= 0;
        bool startXref = tail.AsSpan().IndexOf("startxref"u8) >= 0;
        bool success = eof && startXref;
        return new FormatProbe(
            true,
            success,
            success,
            false,
            success ? 35 : 24,
            success ? "PDF EOF + startxref doğrulandı" : "PDF sonlandırma/xref yapısı eksik");
    }

    private static FormatProbe VerifySqliteDatabase(string path, long length, CancellationToken cancellationToken)
    {
        byte[] header = ReadWindow(path, 0, (int)Math.Min(4096, length), cancellationToken);
        if (header.Length < 100 || !SqliteRecoveryService.LooksLikeDatabaseHeader(header, length))
            return new FormatProbe(true, false, false, false, 28, "SQLite header/page geometry doğrulanamadı");

        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16, 2));
        if (pageSize == 1)
            pageSize = 65536;
        bool aligned = pageSize > 0 && length >= pageSize && length % pageSize == 0;
        bool pageOne = header.Length > 100 && header[100] is 0x02 or 0x05 or 0x0A or 0x0D;
        bool success = aligned && pageOne;
        return new FormatProbe(
            true,
            success,
            success,
            false,
            success ? 36 : 26,
            success ? $"SQLite page geometry doğrulandı • {pageSize:N0} B sayfa" : "SQLite page-1 veya dosya hizası zayıf");
    }

    private static FormatProbe VerifySqliteWal(string path, long length, CancellationToken cancellationToken)
    {
        byte[] header = ReadWindow(path, 0, (int)Math.Min(64, length), cancellationToken);
        if (header.Length < 32 || !SqliteRecoveryService.LooksLikeWalHeader(header))
            return new FormatProbe(true, false, false, false, 26, "SQLite WAL header doğrulanamadı");
        int pageSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
        if (pageSize == 1) pageSize = 65536;
        long frameSize = 24L + pageSize;
        bool geometry = pageSize is >= 512 and <= 65536 && length >= 32 + frameSize && (length - 32) % frameSize == 0;
        return new FormatProbe(true, geometry, geometry, false, geometry ? 34 : 24,
            geometry ? "SQLite WAL frame geometry doğrulandı" : "SQLite WAL frame sınırı kısmi");
    }

    private static FormatProbe VerifySqliteJournal(string path, long length, CancellationToken cancellationToken)
    {
        byte[] header = ReadWindow(path, 0, (int)Math.Min(64, length), cancellationToken);
        bool valid = header.Length >= 28 && SqliteRecoveryService.LooksLikeJournalHeader(header);
        return new FormatProbe(true, valid, false, false, valid ? 22 : 24,
            valid ? "SQLite rollback journal header doğrulandı" : "SQLite rollback journal yapısı doğrulanamadı");
    }

    private static FormatProbe VerifyRar(string path, long length, CancellationToken cancellationToken)
    {
        byte[] header = ReadWindow(path, 0, (int)Math.Min(64, length), cancellationToken);
        bool valid = FileHeaderValidator.LooksLike("RAR", header, length);
        return new FormatProbe(true, valid, false, false, valid ? 18 : 20,
            valid ? "RAR container başlığı doğrulandı" : "RAR container başlığı geçersiz");
    }

    private static bool ValidateFullReadback(
        string path,
        long length,
        ReadOnlySpan<byte> expectedContentSha256,
        CancellationToken cancellationToken)
    {
        if (length <= 0 || expectedContentSha256.Length != 32)
            return false;

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ReadbackBufferBytes,
            FileOptions.SequentialScan);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[ReadbackBufferBytes];
        long total = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            hash.AppendData(buffer, 0, read);
            total = checked(total + read);
            if (total > length)
                return false;
        }

        if (total != length)
            return false;

        byte[] actualContentSha256 = hash.GetHashAndReset();
        return CryptographicOperations.FixedTimeEquals(actualContentSha256, expectedContentSha256);
    }

    private static byte[] ReadWindow(string path, long offset, int count, CancellationToken cancellationToken)
    {
        if (count <= 0)
            return [];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Math.Min(count, 1024 * 1024), FileOptions.RandomAccess);
        stream.Position = Math.Max(0, offset);
        byte[] buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, total, count - total);
            if (read <= 0)
                break;
            total += read;
        }

        if (total == buffer.Length)
            return buffer;
        if (total <= 0)
            return [];
        Array.Resize(ref buffer, total);
        return buffer;
    }

    private static RecoveryOutputVerificationResult Failed(
        string message,
        long outputBytes,
        long sourceUnreadableBytes,
        bool headerValidationSupported = false,
        bool headerValidated = false,
        bool lengthValidated = false) =>
        new(
            RecoveryOutputVerificationLevel.Failed,
            0,
            "Başarısız",
            message,
            Math.Max(0, outputBytes),
            Math.Max(0, sourceUnreadableBytes),
            headerValidationSupported,
            headerValidated,
            lengthValidated,
            false,
            false,
            false,
            false);

    private static string TrimMessage(string message)
    {
        string value = string.IsNullOrWhiteSpace(message) ? "bilinmeyen hata" : message.Trim();
        return value.Length <= 220 ? value : value[..220] + "…";
    }
}
