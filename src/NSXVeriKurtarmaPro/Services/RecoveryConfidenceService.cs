using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record RecoveryConfidenceResult(
    int Score,
    string Grade,
    string Summary,
    bool RejectAsFalsePositive);

/// <summary>
/// Adds a second validation layer after signature carving. A header alone is never treated
/// as sufficient evidence: source type, parser result, size plausibility and format-specific
/// boundary evidence are combined into one deterministic recovery confidence score.
/// </summary>
public static class RecoveryConfidenceService
{
    private const int HeaderInspectBytes = 64 * 1024;
    private const int TailInspectBytes = 16 * 1024;

    public static RecoveryConfidenceResult EvaluateRawCandidate(
        RawDeviceReader reader,
        long start,
        long length,
        string extension,
        string recoveryState,
        bool hasSyntheticPrefix = false,
        bool hasSyntheticSuffix = false)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string ext = FileTypeHelper.Normalize(extension);
        if (start < 0 || length <= 0)
            return Build(0, "Gecersiz kaynak araligi", reject: true);

        int headerLength = (int)Math.Min(HeaderInspectBytes, length);
        if (headerLength <= 0)
            return Build(0, "Baslik okunamadi", reject: true);

        byte[] header = new byte[headerLength];
        int headerRead = reader.ReadBestEffort(start, header, out long headerUnreadable);
        if (headerRead <= 0)
            return Build(0, "Baslik fiziksel medyadan okunamadi", reject: true);

        bool headerValid = hasSyntheticPrefix ||
                           FileHeaderValidator.LooksLike(ext, header.AsSpan(0, headerRead), length);

        bool reconstruction = IsReconstructionState(recoveryState) || hasSyntheticPrefix;
        if (!headerValid && !reconstruction)
            return Build(12, "Imza bulundu ancak dosya baslik yapisi dogrulanamadi", reject: true);

        int score = headerValid ? 35 : 18;
        var evidence = new List<string>(5)
        {
            headerValid ? "baslik dogrulandi" : "yeniden insa basligi"
        };

        if (headerUnreadable == 0)
        {
            score += 5;
            evidence.Add("baslangic okunabilir");
        }
        else if (headerUnreadable >= headerRead / 4)
        {
            score -= 12;
            evidence.Add("baslangicta okunamayan alan");
        }

        score += GetLengthPlausibilityScore(ext, length);
        score += GetStateScore(recoveryState);

        int structureScore = EvaluateStructureEvidence(reader, start, length, ext, header.AsSpan(0, headerRead), evidence);
        score += structureScore;

        if (hasSyntheticSuffix && ext is "JPG" or "JPEG" or "JPE" or "JFIF")
        {
            score += 25;
            evidence.Add("JPEG EOI yeniden olusturuldu");
        }
        else if (hasSyntheticSuffix && ext is "ZIP" or "DOCX" or "XLSX" or "PPTX")
        {
            score += 28;
            evidence.Add("ZIP central directory yeniden olusturuldu");
        }

        score = Math.Clamp(score, 0, 100);
        int rejectThreshold = reconstruction ? 38 : 55;
        bool reject = score < rejectThreshold;

        string summary = string.Join(" • ", evidence.Distinct(StringComparer.OrdinalIgnoreCase));
        if (reject)
            summary = $"False-positive filtresi • {summary}";

