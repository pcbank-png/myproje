using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal sealed record AudioVideoSyncForensicResult(
    bool AudioTrackPresent,
    bool AudioDecodeVerified,
    bool PacketContinuityValidated,
    bool SyncMeasured,
    bool Healthy,
    int Score,
    long DecodedAudioFrames,
    long PlayedAudioBuffers,
    long LostAudioBuffers,
    double AudioLossRatio,
    long AudioPacketCount,
    long AudioContinuityErrors,
    double StartOffsetMilliseconds,
    double EndOffsetMilliseconds,
    double DriftMilliseconds,
    double DriftPpm,
    string Summary);

internal static class AudioVideoSyncForensicService
{
    private const long PtsWrap = 1L << 33;
    private const long MaxTransportWindowBytes = 64L * 1024 * 1024;
    private const int TransportProbeBytes = 128 * 1024;
    private const int MaxAviProbeBytes = 32 * 1024 * 1024;

    public static AudioVideoSyncForensicResult Analyze(
        string videoPath,
        string extension,
        VideoDecodeHealthResult decode,
        Action<string>? progress = null,
        bool expectedAudioTrack = false)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return NoAudio(decode, "A/V forensic analizi için video dosyası bulunamadı.");

        string ext = FileTypeHelper.Normalize(extension);
        progress?.Invoke("Ses forensic doğrulaması • audio decode, packet sürekliliği ve A/V zamanlaması ölçülüyor...");

        TimelineEvidence timeline = ext switch
        {
            "MP4" or "MOV" or "QT" or "M4V" or "3GP" or "3G2" or "F4V" => AnalyzeIsoBmff(videoPath),
            "TS" or "MTS" or "M2TS" or "M2T" or "TP" or "TRP" or "TOD" => AnalyzeTransportStream(videoPath),
            "AVI" or "DIVX" or "XVID" => AnalyzeAvi(videoPath),
            _ => TimelineEvidence.Unknown(decode.AudioTrackPresent, "decoder")
        };

        bool detectedAudioTrack = decode.AudioTrackPresent || timeline.AudioTrackPresent;
        bool audioTrackPresent = detectedAudioTrack || expectedAudioTrack;
        if (!audioTrackPresent)
        {
            return new AudioVideoSyncForensicResult(
                AudioTrackPresent: false,
                AudioDecodeVerified: false,
                PacketContinuityValidated: false,
                SyncMeasured: false,
                Healthy: true,
                Score: 100,
                DecodedAudioFrames: decode.DecodedAudioFrames,
                PlayedAudioBuffers: decode.PlayedAudioBuffers,
                LostAudioBuffers: decode.LostAudioBuffers,
                AudioLossRatio: 0,
                AudioPacketCount: 0,
                AudioContinuityErrors: 0,
                StartOffsetMilliseconds: 0,
                EndOffsetMilliseconds: 0,
                DriftMilliseconds: 0,
                DriftPpm: 0,
                Summary: "Ses track'i bulunmadı; A/V sync puanı uygulanmadı.");
        }

        double lossRatio = decode.LostAudioBuffers /
                           (double)Math.Max(1, decode.PlayedAudioBuffers + decode.LostAudioBuffers);
        bool decodeVerified = decode.AudioDecodeVerified ||
                              decode.DecodedAudioFrames > 0 ||
                              decode.PlayedAudioBuffers > 0;

        int decodeScore = decodeVerified ? 45 : 0;
        int bufferScore = ScoreAudioBuffers(decodeVerified, decode.PlayedAudioBuffers, decode.LostAudioBuffers, lossRatio);
        int continuityScore = ScoreContinuity(timeline);
        int syncScore = ScoreSync(timeline);
        int score = Math.Clamp(decodeScore + bufferScore + continuityScore + syncScore, 0, 100);

        if (!decodeVerified)
            score = Math.Min(score, 45);
        if (lossRatio > 0.20d)
            score = Math.Min(score, 60);
        if (timeline.SyncMeasured && (Math.Abs(timeline.DriftMilliseconds) > 750d || Math.Abs(timeline.EndOffsetMilliseconds) > 1500d))
            score = Math.Min(score, 55);

        double driftPpm = timeline.DurationMilliseconds > 0 && timeline.SyncMeasured
            ? timeline.DriftMilliseconds / timeline.DurationMilliseconds * 1_000_000d
            : 0d;
        double continuityErrorRatio = timeline.AudioPacketCount > 0
            ? timeline.AudioContinuityErrors / (double)timeline.AudioPacketCount
            : 0d;

