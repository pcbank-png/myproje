using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public readonly record struct Mp4TimeToSampleRun(uint Count, uint Delta);

public readonly record struct Mp4CompositionOffsetRun(uint Count, int Offset);

public enum ReferenceVideoCodecKind
{
    Unknown,
    H264,
    H265,
    Mpeg2Video,
    Mpeg4Part2,
    Av1,
    Vp9,
    ProRes,
    Mjpeg
}

public sealed record ReferenceVideoProfile
{
    public required string SourcePath { get; init; }
    public required string SourceExtension { get; init; }
    public required string ContainerKind { get; init; }
    public required ReferenceVideoCodecKind VideoCodec { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double? FrameRate { get; init; }
    public uint VideoTimescale { get; init; }
    public uint VideoSampleDelta { get; init; }
    public long VideoSampleCount { get; init; }
    public Mp4TimeToSampleRun[] VideoTimeToSampleRuns { get; init; } = Array.Empty<Mp4TimeToSampleRun>();
    public Mp4CompositionOffsetRun[] VideoCompositionOffsetRuns { get; init; } = Array.Empty<Mp4CompositionOffsetRun>();
    public int PreferredSamplesPerChunk { get; init; } = 1;
    public int NalLengthSize { get; init; } = 4;
    public byte[]? Vps { get; init; }
    public byte[]? Sps { get; init; }
    public byte[]? Pps { get; init; }
    public string VideoProfile { get; init; } = "—";
    public string VideoLevel { get; init; } = "—";
    public string AudioCodec { get; init; } = "Yok/Bilinmiyor";
    public string AudioSampleEntryType { get; init; } = string.Empty;
    public byte[]? AudioSampleEntryBox { get; init; }
    public uint AudioTimescale { get; init; }
    public uint AudioSampleDelta { get; init; }
    public long AudioSampleCount { get; init; }
    public Mp4TimeToSampleRun[] AudioTimeToSampleRuns { get; init; } = Array.Empty<Mp4TimeToSampleRun>();
    public int AudioPreferredSamplesPerChunk { get; init; } = 1;
    public uint AudioFixedSampleSize { get; init; }
    public uint[] AudioSampleSizePattern { get; init; } = Array.Empty<uint>();
    public int AudioChannels { get; init; }
    public int AudioSampleRate { get; init; }
    public int AudioBitsPerSample { get; init; }
    public string MajorBrand { get; init; } = string.Empty;
    public string[] CompatibleBrands { get; init; } = Array.Empty<string>();
    public string[] TopLevelBoxOrder { get; init; } = Array.Empty<string>();
    public bool MoovBeforeMdat { get; init; }
    public byte[]? FtypBox { get; init; }
    public int TransportPacketSize { get; init; }
    public int TransportSyncOffset { get; init; }
    public int? PmtPid { get; init; }
    public int? PcrPid { get; init; }
    public int? VideoPid { get; init; }
    public int? AudioPid { get; init; }
    public byte[]? PatPacketTemplate { get; init; }
    public byte[]? PmtPacketTemplate { get; init; }
    public long? TransportPcrDelta { get; init; }
    public long? TransportPtsDelta { get; init; }
    public int Confidence { get; init; }

    public string CodecText => VideoCodec switch
    {
        ReferenceVideoCodecKind.H264 => "H.264/AVC",
        ReferenceVideoCodecKind.H265 => "H.265/HEVC",
        ReferenceVideoCodecKind.Mpeg2Video => "MPEG-2 Video",
        ReferenceVideoCodecKind.Mpeg4Part2 => "MPEG-4 Part 2",
        ReferenceVideoCodecKind.Av1 => "AV1",
        ReferenceVideoCodecKind.Vp9 => "VP9",
        ReferenceVideoCodecKind.ProRes => "ProRes",
        ReferenceVideoCodecKind.Mjpeg => "MJPEG",
        _ => "Bilinmiyor"
    };

    public string Summary
    {
        get
        {
            string geometry = Width > 0 && Height > 0 ? $"{Width}×{Height}" : "çözünürlük ?";
            string fps = FrameRate is > 0 ? $"{FrameRate.Value:0.###} fps" : "fps ?";
            string timing = VideoTimescale > 0 ? $"timescale {VideoTimescale:N0}" : "timescale ?";
            return $"{CodecText} • {geometry} • {fps} • {timing} • Ses: {AudioCodec} • Güven %{Confidence}";
        }
    }
}

internal static class ReferenceVideoProfileService
{
    private const long MaxMoovBytes = 128L * 1024 * 1024;
    private const long MaxTransportProbeBytes = 128L * 1024 * 1024;
    private const int MaxParameterSetBytes = 1024 * 1024;

    private sealed record BoxInfo(string Type, long Offset, long Size, int HeaderSize)
    {
        public long PayloadOffset => Offset + HeaderSize;
        public long PayloadSize => Size - HeaderSize;
    }

    private sealed class Mp4TrackProfile
    {
        public string Handler { get; set; } = string.Empty;
        public string SampleEntry { get; set; } = string.Empty;
        public ReferenceVideoCodecKind VideoCodec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public uint Timescale { get; set; }
        public uint SampleDelta { get; set; }
        public long SampleCount { get; set; }
        public List<Mp4TimeToSampleRun> TimeToSampleRuns { get; } = [];
        public List<Mp4CompositionOffsetRun> CompositionOffsetRuns { get; } = [];
        public int PreferredSamplesPerChunk { get; set; } = 1;
        public uint FixedSampleSize { get; set; }
        public uint[] SampleSizePattern { get; set; } = Array.Empty<uint>();
        public byte[]? SampleEntryBox { get; set; }
        public int NalLengthSize { get; set; } = 4;
        public byte[]? Vps { get; set; }
        public byte[]? Sps { get; set; }
        public byte[]? Pps { get; set; }
        public string VideoProfile { get; set; } = "—";
        public string VideoLevel { get; set; } = "—";
        public string AudioCodec { get; set; } = "Yok/Bilinmiyor";
        public int AudioChannels { get; set; }
        public int AudioSampleRate { get; set; }
        public int AudioBitsPerSample { get; set; }
    }

    private sealed class TsProfileState
    {
        public int PacketSize { get; init; }
        public int SyncOffset { get; init; }
        public int? PmtPid { get; set; }
        public int? PcrPid { get; set; }
        public int? VideoPid { get; set; }
        public int? AudioPid { get; set; }
        public int VideoStreamType { get; set; }
        public int AudioStreamType { get; set; }
        public byte[]? PatPacket { get; set; }
        public byte[]? PmtPacket { get; set; }
        public MemoryStream VideoPayload { get; } = new();
        public List<long> VideoPts { get; } = new();
        public List<long> PcrValues { get; } = new();
    }

    public static bool SupportsReference(string extension)
    {
        string ext = FileTypeHelper.Normalize(extension);
        return ext is "MP4" or "MOV" or "QT" or "M4V" or "3GP" or "3G2" or "F4V" or
            "MTS" or "M2TS" or "M2T" or "TS" or "TP" or "TRP";
    }

