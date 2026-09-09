using System.Buffers.Binary;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

internal readonly record struct Mp4ByteRange(long Offset, long Length)
{
    public long End => checked(Offset + Length);
}

internal readonly record struct Mp4AudioSourceSample(long Offset, uint Size, uint Duration);

internal sealed class Mp4RecoveredAudioTrack
{
    public required string Codec { get; init; }
    public required string SampleEntryType { get; init; }
    public required byte[] SampleEntryBox { get; init; }
    public required uint Timescale { get; init; }
    public required int Channels { get; init; }
    public required int SampleRate { get; init; }
    public required int BitsPerSample { get; init; }
    public required int PreferredSamplesPerChunk { get; init; }
    public required string RecoveryMode { get; init; }
    public List<Mp4AudioSourceSample> Samples { get; } = [];
    public List<Mp4ByteRange> FixedPayloadRanges { get; } = [];
    public uint FixedSampleSize { get; init; }
    public uint FixedSampleDelta { get; init; }
    public long FixedSampleCount { get; set; }
    public ulong OutputPayloadOffset { get; set; }
    public long OutputPayloadBytes { get; set; }

    public long SampleCount => FixedSampleSize > 0 ? FixedSampleCount : Samples.Count;

    public ulong MediaDuration
    {
        get
        {
            if (FixedSampleSize > 0)
                return checked((ulong)FixedSampleCount * Math.Max(1u, FixedSampleDelta));
            ulong total = 0;
            foreach (Mp4AudioSourceSample sample in Samples)
                total = checked(total + sample.Duration);
            return total;
        }
    }
}

internal static class Mp4MultiTrackAudioRecoveryService
{
    private const int IoBufferSize = 1024 * 1024;
    private const int SearchBlockSize = 1024 * 1024;
    private const int MinimumCompressedFrames = 3;
    private const long MinimumCompressedAudioBytes = 512;
    private const long MinimumPcmAudioBytes = 4096;
    private const int MaxReferencePattern = 4096;

    private static readonly int[] AacSampleRates =
    [
        96000, 88200, 64000, 48000, 44100, 32000, 24000,
        22050, 16000, 12000, 11025, 8000, 7350
    ];

    // AC-3 frame sizes from A/52, in 16-bit words for 48/44.1/32 kHz.
    private static readonly ushort[,] Ac3FrameSizeWords =
    {
        { 64, 69, 96 }, { 64, 70, 96 }, { 80, 87, 120 }, { 80, 88, 120 },
        { 96, 104, 144 }, { 96, 105, 144 }, { 112, 121, 168 }, { 112, 122, 168 },
        { 128, 139, 192 }, { 128, 140, 192 }, { 160, 174, 240 }, { 160, 175, 240 },
        { 192, 208, 288 }, { 192, 209, 288 }, { 224, 243, 336 }, { 224, 244, 336 },
        { 256, 278, 384 }, { 256, 279, 384 }, { 320, 348, 480 }, { 320, 349, 480 },
        { 384, 417, 576 }, { 384, 418, 576 }, { 448, 487, 672 }, { 448, 488, 672 },
        { 512, 557, 768 }, { 512, 558, 768 }, { 640, 696, 960 }, { 640, 697, 960 },
        { 768, 835, 1152 }, { 768, 836, 1152 }, { 896, 975, 1344 }, { 896, 976, 1344 },
        { 1024, 1114, 1536 }, { 1024, 1115, 1536 }, { 1152, 1253, 1728 }, { 1152, 1254, 1728 },
        { 1280, 1393, 1920 }, { 1280, 1394, 1920 }
    };

    private readonly record struct AdtsHeader(
        int HeaderSize,
        int FrameSize,
        int SampleRate,
        int SampleRateIndex,
        int ChannelConfig,
        int Channels,
        int ObjectType,
        uint Duration);

    private readonly record struct Ac3Header(
        int FrameSize,
        int SampleRate,
        int Channels,
        int Fscod,
        int Bsid,
        int Bsmod,
        int Acmod,
        int LfeOn,
        int BitRateCode,
        uint Duration);