        return Build(score, summary, reject);
    }

    public static void ApplyMetadataConfidence(RecoveryFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RecoveryConfidenceScore >= 0)
            return;

        int score = item.SourceKind switch
        {
            RecoverySourceKind.NtfsResident => 98,
            RecoverySourceKind.NtfsRunList => 93,
            RecoverySourceKind.ExFatContiguous => 88,
            RecoverySourceKind.FatContiguous => 86,
            RecoverySourceKind.Extents => 82,
            _ => 70
        };

        score += item.RecoveryState switch
        {
            "Çok İyi" => 2,
            "İyi" => 1,
            "Kısmi" => -10,
            "Zayıf" => -22,
            _ => 0
        };

        if (item.SizeBytes <= 0)
            score -= 35;
        if (!FileTypeHelper.IsSupported(item.Extension) && !item.IsExistingFile)
            score -= 20;
        if (item.IsExistingFile)
            score += 8;
        if (item.SourceText.Contains("Kamera sıra", StringComparison.OrdinalIgnoreCase))
            score += 3;

        string summary = item.IsExistingFile
            ? "Mevcut dosya sistemi kaydı + doğrulanmış kaynak zinciri"
            : "Dosya sistemi metadata + kaynak zinciri";
        Apply(item, Build(Math.Clamp(score, 0, 100), summary, reject: false));
    }

    public static void ApplyDerivedConfidence(RecoveryFileItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RecoveryConfidenceScore >= 0)
            return;

        int score = item.RecoveryState switch
        {
            "Çok İyi" => 90,
            "İyi" => 82,
            "Parçalı Video" => 74,
            "Yeniden İnşa" => 70,
            "Video Akışı" => 66,
            "Kısmi" => 58,
            "Video Parçası" => 48,
            "Zayıf" => 35,
            _ => item.SourceKind == RecoverySourceKind.RawContiguous ? 55 : 75
        };

        Apply(item, Build(score, "Reconstruction/metadata zinciri", reject: false));
    }

    public static void Apply(RecoveryFileItem item, RecoveryConfidenceResult result)
    {
        item.RecoveryConfidenceScore = result.Score;
        item.RecoveryConfidenceGrade = result.Grade;
        item.RecoveryConfidenceSummary = result.Summary;
    }

    private static int EvaluateStructureEvidence(
        RawDeviceReader reader,
        long start,
        long length,
        string extension,
        ReadOnlySpan<byte> header,
        List<string> evidence)
    {
        switch (extension)
        {
            case "JPG":
            case "JPEG":
            case "JPE":
            case "JFIF":
                if (TailEndsWith(reader, start, length, [0xFF, 0xD9]))
                {
                    evidence.Add("JPEG EOI dogrulandi");
                    return 20;
                }
                evidence.Add("JPEG sonlandirma zayif");
                return -12;

            case "PNG":
            case "APNG":
                if (TailContains(reader, start, length, "IEND"u8))
                {
                    evidence.Add("PNG IEND dogrulandi");
                    return 20;
                }
                evidence.Add("PNG IEND bulunamadi");
                return -12;

            case "GIF":
                if (TailEndsWith(reader, start, length, [0x3B]))
                {
                    evidence.Add("GIF trailer dogrulandi");
                    return 18;
                }
                return -8;

            case "PDF":
                if (TailContains(reader, start, length, "%%EOF"u8))
                {
                    evidence.Add("PDF EOF dogrulandi");
                    return 20;
                }
                evidence.Add("PDF EOF bulunamadi");
                return -10;

            case "ZIP":
            case "DOCX":
            case "XLSX":
            case "PPTX":
                if (TailContains(reader, start, length, [0x50, 0x4B, 0x05, 0x06]) ||
                    TailContains(reader, start, length, [0x50, 0x4B, 0x06, 0x06]))
                {
                    evidence.Add("ZIP central directory sonu dogrulandi");
                    return 20;
                }
                evidence.Add("ZIP merkezi dizin sonu zayif");
                return -10;

            case "BMP":
            case "DIB":
                if (BmpStructureValidator.TryValidate(header, length, out _))
                {
                    evidence.Add("BMP geometri dogrulandi");
                    return 18;
                }
                return -12;

            case "AVI":
            case "DIVX":
            case "XVID":
            case "WEBP":
                if (header.Length >= 12)
                {
                    uint declared = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
                    long declaredLength = (long)declared + 8;
                    if (declaredLength > 0 && declaredLength <= length + 4096)
                    {
                        evidence.Add("RIFF uzunlugu tutarli");
                        return 15;
                    }
                }
                return 2;

            case "TS":
            case "MTS":
            case "M2TS":
            case "M2T":
            case "TP":
            case "TRP":
                if (HasTransportContinuityEvidence(reader, start, length))
                {
                    evidence.Add("TS paket senkronu bas/son dogrulandi");
                    return 18;
                }
                evidence.Add("TS son bolge senkronu zayif");
                return -5;

            case "HEIC":
            case "HEIF":
            case "AVIF":
                HeifStructureResult heif = HeifRecoveryService.Analyze(reader, start, length, CancellationToken.None);
                if (heif.IsValid)
                {
                    evidence.Add(heif.Summary);
                    return heif.StructuralScore >= 88 ? 22 : 17;
                }
                evidence.Add("HEIF meta/item yapisi dogrulanamadi");
                return -18;

            case "SQLITE":
            case "DB":
            case "DB3":
                if (SqliteRecoveryService.LooksLikeDatabaseHeader(header, length))
                {
                    evidence.Add("SQLite DB header/page geometry dogrulandi");
                    return 20;
                }
                return -20;

            case "WAL":
                if (SqliteRecoveryService.LooksLikeWalHeader(header))
                {
                    evidence.Add("SQLite WAL frame geometry dogrulandi");
                    return 18;
                }
                return -18;

            case "JOURNAL":
                if (SqliteRecoveryService.LooksLikeJournalHeader(header))
                {
                    evidence.Add("SQLite rollback journal header dogrulandi");
                    return 18;
                }
                return -18;

            case "MP4":
            case "M4V":
            case "MOV":
            case "QT":
            case "3GP":
            case "3G2":
            case "F4V":
                if (header.Length >= 12 && IsIsoBoxType(header.Slice(4, 4)))
                {
                    evidence.Add("ISO-BMFF box yapisi dogrulandi");
                    return 15;
                }
                return 4;

            case "TIF":
            case "TIFF":
            case "DNG":
            case "NEF":
            case "ARW":
            case "CR2":
            case "ORF":
            case "RW2":
            case "PEF":
            case "SRW":
            case "3FR":
            case "IIQ":
            case "KDC":
            case "RWL":
                if (LooksLikePlausibleTiffOffset(header, length) || FileHeaderValidator.LooksLike(extension, header, length))
                {
                    evidence.Add("TIFF/RAW IFD ofseti tutarli");
                    return 14;
                }
                return 2;

            case "RAF":
            case "X3F":
            case "CR3":
                if (FileHeaderValidator.LooksLike(extension, header, length))
                {
                    evidence.Add("Kamera RAW container yapisi dogrulandi");
                    return 16;
                }
                return -8;

            default:
                return 5;
        }
    }

    private static bool HasTransportContinuityEvidence(RawDeviceReader reader, long start, long length)
    {
        int headLength = (int)Math.Min(4096L, length);
        byte[] head = new byte[headLength];
        int headRead = reader.ReadBestEffort(start, head, out _);
        if (headRead <= 0 || !HasTransportSyncAnywhere(head.AsSpan(0, headRead)))
            return false;

        int tailLength = (int)Math.Min(8192L, length);
        long tailStart = start + Math.Max(0, length - tailLength);
        byte[] tail = new byte[tailLength];
        int tailRead = reader.ReadBestEffort(tailStart, tail, out _);
        return tailRead > 0 && HasTransportSyncAnywhere(tail.AsSpan(0, tailRead));
    }

    private static bool HasTransportSyncAnywhere(ReadOnlySpan<byte> data)
    {
        foreach ((int packetSize, int syncOffset) in new[] { (188, 0), (192, 4) })
        {
            int limit = Math.Min(packetSize, data.Length);
            for (int start = 0; start < limit; start++)
            {
                int first = start + syncOffset;
                if (first < 0 || first >= data.Length || data[first] != 0x47)
                    continue;

                bool ok = true;
                for (int packet = 1; packet < 4; packet++)
                {
                    int index = first + packet * packetSize;
                    if (index >= data.Length || data[index] != 0x47)
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                    return true;
            }
        }

        return false;
    }

    private static bool LooksLikePlausibleTiffOffset(ReadOnlySpan<byte> header, long length)
    {
        if (header.Length < 8)
            return false;

        bool little = header[0] == (byte)'I' && header[1] == (byte)'I';
        bool big = header[0] == (byte)'M' && header[1] == (byte)'M';
        if (!little && !big)
            return false;

        uint offset = little
            ? BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        return offset >= 8 && offset < length;
    }

    private static bool IsIsoBoxType(ReadOnlySpan<byte> type) =>
        type.SequenceEqual("ftyp"u8) || type.SequenceEqual("styp"u8) ||
        type.SequenceEqual("moof"u8) || type.SequenceEqual("mdat"u8) ||
        type.SequenceEqual("moov"u8);

    private static bool TailEndsWith(RawDeviceReader reader, long start, long length, ReadOnlySpan<byte> marker)
    {
        int inspect = (int)Math.Min(TailInspectBytes, length);
        if (inspect < marker.Length)
            return false;

        byte[] tail = new byte[inspect];
        int read = reader.ReadBestEffort(start + length - inspect, tail, out long unreadable);
        if (read < marker.Length || unreadable >= inspect / 2)
            return false;

        return tail.AsSpan(0, read).EndsWith(marker);
    }

    private static bool TailContains(RawDeviceReader reader, long start, long length, ReadOnlySpan<byte> marker)
    {
        int inspect = (int)Math.Min(TailInspectBytes, length);
        if (inspect < marker.Length)
            return false;

        byte[] tail = new byte[inspect];
        int read = reader.ReadBestEffort(start + length - inspect, tail, out long unreadable);
        if (read < marker.Length || unreadable >= inspect / 2)
            return false;

        return tail.AsSpan(0, read).IndexOf(marker) >= 0;
    }

    private static int GetLengthPlausibilityScore(string extension, long length)
    {
        long min = extension switch
        {
            "JPG" or "JPEG" or "JPE" or "JFIF" => 128,
            "PNG" or "APNG" => 64,
            "GIF" => 32,
            "BMP" or "DIB" => 54,
            "PDF" => 32,
            "ZIP" or "DOCX" or "XLSX" or "PPTX" => 30,
            "SQLITE" or "DB" or "DB3" => 512,
            "WAL" => 32 + 512 + 24,
            "JOURNAL" => 512 + 512 + 8,
            "HEIC" or "HEIF" or "AVIF" => 128,
            "MP4" or "MOV" or "M4V" or "MTS" or "M2TS" or "TS" => 1024,
            _ => 16
        };

        return length >= min ? 10 : -25;
    }

    private static int GetStateScore(string state) => state switch
    {
        "Çok İyi" => 25,
        "İyi" => 22,
        "Parçalı Video" => 18,
        "Yeniden İnşa" => 16,
        "Video Akışı" => 14,
        "Kısmi" => 8,
        "Video Parçası" => 4,
        "Zayıf" => -8,
        _ => 8
    };

    private static bool IsReconstructionState(string state) => state is
        "Parçalı Video" or "Yeniden İnşa" or "Video Akışı" or "Video Parçası";

    private static RecoveryConfidenceResult Build(int score, string summary, bool reject)
    {
        score = Math.Clamp(score, 0, 100);
        string grade = score switch
        {
            >= 90 => "Cok Yuksek",
            >= 78 => "Yuksek",
            >= 65 => "Iyi",
            >= 55 => "Orta",
            >= 40 => "Dusuk",
            _ => "Zayif"
        };
        return new RecoveryConfidenceResult(score, grade, summary, reject);
    }
}