    public static bool IsCompatible(ReferenceVideoProfile profile, string targetExtension)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string ext = FileTypeHelper.Normalize(targetExtension);
        bool targetMp4 = ext is "MP4" or "MOV" or "QT" or "M4V" or "3GP" or "3G2" or "F4V";
        bool targetTs = ext is "MTS" or "M2TS" or "M2T" or "TS" or "TP" or "TRP";
        return (targetMp4 && profile.ContainerKind == "ISO-BMFF") ||
               (targetTs && profile.ContainerKind == "MPEG-TS");
    }

    public static ReferenceVideoProfile Analyze(string path, Action<string>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Referans video bulunamadı.", path);

        string extension = FileTypeHelper.Normalize(Path.GetExtension(path));
        if (!SupportsReference(extension))
            throw new InvalidDataException("Referans motoru bu kapsayıcıyı henüz desteklemiyor. MP4/MOV veya MTS/M2TS/TS seçin.");

        return extension is "MTS" or "M2TS" or "M2T" or "TS" or "TP" or "TRP"
            ? AnalyzeTransportStream(path, extension, progress)
            : AnalyzeMp4(path, extension, progress);
    }

    private static ReferenceVideoProfile AnalyzeMp4(string path, string extension, Action<string>? progress)
    {
        progress?.Invoke("Referans MP4/MOV • ftyp/moov/trak tabloları okunuyor...");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.RandomAccess);
        List<BoxInfo> top = ReadBoxes(stream, 0, stream.Length, maxBoxes: 4096);
        BoxInfo? ftyp = top.FirstOrDefault(box => box.Type == "ftyp");
        BoxInfo? moov = top.FirstOrDefault(box => box.Type == "moov");
        BoxInfo? mdat = top.FirstOrDefault(box => box.Type == "mdat");
        if (ftyp is null || moov is null || mdat is null)
            throw new InvalidDataException("Sağlam referansta ftyp/moov/mdat üçlüsü doğrulanamadı.");
        if (moov.Size <= 8 || moov.Size > MaxMoovBytes)
            throw new InvalidDataException("Referans moov kutusu güvenli analiz sınırının dışında.");

        byte[] ftypBytes = ReadBoxBytes(stream, ftyp);
        (string majorBrand, string[] compatibleBrands) = ParseFtyp(ftypBytes);
        byte[] moovBytes = ReadBoxBytes(stream, moov);
        List<Mp4TrackProfile> tracks = ParseMoov(moovBytes);
        Mp4TrackProfile? video = tracks.FirstOrDefault(track => track.Handler == "vide" && track.VideoCodec != ReferenceVideoCodecKind.Unknown);
        if (video is null)
            throw new InvalidDataException("Referans videoda desteklenen H.264/H.265 video track'i bulunamadı.");
        if (video.VideoCodec is not (ReferenceVideoCodecKind.H264 or ReferenceVideoCodecKind.H265))
            throw new InvalidDataException("Bu adımda referans kurtarma H.264/H.265 video track'leriyle sınırlandırılmıştır.");
        if (video.Sps is null || video.Pps is null || (video.VideoCodec == ReferenceVideoCodecKind.H265 && video.Vps is null))
            throw new InvalidDataException("Referans video codec parameter-set bilgileri eksik.");

        Mp4TrackProfile? audio = tracks.FirstOrDefault(track => track.Handler == "soun");
        double? fps = video.Timescale > 0 && video.SampleDelta > 0
            ? video.Timescale / (double)video.SampleDelta
            : null;
        int confidence = 78;
        if (video.Width > 0 && video.Height > 0) confidence += 5;
        if (fps is > 1 and < 240) confidence += 5;
        if (video.SampleCount > 0) confidence += 3;
        if (audio is not null && audio.AudioCodec != "Yok/Bilinmiyor") confidence += 3;
        if (video.NalLengthSize is >= 1 and <= 4) confidence += 3;
        if (majorBrand.Length == 4) confidence += 3;

