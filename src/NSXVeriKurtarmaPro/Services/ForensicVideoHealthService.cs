using System.Buffers.Binary;
using System.IO;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal sealed record ForensicVideoHealthResult(
    bool Success,
    int Score,
    int ContainerScore,
    int CodecScore,
    int KeyframeScore,
    int TimestampScore,
    int FragmentScore,
    int ReadabilityScore,
    string Grade,
    string Summary,
    int AudioSyncScore = 100,
    bool AudioTrackPresent = false);

internal static class ForensicVideoHealthService
{
    private const int ContainerWeight = 20;
    private const int CodecWeight = 25;
    private const int KeyframeWeight = 15;
    private const int TimestampWeight = 15;
    private const int FragmentWeight = 15;
    private const int ReadabilityWeight = 10;
    private const int SampleWindowBytes = 1024 * 1024;

    private static readonly byte[] AsfHeaderGuid =
    {
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] AsfDataGuid =
    {
        0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] EbmlHeader = { 0x1A, 0x45, 0xDF, 0xA3 };
    private static readonly byte[] EbmlSegment = { 0x18, 0x53, 0x80, 0x67 };
    private static readonly byte[] EbmlCluster = { 0x1F, 0x43, 0xB6, 0x75 };
    private static readonly byte[] MxfKlvPrefix = { 0x06, 0x0E, 0x2B, 0x34 };
    private static readonly byte[] MpegSequence = { 0x00, 0x00, 0x01, 0xB3 };
    private static readonly byte[] MpegPicture = { 0x00, 0x00, 0x01, 0x00 };
    private static readonly byte[] MpegPack = { 0x00, 0x00, 0x01, 0xBA };

    public static ForensicVideoHealthResult Evaluate(
        RecoveryFileItem item,
        string videoPath,
        VideoDecodeHealthResult decode,
        AudioVideoSyncForensicResult audioSync,
        Action<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Failed("Forensic video sağlık analizi için çıktı dosyası bulunamadı.");

        try
        {
            progress?.Invoke("Forensic sağlık analizi • kapsayıcı ve codec bütünlüğü ölçülüyor...");

            string extension = FileTypeHelper.Normalize(item.Extension);
            using var stream = new FileStream(
                videoPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.RandomAccess);

            long length = stream.Length;
            if (length <= 0)
                return Failed("Video çıktısı boş olduğu için sağlık puanı üretilemedi.");

            byte[] first = ReadWindow(stream, 0, Math.Min(SampleWindowBytes, length));
            List<byte[]> windows = ReadDistributedWindows(stream, length);
            CodecEvidence codecEvidence = AnalyzeCodecEvidence(windows, extension);

            int containerScore = ScoreContainer(stream, extension, first, windows);
            int codecScore = ScoreCodec(decode, codecEvidence);
            int keyframeScore = ScoreKeyframes(decode, codecEvidence);
            int timestampScore = ScoreTimeline(decode);
            int fragmentScore = ScoreFragmentIntegrity(item);
            int readabilityScore = ScoreReadability(item);

            int videoTotal = Math.Clamp(
                containerScore + codecScore + keyframeScore + timestampScore + fragmentScore + readabilityScore,
                0,
                100);
            int total = audioSync.AudioTrackPresent
                ? Math.Clamp((int)Math.Round(videoTotal * 0.78d + audioSync.Score * 0.22d, MidpointRounding.AwayFromZero), 0, 100)
                : videoTotal;

            // A visually perfect video cannot remain at 100 when its audio track cannot be decoded
            // or has severe sync/buffer damage. Keep video recovery usable, but cap forensic health.
            if (audioSync.AudioTrackPresent && !audioSync.AudioDecodeVerified)
                total = Math.Min(total, 79);
            if (audioSync.AudioTrackPresent && audioSync.AudioLossRatio > 0.20d)
                total = Math.Min(total, 74);
            if (audioSync.SyncMeasured && Math.Abs(audioSync.DriftMilliseconds) > 750d)
                total = Math.Min(total, 69);

            bool criticalDecode = decode.Success;
            bool criticalStructure = containerScore >= 10;
            bool criticalCodec = codecScore >= 14;
            bool success = criticalDecode && criticalStructure && criticalCodec && videoTotal >= 60;
            string grade = Grade(total);

            string summary =
                $"Video sağlık %{total} ({grade}) • video çekirdek %{videoTotal}, kapsayıcı {containerScore}/{ContainerWeight}, " +
                $"codec {codecScore}/{CodecWeight}, keyframe {keyframeScore}/{KeyframeWeight}, " +
                $"zaman {timestampScore}/{TimestampWeight}, fragment {fragmentScore}/{FragmentWeight}, " +
                $"okunabilirlik {readabilityScore}/{ReadabilityWeight}. {audioSync.Summary}";

            if (!success)
            {
                string reason = !criticalDecode
                    ? " Gerçek decoder doğrulaması başarısız."
                    : !criticalStructure
                        ? " Kapsayıcı bütünlüğü güvenli eşik altında."
                        : !criticalCodec
                            ? " Video codec kanıtı güvenli eşik altında."
                            : " Toplam sağlık puanı güvenli eşik altında.";
                summary += reason;
            }

            return new ForensicVideoHealthResult(
                success,
                total,
                containerScore,
                codecScore,
                keyframeScore,
                timestampScore,
                fragmentScore,
                readabilityScore,
                grade,
                summary,
                audioSync.Score,
                audioSync.AudioTrackPresent);
        }
        catch (Exception ex)
        {
            return Failed($"Forensic video sağlık analizi tamamlanamadı: {ex.Message}");
        }
    }

