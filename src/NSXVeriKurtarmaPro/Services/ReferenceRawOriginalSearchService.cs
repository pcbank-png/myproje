using System.Buffers;
using System.Buffers.Binary;
using System.Windows.Media;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Referans fotografin buyuk/orijinal kopyasini ana Quick/Deep Scan motorundan bagimsiz
/// olarak ham medyada arar. Motor yalniz JPEG SOI adaylarini sequential I/O ile tarar;
/// header geometri filtresinden gecmeyen dosyalarda pahali decode/reconstruction calistirmaz.
/// Ayni fiziksel HDD uzerinde ana taramayla eszamanli calistirilmasi ViewModel tarafinda
/// bilincli olarak engellenir; bu sayede normal tarama hizi ve seek paterni degismez.
/// </summary>
public sealed class ReferenceRawOriginalSearchService
{
    private const int MinimumJpegBytes = 8 * 1024;
    private const int HeaderProbeBytes = 256 * 1024;
    private const int PartialProbeBytes = 16 * 1024 * 1024;
    private const int MaxDecodeBytes = 96 * 1024 * 1024;
    private const int MinimumVerifiedSimilarity = 78;
    private const int MinimumPartialSimilarity = 72;
    private const double MinimumAspectSimilarity = 0.88d;
    private const long ProgressByteInterval = 256L * 1024 * 1024;
    private const int ProgressTimeIntervalMs = 500;