        bool healthy = decodeVerified &&
                       lossRatio <= 0.20d &&
                       (!timeline.PacketContinuityValidated || continuityErrorRatio <= 0.02d) &&
                       (!timeline.SyncMeasured ||
                        (Math.Abs(timeline.DriftMilliseconds) <= 750d && Math.Abs(timeline.EndOffsetMilliseconds) <= 1500d));

        string decodeText = decodeVerified
            ? $"audio decode OK ({decode.DecodedAudioFrames:N0} blok, {decode.PlayedAudioBuffers:N0} buffer)"
            : "audio decode BAŞARISIZ";
        string bufferText = $"buffer kaybı %{lossRatio * 100:0.00}";
        string continuityText = timeline.PacketContinuityValidated
            ? $"packet sürekliliği {timeline.AudioContinuityErrors:N0}/{timeline.AudioPacketCount:N0} hata"
            : "packet sürekliliği kapsayıcıdan ölçülemedi";
        string syncText = timeline.SyncMeasured
            ? $"A/V başlangıç {FormatSigned(timeline.StartOffsetMilliseconds)} ms, son {FormatSigned(timeline.EndOffsetMilliseconds)} ms, " +
              $"drift {FormatSigned(timeline.DriftMilliseconds)} ms ({FormatSigned(driftPpm)} ppm)"
            : "A/V PTS drift bu kapsayıcıda güvenilir ölçülemedi";
        string expectationText = expectedAudioTrack && !detectedAudioTrack
            ? " Referans profil audio track bekliyor ancak onarılmış dosyada track algılanamadı."
            : string.Empty;