    public static Mp4RecoveredAudioTrack? Recover(
        FileStream source,
        IReadOnlyList<Mp4ByteRange> mediaRegions,
        IReadOnlyList<Mp4ByteRange> videoRanges,
        ReferenceVideoProfile? referenceProfile,
        Action<string>? progress)
    {
        if (mediaRegions.Count == 0)
            return null;

        List<Mp4ByteRange> residual = BuildResidualRanges(mediaRegions, videoRanges);
        if (residual.Count == 0)
            return null;

        string referenceCodec = NormalizeCodec(referenceProfile?.AudioCodec);
        progress?.Invoke($"Multi-track moov • {residual.Count:N0} ses adayı boşluk analiz ediliyor...");

        var candidates = new List<Mp4RecoveredAudioTrack>();
        if (referenceCodec is "AAC" or "")
        {
            Mp4RecoveredAudioTrack? aac = RecoverAdts(source, residual, referenceProfile);
            if (aac is not null)
                candidates.Add(aac);
        }

        if (referenceCodec is "AC-3" or "")
        {
            Mp4RecoveredAudioTrack? ac3 = RecoverAc3(source, residual, referenceProfile);
            if (ac3 is not null)
                candidates.Add(ac3);
        }

        if (referenceCodec == "PCM")
        {
            Mp4RecoveredAudioTrack? pcm = RecoverPcm(residual, referenceProfile);
            if (pcm is not null)
                candidates.Add(pcm);
        }

        if (referenceProfile is not null &&
            (referenceCodec is "AAC" or "E-AC-3" or "ALAC" or "OPUS") &&
            candidates.All(candidate => NormalizeCodec(candidate.Codec) != referenceCodec))
        {
            Mp4RecoveredAudioTrack? guided = RecoverByReferenceSamplePattern(residual, referenceProfile);
            if (guided is not null)
                candidates.Add(guided);
        }

        if (candidates.Count == 0)
            return null;

        Mp4RecoveredAudioTrack best = candidates
            .OrderByDescending(candidate => ReferenceMatches(candidate, referenceProfile))
            .ThenByDescending(candidate => candidate.OutputPayloadBytes > 0 ? candidate.OutputPayloadBytes : EstimatePayloadBytes(candidate))
            .ThenByDescending(candidate => candidate.SampleCount)
            .First();

        progress?.Invoke(
            $"Multi-track moov • {best.Codec} ses izi bulundu: {best.SampleCount:N0} sample, " +
            $"{best.SampleRate:N0} Hz, {best.Channels} kanal ({best.RecoveryMode}).");
        return best;
    }

    public static void WritePayload(FileStream source, Stream output, Mp4RecoveredAudioTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        track.OutputPayloadOffset = checked((ulong)output.Position);
        long start = output.Position;
        byte[] buffer = new byte[IoBufferSize];

        if (track.FixedSampleSize > 0)
        {
            foreach (Mp4ByteRange range in track.FixedPayloadRanges)
                CopyRange(source, output, range.Offset, range.Length, buffer);
        }
        else
        {
            foreach (Mp4AudioSourceSample sample in track.Samples)
                CopyRange(source, output, sample.Offset, sample.Size, buffer);
        }

        track.OutputPayloadBytes = checked(output.Position - start);
    }