    public IReadOnlyList<ReferenceRawSearchMatch> Search(
        StorageDeviceInfo device,
        ReferenceImageSignature referenceSignature,
        ReferenceRawSearchOptions options,
        IProgress<ReferenceRawSearchProgress>? progress,
        Action<ReferenceRawSearchMatch>? matchFound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(referenceSignature);
        ArgumentNullException.ThrowIfNull(options);

        RecoveryMediaProfile profile = RecoveryMediaProfileService.Create(device);
        using RawDeviceReader reader = RawDeviceReader.OpenDevice(device, mediaProfile: profile);

        long total = Math.Max(0L, reader.VolumeLength);
        if (total <= 0)
            return Array.Empty<ReferenceRawSearchMatch>();

        int blockSize = Math.Clamp(profile.ScanBlockSize, 4 * 1024 * 1024, 32 * 1024 * 1024);
        byte[] rented = ArrayPool<byte>.Shared.Rent(blockSize + 2);
        var matches = new List<ReferenceRawSearchMatch>();
        var seenStarts = new HashSet<long>(options.ExistingRawOffsets ?? Array.Empty<long>());

        int headerCandidates = 0;
        int decodedCandidates = 0;
        long processed = 0;
        int carry = 0;
        long lastProgressBytes = 0;
        long lastProgressTick = Environment.TickCount64;

        try
        {
            while (processed < total)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int request = (int)Math.Min(blockSize, total - processed);
                Span<byte> target = rented.AsSpan(carry, request);
                int read = reader.ReadBestEffort(processed, target, out _);
                if (read <= 0)
                    break;

                int available = carry + read;
                long bufferBaseOffset = processed - carry;
                ReadOnlySpan<byte> span = rented.AsSpan(0, available);
                long advanceTo = -1;

                for (int index = 0; index + 2 < span.Length; index++)
                {
                    if (span[index] != 0xFF || span[index + 1] != 0xD8 || span[index + 2] != 0xFF)
                        continue;

                    long candidateStart = bufferBaseOffset + index;
                    if (candidateStart < 0 || candidateStart >= total || !seenStarts.Add(candidateStart))
                        continue;

                    if (!TryReadHeader(reader, candidateStart, total, out JpegHeaderProbe header))
                        continue;

                    headerCandidates++;
                    if (!PassesGeometryFilters(header.Width, header.Height, referenceSignature, options))
                        continue;

                    cancellationToken.ThrowIfCancellationRequested();

                    RawFileAnalysis? analysis = RawFileAnalyzer.Analyze(
                        reader,
                        SignatureKind.Jpeg,
                        candidateStart,
                        total,
                        cancellationToken);

                    bool verified = analysis is { Length: >= MinimumJpegBytes } &&
                                    analysis.Length <= total - candidateStart;
                    long verifiedEnd = verified ? candidateStart + analysis!.Length : -1;

                    ReferenceRawSearchMatch? match = verified
                        ? TryBuildVerifiedMatch(
                            reader,
                            candidateStart,
                            total,
                            header,
                            analysis!,
                            referenceSignature,
                            options,
                            cancellationToken)
                        : TryBuildPartialMatch(
                            reader,
                            candidateStart,
                            total,
                            header,
                            referenceSignature,
                            options,
                            cancellationToken);

                    if (match is not null)
                    {
                        decodedCandidates++;
                        matches.Add(match);
                        matchFound?.Invoke(match);
                        progress?.Report(new ReferenceRawSearchProgress(
                            Math.Min(total, processed + read),
                            total,
                            headerCandidates,
                            decodedCandidates,
                            matches.Count,
                            $"Referans RAW araması • %{match.SimilarityScore} eşleşme • {match.Item.ResolutionText}"));
                    }

                    // Dogrulanmis JPEG'in kendi entropy/EXIF alani icindeki ikinci SOI'leri
                    // tekrar aday sayma. Dosya mevcut bellek blogunu asiyorsa bir sonraki
                    // sequential okuma dogrudan dosya sonundan devam eder; HDD seek thrash azalir.
                    if (verifiedEnd > candidateStart)
                    {
                        long bufferEnd = bufferBaseOffset + available;
                        if (verifiedEnd >= bufferEnd)
                        {
                            advanceTo = verifiedEnd;
                            break;
                        }

                        long nextIndex = verifiedEnd - bufferBaseOffset;
                        if (nextIndex > index && nextIndex < span.Length)
                            index = (int)nextIndex - 1;
                    }
                }

                long naturalNext = processed + read;
                if (advanceTo > naturalNext)
                {
                    processed = Math.Min(total, advanceTo);
                    carry = 0;
                }
                else
                {
                    carry = Math.Min(2, available);
                    if (carry > 0)
                        span.Slice(available - carry, carry).CopyTo(rented.AsSpan(0, carry));
                    processed = naturalNext;
                }
                long now = Environment.TickCount64;
                if (processed >= total ||
                    processed - lastProgressBytes >= ProgressByteInterval ||
                    now - lastProgressTick >= ProgressTimeIntervalMs)
                {
                    progress?.Report(new ReferenceRawSearchProgress(
                        processed,
                        total,
                        headerCandidates,
                        decodedCandidates,
                        matches.Count,
                        matches.Count == 0
                            ? "RAW JPEG başlangıçları hedefli olarak taranıyor…"
                            : $"{matches.Count:N0} büyük/orijinal aday bulundu • RAW tarama devam ediyor"));
                    lastProgressBytes = processed;
                    lastProgressTick = now;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return matches
            .OrderByDescending(m => m.SimilarityScore)
            .ThenByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Item.SizeBytes)
            .ToList();
    }

    private static ReferenceRawSearchMatch? TryBuildVerifiedMatch(
        RawDeviceReader reader,
        long start,
        long total,
        JpegHeaderProbe header,
        RawFileAnalysis analysis,
        ReferenceImageSignature referenceSignature,
        ReferenceRawSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (analysis.Length <= 0 || analysis.Length > total - start || analysis.Length > MaxDecodeBytes)
            return null;

        if (!TryReadCandidateBytes(reader, start, analysis.Length, analysis.AppendData, cancellationToken, out byte[] bytes))
            return null;

        ImageSource? preview;
        try
        {
            preview = GlobalImageCodec.CreateThumbnail(bytes, 420);
        }
        catch
        {
            return null;
        }

        if (preview is null)
            return null;

        (uint displayWidth, uint displayHeight) = NormalizeDisplayDimensions(header.Width, header.Height, preview);
        int score = ReferenceImageSearchService.CalculateSimilarity(referenceSignature, preview);
        if (score < MinimumVerifiedSimilarity)
            return null;

        RecoveryConfidenceResult confidence = RecoveryConfidenceService.EvaluateRawCandidate(
            reader,
            start,
            analysis.Length,
            "JPG",
            analysis.RecoveryState,
            hasSyntheticSuffix: analysis.AppendData is { Length: > 0 });

        if (confidence.RejectAsFalsePositive)
            return null;

        bool larger = IsLargerThanReference(header.Width, header.Height, options);
        string label = BuildMatchLabel(score, larger, analysis.RecoveryState, partial: false);
        RecoveryFileItem item = CreateResultItem(
            start,
            analysis.Length,
            analysis.RecoveryState,
            analysis.AppendData,
            preview,
            displayWidth,
            displayHeight,
            score,
            label,
            confidence);

        return new ReferenceRawSearchMatch(item, preview, displayWidth, displayHeight, score, label, IsPartial: false);
    }

    private static ReferenceRawSearchMatch? TryBuildPartialMatch(
        RawDeviceReader reader,
        long start,
        long total,
        JpegHeaderProbe header,
        ReferenceImageSignature referenceSignature,
        ReferenceRawSearchOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.IncludeDamaged)
            return null;

        int request = (int)Math.Min(PartialProbeBytes, total - start);
        if (request < MinimumJpegBytes)
            return null;

        byte[] probe = new byte[request];
        int read = reader.ReadBestEffort(start, probe, out long unreadableBytes);
        if (read < MinimumJpegBytes || cancellationToken.IsCancellationRequested)
            return null;

        int boundary = FindJpegEndOrNextStart(probe.AsSpan(0, read), Math.Max(header.EntropyOffset, MinimumJpegBytes));
        int candidateLength = boundary > MinimumJpegBytes ? boundary : read;
        if (candidateLength < MinimumJpegBytes)
            return null;

        byte[] decodeBytes = new byte[candidateLength + 2];
        Buffer.BlockCopy(probe, 0, decodeBytes, 0, candidateLength);
        bool hasEoi = candidateLength >= 2 && decodeBytes[candidateLength - 2] == 0xFF && decodeBytes[candidateLength - 1] == 0xD9;
        if (!hasEoi)
        {
            decodeBytes[candidateLength] = 0xFF;
            decodeBytes[candidateLength + 1] = 0xD9;
        }
        else
        {
            Array.Resize(ref decodeBytes, candidateLength);
        }

        ImageSource? preview;
        try
        {
            preview = GlobalImageCodec.CreateThumbnail(decodeBytes, 420);
        }
        catch
        {
            return null;
        }

        if (preview is null)
            return null;

        (uint displayWidth, uint displayHeight) = NormalizeDisplayDimensions(header.Width, header.Height, preview);
        int score = ReferenceImageSearchService.CalculateSimilarity(referenceSignature, preview);
        if (score < MinimumPartialSimilarity)
            return null;

        bool larger = IsLargerThanReference(header.Width, header.Height, options);
        string label = BuildMatchLabel(score, larger, "Parçalı JPEG Adayı", partial: true);
        var confidence = new RecoveryConfidenceResult(
            Math.Clamp(score - 8, 0, 100),
            score >= 88 ? "Yüksek" : "İncelenmeli",
            unreadableBytes > 0
                ? "Referansla görsel eşleşme var; kaynak bölgede okunamayan sektörler görüldü."
                : "Referansla görsel eşleşme var; JPEG sonlandırması/fragment zinciri eksik olabilir.",
            RejectAsFalsePositive: false);

        RecoveryFileItem item = CreateResultItem(
            start,
            candidateLength,
            "Parçalı JPEG Adayı",
            hasEoi ? null : [0xFF, 0xD9],
            preview,
            displayWidth,
            displayHeight,
            score,
            label,
            confidence);

        return new ReferenceRawSearchMatch(item, preview, displayWidth, displayHeight, score, label, IsPartial: true);
    }