    private static int ScoreContainer(
        FileStream stream,
        string extension,
        byte[] first,
        IReadOnlyList<byte[]> windows)
    {
        bool headerValid = first.Length > 0 && FileHeaderValidator.LooksLike(extension, first, stream.Length);
        int baseScore = headerValid ? 8 : 0;

        int structural = extension switch
        {
            "MP4" or "M4V" or "MOV" or "QT" or "3GP" or "3G2" or "F4V" => ScoreIsoBmff(stream),
            "MTS" or "M2TS" or "TS" or "M2T" or "TP" or "TRP" or "TOD" => ScoreTransportStream(first),
            "AVI" or "DIVX" or "XVID" => ScoreAvi(first),
            "MKV" or "WEBM" => ScoreMatroska(windows),
            "WMV" or "ASF" or "DVR-MS" => ScoreAsf(windows),
            "FLV" => ScoreFlv(stream),
            "MXF" => ScoreMxf(windows),
            "MPG" or "MPEG" or "MPE" or "MPV" or "M1V" or "M2V" or "VOB" or "EVO" or "MOD" => ScoreMpeg(windows),
            "H264" or "AVC" or "H265" or "HEVC" => ScoreElementaryVideo(windows),
            "WTV" => ScoreWtv(first),
            "DV" => ScoreDv(first),
            "OGV" => ScoreOggVideo(windows),
            "RM" or "RMVB" => ScoreRealMedia(windows),
            "NSV" => ScoreNsv(windows),
            "ROQ" => ScoreRoq(first, windows),
            "BIK" or "BINK" or "BK2" or "BIK2" => ScoreBink(first),
            "SMK" => ScoreSmacker(first),
            _ => headerValid ? 8 : 0
        };

        return Math.Clamp(baseScore + structural, 0, ContainerWeight);
    }

    private static int ScoreIsoBmff(FileStream stream)
    {
        bool hasFtyp = false;
        bool hasMoov = false;
        bool hasMdat = false;
        bool malformed = false;
        long position = 0;
        int atomCount = 0;
        byte[] header = new byte[16];

        while (position + 8 <= stream.Length && atomCount < 100000)
        {
            stream.Position = position;
            int read = ReadAtMost(stream, header, 0, 16);
            if (read < 8)
                break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            long size;
            int headerSize = 8;

            if (size32 == 1)
            {
                if (read < 16)
                {
                    malformed = true;
                    break;
                }
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (size64 > long.MaxValue)
                {
                    malformed = true;
                    break;
                }
                size = (long)size64;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = stream.Length - position;
            }
            else
            {
                size = size32;
            }

            if (size < headerSize || position + size > stream.Length)
            {
                malformed = true;
                break;
            }

            hasFtyp |= type == "ftyp";
            hasMoov |= type == "moov";
            hasMdat |= type == "mdat";
            position += size;
            atomCount++;

            if (size32 == 0)
                break;
        }

        int score = 0;
        if (hasFtyp) score += 3;
        if (hasMoov) score += 4;
        if (hasMdat) score += 4;
        if (!malformed && atomCount >= 2) score += 1;
        return Math.Min(12, score);
    }