        progress?.Invoke("Referans MP4/MOV • codec, zaman tabanı, sample düzeni ve ses karakteristiği çıkarıldı.");
        return new ReferenceVideoProfile
        {
            SourcePath = path,
            SourceExtension = extension,
            ContainerKind = "ISO-BMFF",
            VideoCodec = video.VideoCodec,
            Width = video.Width,
            Height = video.Height,
            FrameRate = fps,
            VideoTimescale = video.Timescale,
            VideoSampleDelta = video.SampleDelta,
            VideoSampleCount = video.SampleCount,
            VideoTimeToSampleRuns = video.TimeToSampleRuns.ToArray(),
            VideoCompositionOffsetRuns = video.CompositionOffsetRuns.ToArray(),
            PreferredSamplesPerChunk = Math.Clamp(video.PreferredSamplesPerChunk, 1, 4096),
            NalLengthSize = Math.Clamp(video.NalLengthSize, 1, 4),
            Vps = Clone(video.Vps),
            Sps = Clone(video.Sps),
            Pps = Clone(video.Pps),
            VideoProfile = video.VideoProfile,
            VideoLevel = video.VideoLevel,
            AudioCodec = audio?.AudioCodec ?? "Yok/Bilinmiyor",
            AudioSampleEntryType = audio?.SampleEntry ?? string.Empty,
            AudioSampleEntryBox = Clone(audio?.SampleEntryBox),
            AudioTimescale = audio?.Timescale ?? 0,
            AudioSampleDelta = audio?.SampleDelta ?? 0,
            AudioSampleCount = audio?.SampleCount ?? 0,
            AudioTimeToSampleRuns = audio?.TimeToSampleRuns.ToArray() ?? Array.Empty<Mp4TimeToSampleRun>(),
            AudioPreferredSamplesPerChunk = Math.Clamp(audio?.PreferredSamplesPerChunk ?? 1, 1, 4096),
            AudioFixedSampleSize = audio?.FixedSampleSize ?? 0,
            AudioSampleSizePattern = audio?.SampleSizePattern.ToArray() ?? Array.Empty<uint>(),
            AudioChannels = audio?.AudioChannels ?? 0,
            AudioSampleRate = audio?.AudioSampleRate ?? 0,
            AudioBitsPerSample = audio?.AudioBitsPerSample ?? 0,
            MajorBrand = majorBrand,
            CompatibleBrands = compatibleBrands,
            TopLevelBoxOrder = top.Select(box => box.Type).Take(64).ToArray(),
            MoovBeforeMdat = moov.Offset < mdat.Offset,
            FtypBox = ftypBytes,
            Confidence = Math.Clamp(confidence, 0, 99)
        };
    }

    private static ReferenceVideoProfile AnalyzeTransportStream(string path, string extension, Action<string>? progress)
    {
        progress?.Invoke("Referans MTS/M2TS/TS • paket geometrisi, PAT/PMT ve PID haritası okunuyor...");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        (int packetSize, int syncOffset, long packetStart) = DetectTransportGeometry(stream)
            ?? throw new InvalidDataException("Referans videoda güvenilir 188/192 bayt transport-stream geometrisi bulunamadı.");

        var state = new TsProfileState { PacketSize = packetSize, SyncOffset = syncOffset };
        byte[] packet = new byte[packetSize];
        long end = Math.Min(stream.Length, checked(packetStart + MaxTransportProbeBytes));
        long position = packetStart;
        while (position + packetSize <= end)
        {
            stream.Position = position;
            if (stream.Read(packet, 0, packet.Length) != packet.Length)
                break;
            if (packet[syncOffset] != 0x47)
            {
                position++;
                continue;
            }

            int pid = ((packet[syncOffset + 1] & 0x1F) << 8) | packet[syncOffset + 2];
            bool payloadStart = (packet[syncOffset + 1] & 0x40) != 0;
            int payloadOffset = GetTsPayloadOffset(packet, syncOffset);
            if (payloadOffset < 0 || payloadOffset >= packet.Length)
            {
                position += packetSize;
                continue;
            }

            if (pid == 0 && payloadStart && TryParsePat(packet.AsSpan(payloadOffset), out int pmtPid))
            {
                state.PmtPid ??= pmtPid;
                state.PatPacket ??= packet.ToArray();
            }
            else if (state.PmtPid.HasValue && pid == state.PmtPid.Value && payloadStart &&
                     TryParsePmt(packet.AsSpan(payloadOffset), out int pcrPid, out int videoPid, out int videoType, out int audioPid, out int audioType))
            {
                state.PcrPid = pcrPid;
                if (videoPid >= 0) { state.VideoPid = videoPid; state.VideoStreamType = videoType; }
                if (audioPid >= 0) { state.AudioPid = audioPid; state.AudioStreamType = audioType; }
                state.PmtPacket ??= packet.ToArray();
            }

            if (state.PcrPid.HasValue && pid == state.PcrPid.Value && TryReadTransportPcr(packet, syncOffset, out long pcr))
                state.PcrValues.Add(pcr);

            if (state.VideoPid.HasValue && pid == state.VideoPid.Value)
            {
                ReadOnlySpan<byte> payload = packet.AsSpan(payloadOffset);
                if (payloadStart && TryReadPesPts(payload, out long pts))
                    state.VideoPts.Add(pts);
                int elementaryStart = payloadStart ? GetPesPayloadOffset(payload) : 0;
                if (elementaryStart >= 0 && elementaryStart < payload.Length && state.VideoPayload.Length < 32L * 1024 * 1024)
                {
                    ReadOnlySpan<byte> elementary = payload[elementaryStart..];
                    int writable = (int)Math.Min(elementary.Length, 32L * 1024 * 1024 - state.VideoPayload.Length);
                    state.VideoPayload.Write(elementary[..writable]);
                }
            }

            position += packetSize;
            if (state.PmtPacket is not null && state.VideoPayload.Length >= 8L * 1024 * 1024 && state.VideoPts.Count >= 32)
                break;
        }

        ReferenceVideoCodecKind codec = MapTsVideoCodec(state.VideoStreamType);
        if (codec is not (ReferenceVideoCodecKind.H264 or ReferenceVideoCodecKind.H265))
            throw new InvalidDataException("Referans TS akışında H.264/H.265 video PID'i doğrulanamadı.");

        byte[] elementaryBytes = state.VideoPayload.ToArray();
        ExtractAnnexBParameterSets(elementaryBytes, codec, out byte[]? vps, out byte[]? sps, out byte[]? pps);
        if (sps is null || pps is null || (codec == ReferenceVideoCodecKind.H265 && vps is null))
            throw new InvalidDataException("Referans TS video PID'i içinde gerekli SPS/PPS/VPS parameter-set'leri bulunamadı.");

        int width = 0, height = 0;
        double? fps = null;
        string videoProfile = "—", videoLevel = "—";
        if (codec == ReferenceVideoCodecKind.H264)
        {
            _ = TryParseH264Sps(sps, out width, out height, out fps, out videoProfile, out videoLevel);
        }
        else
        {
            _ = TryParseH265Sps(sps, out width, out height, out videoProfile, out videoLevel);
        }

        uint timescale = 90000;
        uint? measuredPtsDelta = EstimatePtsDelta(state.VideoPts);
        uint sampleDelta = fps is > 1 and < 240
            ? Math.Max(1u, checked((uint)Math.Round(timescale / fps.Value)))
            : measuredPtsDelta ?? 3000u;
        long? pcrDelta = EstimateWrappedDelta(state.PcrValues, checked((1L << 33) * 300L));
        if (fps is null && sampleDelta > 0)
            fps = timescale / (double)sampleDelta;

        int confidence = 72;
        if (state.PatPacket is not null && state.PmtPacket is not null) confidence += 8;
        if (state.VideoPid.HasValue) confidence += 5;
        if (width > 0 && height > 0) confidence += 5;
        if (fps is > 1 and < 240) confidence += 4;
        if (state.AudioPid.HasValue) confidence += 3;

        progress?.Invoke("Referans MTS/M2TS/TS • codec parameter-set'leri, PTS ritmi ve ses/video PID karakteristiği çıkarıldı.");
        return new ReferenceVideoProfile
        {
            SourcePath = path,
            SourceExtension = extension,
            ContainerKind = "MPEG-TS",
            VideoCodec = codec,
            Width = width,
            Height = height,
            FrameRate = fps,
            VideoTimescale = timescale,
            VideoSampleDelta = sampleDelta,
            NalLengthSize = 4,
            Vps = Clone(vps),
            Sps = Clone(sps),
            Pps = Clone(pps),
            VideoProfile = videoProfile,
            VideoLevel = videoLevel,
            AudioCodec = MapTsAudioCodec(state.AudioStreamType),
            TransportPacketSize = state.PacketSize,
            TransportSyncOffset = state.SyncOffset,
            PmtPid = state.PmtPid,
            PcrPid = state.PcrPid,
            VideoPid = state.VideoPid,
            AudioPid = state.AudioPid,
            PatPacketTemplate = Clone(state.PatPacket),
            PmtPacketTemplate = Clone(state.PmtPacket),
            TransportPcrDelta = pcrDelta,
            TransportPtsDelta = measuredPtsDelta,
            Confidence = Math.Clamp(confidence, 0, 99)
        };
    }

    private static List<Mp4TrackProfile> ParseMoov(byte[] moov)
    {
        var result = new List<Mp4TrackProfile>();
        foreach ((int offset, int size, int header, string type) in EnumerateBoxes(moov, 8, moov.Length))
        {
            if (type != "trak")
                continue;
            Mp4TrackProfile? track = ParseTrak(moov, offset + header, offset + size);
            if (track is not null)
                result.Add(track);
        }
        return result;
    }

    private static Mp4TrackProfile? ParseTrak(byte[] data, int start, int end)
    {
        (int offset, int size, int header, string type)? mdia = FindBox(data, start, end, "mdia");
        if (mdia is null)
            return null;
        int mdiaStart = mdia.Value.offset + mdia.Value.header;
        int mdiaEnd = mdia.Value.offset + mdia.Value.size;
        var track = new Mp4TrackProfile();

        var hdlr = FindBox(data, mdiaStart, mdiaEnd, "hdlr");
        if (hdlr.HasValue && hdlr.Value.offset + hdlr.Value.header + 12 <= mdiaEnd)
            track.Handler = ReadAscii(data, hdlr.Value.offset + hdlr.Value.header + 8, 4);

        var mdhd = FindBox(data, mdiaStart, mdiaEnd, "mdhd");
        if (mdhd.HasValue)
            track.Timescale = ReadMdhdTimescale(data, mdhd.Value.offset + mdhd.Value.header, mdhd.Value.offset + mdhd.Value.size);

        var minf = FindBox(data, mdiaStart, mdiaEnd, "minf");
        if (!minf.HasValue)
            return track;
        var stbl = FindBox(data, minf.Value.offset + minf.Value.header, minf.Value.offset + minf.Value.size, "stbl");
        if (!stbl.HasValue)
            return track;
        int stblStart = stbl.Value.offset + stbl.Value.header;
        int stblEnd = stbl.Value.offset + stbl.Value.size;

        ParseStsd(data, stblStart, stblEnd, track);
        ParseStts(data, stblStart, stblEnd, track);
        ParseCtts(data, stblStart, stblEnd, track);
        ParseStsz(data, stblStart, stblEnd, track);
        int chunkCount = ReadChunkCount(data, stblStart, stblEnd);
        track.PreferredSamplesPerChunk = ReadPreferredSamplesPerChunk(data, stblStart, stblEnd, chunkCount);
        return track;
    }

    private static void ParseStsd(byte[] data, int start, int end, Mp4TrackProfile track)
    {
        var stsd = FindBox(data, start, end, "stsd");
        if (!stsd.HasValue)
            return;
        int payload = stsd.Value.offset + stsd.Value.header;
        int boxEnd = stsd.Value.offset + stsd.Value.size;
        if (payload + 8 > boxEnd)
            return;
        uint entryCount = ReadU32(data, payload + 4);
        int position = payload + 8;
        for (uint index = 0; index < entryCount && position + 8 <= boxEnd; index++)
        {
            uint size = ReadU32(data, position);
            if (size < 8 || size > int.MaxValue || position + size > boxEnd)
                break;
            string type = ReadAscii(data, position + 4, 4);
            track.SampleEntry = type;
            if (size <= 1024 * 1024)
                track.SampleEntryBox = data.AsSpan(position, checked((int)size)).ToArray();
            if (track.Handler == "vide")
                ParseVideoSampleEntry(data, position, checked((int)size), type, track);
            else if (track.Handler == "soun")
                ParseAudioSampleEntry(data, position, checked((int)size), type, track);
            position += checked((int)size);
        }
    }

    private static void ParseVideoSampleEntry(byte[] data, int start, int size, string type, Mp4TrackProfile track)
    {
        int end = start + size;
        if (start + 36 <= end)
        {
            track.Width = ReadU16(data, start + 32);
            track.Height = ReadU16(data, start + 34);
        }
        track.VideoCodec = type switch
        {
            "avc1" or "avc3" => ReferenceVideoCodecKind.H264,
            "hvc1" or "hev1" => ReferenceVideoCodecKind.H265,
            "av01" => ReferenceVideoCodecKind.Av1,
            "vp09" => ReferenceVideoCodecKind.Vp9,
            "mp4v" => ReferenceVideoCodecKind.Mpeg4Part2,
            "jpeg" or "mjpa" or "mjpb" => ReferenceVideoCodecKind.Mjpeg,
            "apch" or "apcn" or "apcs" or "apco" or "ap4h" or "ap4x" => ReferenceVideoCodecKind.ProRes,
            _ => ReferenceVideoCodecKind.Unknown
        };

        int childStart = Math.Min(end, start + 86);
        foreach ((int offset, int childSize, int header, string childType) in EnumerateBoxes(data, childStart, end))
        {
            int payload = offset + header;
            int payloadLength = childSize - header;
            if (childType == "avcC" && track.VideoCodec == ReferenceVideoCodecKind.H264)
                ParseAvcC(data.AsSpan(payload, payloadLength), track);
            else if (childType == "hvcC" && track.VideoCodec == ReferenceVideoCodecKind.H265)
                ParseHvcC(data.AsSpan(payload, payloadLength), track);
        }
    }

    private static void ParseAudioSampleEntry(byte[] data, int start, int size, string type, Mp4TrackProfile track)
    {
        int end = start + size;
        track.AudioCodec = type switch
        {
            "mp4a" => "AAC",
            "ac-3" => "AC-3",
            "ec-3" => "E-AC-3",
            "alac" => "ALAC",
            "Opus" or "opus" => "Opus",
            "lpcm" or "sowt" or "twos" or "in24" or "in32" => "PCM",
            _ => string.IsNullOrWhiteSpace(type) ? "Yok/Bilinmiyor" : type
        };
        if (start + 36 <= end)
        {
            track.AudioChannels = ReadU16(data, start + 24);
            track.AudioBitsPerSample = ReadU16(data, start + 26);
            uint fixedRate = ReadU32(data, start + 32);
            track.AudioSampleRate = checked((int)(fixedRate >> 16));
        }
    }

    private static void ParseAvcC(ReadOnlySpan<byte> avcC, Mp4TrackProfile track)
    {
        if (avcC.Length < 7 || avcC[0] != 1)
            return;
        track.VideoProfile = AvcProfileName(avcC[1]);
        track.VideoLevel = (avcC[3] / 10d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        track.NalLengthSize = (avcC[4] & 0x03) + 1;
        int position = 5;
        int spsCount = avcC[position++] & 0x1F;
        for (int i = 0; i < spsCount && position + 2 <= avcC.Length; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(avcC.Slice(position, 2));
            position += 2;
            if (length <= 0 || length > MaxParameterSetBytes || position + length > avcC.Length)
                return;
            track.Sps ??= avcC.Slice(position, length).ToArray();
            position += length;
        }
        if (position >= avcC.Length)
            return;
        int ppsCount = avcC[position++];
        for (int i = 0; i < ppsCount && position + 2 <= avcC.Length; i++)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(avcC.Slice(position, 2));
            position += 2;
            if (length <= 0 || length > MaxParameterSetBytes || position + length > avcC.Length)
                return;
            track.Pps ??= avcC.Slice(position, length).ToArray();
            position += length;
        }
    }

    private static void ParseHvcC(ReadOnlySpan<byte> hvcC, Mp4TrackProfile track)
    {
        if (hvcC.Length < 23 || hvcC[0] != 1)
            return;
        byte profile = (byte)(hvcC[1] & 0x1F);
        track.VideoProfile = profile switch { 1 => "Main", 2 => "Main 10", 3 => "Main Still Picture", _ => $"HEVC Profile {profile}" };
        track.VideoLevel = (hvcC[12] / 30d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        track.NalLengthSize = (hvcC[21] & 0x03) + 1;
        int arrays = hvcC[22];
        int position = 23;
        for (int arrayIndex = 0; arrayIndex < arrays && position + 3 <= hvcC.Length; arrayIndex++)
        {
            int nalType = hvcC[position++] & 0x3F;
            int count = BinaryPrimitives.ReadUInt16BigEndian(hvcC.Slice(position, 2));
            position += 2;
            for (int i = 0; i < count && position + 2 <= hvcC.Length; i++)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(hvcC.Slice(position, 2));
                position += 2;
                if (length <= 0 || length > MaxParameterSetBytes || position + length > hvcC.Length)
                    return;
                byte[] nal = hvcC.Slice(position, length).ToArray();
                if (nalType == 32) track.Vps ??= nal;
                else if (nalType == 33) track.Sps ??= nal;
                else if (nalType == 34) track.Pps ??= nal;
                position += length;
            }
        }
    }

    private static void ParseStts(byte[] data, int start, int end, Mp4TrackProfile track)
    {
        var box = FindBox(data, start, end, "stts");
        if (!box.HasValue)
            return;
        int position = box.Value.offset + box.Value.header;
        int boxEnd = box.Value.offset + box.Value.size;
        if (position + 8 > boxEnd)
            return;
        uint entryCount = ReadU32(data, position + 4);
        position += 8;
        var byDelta = new Dictionary<uint, ulong>();
        ulong totalSamples = 0;
        for (uint i = 0; i < entryCount && position + 8 <= boxEnd; i++, position += 8)
        {
            uint count = ReadU32(data, position);
            uint delta = ReadU32(data, position + 4);
            if (count == 0 || delta == 0)
                continue;
            if (track.TimeToSampleRuns.Count < 4096)
                track.TimeToSampleRuns.Add(new Mp4TimeToSampleRun(count, delta));
            totalSamples += count;
            byDelta[delta] = byDelta.TryGetValue(delta, out ulong existing) ? existing + count : count;
        }
        if (byDelta.Count > 0)
            track.SampleDelta = byDelta.OrderByDescending(pair => pair.Value).First().Key;
        if (track.SampleCount <= 0 && totalSamples <= long.MaxValue)
            track.SampleCount = (long)totalSamples;
    }

    private static void ParseCtts(byte[] data, int start, int end, Mp4TrackProfile track)
    {
        var box = FindBox(data, start, end, "ctts");
        if (!box.HasValue)
            return;
        int payload = box.Value.offset + box.Value.header;
        int boxEnd = box.Value.offset + box.Value.size;
        if (payload + 8 > boxEnd)
            return;
        byte version = data[payload];
        if (version > 1)
            return;
        uint entryCount = ReadU32(data, payload + 4);
        int position = payload + 8;
        for (uint i = 0; i < entryCount && position + 8 <= boxEnd; i++, position += 8)
        {
            uint count = ReadU32(data, position);
            uint raw = ReadU32(data, position + 4);
            int offset = version == 1 ? unchecked((int)raw) : raw > int.MaxValue ? int.MaxValue : (int)raw;
            if (count == 0)
                continue;
            if (track.CompositionOffsetRuns.Count < 4096)
                track.CompositionOffsetRuns.Add(new Mp4CompositionOffsetRun(count, offset));
        }
    }

    private static void ParseStsz(byte[] data, int start, int end, Mp4TrackProfile track)
    {
        var box = FindBox(data, start, end, "stsz");
        if (!box.HasValue)
            return;
        int payload = box.Value.offset + box.Value.header;
        int boxEnd = box.Value.offset + box.Value.size;
        if (payload + 12 > boxEnd)
            return;
        uint fixedSize = ReadU32(data, payload + 4);
        uint sampleCount = ReadU32(data, payload + 8);
        track.FixedSampleSize = fixedSize;
        track.SampleCount = sampleCount;
        if (fixedSize != 0 || sampleCount == 0)
            return;

        int available = Math.Max(0, (boxEnd - (payload + 12)) / 4);
        int count = (int)Math.Min(Math.Min(sampleCount, 4096u), checked((uint)available));
        if (count <= 0)
            return;
        uint[] pattern = new uint[count];
        int position = payload + 12;
        for (int i = 0; i < count; i++, position += 4)
            pattern[i] = ReadU32(data, position);
        track.SampleSizePattern = pattern;
    }

    private static int ReadChunkCount(byte[] data, int start, int end)
    {
        var stco = FindBox(data, start, end, "stco") ?? FindBox(data, start, end, "co64");
        if (!stco.HasValue)
            return 0;
        int payload = stco.Value.offset + stco.Value.header;
        int boxEnd = stco.Value.offset + stco.Value.size;
        if (payload + 8 > boxEnd)
            return 0;
        uint count = ReadU32(data, payload + 4);
        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static int ReadPreferredSamplesPerChunk(byte[] data, int start, int end, int chunkCount)
    {
        var box = FindBox(data, start, end, "stsc");
        if (!box.HasValue)
            return 1;
        int payload = box.Value.offset + box.Value.header;
        int boxEnd = box.Value.offset + box.Value.size;
        if (payload + 8 > boxEnd)
            return 1;
        uint entryCount = ReadU32(data, payload + 4);
        int position = payload + 8;
        var entries = new List<(uint FirstChunk, uint Samples)>();
        for (uint i = 0; i < entryCount && position + 12 <= boxEnd; i++, position += 12)
        {
            uint firstChunk = ReadU32(data, position);
            uint samples = ReadU32(data, position + 4);
            if (firstChunk > 0 && samples > 0 && samples <= 4096)
                entries.Add((firstChunk, samples));
        }
        if (entries.Count == 0)
            return 1;
        if (chunkCount <= 0)
            return checked((int)entries[0].Samples);

        var weights = new Dictionary<uint, long>();
        for (int i = 0; i < entries.Count; i++)
        {
            uint from = entries[i].FirstChunk;
            uint toExclusive = i + 1 < entries.Count ? entries[i + 1].FirstChunk : checked((uint)chunkCount + 1);
            if (toExclusive <= from)
                continue;
            long weight = toExclusive - from;
            weights[entries[i].Samples] = weights.TryGetValue(entries[i].Samples, out long existing) ? existing + weight : weight;
        }
        return weights.Count == 0 ? checked((int)entries[0].Samples) : checked((int)weights.OrderByDescending(pair => pair.Value).First().Key);
    }

    private static uint ReadMdhdTimescale(byte[] data, int payload, int end)
    {
        if (payload + 4 > end)
            return 0;
        int version = data[payload];
        int offset = version == 1 ? payload + 20 : payload + 12;
        return offset + 4 <= end ? ReadU32(data, offset) : 0;
    }

    private static List<BoxInfo> ReadBoxes(FileStream stream, long start, long end, int maxBoxes)
    {
        var result = new List<BoxInfo>();
        long offset = start;
        Span<byte> header = stackalloc byte[16];
        while (offset + 8 <= end && result.Count < maxBoxes)
        {
            stream.Position = offset;
            if (stream.Read(header[..8]) != 8)
                break;
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            if (!IsBoxType(type))
                break;
            int headerSize = 8;
            long size;
            if (size32 == 1)
            {
                if (stream.Read(header[..8]) != 8)
                    break;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
                if (size64 < 16 || size64 > long.MaxValue)
                    break;
                size = (long)size64;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                size = end - offset;
            }
            else
            {
                size = size32;
            }
            if (size < headerSize || offset + size > end)
                break;
            result.Add(new BoxInfo(type, offset, size, headerSize));
            offset += size;
        }
        return result;
    }

    private static byte[] ReadBoxBytes(FileStream stream, BoxInfo box)
    {
        if (box.Size > int.MaxValue)
            throw new InvalidDataException($"{box.Type} kutusu analiz için çok büyük.");
        byte[] data = new byte[(int)box.Size];
        stream.Position = box.Offset;
        stream.ReadExactly(data);
        return data;
    }

    private static (string MajorBrand, string[] CompatibleBrands) ParseFtyp(byte[] ftyp)
    {
        if (ftyp.Length < 16 || ReadAscii(ftyp, 4, 4) != "ftyp")
            return (string.Empty, Array.Empty<string>());
        string major = ReadAscii(ftyp, 8, 4);
        var compatible = new List<string>();
        for (int offset = 16; offset + 4 <= ftyp.Length; offset += 4)
        {
            string brand = ReadAscii(ftyp, offset, 4);
            if (IsBoxType(brand)) compatible.Add(brand);
        }
        return (major, compatible.ToArray());
    }

    private static IEnumerable<(int offset, int size, int header, string type)> EnumerateBoxes(byte[] data, int start, int end)
    {
        int position = start;
        while (position + 8 <= end)
        {
            uint size32 = ReadU32(data, position);
            string type = ReadAscii(data, position + 4, 4);
            if (!IsBoxType(type))
                yield break;
            int header = 8;
            long size = size32;
            if (size32 == 1)
            {
                if (position + 16 > end)
                    yield break;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(position + 8, 8));
                if (size64 > int.MaxValue)
                    yield break;
                size = (long)size64;
                header = 16;
            }
            else if (size32 == 0)
            {
                size = end - position;
            }
            if (size < header || size > int.MaxValue || position + size > end)
                yield break;
            int intSize = (int)size;
            yield return (position, intSize, header, type);
            position += intSize;
        }
    }

    private static (int offset, int size, int header, string type)? FindBox(byte[] data, int start, int end, string type)
    {
        foreach (var box in EnumerateBoxes(data, start, end))
            if (box.type == type)
                return box;
        return null;
    }

    private static (int PacketSize, int SyncOffset, long PacketStart)? DetectTransportGeometry(FileStream stream)
    {
        if (stream.Length < 188 * 8L)
            return null;
        const int blockSize = 4 * 1024 * 1024;
        const int tail = 192 * 8 + 8;
        byte[] buffer = new byte[blockSize + tail];
        int carry = 0;
        long position = 0;
        while (position < stream.Length && position < 64L * 1024 * 1024)
        {
            int request = (int)Math.Min(blockSize, Math.Min(stream.Length - position, 64L * 1024 * 1024 - position));
            stream.Position = position;
            int read = stream.Read(buffer, carry, request);
            if (read <= 0) break;
            int count = carry + read;
            for (int i = 0; i < count; i++)
            {
                if (HasTsSync(buffer.AsSpan(0, count), i, 188, 0, 8))
                    return (188, 0, position - carry + i);
                if (HasTsSync(buffer.AsSpan(0, count), i, 192, 4, 8))
                    return (192, 4, position - carry + i);
            }
            carry = Math.Min(tail, count);
            if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return null;
    }

    private static bool HasTsSync(ReadOnlySpan<byte> data, int start, int packetSize, int syncOffset, int count)
    {
        int last = start + syncOffset + packetSize * (count - 1);
        if (start < 0 || last >= data.Length)
            return false;
        for (int i = 0; i < count; i++)
            if (data[start + syncOffset + i * packetSize] != 0x47)
                return false;
        return true;
    }

    private static int GetTsPayloadOffset(byte[] packet, int syncOffset)
    {
        if (syncOffset < 0 || syncOffset + 4 > packet.Length || packet[syncOffset] != 0x47)
            return -1;
        int adaptationControl = (packet[syncOffset + 3] >> 4) & 0x03;
        if (adaptationControl is 0 or 2)
            return -1;
        int position = syncOffset + 4;
        if (adaptationControl == 3)
        {
            if (position >= packet.Length) return -1;
            int length = packet[position];
            position += 1 + length;
        }
        return position <= packet.Length ? position : -1;
    }

    private static bool TryParsePat(ReadOnlySpan<byte> payload, out int pmtPid)
    {
        pmtPid = -1;
        if (payload.Length < 9)
            return false;
        int pointer = payload[0];
        int start = 1 + pointer;
        if (start + 8 > payload.Length || payload[start] != 0x00)
            return false;
        int sectionLength = ((payload[start + 1] & 0x0F) << 8) | payload[start + 2];
        int sectionEnd = Math.Min(payload.Length, start + 3 + sectionLength);
        for (int position = start + 8; position + 4 <= sectionEnd - 4; position += 4)
        {
            int program = (payload[position] << 8) | payload[position + 1];
            if (program == 0) continue;
            pmtPid = ((payload[position + 2] & 0x1F) << 8) | payload[position + 3];
            return pmtPid is >= 0 and <= 0x1FFF;
        }
        return false;
    }

    private static bool TryParsePmt(ReadOnlySpan<byte> payload, out int pcrPid, out int videoPid, out int videoType, out int audioPid, out int audioType)
    {
        pcrPid = videoPid = audioPid = -1;
        videoType = audioType = 0;
        if (payload.Length < 13)
            return false;
        int pointer = payload[0];
        int start = 1 + pointer;
        if (start + 12 > payload.Length || payload[start] != 0x02)
            return false;
        int sectionLength = ((payload[start + 1] & 0x0F) << 8) | payload[start + 2];
        int sectionEnd = Math.Min(payload.Length, start + 3 + sectionLength);
        pcrPid = ((payload[start + 8] & 0x1F) << 8) | payload[start + 9];
        int programInfoLength = ((payload[start + 10] & 0x0F) << 8) | payload[start + 11];
        int position = start + 12 + programInfoLength;
        int dataEnd = sectionEnd - 4;
        while (position + 5 <= dataEnd)
        {
            int streamType = payload[position];
            int pid = ((payload[position + 1] & 0x1F) << 8) | payload[position + 2];
            int esInfoLength = ((payload[position + 3] & 0x0F) << 8) | payload[position + 4];
            if (videoPid < 0 && IsVideoStreamType(streamType)) { videoPid = pid; videoType = streamType; }
            else if (audioPid < 0 && IsAudioStreamType(streamType)) { audioPid = pid; audioType = streamType; }
            position += 5 + esInfoLength;
        }
        return videoPid >= 0;
    }

    private static bool TryReadPesPts(ReadOnlySpan<byte> payload, out long pts)
    {
        pts = 0;
        if (payload.Length < 14 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return false;
        int flags = (payload[7] >> 6) & 0x03;
        if (flags is not (2 or 3))
            return false;
        if (payload.Length < 14)
            return false;
        pts = ((long)(payload[9] & 0x0E) << 29) |
              ((long)payload[10] << 22) |
              ((long)(payload[11] & 0xFE) << 14) |
              ((long)payload[12] << 7) |
              (long)((payload[13] & 0xFE) >> 1);
        return true;
    }

    private static int GetPesPayloadOffset(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return 0;
        int headerLength = payload[8];
        int offset = 9 + headerLength;
        return offset <= payload.Length ? offset : -1;
    }

    private static bool TryReadTransportPcr(byte[] packet, int syncOffset, out long pcr)
    {
        pcr = 0;
        if (syncOffset < 0 || syncOffset + 12 > packet.Length || packet[syncOffset] != 0x47)
            return false;
        int control = (packet[syncOffset + 3] >> 4) & 0x03;
        if (control is not (2 or 3))
            return false;
        int length = packet[syncOffset + 4];
        if (length < 7 || syncOffset + 5 + length > packet.Length || (packet[syncOffset + 5] & 0x10) == 0)
            return false;
        int at = syncOffset + 6;
        long baseValue = ((long)packet[at] << 25) |
                         ((long)packet[at + 1] << 17) |
                         ((long)packet[at + 2] << 9) |
                         ((long)packet[at + 3] << 1) |
                         (long)(packet[at + 4] >> 7);
        int extension = ((packet[at + 4] & 1) << 8) | packet[at + 5];
        pcr = checked(baseValue * 300L + extension);
        return true;
    }

    private static long? EstimateWrappedDelta(IReadOnlyList<long> values, long wrap)
    {
        if (values.Count < 3 || wrap <= 0)
            return null;
        var deltas = new List<long>();
        for (int i = 1; i < values.Count; i++)
        {
            long delta = values[i] - values[i - 1];
            if (delta < 0)
                delta += wrap;
            if (delta > 0 && delta < wrap / 2)
                deltas.Add(delta);
        }
        if (deltas.Count == 0)
            return null;
        deltas.Sort();
        return deltas[deltas.Count / 2];
    }

    private static uint? EstimatePtsDelta(IReadOnlyList<long> pts)
    {
        if (pts.Count < 3)
            return null;
        var deltas = new List<long>();
        for (int i = 1; i < pts.Count; i++)
        {
            long delta = pts[i] - pts[i - 1];
            if (delta < 0) delta += 1L << 33;
            if (delta is > 0 and < 90000) deltas.Add(delta);
        }
        if (deltas.Count == 0)
            return null;
        deltas.Sort();
        long median = deltas[deltas.Count / 2];
        return median > uint.MaxValue ? null : (uint)median;
    }

    private static void ExtractAnnexBParameterSets(byte[] data, ReferenceVideoCodecKind codec, out byte[]? vps, out byte[]? sps, out byte[]? pps)
    {
        vps = sps = pps = null;
        List<(int Start, int Prefix)> starts = FindAnnexBStarts(data);
        for (int i = 0; i < starts.Count; i++)
        {
            int payload = starts[i].Start + starts[i].Prefix;
            int end = i + 1 < starts.Count ? starts[i + 1].Start : data.Length;
            if (payload >= end)
                continue;
            int length = end - payload;
            if (length > MaxParameterSetBytes)
                continue;
            int type = codec == ReferenceVideoCodecKind.H264 ? data[payload] & 0x1F : (data[payload] >> 1) & 0x3F;
            if (codec == ReferenceVideoCodecKind.H264)
            {
                if (type == 7 && sps is null) sps = data.AsSpan(payload, length).ToArray();
                else if (type == 8 && pps is null) pps = data.AsSpan(payload, length).ToArray();
            }
            else
            {
                if (type == 32 && vps is null) vps = data.AsSpan(payload, length).ToArray();
                else if (type == 33 && sps is null) sps = data.AsSpan(payload, length).ToArray();
                else if (type == 34 && pps is null) pps = data.AsSpan(payload, length).ToArray();
            }
            if (sps is not null && pps is not null && (codec == ReferenceVideoCodecKind.H264 || vps is not null))
                return;
        }
    }

    private static List<(int Start, int Prefix)> FindAnnexBStarts(byte[] data)
    {
        var result = new List<(int, int)>();
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0)
                continue;
            if (data[i + 2] == 1) { result.Add((i, 3)); i += 2; }
            else if (i + 4 <= data.Length && data[i + 2] == 0 && data[i + 3] == 1) { result.Add((i, 4)); i += 3; }
        }
        return result;
    }

    private static ReferenceVideoCodecKind MapTsVideoCodec(int streamType) => streamType switch
    {
        0x1B => ReferenceVideoCodecKind.H264,
        0x24 => ReferenceVideoCodecKind.H265,
        0x02 => ReferenceVideoCodecKind.Mpeg2Video,
        0x10 => ReferenceVideoCodecKind.Mpeg4Part2,
        _ => ReferenceVideoCodecKind.Unknown
    };

    private static string MapTsAudioCodec(int streamType) => streamType switch
    {
        0x0F => "AAC",
        0x11 => "AAC-LATM",
        0x03 or 0x04 => "MPEG Audio",
        0x81 => "AC-3",
        0x87 => "E-AC-3",
        0x83 => "LPCM",
        _ => "Yok/Bilinmiyor"
    };

    private static bool IsVideoStreamType(int type) => type is 0x01 or 0x02 or 0x10 or 0x1B or 0x24;
    private static bool IsAudioStreamType(int type) => type is 0x03 or 0x04 or 0x0F or 0x11 or 0x81 or 0x83 or 0x87;

    private static bool TryParseH264Sps(byte[] sps, out int width, out int height, out double? fps, out string profileName, out string levelText)
    {
        width = height = 0; fps = null; profileName = "—"; levelText = "—";
        if (sps.Length < 4)
            return false;
        profileName = AvcProfileName(sps[1]);
        levelText = (sps[3] / 10d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(1)));
        if (!bits.TryReadBits(8, out uint profile) || !bits.TrySkip(16) || !bits.TryReadUE(out _)) return false;
        uint chroma = 1;
        bool separate = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (!bits.TryReadUE(out chroma)) return false;
            if (chroma == 3 && !bits.TryReadBit(out separate)) return false;
            if (!bits.TryReadUE(out _) || !bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadBit(out bool scaling)) return false;
            if (scaling)
            {
                int count = chroma != 3 ? 8 : 12;
                for (int i = 0; i < count; i++)
                {
                    if (!bits.TryReadBit(out bool present)) return false;
                    if (present && !SkipScalingList(bits, i < 6 ? 16 : 64)) return false;
                }
            }
        }
        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint pocType)) return false;
        if (pocType == 0) { if (!bits.TryReadUE(out _)) return false; }
        else if (pocType == 1)
        {
            if (!bits.TrySkip(1) || !bits.TryReadSE(out _) || !bits.TryReadSE(out _) || !bits.TryReadUE(out uint cycle)) return false;
            for (uint i = 0; i < cycle; i++) if (!bits.TryReadSE(out _)) return false;
        }
        if (!bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadUE(out uint widthMbs) || !bits.TryReadUE(out uint heightMaps) || !bits.TryReadBit(out bool frameOnly)) return false;
        if (!frameOnly && !bits.TrySkip(1)) return false;
        if (!bits.TrySkip(1) || !bits.TryReadBit(out bool crop)) return false;
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (crop && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;
        int baseWidth = checked((int)((widthMbs + 1) * 16));
        int baseHeight = checked((int)((2 - (frameOnly ? 1 : 0)) * (heightMaps + 1) * 16));
        uint chromaArray = separate ? 0u : chroma;
        int subWidth = chromaArray is 1 or 2 ? 2 : 1;
        int subHeight = chromaArray == 1 ? 2 : 1;
        int cropX = chromaArray == 0 ? 1 : subWidth;
        int cropY = chromaArray == 0 ? 2 - (frameOnly ? 1 : 0) : subHeight * (2 - (frameOnly ? 1 : 0));
        width = baseWidth - checked((int)((left + right) * (uint)cropX));
        height = baseHeight - checked((int)((top + bottom) * (uint)cropY));
        if (width <= 0 || height <= 0) return false;
        if (bits.TryReadBit(out bool vui) && vui) fps = TryReadH264VuiRate(bits);
        return true;
    }

    private static bool TryParseH265Sps(byte[] sps, out int width, out int height, out string profileName, out string levelText)
    {
        width = height = 0; profileName = "—"; levelText = "—";
        if (sps.Length < 6) return false;
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(2)));
        if (!bits.TrySkip(4) || !bits.TryReadBits(3, out uint layers) || !bits.TrySkip(1)) return false;
        if (!bits.TryReadBits(2, out _) || !bits.TryReadBits(1, out _) || !bits.TryReadBits(5, out uint profileIdc) ||
            !bits.TrySkip(32) || !bits.TrySkip(48) || !bits.TryReadBits(8, out uint levelIdc)) return false;
        profileName = profileIdc switch { 1 => "Main", 2 => "Main 10", 3 => "Main Still Picture", _ => $"HEVC Profile {profileIdc}" };
        levelText = (levelIdc / 30d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        bool[] profilePresent = new bool[(int)layers];
        bool[] levelPresent = new bool[(int)layers];
        for (int i = 0; i < layers; i++)
            if (!bits.TryReadBit(out profilePresent[i]) || !bits.TryReadBit(out levelPresent[i])) return false;
        if (layers > 0) for (int i = (int)layers; i < 8; i++) if (!bits.TrySkip(2)) return false;
        for (int i = 0; i < layers; i++)
        {
            if (profilePresent[i] && !bits.TrySkip(88)) return false;
            if (levelPresent[i] && !bits.TrySkip(8)) return false;
        }
        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint chroma)) return false;
        if (chroma == 3 && !bits.TrySkip(1)) return false;
        if (!bits.TryReadUE(out uint picWidth) || !bits.TryReadUE(out uint picHeight) || !bits.TryReadBit(out bool window)) return false;
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (window && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;
        int subWidth = chroma is 1 or 2 ? 2 : 1;
        int subHeight = chroma == 1 ? 2 : 1;
        width = checked((int)picWidth - (int)((left + right) * (uint)subWidth));
        height = checked((int)picHeight - (int)((top + bottom) * (uint)subHeight));
        return width > 0 && height > 0;
    }

    private static string AvcProfileName(byte profile) => profile switch
    {
        66 => "Baseline",
        77 => "Main",
        88 => "Extended",
        100 => "High",
        110 => "High 10",
        122 => "High 4:2:2",
        244 => "High 4:4:4",
        _ => $"AVC Profile {profile}"
    };

    private static bool SkipScalingList(BitReader bits, int size)
    {
        int last = 8, next = 8;
        for (int i = 0; i < size; i++)
        {
            if (next != 0)
            {
                if (!bits.TryReadSE(out int delta)) return false;
                next = (last + delta + 256) % 256;
            }
            last = next == 0 ? last : next;
        }
        return true;
    }

    private static double? TryReadH264VuiRate(BitReader bits)
    {
        if (!bits.TryReadBit(out bool aspect)) return null;
        if (aspect)
        {
            if (!bits.TryReadBits(8, out uint idc)) return null;
            if (idc == 255 && !bits.TrySkip(32)) return null;
        }
        if (!bits.TryReadBit(out bool overscan)) return null;
        if (overscan && !bits.TrySkip(1)) return null;
        if (!bits.TryReadBit(out bool signal)) return null;
        if (signal)
        {
            if (!bits.TrySkip(4) || !bits.TryReadBit(out bool colour)) return null;
            if (colour && !bits.TrySkip(24)) return null;
        }
        if (!bits.TryReadBit(out bool chromaLoc)) return null;
        if (chromaLoc && (!bits.TryReadUE(out _) || !bits.TryReadUE(out _))) return null;
        if (!bits.TryReadBit(out bool timing) || !timing) return null;
        if (!bits.TryReadBits(32, out uint tick) || !bits.TryReadBits(32, out uint scale) || !bits.TrySkip(1) || tick == 0 || scale == 0) return null;
        double rate = scale / (2d * tick);
        return rate is > 1 and < 240 ? rate : null;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> source)
    {
        byte[] result = new byte[source.Length];
        int write = 0, zeros = 0;
        foreach (byte value in source)
        {
            if (zeros >= 2 && value == 0x03) { zeros = 0; continue; }
            result[write++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.AsSpan(0, write).ToArray();
    }

    private static byte[]? Clone(byte[]? value) => value is null ? null : value.ToArray();
    private static ushort ReadU16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    private static uint ReadU32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
    private static string ReadAscii(byte[] data, int offset, int count) => Encoding.ASCII.GetString(data, offset, count);
    private static bool IsBoxType(string type) => type.Length == 4 && type.All(ch => ch is >= ' ' and <= '~');

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;
        public BitReader(byte[] data) => _data = data;
        public bool TryReadBit(out bool value)
        {
            value = false;
            if (_bit >= _data.Length * 8) return false;
            value = ((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1) != 0;
            _bit++;
            return true;
        }
        public bool TrySkip(int count)
        {
            if (count < 0 || _bit + count > _data.Length * 8) return false;
            _bit += count;
            return true;
        }
        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (count is < 0 or > 32 || _bit + count > _data.Length * 8) return false;
            for (int i = 0; i < count; i++) { TryReadBit(out bool bit); value = (value << 1) | (bit ? 1u : 0u); }
            return true;
        }
        public bool TryReadUE(out uint value)
        {
            value = 0;
            int zeros = 0;
            while (true)
            {
                if (!TryReadBit(out bool bit)) return false;
                if (bit) break;
                if (++zeros > 31) return false;
            }
            if (zeros == 0) return true;
            if (!TryReadBits(zeros, out uint suffix)) return false;
            value = ((1u << zeros) - 1u) + suffix;
            return true;
        }
        public bool TryReadSE(out int value)
        {
            value = 0;
            if (!TryReadUE(out uint ue)) return false;
            long code = ue;
            long signed = (code & 1) == 0 ? -(code / 2) : (code + 1) / 2;
            if (signed < int.MinValue || signed > int.MaxValue) return false;
            value = (int)signed;
            return true;
        }
    }
}