    private static RecoveryFileItem CreateResultItem(
        long start,
        long length,
        string recoveryState,
        byte[]? suffix,
        ImageSource preview,
        uint width,
        uint height,
        int score,
        string label,
        RecoveryConfidenceResult confidence)
    {
        var item = new RecoveryFileItem
        {
            FileName = $"Referans_Orijinal_Aday_{start:X}.jpg",
            Extension = "JPG",
            SizeBytes = length,
            RecoveryState = recoveryState,
            TypeGlyph = FileTypeHelper.GetGlyph("JPG"),
            SourceText = "Referans RAW orijinal araması",
            SourceKind = RecoverySourceKind.RawContiguous,
            SourceOffset = start,
            SuffixData = suffix,
            RecoveryConfidenceScore = confidence.Score,
            RecoveryConfidenceGrade = confidence.Grade,
            RecoveryConfidenceSummary = confidence.Summary,
            PreviewImage = preview
        };
        item.SetResolution(width, height);
        item.SetReferenceSearchMatch(score, label);
        return item;
    }

    private static bool TryReadCandidateBytes(
        RawDeviceReader reader,
        long start,
        long length,
        byte[]? suffix,
        CancellationToken cancellationToken,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (length <= 0 || length > MaxDecodeBytes || length > int.MaxValue)
            return false;

        int payloadLength = checked((int)length);
        int suffixLength = suffix?.Length ?? 0;
        bytes = new byte[checked(payloadLength + suffixLength)];
        int read = reader.ReadBestEffort(start, bytes.AsSpan(0, payloadLength), out _);
        if (read < Math.Min(payloadLength, MinimumJpegBytes) || cancellationToken.IsCancellationRequested)
        {
            bytes = Array.Empty<byte>();
            return false;
        }

        if (read < payloadLength)
            Array.Clear(bytes, read, payloadLength - read);
        if (suffixLength > 0)
            suffix!.CopyTo(bytes, payloadLength);
        return true;
    }