    private static int ScoreTransportStream(byte[] first)
    {
        double ratio188 = TransportSyncRatio(first, 188, 0);
        double ratio192 = TransportSyncRatio(first, 192, 4);
        double ratio = Math.Max(ratio188, ratio192);

        if (ratio >= 0.985d) return 12;
        if (ratio >= 0.95d) return 10;
        if (ratio >= 0.85d) return 7;
        if (ratio >= 0.60d) return 4;
        return 0;
    }

    private static int ScoreAvi(byte[] first)
    {
        if (first.Length < 12 || !AsciiAt(first, 0, "RIFF") || !AsciiAt(first, 8, "AVI "))
            return 0;

        int score = 5;
        if (ContainsAscii(first, "hdrl")) score += 3;
        if (ContainsAscii(first, "movi")) score += 4;
        return Math.Min(12, score);
    }

    private static int ScoreMatroska(IReadOnlyList<byte[]> windows)
    {
        bool ebml = WindowsContain(windows, EbmlHeader);
        bool segment = WindowsContain(windows, EbmlSegment);
        int clusters = CountAcrossWindows(windows, EbmlCluster, 4);

        int score = 0;
        if (ebml) score += 4;
        if (segment) score += 3;
        if (clusters >= 2) score += 5;
        else if (clusters == 1) score += 3;
        return Math.Min(12, score);
    }

    private static int ScoreAsf(IReadOnlyList<byte[]> windows)
    {
        bool header = WindowsContain(windows, AsfHeaderGuid);
        bool data = WindowsContain(windows, AsfDataGuid);
        return (header ? 6 : 0) + (data ? 6 : 0);
    }

    private static int ScoreFlv(FileStream stream)
    {
        if (stream.Length < 13)
            return 0;

        byte[] header = new byte[13];
        stream.Position = 0;
        if (ReadAtMost(stream, header, 0, header.Length) < header.Length ||
            header[0] != (byte)'F' || header[1] != (byte)'L' || header[2] != (byte)'V')
            return 0;

        long dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        if (dataOffset < 9 || dataOffset + 4 > stream.Length)
            return 3;

        long position = dataOffset + 4;
        int validTags = 0;
        int invalidTags = 0;
        byte[] tagHeader = new byte[11];
        byte[] previousSize = new byte[4];

        while (position + 15 <= stream.Length && validTags < 128 && invalidTags < 8)
        {
            stream.Position = position;
            if (ReadAtMost(stream, tagHeader, 0, tagHeader.Length) < tagHeader.Length)
                break;

            int type = tagHeader[0];
            int dataSize = (tagHeader[1] << 16) | (tagHeader[2] << 8) | tagHeader[3];
            long next = position + 11L + dataSize;
            if ((type != 8 && type != 9 && type != 18) || dataSize < 0 || next + 4 > stream.Length)
            {
                invalidTags++;
                position++;
                continue;
            }

            stream.Position = next;
            if (ReadAtMost(stream, previousSize, 0, 4) < 4)
                break;

            uint declared = BinaryPrimitives.ReadUInt32BigEndian(previousSize);
            if (declared == 11u + (uint)dataSize)
                validTags++;
            else
                invalidTags++;

            position = next + 4;
        }

        if (validTags >= 12 && invalidTags <= 1) return 12;
        if (validTags >= 5) return 9;
        if (validTags >= 2) return 6;
        return 3;
    }

    private static int ScoreMxf(IReadOnlyList<byte[]> windows)
    {
        int klvCount = CountAcrossWindows(windows, MxfKlvPrefix, 64);
        if (klvCount >= 8) return 12;
        if (klvCount >= 4) return 10;
        if (klvCount >= 2) return 7;
        if (klvCount == 1) return 4;
        return 0;
    }