        return new AudioVideoSyncForensicResult(
            AudioTrackPresent: true,
            AudioDecodeVerified: decodeVerified,
            PacketContinuityValidated: timeline.PacketContinuityValidated,
            SyncMeasured: timeline.SyncMeasured,
            Healthy: healthy,
            Score: score,
            DecodedAudioFrames: decode.DecodedAudioFrames,
            PlayedAudioBuffers: decode.PlayedAudioBuffers,
            LostAudioBuffers: decode.LostAudioBuffers,
            AudioLossRatio: lossRatio,
            AudioPacketCount: timeline.AudioPacketCount,
            AudioContinuityErrors: timeline.AudioContinuityErrors,
            StartOffsetMilliseconds: timeline.StartOffsetMilliseconds,
            EndOffsetMilliseconds: timeline.EndOffsetMilliseconds,
            DriftMilliseconds: timeline.DriftMilliseconds,
            DriftPpm: driftPpm,
            Summary: $"Ses/A-V %{score} • {decodeText}; {bufferText}; {continuityText}; {syncText}. Kaynak: {timeline.Source}.{expectationText}" );
    }

    private static int ScoreAudioBuffers(bool decodeVerified, long played, long lost, double lossRatio)
    {
        if (!decodeVerified)
            return 0;
        if (played + lost <= 0)
            return 12;
        if (lossRatio <= 0.001d) return 20;
        if (lossRatio <= 0.01d) return 18;
        if (lossRatio <= 0.05d) return 15;
        if (lossRatio <= 0.10d) return 11;
        if (lossRatio <= 0.20d) return 6;
        return 0;
    }

    private static int ScoreContinuity(TimelineEvidence timeline)
    {
        if (!timeline.PacketContinuityValidated)
            return 15;
        if (timeline.AudioPacketCount <= 0)
            return 3;

        double ratio = timeline.AudioContinuityErrors / (double)timeline.AudioPacketCount;
        if (ratio <= 0d) return 15;
        if (ratio <= 0.001d) return 14;
        if (ratio <= 0.005d) return 12;
        if (ratio <= 0.01d) return 9;
        if (ratio <= 0.02d) return 6;
        return 0;
    }

    private static int ScoreSync(TimelineEvidence timeline)
    {
        if (!timeline.SyncMeasured)
            return 20;

        double start = Math.Abs(timeline.StartOffsetMilliseconds);
        double end = Math.Abs(timeline.EndOffsetMilliseconds);
        double drift = Math.Abs(timeline.DriftMilliseconds);

        if (start <= 80d && end <= 120d && drift <= 40d) return 20;
        if (start <= 150d && end <= 200d && drift <= 80d) return 18;
        if (start <= 250d && end <= 350d && drift <= 150d) return 14;
        if (start <= 500d && end <= 700d && drift <= 300d) return 9;
        if (start <= 1000d && end <= 1500d && drift <= 750d) return 4;
        return 0;
    }

    private static TimelineEvidence AnalyzeIsoBmff(string path)
    {
        try
        {
            ReferenceVideoProfile profile = ReferenceVideoProfileService.Analyze(path);
            bool audioPresent = profile.AudioTimescale > 0 &&
                                (profile.AudioSampleCount > 0 || profile.AudioTimeToSampleRuns.Length > 0) &&
                                !string.Equals(profile.AudioCodec, "Yok/Bilinmiyor", StringComparison.OrdinalIgnoreCase);
            if (!audioPresent)
                return TimelineEvidence.Unknown(false, "ISO-BMFF track tabloları");

            double videoDuration = DurationMilliseconds(profile.VideoTimeToSampleRuns, profile.VideoTimescale);
            double audioDuration = DurationMilliseconds(profile.AudioTimeToSampleRuns, profile.AudioTimescale);
            long audioRunSamples = SumSampleCounts(profile.AudioTimeToSampleRuns);
            long packetCount = profile.AudioSampleCount > 0 ? profile.AudioSampleCount : audioRunSamples;
            long continuityErrors = profile.AudioSampleCount > 0 && audioRunSamples > 0
                ? Math.Abs(profile.AudioSampleCount - audioRunSamples)
                : 0;
            bool continuityValidated = packetCount > 0 && audioRunSamples > 0;

            if (videoDuration <= 0 || audioDuration <= 0)
            {
                return new TimelineEvidence(
                    true, false, continuityValidated, packetCount, continuityErrors,
                    0, 0, 0, Math.Max(videoDuration, audioDuration), "ISO-BMFF stts/stsz");
            }

            double videoStart = profile.VideoCompositionOffsetRuns.Length > 0 && profile.VideoTimescale > 0
                ? profile.VideoCompositionOffsetRuns[0].Offset * 1000d / profile.VideoTimescale
                : 0d;
            const double audioStart = 0d;
            double startOffset = audioStart - videoStart;
            double endOffset = audioDuration - (videoStart + videoDuration);
            double drift = endOffset - startOffset;

            return new TimelineEvidence(
                true, true, continuityValidated, packetCount, continuityErrors,
                startOffset, endOffset, drift, Math.Max(videoDuration, audioDuration),
                "ISO-BMFF stts/ctts/stsz");
        }
        catch
        {
            return TimelineEvidence.Unknown(false, "ISO-BMFF analiz fallback");
        }
    }

    private static TimelineEvidence AnalyzeAvi(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            int length = checked((int)Math.Min(stream.Length, MaxAviProbeBytes));
            if (length < 64)
                return TimelineEvidence.Unknown(false, "AVI strh");
            byte[] data = new byte[length];
            int read = ReadAtMost(stream, data, 0, data.Length);
            if (read < data.Length)
                Array.Resize(ref data, read);

            AviTrackTiming? video = null;
            AviTrackTiming? audio = null;
            ReadOnlySpan<byte> tag = "strh"u8;
            int position = 0;
            while (position + 64 <= data.Length)
            {
                int found = IndexOf(data, tag, position);
                if (found < 0 || found + 64 > data.Length)
                    break;
                int payload = found + 8;
                uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(found + 4, 4));
                if (chunkSize >= 56 && payload + 56 <= data.Length)
                {
                    string type = Encoding.ASCII.GetString(data, payload, 4);
                    uint scale = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 20, 4));
                    uint rate = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 24, 4));
                    uint start = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 28, 4));
                    uint samples = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(payload + 32, 4));
                    if (scale > 0 && rate > 0 && samples > 0)
                    {
                        var timing = new AviTrackTiming(
                            start * (double)scale / rate * 1000d,
                            samples * (double)scale / rate * 1000d,
                            samples);
                        if (type == "vids" && video is null) video = timing;
                        if (type == "auds" && audio is null) audio = timing;
                    }
                }
                position = found + 4;
            }

            if (audio is null)
                return TimelineEvidence.Unknown(false, "AVI strh");
            if (video is null)
                return new TimelineEvidence(true, false, false, audio.Value.SampleCount, 0, 0, 0, 0, audio.Value.DurationMilliseconds, "AVI strh");

            double startOffset = audio.Value.StartMilliseconds - video.Value.StartMilliseconds;
            double endOffset = (audio.Value.StartMilliseconds + audio.Value.DurationMilliseconds) -
                               (video.Value.StartMilliseconds + video.Value.DurationMilliseconds);
            double drift = endOffset - startOffset;
            return new TimelineEvidence(
                true, true, false, audio.Value.SampleCount, 0,
                startOffset, endOffset, drift,
                Math.Max(audio.Value.DurationMilliseconds, video.Value.DurationMilliseconds), "AVI strh");
        }
        catch
        {
            return TimelineEvidence.Unknown(false, "AVI analiz fallback");
        }
    }

    private static TimelineEvidence AnalyzeTransportStream(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
            TransportGeometry? geometry = DetectTransportGeometry(stream);
            if (geometry is null)
                return TimelineEvidence.Unknown(false, "MPEG-TS geometri");

            if (!TryFindTransportProgram(stream, geometry.Value, out int videoPid, out int audioPid))
                return TimelineEvidence.Unknown(false, "MPEG-TS PAT/PMT");
            if (audioPid < 0)
                return TimelineEvidence.Unknown(false, "MPEG-TS PMT");

            long payloadLength = Math.Max(0, stream.Length - geometry.Value.PacketStart);
            TsRangeEvidence first;
            TsRangeEvidence last;
            if (payloadLength <= MaxTransportWindowBytes * 2)
            {
                first = ScanTransportRange(stream, geometry.Value, geometry.Value.PacketStart, stream.Length, videoPid, audioPid);
                last = first;
            }
            else
            {
                long firstEnd = Math.Min(stream.Length, geometry.Value.PacketStart + MaxTransportWindowBytes);
                first = ScanTransportRange(stream, geometry.Value, geometry.Value.PacketStart, firstEnd, videoPid, audioPid);
                long lastStart = Math.Max(geometry.Value.PacketStart, stream.Length - MaxTransportWindowBytes);
                last = ScanTransportRange(stream, geometry.Value, lastStart, stream.Length, videoPid, audioPid);
            }

            long audioPackets = first.AudioPackets + (ReferenceEquals(first, last) ? 0 : last.AudioPackets);
            long continuityErrors = first.ContinuityErrors + (ReferenceEquals(first, last) ? 0 : last.ContinuityErrors);
            bool continuityValidated = audioPackets >= 16;

            long? firstVideo = first.FirstVideoPts ?? last.FirstVideoPts;
            long? firstAudio = first.FirstAudioPts ?? last.FirstAudioPts;
            long? lastVideo = last.LastVideoPts ?? first.LastVideoPts;
            long? lastAudio = last.LastAudioPts ?? first.LastAudioPts;
            if (!firstVideo.HasValue || !firstAudio.HasValue || !lastVideo.HasValue || !lastAudio.HasValue)
            {
                return new TimelineEvidence(
                    true, false, continuityValidated, audioPackets, continuityErrors,
                    0, 0, 0, 0, "MPEG-TS continuity/PES");
            }

            double startOffset = SignedPtsDifference(firstAudio.Value, firstVideo.Value) / 90d;
            double endOffset = SignedPtsDifference(lastAudio.Value, lastVideo.Value) / 90d;
            double drift = endOffset - startOffset;
            double duration = ForwardPtsDistance(lastVideo.Value, firstVideo.Value) / 90d;
            return new TimelineEvidence(
                true, true, continuityValidated, audioPackets, continuityErrors,
                startOffset, endOffset, drift, duration, "MPEG-TS PAT/PMT + PES PTS + continuity counter");
        }
        catch
        {
            return TimelineEvidence.Unknown(false, "MPEG-TS analiz fallback");
        }
    }

    private static TransportGeometry? DetectTransportGeometry(FileStream stream)
    {
        int request = checked((int)Math.Min(stream.Length, TransportProbeBytes));
        if (request < 188 * 6)
            return null;
        byte[] probe = new byte[request];
        stream.Position = 0;
        int read = ReadAtMost(stream, probe, 0, probe.Length);
        if (read < 188 * 6)
            return null;

        foreach ((int packetSize, int syncOffset) in new[] { (192, 4), (188, 0) })
        {
            int searchLimit = Math.Min(read - packetSize * 6, packetSize * 16);
            for (int syncPosition = syncOffset; syncPosition <= searchLimit + syncOffset; syncPosition++)
            {
                bool valid = true;
                for (int n = 0; n < 6; n++)
                {
                    int index = syncPosition + n * packetSize;
                    if (index >= read || probe[index] != 0x47)
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid)
                    continue;
                long packetStart = syncPosition - syncOffset;
                if (packetStart >= 0)
                    return new TransportGeometry(packetSize, syncOffset, packetStart);
            }
        }
        return null;
    }

    private static bool TryFindTransportProgram(
        FileStream stream,
        TransportGeometry geometry,
        out int videoPid,
        out int audioPid)
    {
        videoPid = -1;
        audioPid = -1;
        int pmtPid = -1;
        byte[] packet = new byte[geometry.PacketSize];
        long end = Math.Min(stream.Length, geometry.PacketStart + 32L * 1024 * 1024);
        for (long position = geometry.PacketStart; position + geometry.PacketSize <= end; position += geometry.PacketSize)
        {
            stream.Position = position;
            if (ReadAtMost(stream, packet, 0, packet.Length) != packet.Length || packet[geometry.SyncOffset] != 0x47)
                continue;
            int pid = ((packet[geometry.SyncOffset + 1] & 0x1F) << 8) | packet[geometry.SyncOffset + 2];
            bool payloadStart = (packet[geometry.SyncOffset + 1] & 0x40) != 0;
            int payloadOffset = GetTransportPayloadOffset(packet, geometry.SyncOffset, out _, out _);
            if (payloadOffset < 0 || payloadOffset >= packet.Length)
                continue;
            ReadOnlySpan<byte> payload = packet.AsSpan(payloadOffset);
            if (pid == 0 && payloadStart && TryParsePat(payload, out int foundPmt))
                pmtPid = foundPmt;
            else if (pmtPid >= 0 && pid == pmtPid && payloadStart && TryParsePmt(payload, out int foundVideo, out int foundAudio))
            {
                videoPid = foundVideo;
                audioPid = foundAudio;
                return videoPid >= 0 || audioPid >= 0;
            }
        }
        return audioPid >= 0;
    }

    private static TsRangeEvidence ScanTransportRange(
        FileStream stream,
        TransportGeometry geometry,
        long rangeStart,
        long rangeEnd,
        int videoPid,
        int audioPid)
    {
        long start = AlignTransportPosition(rangeStart, geometry.PacketStart, geometry.PacketSize);
        long end = Math.Min(stream.Length, rangeEnd);
        byte[] packet = new byte[geometry.PacketSize];
        var evidence = new TsRangeEvidence();
        int? previousAudioCc = null;

        for (long position = start; position + geometry.PacketSize <= end; position += geometry.PacketSize)
        {
            stream.Position = position;
            if (ReadAtMost(stream, packet, 0, packet.Length) != packet.Length || packet[geometry.SyncOffset] != 0x47)
                continue;

            int pid = ((packet[geometry.SyncOffset + 1] & 0x1F) << 8) | packet[geometry.SyncOffset + 2];
            bool payloadStart = (packet[geometry.SyncOffset + 1] & 0x40) != 0;
            int cc = packet[geometry.SyncOffset + 3] & 0x0F;
            int payloadOffset = GetTransportPayloadOffset(packet, geometry.SyncOffset, out bool hasPayload, out bool discontinuity);
            if (pid == audioPid && hasPayload)
            {
                evidence.AudioPackets++;
                if (discontinuity)
                {
                    previousAudioCc = null;
                }
                else if (previousAudioCc.HasValue)
                {
                    int expected = (previousAudioCc.Value + 1) & 0x0F;
                    if (cc != expected)
                        evidence.ContinuityErrors++;
                }
                previousAudioCc = cc;
            }

            if (!payloadStart || payloadOffset < 0 || payloadOffset >= packet.Length)
                continue;
            ReadOnlySpan<byte> payload = packet.AsSpan(payloadOffset);
            if (!TryReadPesPts(payload, out long pts))
                continue;

            if (pid == videoPid)
            {
                evidence.FirstVideoPts ??= pts;
                evidence.LastVideoPts = pts;
            }
            else if (pid == audioPid)
            {
                evidence.FirstAudioPts ??= pts;
                evidence.LastAudioPts = pts;
            }
        }
        return evidence;
    }

    private static int GetTransportPayloadOffset(
        ReadOnlySpan<byte> packet,
        int syncOffset,
        out bool hasPayload,
        out bool discontinuity)
    {
        hasPayload = false;
        discontinuity = false;
        if (syncOffset < 0 || syncOffset + 4 > packet.Length || packet[syncOffset] != 0x47)
            return -1;
        int adaptationControl = (packet[syncOffset + 3] >> 4) & 0x03;
        if (adaptationControl == 0 || adaptationControl == 2)
        {
            if (adaptationControl == 2 && syncOffset + 5 <= packet.Length)
            {
                int adaptationLength = packet[syncOffset + 4];
                if (adaptationLength > 0 && syncOffset + 5 < packet.Length)
                    discontinuity = (packet[syncOffset + 5] & 0x80) != 0;
            }
            return -1;
        }

        hasPayload = true;
        if (adaptationControl == 1)
            return syncOffset + 4;

        if (syncOffset + 5 > packet.Length)
            return -1;
        int length = packet[syncOffset + 4];
        if (length > 0 && syncOffset + 5 < packet.Length)
            discontinuity = (packet[syncOffset + 5] & 0x80) != 0;
        int payloadOffset = syncOffset + 5 + length;
        return payloadOffset <= packet.Length ? payloadOffset : -1;
    }

    private static bool TryParsePat(ReadOnlySpan<byte> payload, out int pmtPid)
    {
        pmtPid = -1;
        if (!TryGetPsiSection(payload, out ReadOnlySpan<byte> section) || section.Length < 12 || section[0] != 0x00)
            return false;
        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int sectionEnd = Math.Min(section.Length, 3 + sectionLength - 4);
        for (int position = 8; position + 4 <= sectionEnd; position += 4)
        {
            int program = (section[position] << 8) | section[position + 1];
            if (program == 0)
                continue;
            pmtPid = ((section[position + 2] & 0x1F) << 8) | section[position + 3];
            return true;
        }
        return false;
    }

    private static bool TryParsePmt(ReadOnlySpan<byte> payload, out int videoPid, out int audioPid)
    {
        videoPid = -1;
        audioPid = -1;
        if (!TryGetPsiSection(payload, out ReadOnlySpan<byte> section) || section.Length < 16 || section[0] != 0x02)
            return false;
        int sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        int sectionEnd = Math.Min(section.Length, 3 + sectionLength - 4);
        int programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        int position = 12 + programInfoLength;
        while (position + 5 <= sectionEnd)
        {
            int streamType = section[position];
            int pid = ((section[position + 1] & 0x1F) << 8) | section[position + 2];
            int esInfoLength = ((section[position + 3] & 0x0F) << 8) | section[position + 4];
            int descriptorsEnd = Math.Min(sectionEnd, position + 5 + esInfoLength);
            ReadOnlySpan<byte> descriptors = section.Slice(position + 5, Math.Max(0, descriptorsEnd - (position + 5)));
            if (videoPid < 0 && IsVideoStreamType(streamType))
                videoPid = pid;
            if (audioPid < 0 && IsAudioStreamType(streamType, descriptors))
                audioPid = pid;
            position = descriptorsEnd;
        }
        return videoPid >= 0 || audioPid >= 0;
    }

    private static bool TryGetPsiSection(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> section)
    {
        section = default;
        if (payload.Length < 2)
            return false;
        int pointer = payload[0];
        int start = 1 + pointer;
        if (start < 0 || start + 3 > payload.Length)
            return false;
        section = payload[start..];
        return true;
    }

    private static bool IsVideoStreamType(int streamType) =>
        streamType is 0x01 or 0x02 or 0x10 or 0x1B or 0x24 or 0x27 or 0x42 or 0xEA;

    private static bool IsAudioStreamType(int streamType, ReadOnlySpan<byte> descriptors)
    {
        if (streamType is 0x03 or 0x04 or 0x0F or 0x11 or 0x80 or 0x81 or 0x83 or 0x84 or 0x87)
            return true;
        if (streamType != 0x06)
            return false;

        int position = 0;
        while (position + 2 <= descriptors.Length)
        {
            int tag = descriptors[position];
            int length = descriptors[position + 1];
            if (position + 2 + length > descriptors.Length)
                break;
            if (tag is 0x6A or 0x7A or 0x7B or 0x7C)
                return true;
            if (tag == 0x05 && length >= 4)
            {
                ReadOnlySpan<byte> id = descriptors.Slice(position + 2, 4);
                if (id.SequenceEqual("AC-3"u8) || id.SequenceEqual("EAC3"u8) || id.SequenceEqual("DTS1"u8))
                    return true;
            }
            position += 2 + length;
        }
        return false;
    }

    private static bool TryReadPesPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;
        if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return false;
        int ptsDtsFlags = (payload[7] >> 6) & 0x03;
        int headerLength = payload[8];
        if ((ptsDtsFlags & 0x02) == 0 || headerLength < 5 || 9 + headerLength > payload.Length)
            return false;

        ReadOnlySpan<byte> value = payload.Slice(9, 5);
        if ((value[0] & 0x01) == 0 || (value[2] & 0x01) == 0 || (value[4] & 0x01) == 0)
            return false;
        pts = ((long)(value[0] & 0x0E) << 29) |
              ((long)value[1] << 22) |
              ((long)(value[2] & 0xFE) << 14) |
              ((long)value[3] << 7) |
              ((long)(value[4] & 0xFE) >> 1);
        pts &= PtsWrap - 1;
        return true;
    }

    private static double DurationMilliseconds(IReadOnlyList<Mp4TimeToSampleRun> runs, uint timescale)
    {
        if (timescale == 0 || runs.Count == 0)
            return 0;
        decimal ticks = 0;
        foreach (Mp4TimeToSampleRun run in runs)
            ticks += (decimal)run.Count * run.Delta;
        return (double)(ticks * 1000m / timescale);
    }

    private static long SumSampleCounts(IReadOnlyList<Mp4TimeToSampleRun> runs)
    {
        long total = 0;
        foreach (Mp4TimeToSampleRun run in runs)
        {
            if (run.Count > long.MaxValue - total)
                return long.MaxValue;
            total += run.Count;
        }
        return total;
    }

    private static long SignedPtsDifference(long left, long right)
    {
        long diff = (left - right) % PtsWrap;
        if (diff > PtsWrap / 2) diff -= PtsWrap;
        if (diff < -PtsWrap / 2) diff += PtsWrap;
        return diff;
    }

    private static long ForwardPtsDistance(long later, long earlier)
    {
        long distance = (later - earlier) % PtsWrap;
        if (distance < 0)
            distance += PtsWrap;
        return distance;
    }

    private static long AlignTransportPosition(long position, long packetStart, int packetSize)
    {
        if (position <= packetStart)
            return packetStart;
        long delta = position - packetStart;
        long packets = (delta + packetSize - 1) / packetSize;
        return packetStart + packets * packetSize;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> pattern, int start)
    {
        if (pattern.Length == 0 || start < 0)
            return -1;
        for (int i = start; i + pattern.Length <= data.Length; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }
        return -1;
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

    private static string FormatSigned(double value) =>
        value >= 0 ? $"+{value:0.0}" : value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static AudioVideoSyncForensicResult NoAudio(VideoDecodeHealthResult decode, string summary) =>
        new(false, false, false, false, true, 100,
            decode.DecodedAudioFrames, decode.PlayedAudioBuffers, decode.LostAudioBuffers, 0,
            0, 0, 0, 0, 0, 0, summary);

    private readonly record struct TimelineEvidence(
        bool AudioTrackPresent,
        bool SyncMeasured,
        bool PacketContinuityValidated,
        long AudioPacketCount,
        long AudioContinuityErrors,
        double StartOffsetMilliseconds,
        double EndOffsetMilliseconds,
        double DriftMilliseconds,
        double DurationMilliseconds,
        string Source)
    {
        public static TimelineEvidence Unknown(bool audioTrackPresent, string source) =>
            new(audioTrackPresent, false, false, 0, 0, 0, 0, 0, 0, source);
    }

    private readonly record struct TransportGeometry(int PacketSize, int SyncOffset, long PacketStart);

    private sealed class TsRangeEvidence
    {
        public long AudioPackets { get; set; }
        public long ContinuityErrors { get; set; }
        public long? FirstVideoPts { get; set; }
        public long? LastVideoPts { get; set; }
        public long? FirstAudioPts { get; set; }
        public long? LastAudioPts { get; set; }
    }

    private readonly record struct AviTrackTiming(double StartMilliseconds, double DurationMilliseconds, long SampleCount);
}