    private static bool PassesGeometryFilters(
        uint width,
        uint height,
        ReferenceImageSignature referenceSignature,
        ReferenceRawSearchOptions options)
    {
        if (width == 0 || height == 0)
            return false;

        long pixels = checked((long)width * height);
        long referencePixels = checked((long)options.ReferenceWidth * options.ReferenceHeight);

        if (options.MinimumOneMegapixel && pixels < 1_000_000L)
            return false;
        if (options.OnlyLarger && referencePixels > 0 && pixels <= referencePixels)
            return false;

        double aspect = width / (double)height;
        double referenceAspect = referenceSignature.AspectRatio;
        if (referenceAspect > 0d)
        {
            double direct = Math.Min(aspect, referenceAspect) / Math.Max(aspect, referenceAspect);
            double rotatedAspect = 1d / aspect;
            double rotated = Math.Min(rotatedAspect, referenceAspect) / Math.Max(rotatedAspect, referenceAspect);
            if (Math.Max(direct, rotated) < MinimumAspectSimilarity)
                return false;
        }

        return true;
    }

    private static (uint Width, uint Height) NormalizeDisplayDimensions(uint width, uint height, ImageSource preview)
    {
        if (width == 0 || height == 0 || preview is not System.Windows.Media.Imaging.BitmapSource bitmap ||
            bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            return (width, height);

        double previewAspect = bitmap.PixelWidth / (double)bitmap.PixelHeight;
        double directAspect = width / (double)height;
        double rotatedAspect = height / (double)width;
        double directError = Math.Abs(Math.Log(Math.Max(1e-9, directAspect / previewAspect)));
        double rotatedError = Math.Abs(Math.Log(Math.Max(1e-9, rotatedAspect / previewAspect)));
        return rotatedError + 0.01d < directError ? (height, width) : (width, height);
    }

    private static bool IsLargerThanReference(uint width, uint height, ReferenceRawSearchOptions options)
    {
        long referencePixels = checked((long)options.ReferenceWidth * options.ReferenceHeight);
        return referencePixels > 0 && checked((long)width * height) > referencePixels;
    }

    private static string BuildMatchLabel(int score, bool larger, string state, bool partial)
    {
        if (partial)
            return score >= 88 ? "Parçalı Orijinal Adayı" : "Onarılabilir Büyük Aday";
        if (larger && score >= 90)
            return "Muhtemel Orijinal";
        if (larger && score >= 82)
            return "Güçlü Büyük Eşleşme";
        if (state.Contains("Yeniden", StringComparison.OrdinalIgnoreCase))
            return "Onarılabilir Aday";
        return "RAW Benzer Görsel";
    }

    private static bool TryReadHeader(
        RawDeviceReader reader,
        long start,
        long total,
        out JpegHeaderProbe header)
    {
        header = default;
        int request = (int)Math.Min(HeaderProbeBytes, total - start);
        if (request < 16)
            return false;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(request);
        try
        {
            int read = reader.ReadBestEffort(start, buffer.AsSpan(0, request), out _);
            if (read < 16)
                return false;
            return TryParseJpegHeader(buffer.AsSpan(0, read), out header);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool TryParseJpegHeader(ReadOnlySpan<byte> data, out JpegHeaderProbe header)
    {
        header = default;
        if (data.Length < 16 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF)
            return false;

        int position = 2;
        uint width = 0;
        uint height = 0;

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            while (position < data.Length && data[position] == 0xFF)
                position++;
            if (position >= data.Length)
                return false;

            byte marker = data[position++];
            if (marker == 0xD9)
                return false;
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;

            if (position + 2 > data.Length)
                return false;

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
                return false;

            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 8)
                    return false;
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position + 5, 2));
                if (width == 0 || height == 0)
                    return false;
            }