    private static Mp4RecoveredAudioTrack? RecoverAdts(
        FileStream source,
        IReadOnlyList<Mp4ByteRange> residual,
        ReferenceVideoProfile? reference)
    {
        var samples = new List<Mp4AudioSourceSample>();
        AdtsHeader? identity = null;

        foreach (Mp4ByteRange range in residual)
        {
            long position = range.Offset;
            while (position + 7 <= range.End)
            {
                long runStart = FindNextAdtsRun(source, position, range.End, identity);
                if (runStart < 0)
                    break;

                long cursor = runStart;
                int runFrames = 0;
                AdtsHeader? runIdentity = identity;
                while (cursor + 7 <= range.End && TryReadAdtsHeader(source, cursor, range.End, out AdtsHeader header))
                {
                    if (runIdentity.HasValue && !SameAdtsIdentity(runIdentity.Value, header))
                        break;
                    runIdentity ??= header;
                    int rawSize = header.FrameSize - header.HeaderSize;
                    if (rawSize <= 0)
                        break;
                    samples.Add(new Mp4AudioSourceSample(
                        checked(cursor + header.HeaderSize),
                        checked((uint)rawSize),
                        header.Duration));
                    cursor = checked(cursor + header.FrameSize);
                    runFrames++;
                }

                if (runFrames < MinimumCompressedFrames)
                {
                    if (runFrames > 0)
                        samples.RemoveRange(samples.Count - runFrames, runFrames);
                    position = runStart + 1;
                }
                else
                {
                    identity ??= runIdentity;
                    position = cursor;
                }
            }
        }

        if (!identity.HasValue || samples.Count < MinimumCompressedFrames || samples.Sum(sample => (long)sample.Size) < MinimumCompressedAudioBytes)
            return null;

        AdtsHeader id = identity.Value;
        if (reference is not null && NormalizeCodec(reference.AudioCodec) == "AAC")
        {
            if (reference.AudioSampleRate > 0 && Math.Abs(reference.AudioSampleRate - id.SampleRate) > 1)
                return null;
            if (reference.AudioChannels > 0 && id.Channels > 0 && reference.AudioChannels != id.Channels)
                return null;
        }

        byte[] sampleEntry = TryUseReferenceSampleEntry(reference, "AAC", "mp4a")
            ?? BuildAacSampleEntry(id.SampleRate, id.SampleRateIndex, id.ChannelConfig, id.Channels, id.ObjectType);

        uint timescale = reference is { AudioTimescale: > 0 } ? reference.AudioTimescale : checked((uint)id.SampleRate);
        int preferred = Math.Clamp(reference?.AudioPreferredSamplesPerChunk ?? 32, 1, 4096);
        if (timescale != id.SampleRate)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                Mp4AudioSourceSample sample = samples[i];
                uint scaled = ScaleDuration(sample.Duration, checked((uint)id.SampleRate), timescale);
                samples[i] = sample with { Duration = scaled };
            }
        }

        var track = new Mp4RecoveredAudioTrack
        {
            Codec = "AAC",
            SampleEntryType = "mp4a",
            SampleEntryBox = sampleEntry,
            Timescale = timescale,
            Channels = reference is { AudioChannels: > 0 } ? reference.AudioChannels : Math.Max(1, id.Channels),
            SampleRate = id.SampleRate,
            BitsPerSample = reference is { AudioBitsPerSample: > 0 } ? reference.AudioBitsPerSample : 16,
            PreferredSamplesPerChunk = preferred,
            RecoveryMode = "ADTS syncframe + raw AAC access-unit carving"
        };
        track.Samples.AddRange(samples);
        return track;
    }

    private static Mp4RecoveredAudioTrack? RecoverAc3(
        FileStream source,
        IReadOnlyList<Mp4ByteRange> residual,
        ReferenceVideoProfile? reference)
    {
        var samples = new List<Mp4AudioSourceSample>();
        Ac3Header? identity = null;

        foreach (Mp4ByteRange range in residual)
        {
            long position = range.Offset;
            while (position + 8 <= range.End)
            {
                long runStart = FindNextAc3Run(source, position, range.End, identity);
                if (runStart < 0)
                    break;

                long cursor = runStart;
                int runFrames = 0;
                Ac3Header? runIdentity = identity;
                while (cursor + 8 <= range.End && TryReadAc3Header(source, cursor, range.End, out Ac3Header header))
                {
                    if (runIdentity.HasValue && !SameAc3Identity(runIdentity.Value, header))
                        break;
                    runIdentity ??= header;
                    samples.Add(new Mp4AudioSourceSample(cursor, checked((uint)header.FrameSize), header.Duration));
                    cursor = checked(cursor + header.FrameSize);
                    runFrames++;
                }

                if (runFrames < MinimumCompressedFrames)
                {
                    if (runFrames > 0)
                        samples.RemoveRange(samples.Count - runFrames, runFrames);
                    position = runStart + 1;
                }
                else
                {
                    identity ??= runIdentity;
                    position = cursor;
                }
            }
        }

        if (!identity.HasValue || samples.Count < MinimumCompressedFrames || samples.Sum(sample => (long)sample.Size) < MinimumCompressedAudioBytes)
            return null;

        Ac3Header id = identity.Value;
        if (reference is not null && NormalizeCodec(reference.AudioCodec) == "AC-3")
        {
            if (reference.AudioSampleRate > 0 && reference.AudioSampleRate != id.SampleRate)
                return null;
            if (reference.AudioChannels > 0 && reference.AudioChannels != id.Channels)
                return null;
        }

        byte[] sampleEntry = TryUseReferenceSampleEntry(reference, "AC-3", "ac-3") ?? BuildAc3SampleEntry(id);
        uint timescale = reference is { AudioTimescale: > 0 } ? reference.AudioTimescale : checked((uint)id.SampleRate);
        if (timescale != id.SampleRate)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                Mp4AudioSourceSample sample = samples[i];
                samples[i] = sample with { Duration = ScaleDuration(sample.Duration, checked((uint)id.SampleRate), timescale) };
            }
        }

        var track = new Mp4RecoveredAudioTrack
        {
            Codec = "AC-3",
            SampleEntryType = "ac-3",
            SampleEntryBox = sampleEntry,
            Timescale = timescale,
            Channels = id.Channels,
            SampleRate = id.SampleRate,
            BitsPerSample = 16,
            PreferredSamplesPerChunk = Math.Clamp(reference?.AudioPreferredSamplesPerChunk ?? 16, 1, 4096),
            RecoveryMode = "AC-3 syncframe carving"
        };
        track.Samples.AddRange(samples);
        return track;
    }

    private static Mp4RecoveredAudioTrack? RecoverPcm(
        IReadOnlyList<Mp4ByteRange> residual,
        ReferenceVideoProfile? reference)
    {
        if (reference is null || NormalizeCodec(reference.AudioCodec) != "PCM" ||
            reference.AudioSampleEntryBox is not { Length: >= 36 } sampleEntry ||
            reference.AudioSampleRate <= 0 || reference.AudioChannels <= 0)
            return null;

        uint bytesPerFrame = reference.AudioFixedSampleSize;
        if (bytesPerFrame == 0)
        {
            int bits = reference.AudioBitsPerSample > 0 ? reference.AudioBitsPerSample : 16;
            long computed = (long)reference.AudioChannels * ((bits + 7) / 8);
            if (computed <= 0 || computed > uint.MaxValue)
                return null;
            bytesPerFrame = (uint)computed;
        }
        if (bytesPerFrame == 0)
            return null;

        var accepted = new List<Mp4ByteRange>();
        long totalBytes = 0;
        foreach (Mp4ByteRange range in residual)
        {
            long aligned = range.Length - range.Length % bytesPerFrame;
            if (aligned < bytesPerFrame * 64L)
                continue;
            accepted.Add(new Mp4ByteRange(range.Offset, aligned));
            totalBytes = checked(totalBytes + aligned);
        }
        if (totalBytes < Math.Max(MinimumPcmAudioBytes, bytesPerFrame * 512L))
            return null;

        long sampleCount = totalBytes / bytesPerFrame;
        if (sampleCount <= 0 || sampleCount > uint.MaxValue)
            return null;

        var track = new Mp4RecoveredAudioTrack
        {
            Codec = "PCM",
            SampleEntryType = string.IsNullOrWhiteSpace(reference.AudioSampleEntryType) ? "lpcm" : reference.AudioSampleEntryType,
            SampleEntryBox = sampleEntry.ToArray(),
            Timescale = reference.AudioTimescale > 0 ? reference.AudioTimescale : checked((uint)reference.AudioSampleRate),
            Channels = reference.AudioChannels,
            SampleRate = reference.AudioSampleRate,
            BitsPerSample = reference.AudioBitsPerSample > 0 ? reference.AudioBitsPerSample : checked((int)(bytesPerFrame * 8u / (uint)reference.AudioChannels)),
            PreferredSamplesPerChunk = Math.Clamp(reference.AudioPreferredSamplesPerChunk, 1, 4096),
            RecoveryMode = "referans PCM sample geometrisi + video dışı mdat extent'leri",
            FixedSampleSize = bytesPerFrame,
            FixedSampleDelta = reference.AudioSampleDelta > 0 ? reference.AudioSampleDelta : 1,
            FixedSampleCount = sampleCount
        };
        track.FixedPayloadRanges.AddRange(accepted);
        return track;
    }

    private static Mp4RecoveredAudioTrack? RecoverByReferenceSamplePattern(
        IReadOnlyList<Mp4ByteRange> residual,
        ReferenceVideoProfile reference)
    {
        if (reference.AudioSampleEntryBox is not { Length: >= 16 } sampleEntry ||
            reference.AudioSampleSizePattern.Length == 0 ||
            reference.AudioTimescale == 0 || reference.AudioSampleDelta == 0)
            return null;

        uint[] pattern = reference.AudioSampleSizePattern
            .Where(size => size > 0 && size <= 16 * 1024 * 1024)
            .Take(MaxReferencePattern)
            .ToArray();
        if (pattern.Length == 0)
            return null;

        var samples = new List<Mp4AudioSourceSample>();
        int patternIndex = 0;
        var timing = new ReferenceDurationCursor(reference.AudioTimeToSampleRuns, reference.AudioSampleDelta);
        foreach (Mp4ByteRange range in residual)
        {
            if (!TrySplitExactPattern(range, pattern, patternIndex, reference.AudioPreferredSamplesPerChunk, out List<(long Offset, uint Size)> split, out int nextIndex))
                continue;
            foreach ((long offset, uint size) in split)
                samples.Add(new Mp4AudioSourceSample(offset, size, timing.Next()));
            patternIndex = nextIndex;
        }

        if (samples.Count < MinimumCompressedFrames || samples.Sum(sample => (long)sample.Size) < MinimumCompressedAudioBytes)
            return null;

        var track = new Mp4RecoveredAudioTrack
        {
            Codec = reference.AudioCodec,
            SampleEntryType = reference.AudioSampleEntryType,
            SampleEntryBox = sampleEntry.ToArray(),
            Timescale = reference.AudioTimescale,
            Channels = reference.AudioChannels,
            SampleRate = reference.AudioSampleRate,
            BitsPerSample = reference.AudioBitsPerSample,
            PreferredSamplesPerChunk = Math.Clamp(reference.AudioPreferredSamplesPerChunk, 1, 4096),
            RecoveryMode = "aynı cihaz referansındaki kesin stsz/stsc sample deseni"
        };
        track.Samples.AddRange(samples);
        return track;
    }

    private static bool TrySplitExactPattern(
        Mp4ByteRange range,
        IReadOnlyList<uint> pattern,
        int preferredStart,
        int preferredSamplesPerChunk,
        out List<(long Offset, uint Size)> split,
        out int nextPatternIndex)
    {
        split = [];
        nextPatternIndex = preferredStart;
        if (range.Length <= 0 || pattern.Count == 0)
            return false;

        int attempts = Math.Min(pattern.Count, 256);
        int expectedChunk = Math.Clamp(preferredSamplesPerChunk, 1, 4096);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int index = (preferredStart + attempt) % pattern.Count;
            long sum = 0;
            int count = 0;
            while (sum < range.Length && count < expectedChunk * 4 + 32)
            {
                uint size = pattern[index];
                if (size == 0 || size > range.Length - sum)
                    break;
                sum += size;
                count++;
                index = (index + 1) % pattern.Count;
            }
            if (sum != range.Length || count == 0)
                continue;

            long offset = range.Offset;
            index = (preferredStart + attempt) % pattern.Count;
            for (int i = 0; i < count; i++)
            {
                uint size = pattern[index];
                split.Add((offset, size));
                offset = checked(offset + size);
                index = (index + 1) % pattern.Count;
            }
            nextPatternIndex = index;
            return true;
        }
        return false;
    }

    private static List<Mp4ByteRange> BuildResidualRanges(
        IReadOnlyList<Mp4ByteRange> mediaRegions,
        IReadOnlyList<Mp4ByteRange> videoRanges)
    {
        List<Mp4ByteRange> videos = Coalesce(videoRanges);
        var result = new List<Mp4ByteRange>();
        foreach (Mp4ByteRange media in mediaRegions.OrderBy(range => range.Offset))
        {
            long cursor = media.Offset;
            long mediaEnd = media.End;
            foreach (Mp4ByteRange video in videos)
            {
                if (video.End <= cursor)
                    continue;
                if (video.Offset >= mediaEnd)
                    break;
                long start = Math.Max(video.Offset, media.Offset);
                if (start > cursor)
                    AddResidual(result, cursor, start - cursor);
                cursor = Math.Max(cursor, Math.Min(mediaEnd, video.End));
                if (cursor >= mediaEnd)
                    break;
            }
            if (cursor < mediaEnd)
                AddResidual(result, cursor, mediaEnd - cursor);
        }
        return result;
    }

    private static void AddResidual(List<Mp4ByteRange> result, long offset, long length)
    {
        if (length >= 7)
            result.Add(new Mp4ByteRange(offset, length));
    }

    private static List<Mp4ByteRange> Coalesce(IReadOnlyList<Mp4ByteRange> ranges)
    {
        var sorted = ranges.Where(range => range.Length > 0).OrderBy(range => range.Offset).ToArray();
        var result = new List<Mp4ByteRange>();
        foreach (Mp4ByteRange range in sorted)
        {
            if (result.Count == 0)
            {
                result.Add(range);
                continue;
            }
            Mp4ByteRange last = result[^1];
            long lastEnd = last.End;
            bool touches = range.Offset <= lastEnd || (lastEnd < long.MaxValue && range.Offset == lastEnd + 1);
            if (touches)
            {
                long end = Math.Max(lastEnd, range.End);
                result[^1] = new Mp4ByteRange(last.Offset, end - last.Offset);
            }
            else
            {
                result.Add(range);
            }
        }
        return result;
    }

    private static long FindNextAdtsRun(FileStream source, long start, long end, AdtsHeader? identity)
    {
        byte[] buffer = new byte[SearchBlockSize + 16];
        int carry = 0;
        long position = start;
        while (position < end)
        {
            int request = (int)Math.Min(SearchBlockSize, end - position);
            source.Position = position;
            int read = source.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            for (int i = 0; i + 7 <= count; i++)
            {
                if (buffer[i] != 0xFF || (buffer[i + 1] & 0xF6) != 0xF0)
                    continue;
                long absolute = position - carry + i;
                if (ValidateAdtsRun(source, absolute, end, identity, MinimumCompressedFrames))
                    return absolute;
            }
            carry = Math.Min(16, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return -1;
    }

    private static bool ValidateAdtsRun(FileStream source, long offset, long end, AdtsHeader? expected, int required)
    {
        AdtsHeader? identity = expected;
        long position = offset;
        for (int i = 0; i < required; i++)
        {
            if (!TryReadAdtsHeader(source, position, end, out AdtsHeader header))
                return false;
            if (identity.HasValue && !SameAdtsIdentity(identity.Value, header))
                return false;
            identity ??= header;
            position = checked(position + header.FrameSize);
        }
        return true;
    }

    private static bool TryReadAdtsHeader(FileStream source, long offset, long end, out AdtsHeader header)
    {
        header = default;
        if (offset < 0 || offset + 7 > end)
            return false;
        Span<byte> data = stackalloc byte[9];
        source.Position = offset;
        int request = (int)Math.Min(data.Length, end - offset);
        int read = source.Read(data[..request]);
        if (read < 7)
            return false;
        if (data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
            return false;

        bool protectionAbsent = (data[1] & 0x01) != 0;
        int headerSize = protectionAbsent ? 7 : 9;
        if (read < headerSize)
            return false;
        int objectType = ((data[2] >> 6) & 0x03) + 1;
        int sampleRateIndex = (data[2] >> 2) & 0x0F;
        if (sampleRateIndex < 0 || sampleRateIndex >= AacSampleRates.Length)
            return false;
        int sampleRate = AacSampleRates[sampleRateIndex];
        int channelConfig = ((data[2] & 0x01) << 2) | ((data[3] >> 6) & 0x03);
        int channels = channelConfig switch { 0 => 0, 7 => 8, _ => channelConfig };
        int frameSize = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
        int rawBlocks = data[6] & 0x03;
        if (frameSize <= headerSize || frameSize > 8192 || offset + frameSize > end)
            return false;
        header = new AdtsHeader(headerSize, frameSize, sampleRate, sampleRateIndex, channelConfig, channels, objectType, checked((uint)(1024 * (rawBlocks + 1))));
        return true;
    }

    private static bool SameAdtsIdentity(AdtsHeader left, AdtsHeader right) =>
        left.SampleRate == right.SampleRate && left.ChannelConfig == right.ChannelConfig && left.ObjectType == right.ObjectType;

    private static long FindNextAc3Run(FileStream source, long start, long end, Ac3Header? identity)
    {
        byte[] buffer = new byte[SearchBlockSize + 16];
        int carry = 0;
        long position = start;
        while (position < end)
        {
            int request = (int)Math.Min(SearchBlockSize, end - position);
            source.Position = position;
            int read = source.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            for (int i = 0; i + 8 <= count; i++)
            {
                if (buffer[i] != 0x0B || buffer[i + 1] != 0x77)
                    continue;
                long absolute = position - carry + i;
                if (ValidateAc3Run(source, absolute, end, identity, MinimumCompressedFrames))
                    return absolute;
            }
            carry = Math.Min(16, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return -1;
    }

    private static bool ValidateAc3Run(FileStream source, long offset, long end, Ac3Header? expected, int required)
    {
        Ac3Header? identity = expected;
        long position = offset;
        for (int i = 0; i < required; i++)
        {
            if (!TryReadAc3Header(source, position, end, out Ac3Header header))
                return false;
            if (identity.HasValue && !SameAc3Identity(identity.Value, header))
                return false;
            identity ??= header;
            position = checked(position + header.FrameSize);
        }
        return true;
    }

    private static bool TryReadAc3Header(FileStream source, long offset, long end, out Ac3Header header)
    {
        header = default;
        if (offset < 0 || offset + 8 > end)
            return false;
        Span<byte> data = stackalloc byte[8];
        source.Position = offset;
        if (source.Read(data) != data.Length || data[0] != 0x0B || data[1] != 0x77)
            return false;

        int fscod = data[4] >> 6;
        int frmsizecod = data[4] & 0x3F;
        if (fscod > 2 || frmsizecod >= 38)
            return false;
        int bsid = data[5] >> 3;
        if (bsid > 10)
            return false; // E-AC-3 is handled only through exact reference sample patterns.

        int sampleRate = fscod switch { 0 => 48000, 1 => 44100, 2 => 32000, _ => 0 };
        int frameSize = Ac3FrameSizeWords[frmsizecod, fscod] * 2;
        if (frameSize < 64 || offset + frameSize > end)
            return false;

        var bits = new HeaderBitReader(data);
        if (!bits.TrySkip(40) || !bits.TryRead(5, out uint parsedBsid) || !bits.TryRead(3, out uint bsmod) ||
            !bits.TryRead(3, out uint acmod))
            return false;
        if (parsedBsid != bsid)
            return false;
        if ((acmod & 0x01) != 0 && acmod != 1 && !bits.TrySkip(2))
            return false;
        if ((acmod & 0x04) != 0 && !bits.TrySkip(2))
            return false;
        if (acmod == 2 && !bits.TrySkip(2))
            return false;
        if (!bits.TryRead(1, out uint lfeon))
            return false;

        int channels = acmod switch
        {
            0 => 2,
            1 => 1,
            2 => 2,
            3 => 3,
            4 => 3,
            5 => 4,
            6 => 4,
            7 => 5,
            _ => 2
        } + (lfeon != 0 ? 1 : 0);

        header = new Ac3Header(
            frameSize,
            sampleRate,
            channels,
            fscod,
            bsid,
            checked((int)bsmod),
            checked((int)acmod),
            checked((int)lfeon),
            frmsizecod >> 1,
            1536);
        return true;
    }

    private static bool SameAc3Identity(Ac3Header left, Ac3Header right) =>
        left.SampleRate == right.SampleRate && left.Bsid == right.Bsid && left.Acmod == right.Acmod && left.LfeOn == right.LfeOn;

    private static byte[]? TryUseReferenceSampleEntry(ReferenceVideoProfile? reference, string codec, string expectedType)
    {
        if (reference is null || NormalizeCodec(reference.AudioCodec) != NormalizeCodec(codec) ||
            !string.Equals(reference.AudioSampleEntryType, expectedType, StringComparison.OrdinalIgnoreCase) ||
            reference.AudioSampleEntryBox is not { Length: >= 16 and <= 1048576 } sampleEntry)
            return null;
        return sampleEntry.ToArray();
    }

    private static byte[] BuildAacSampleEntry(int sampleRate, int sampleRateIndex, int channelConfig, int channels, int objectType)
    {
        objectType = Math.Clamp(objectType, 1, 31);
        channelConfig = Math.Clamp(channelConfig, 1, 7);
        channels = Math.Clamp(channels, 1, 8);
        byte[] asc =
        [
            (byte)((objectType << 3) | (sampleRateIndex >> 1)),
            (byte)(((sampleRateIndex & 1) << 7) | (channelConfig << 3))
        ];
        byte[] esds = BuildEsds(asc);
        return Box("mp4a", writer =>
        {
            writer.Write(new byte[6]);
            WriteU16(writer, 1);
            WriteU16(writer, 0); WriteU16(writer, 0); WriteU32(writer, 0);
            WriteU16(writer, checked((ushort)channels));
            WriteU16(writer, 16);
            WriteU16(writer, 0); WriteU16(writer, 0);
            WriteU32(writer, checked((uint)sampleRate << 16));
            writer.Write(esds);
        });
    }

    private static byte[] BuildEsds(byte[] audioSpecificConfig)
    {
        byte[] decoderSpecific = Descriptor(0x05, audioSpecificConfig);
        using var decoderBody = new MemoryStream();
        decoderBody.WriteByte(0x40); // MPEG-4 Audio
        decoderBody.WriteByte(0x15); // AudioStream, upstream=0, reserved=1
        decoderBody.Write(new byte[] { 0, 0, 0 }); // bufferSizeDB
        decoderBody.Write(new byte[8]); // max/avg bitrate unknown
        decoderBody.Write(decoderSpecific);
        byte[] decoderConfig = Descriptor(0x04, decoderBody.ToArray());
        byte[] slConfig = Descriptor(0x06, new byte[] { 0x02 });

        using var esBody = new MemoryStream();
        esBody.WriteByte(0); esBody.WriteByte(1); // ES_ID
        esBody.WriteByte(0); // flags
        esBody.Write(decoderConfig);
        esBody.Write(slConfig);
        byte[] esDescriptor = Descriptor(0x03, esBody.ToArray());
        return Box("esds", writer =>
        {
            writer.Write(new byte[4]);
            writer.Write(esDescriptor);
        });
    }

    private static byte[] Descriptor(byte tag, byte[] body)
    {
        using var stream = new MemoryStream();
        stream.WriteByte(tag);
        WriteDescriptorLength(stream, body.Length);
        stream.Write(body);
        return stream.ToArray();
    }

    private static void WriteDescriptorLength(Stream stream, int length)
    {
        if (length < 0 || length > 0x0FFFFFFF)
            throw new InvalidDataException("ESDS descriptor boyutu geçersiz.");
        Span<byte> encoded = stackalloc byte[4];
        int count = 0;
        encoded[count++] = (byte)(length & 0x7F);
        length >>= 7;
        while (length > 0)
        {
            encoded[count++] = (byte)(0x80 | (length & 0x7F));
            length >>= 7;
        }
        for (int i = count - 1; i >= 0; i--)
            stream.WriteByte(encoded[i]);
    }

    private static byte[] BuildAc3SampleEntry(Ac3Header header)
    {
        uint dac3 = ((uint)header.Fscod << 22) |
                    ((uint)header.Bsid << 17) |
                    ((uint)header.Bsmod << 14) |
                    ((uint)header.Acmod << 11) |
                    ((uint)header.LfeOn << 10) |
                    ((uint)header.BitRateCode << 5);
        byte[] dac3Payload = [(byte)(dac3 >> 16), (byte)(dac3 >> 8), (byte)dac3];
        return Box("ac-3", writer =>
        {
            writer.Write(new byte[6]);
            WriteU16(writer, 1);
            WriteU16(writer, 0); WriteU16(writer, 0); WriteU32(writer, 0);
            WriteU16(writer, checked((ushort)Math.Clamp(header.Channels, 1, ushort.MaxValue)));
            WriteU16(writer, 16);
            WriteU16(writer, 0); WriteU16(writer, 0);
            WriteU32(writer, checked((uint)header.SampleRate << 16));
            writer.Write(Box("dac3", box => box.Write(dac3Payload)));
        });
    }

    private static byte[] Box(string type, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteU32(writer, 0);
        writer.Write(Encoding.ASCII.GetBytes(type));
        body(writer);
        writer.Flush();
        if (stream.Length > uint.MaxValue)
            throw new InvalidDataException($"{type} kutusu 32-bit sınırını aşıyor.");
        stream.Position = 0;
        WriteU32(writer, checked((uint)stream.Length));
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteU16(BinaryWriter writer, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static void WriteU32(BinaryWriter writer, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        writer.Write(bytes);
    }

    private static uint ScaleDuration(uint duration, uint sourceTimescale, uint targetTimescale)
    {
        if (duration == 0 || sourceTimescale == 0 || targetTimescale == 0 || sourceTimescale == targetTimescale)
            return Math.Max(1u, duration);
        ulong scaled = ((ulong)duration * targetTimescale + sourceTimescale / 2u) / sourceTimescale;
        return checked((uint)Math.Clamp(scaled, 1UL, (ulong)uint.MaxValue));
    }

    private static string NormalizeCodec(string? codec)
    {
        if (string.IsNullOrWhiteSpace(codec) || codec.Equals("Yok/Bilinmiyor", StringComparison.OrdinalIgnoreCase))
            return string.Empty;
        string value = codec.Trim().ToUpperInvariant();
        return value switch
        {
            "AAC" => "AAC",
            "AC3" or "AC-3" => "AC-3",
            "EAC3" or "E-AC-3" => "E-AC-3",
            "PCM" or "LPCM" => "PCM",
            "OPUS" => "OPUS",
            "ALAC" => "ALAC",
            _ => value
        };
    }

    private static bool ReferenceMatches(Mp4RecoveredAudioTrack candidate, ReferenceVideoProfile? reference) =>
        reference is not null && NormalizeCodec(candidate.Codec) == NormalizeCodec(reference.AudioCodec);

    private static long EstimatePayloadBytes(Mp4RecoveredAudioTrack track)
    {
        if (track.FixedSampleSize > 0)
            return checked(track.FixedSampleCount * track.FixedSampleSize);
        long total = 0;
        foreach (Mp4AudioSourceSample sample in track.Samples)
            total = checked(total + sample.Size);
        return total;
    }

    private static void CopyRange(FileStream source, Stream output, long offset, long length, byte[] buffer)
    {
        if (offset < 0 || length < 0 || offset > source.Length || length > source.Length - offset)
            throw new InvalidDataException("Ses sample extent'i kaynak mdat sınırının dışında.");
        source.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException("Ses sample verisi beklenenden önce sona erdi.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private sealed class ReferenceDurationCursor
    {
        private readonly Mp4TimeToSampleRun[] _runs;
        private readonly uint _fallback;
        private int _runIndex;
        private uint _remaining;

        public ReferenceDurationCursor(Mp4TimeToSampleRun[] runs, uint fallback)
        {
            _runs = runs.Where(run => run.Count > 0 && run.Delta > 0).ToArray();
            _fallback = Math.Max(1u, fallback);
            if (_runs.Length > 0)
                _remaining = _runs[0].Count;
        }

        public uint Next()
        {
            if (_runs.Length == 0)
                return _fallback;
            if (_remaining == 0)
            {
                _runIndex = (_runIndex + 1) % _runs.Length;
                _remaining = _runs[_runIndex].Count;
            }
            _remaining--;
            return _runs[_runIndex].Delta;
        }
    }

    private sealed class HeaderBitReader
    {
        private readonly byte[] _data;
        private int _position;

        public HeaderBitReader(ReadOnlySpan<byte> data) => _data = data.ToArray();

        public bool TrySkip(int count)
        {
            if (count < 0 || _position + count > _data.Length * 8)
                return false;
            _position += count;
            return true;
        }

        public bool TryRead(int count, out uint value)
        {
            value = 0;
            if (count < 0 || count > 32 || _position + count > _data.Length * 8)
                return false;
            for (int i = 0; i < count; i++)
            {
                int index = _position >> 3;
                int shift = 7 - (_position & 7);
                value = (value << 1) | (uint)((_data[index] >> shift) & 1);
                _position++;
            }
            return true;
        }
    }
}