    private static int ScoreMpeg(IReadOnlyList<byte[]> windows)
    {
        int pack = CountAcrossWindows(windows, MpegPack, 8);
        int sequence = CountAcrossWindows(windows, MpegSequence, 8);
        int picture = CountAcrossWindows(windows, MpegPicture, 32);
        if (sequence > 0 && picture >= 3) return 12;
        if (pack > 0 && picture >= 2) return 10;
        if (sequence > 0 || pack > 0) return 6;
        return 0;
    }

    private static int ScoreElementaryVideo(IReadOnlyList<byte[]> windows)
    {
        CodecEvidence evidence = AnalyzeCodecEvidence(windows, string.Empty);
        if (evidence.ParameterSetCount >= 2 && evidence.NalCount >= 8) return 12;
        if (evidence.NalCount >= 5) return 9;
        if (evidence.NalCount >= 2) return 5;
        return 0;
    }

    private static int ScoreWtv(byte[] first)
    {
        if (first.Length < 0x5C || !FileHeaderValidator.LooksLike("WTV", first, first.Length))
            return 0;
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x30, 4));
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x38, 4));
        uint units = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(0x58, 4));
        if (rootSize is > 0 and <= 0x1000 && rootSector > 0 && units > 0)
            return 12;
        return 6;
    }

    private static int ScoreDv(byte[] first)
    {
        if (first.Length >= 6 * 80 && GlobalVideoRawAnalyzer.LooksLikeDvStart(first.AsSpan(0, 6 * 80)))
            return 12;
        return 0;
    }

    private static int ScoreOggVideo(IReadOnlyList<byte[]> windows)
    {
        int pages = CountAcrossWindows(windows, "OggS"u8.ToArray(), 16);
        bool theora = WindowsContain(windows, Encoding.ASCII.GetBytes("theora"));
        if (theora && pages >= 4) return 12;
        if (theora && pages >= 2) return 10;
        return pages >= 2 ? 5 : 0;
    }

    private static int ScoreRealMedia(IReadOnlyList<byte[]> windows)
    {
        bool rmf = WindowsContain(windows, ".RMF"u8.ToArray());
        bool mdpr = WindowsContain(windows, "MDPR"u8.ToArray());
        bool data = WindowsContain(windows, "DATA"u8.ToArray());
        if (rmf && mdpr && data) return 12;
        if (rmf && data) return 8;
        return 0;
    }

    private static int ScoreNsv(IReadOnlyList<byte[]> windows)
    {
        bool fileHeader = WindowsContain(windows, "NSVf"u8.ToArray());
        int syncHeaders = CountAcrossWindows(windows, "NSVs"u8.ToArray(), 16);
        if (fileHeader && syncHeaders >= 2) return 12;
        if (syncHeaders >= 2) return 9;
        return 0;
    }

    private static int ScoreRoq(byte[] first, IReadOnlyList<byte[]> windows)
    {
        bool header = first.Length >= 8 && first[0] == 0x84 && first[1] == 0x10 &&
                      first[2] == 0xFF && first[3] == 0xFF && first[4] == 0xFF && first[5] == 0xFF;
        int info = CountAcrossWindows(windows, new byte[] { 0x01, 0x10 }, 4);
        int video = CountAcrossWindows(windows, new byte[] { 0x11, 0x10 }, 16) +
                    CountAcrossWindows(windows, new byte[] { 0x02, 0x10 }, 16);
        if (header && info > 0 && video > 0) return 12;
        if (header && video > 0) return 8;
        return 0;
    }

    private static int ScoreBink(byte[] first)
    {
        if (first.Length < 44)
            return 0;
        bool signature = (first[0] == (byte)'B' && first[1] == (byte)'I' && first[2] == (byte)'K') ||
                         (first[0] == (byte)'K' && first[1] == (byte)'B' && first[2] == (byte)'2');
        if (!signature)
            return 0;
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8, 4));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(20, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(24, 4));
        uint fpsNum = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(28, 4));
        uint fpsDen = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(32, 4));
        return frames > 0 && width is > 0 and <= 7680 && height is > 0 and <= 4800 && fpsNum > 0 && fpsDen > 0 ? 12 : 5;
    }

    private static int ScoreSmacker(byte[] first)
    {
        if (first.Length < 104 ||
            !(first.AsSpan(0, 4).SequenceEqual("SMK2"u8) || first.AsSpan(0, 4).SequenceEqual("SMK4"u8)))
            return 0;
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8, 4));
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(12, 4));
        return width > 0 && height > 0 && frames > 0 ? 12 : 5;
    }

    private static int ScoreCodec(VideoDecodeHealthResult decode, CodecEvidence evidence)
    {
        int score = 0;
        if (decode.Success) score += 15;
        else if (decode.PassedSamples > 0) score += 7;

        if (evidence.CodecDetected) score += 5;
        if (evidence.ParameterSetCount >= 2 || evidence.ContainerCodecMarker) score += 5;

        if (decode.Success && score < 20)
            score = 20;

        return Math.Clamp(score, 0, CodecWeight);
    }

    private static int ScoreKeyframes(VideoDecodeHealthResult decode, CodecEvidence evidence)
    {
        if (evidence.KeyframeCount >= 2)
            return KeyframeWeight;
        if (evidence.KeyframeCount == 1)
            return 13;
        if (decode.TimelineValidated && decode.PassedSamples >= 4)
            return 11;
        if (decode.Success)
            return 8;
        return 0;
    }

    private static int ScoreTimeline(VideoDecodeHealthResult decode)
    {
        if (!decode.Success)
            return 0;

        if (!decode.TimelineValidated || decode.TotalSamples <= 1)
            return 7;

        double ratio = Math.Clamp(decode.PassedSamples / (double)decode.TotalSamples, 0d, 1d);
        int score = (int)Math.Round(TimestampWeight * ratio, MidpointRounding.AwayFromZero);
        return Math.Clamp(score, 5, TimestampWeight);
    }

    private static int ScoreFragmentIntegrity(RecoveryFileItem item)
    {
        if (item.SourceKind == RecoverySourceKind.Extents)
        {
            if (item.SourceExtents is not { Count: > 0 } extents)
                return 4;

            long total = 0;
            int valid = 0;
            foreach (SourceExtent extent in extents)
            {
                if (extent.Offset < 0 || extent.Length <= 0)
                    continue;
                valid++;
                try
                {
                    total = checked(total + extent.Length);
                }
                catch (OverflowException)
                {
                    total = long.MaxValue;
                    break;
                }
            }

            if (valid != extents.Count)
                return 6;
            if (item.SizeBytes <= 0)
                return 10;

            double coverage = Math.Clamp(total / (double)item.SizeBytes, 0d, 1d);
            if (coverage >= 0.999d) return 15;
            if (coverage >= 0.98d) return 13;
            if (coverage >= 0.90d) return 10;
            return Math.Max(3, (int)Math.Round(coverage * 10d));
        }

        if (item.SourceKind == RecoverySourceKind.NtfsRunList)
        {
            if (item.DataRuns is not { Count: > 0 } runs)
                return 5;

            long totalClusters = 0;
            long sparseClusters = 0;
            foreach (DataRun run in runs)
            {
                if (run.ClusterCount <= 0)
                    continue;
                totalClusters += run.ClusterCount;
                if (run.IsSparse)
                    sparseClusters += run.ClusterCount;
            }

            if (totalClusters <= 0)
                return 6;
            double sparseRatio = sparseClusters / (double)totalClusters;
            if (sparseRatio <= 0d) return 15;
            if (sparseRatio <= 0.001d) return 13;
            if (sparseRatio <= 0.01d) return 10;
            if (sparseRatio <= 0.05d) return 7;
            return 4;
        }

        if (item.SourceKind == RecoverySourceKind.NtfsResident)
            return 15;

        if (item.RecoveryState.Contains("Parçalı", StringComparison.OrdinalIgnoreCase) ||
            item.RecoveryState.Contains("Yeniden", StringComparison.OrdinalIgnoreCase))
            return 11;

        return 15;
    }

    private static int ScoreReadability(RecoveryFileItem item)
    {
        long unreadable = item.SourceUnreadableBytes;
        if (unreadable < 0)
            return 7;
        if (unreadable == 0)
            return ReadabilityWeight;
        if (item.SizeBytes <= 0)
            return 4;

        double ratio = Math.Clamp(unreadable / (double)item.SizeBytes, 0d, 1d);
        if (ratio <= 0.0001d) return 9;
        if (ratio <= 0.001d) return 8;
        if (ratio <= 0.01d) return 6;
        if (ratio <= 0.05d) return 3;
        return 1;
    }

    private static CodecEvidence AnalyzeCodecEvidence(IReadOnlyList<byte[]> windows, string extension)
    {
        int nalCount = 0;
        int parameterSets = 0;
        int keyframes = 0;
        bool codecDetected = false;
        bool containerCodecMarker = false;

        foreach (byte[] data in windows)
        {
            AnalyzeAnnexB(data, ref nalCount, ref parameterSets, ref keyframes, ref codecDetected);

            int mpegPictures = CountPattern(data, MpegPicture, 64);
            if (mpegPictures > 0)
            {
                codecDetected = true;
                keyframes += CountMpegIFrames(data, 32);
                nalCount += mpegPictures;
            }

            if (ContainsAscii(data, "avc1") || ContainsAscii(data, "avc3") ||
                ContainsAscii(data, "hvc1") || ContainsAscii(data, "hev1") ||
                ContainsAscii(data, "vp09") || ContainsAscii(data, "av01") ||
                ContainsAscii(data, "V_MPEG4/ISO/AVC") || ContainsAscii(data, "V_MPEGH/ISO/HEVC") ||
                ContainsAscii(data, "V_VP9") || ContainsAscii(data, "V_AV1") || ContainsAscii(data, "theora"))
            {
                codecDetected = true;
                containerCodecMarker = true;
            }

            keyframes += CountFlvKeyframes(data, 16);
        }

        if (extension is "H264" or "AVC" or "H265" or "HEVC")
            codecDetected |= nalCount > 0;

        return new CodecEvidence(codecDetected, containerCodecMarker, nalCount, parameterSets, keyframes);
    }

    private static void AnalyzeAnnexB(
        byte[] data,
        ref int nalCount,
        ref int parameterSets,
        ref int keyframes,
        ref bool codecDetected)
    {
        int position = 0;
        while (position + 5 < data.Length)
        {
            int start = FindStartCode(data, position, out int prefixLength);
            if (start < 0 || start + prefixLength >= data.Length)
                break;

            int headerIndex = start + prefixLength;
            byte header = data[headerIndex];
            int h264Type = header & 0x1F;
            int h265Type = (header >> 1) & 0x3F;

            bool h264 = h264Type is >= 1 and <= 12;
            bool h265 = headerIndex + 1 < data.Length &&
                        (data[headerIndex + 1] & 0x07) != 0 &&
                        h265Type is >= 0 and <= 40;

            if (h264)
            {
                codecDetected = true;
                nalCount++;
                if (h264Type is 7 or 8) parameterSets++;
                if (h264Type == 5) keyframes++;
            }
            else if (h265)
            {
                codecDetected = true;
                nalCount++;
                if (h265Type is 32 or 33 or 34) parameterSets++;
                if (h265Type is >= 16 and <= 21) keyframes++;
            }

            position = headerIndex + 1;
        }
    }

    private static int CountMpegIFrames(byte[] data, int limit)
    {
        int count = 0;
        int position = 0;
        while (count < limit)
        {
            int found = IndexOf(data, MpegPicture, position);
            if (found < 0 || found + 6 > data.Length)
                break;

            int pictureCodingType = (data[found + 5] >> 3) & 0x07;
            if (pictureCodingType == 1)
                count++;
            position = found + 4;
        }
        return count;
    }

    private static int CountFlvKeyframes(byte[] data, int limit)
    {
        int count = 0;
        for (int i = 0; i + 12 < data.Length && count < limit; i++)
        {
            if (data[i] != 9)
                continue;

            int dataSize = (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
            if (dataSize <= 0 || i + 11 >= data.Length)
                continue;

            int frameType = (data[i + 11] >> 4) & 0x0F;
            if (frameType == 1)
                count++;
        }
        return count;
    }

    private static List<byte[]> ReadDistributedWindows(FileStream stream, long length)
    {
        var windows = new List<byte[]>(5);
        long[] offsets =
        {
            0,
            Math.Max(0, length / 4 - SampleWindowBytes / 2),
            Math.Max(0, length / 2 - SampleWindowBytes / 2),
            Math.Max(0, length * 3 / 4 - SampleWindowBytes / 2),
            Math.Max(0, length - SampleWindowBytes)
        };

        long lastOffset = -1;
        foreach (long offset in offsets)
        {
            if (offset == lastOffset)
                continue;
            windows.Add(ReadWindow(stream, offset, Math.Min(SampleWindowBytes, length - offset)));
            lastOffset = offset;
        }
        return windows;
    }

    private static byte[] ReadWindow(FileStream stream, long offset, long requested)
    {
        if (requested <= 0 || offset < 0 || offset >= stream.Length)
            return Array.Empty<byte>();

        int count = (int)Math.Min(int.MaxValue, Math.Min(requested, stream.Length - offset));
        byte[] buffer = new byte[count];
        stream.Position = offset;
        int read = ReadAtMost(stream, buffer, 0, count);
        if (read == count)
            return buffer;
        if (read <= 0)
            return Array.Empty<byte>();
        Array.Resize(ref buffer, read);
        return buffer;
    }

    private static int ReadAtMost(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private static double TransportSyncRatio(byte[] data, int packetSize, int syncOffset)
    {
        if (data.Length < syncOffset + packetSize * 4 + 1)
            return 0d;

        int packets = Math.Min(512, (data.Length - syncOffset) / packetSize);
        if (packets <= 0)
            return 0d;

        int matches = 0;
        for (int i = 0; i < packets; i++)
        {
            int index = syncOffset + i * packetSize;
            if (index < data.Length && data[index] == 0x47)
                matches++;
        }
        return matches / (double)packets;
    }

    private static bool WindowsContain(IReadOnlyList<byte[]> windows, byte[] pattern)
    {
        foreach (byte[] window in windows)
        {
            if (IndexOf(window, pattern, 0) >= 0)
                return true;
        }
        return false;
    }

    private static int CountAcrossWindows(IReadOnlyList<byte[]> windows, byte[] pattern, int limit)
    {
        int count = 0;
        foreach (byte[] window in windows)
        {
            count += CountPattern(window, pattern, limit - count);
            if (count >= limit)
                return count;
        }
        return count;
    }

    private static int CountPattern(byte[] data, byte[] pattern, int limit)
    {
        if (pattern.Length == 0 || data.Length < pattern.Length || limit <= 0)
            return 0;

        int count = 0;
        int position = 0;
        while (count < limit)
        {
            int found = IndexOf(data, pattern, position);
            if (found < 0)
                break;
            count++;
            position = found + pattern.Length;
        }
        return count;
    }

    private static int IndexOf(byte[] data, byte[] pattern, int start)
    {
        if (pattern.Length == 0)
            return Math.Clamp(start, 0, data.Length);

        int last = data.Length - pattern.Length;
        for (int i = Math.Max(0, start); i <= last; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static int FindStartCode(byte[] data, int start, out int prefixLength)
    {
        for (int i = Math.Max(0, start); i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                continue;
            if (data[i + 2] == 1)
            {
                prefixLength = 3;
                return i;
            }
            if (i + 4 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
            {
                prefixLength = 4;
                return i;
            }
        }
        prefixLength = 0;
        return -1;
    }

    private static bool ContainsAscii(byte[] data, string text)
    {
        if (string.IsNullOrEmpty(text))
            return true;
        byte[] pattern = System.Text.Encoding.ASCII.GetBytes(text);
        return IndexOf(data, pattern, 0) >= 0;
    }

    private static bool AsciiAt(byte[] data, int offset, string text)
    {
        if (offset < 0 || offset + text.Length > data.Length)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (data[offset + i] != (byte)text[i])
                return false;
        }
        return true;
    }

    private static string Grade(int score) => score switch
    {
        >= 90 => "Mükemmel",
        >= 80 => "Çok İyi",
        >= 70 => "İyi",
        >= 60 => "Kurtarılabilir",
        >= 45 => "Riskli",
        _ => "Zayıf"
    };

    private static ForensicVideoHealthResult Failed(string message) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, "Zayıf", message);

    private sealed record CodecEvidence(
        bool CodecDetected,
        bool ContainerCodecMarker,
        int NalCount,
        int ParameterSetCount,
        int KeyframeCount);
}