            int segmentEnd = position + segmentLength;
            if (marker == 0xDA)
            {
                if (width == 0 || height == 0)
                    return false;
                header = new JpegHeaderProbe(width, height, segmentEnd);
                return true;
            }

            position = segmentEnd;
        }

        return false;
    }

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or
        0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or
        0xCD or 0xCE or 0xCF;

    private static int FindJpegEndOrNextStart(ReadOnlySpan<byte> data, int searchStart)
    {
        int start = Math.Clamp(searchStart, 2, Math.Max(2, data.Length - 2));
        for (int i = start; i + 2 < data.Length; i++)
        {
            if (data[i] != 0xFF)
                continue;

            if (data[i + 1] == 0xD9)
                return i + 2;

            if (i >= MinimumJpegBytes && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
                return i;
        }

        return -1;
    }
}

public sealed record ReferenceRawSearchOptions(
    uint ReferenceWidth,
    uint ReferenceHeight,
    long ReferenceBytes,
    bool OnlyLarger,
    bool MinimumOneMegapixel,
    bool IncludeDamaged,
    IReadOnlyCollection<long>? ExistingRawOffsets = null);

public sealed record ReferenceRawSearchProgress(
    long ProcessedBytes,
    long TotalBytes,
    int HeaderCandidates,
    int DecodedCandidates,
    int MatchCount,
    string Detail)
{
    public double Percent => TotalBytes <= 0 ? 0d : Math.Clamp(ProcessedBytes * 100d / TotalBytes, 0d, 100d);
}

public sealed record ReferenceRawSearchMatch(
    RecoveryFileItem Item,
    ImageSource Preview,
    uint Width,
    uint Height,
    int SimilarityScore,
    string Label,
    bool IsPartial);

public readonly record struct JpegHeaderProbe(uint Width, uint Height, int EntropyOffset);
