using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class Mp4MoovReconstructionService
{
    private const int IoBufferSize = 1024 * 1024;
    private const int SearchBlockSize = 4 * 1024 * 1024;
    private const int ResyncWindow = 16 * 1024 * 1024;
    private const uint DefaultTimescale = 90000;
    private const uint DefaultSampleDelta = 3000;
    private const int MaxParameterSetBytes = 1024 * 1024;

    private enum CodecKind { H264, H265 }
    private readonly record struct MdatRegion(long BoxOffset, long PayloadOffset, long PayloadLength);
    private readonly record struct NalCandidate(long PrefixOffset, int PrefixSize, uint Length, CodecKind Codec, int Type);
    private readonly record struct Sample(uint Size, bool Key, uint Duration = 0, int CompositionOffset = 0);
    private readonly record struct Geometry(int Width, int Height, uint Timescale, uint SampleDelta, int SamplesPerChunk);
    private readonly record struct HevcProfile(byte Space, byte Tier, byte Idc, uint Compat, ulong Constraints, byte Level);

    private sealed class ScanState
    {
        public required CodecKind Codec { get; init; }
        public byte[]? Vps { get; set; }
        public byte[]? Sps { get; set; }
        public byte[]? Pps { get; set; }
        public List<Sample> Samples { get; } = [];
        public uint CurrentSize { get; set; }
        public bool CurrentHasVcl { get; set; }
        public bool CurrentKey { get; set; }
        public long CurrentPayloadStart { get; set; } = -1;
        public long AcceptedNals { get; set; }
        public long SkippedBytes { get; set; }
        public List<Mp4ByteRange> VideoSourceRanges { get; } = [];
        public ReferenceVideoProfile? ReferenceProfile { get; init; }
        public bool ReferenceParameterSetsUsed { get; set; }
        public bool ReferenceTimingUsed { get; set; }
    }

    public static MediaRepairResult Reconstruct(string sourcePath, string destinationPath, Action<string>? progress, ReferenceVideoProfile? referenceProfile = null)
    {
        try
        {
            progress?.Invoke("MP4/MOV moov reconstruction • mdat medya alanları aranıyor...");

            using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferSize, FileOptions.SequentialScan);
            List<MdatRegion> regions = FindMdatRegions(source);
            if (regions.Count == 0)
                throw new InvalidDataException("Yeniden oluşturulabilecek mdat medya alanı bulunamadı.");

            referenceProfile = NormalizeReferenceProfile(referenceProfile);
            NalCandidate? anchor = FindCodecAnchor(source, regions, referenceProfile);
            if (anchor is null)
                throw new InvalidDataException(referenceProfile is null
                    ? "mdat içinde SPS/PPS tabanlı doğrulanabilir H.264/H.265 akışı bulunamadı."
                    : "Referans profile rağmen mdat içinde doğrulanabilir H.264/H.265 NAL zinciri bulunamadı.");

            var state = new ScanState { Codec = anchor.Value.Codec, ReferenceProfile = referenceProfile };
            byte[] ftyp = ReadOrCreateFtyp(source, state.Codec, referenceProfile);
            ulong mediaOffset = checked((ulong)ftyp.Length + 16UL);
            long videoPayloadBytes;
            long payloadBytes;
            ulong moovOffset;
            Geometry geometry;
            Mp4RecoveredAudioTrack? audioTrack = null;

            string? parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);

            // Stream recovered media directly into one mdat. Video is normalized first;
            // a validated audio track is appended as a second media extent and receives
            // its own independent sample/chunk/time tables in the rebuilt moov.
            using (var output = new FileStream(
                       destinationPath,
                       FileMode.Create,
                       FileAccess.ReadWrite,
                       FileShare.None,
                       IoBufferSize,
                       FileOptions.None))
            {
                output.Write(ftyp);
                WriteMdatHeader(output, 16UL); // patched after the streaming pass
                progress?.Invoke(state.Codec == CodecKind.H264
                    ? "H.264 NAL ve access-unit sınırları yeniden çıkarılıyor..."
                    : "H.265/HEVC NAL ve access-unit sınırları yeniden çıkarılıyor...");
                ScanVideo(source, output, regions, anchor.Value, state, progress);
                FinalizeSample(state, output);
                videoPayloadBytes = checked(output.Position - (long)mediaOffset);

                ApplyReferenceFallback(state);
                ValidateState(state, videoPayloadBytes);
                geometry = ResolveGeometry(state);
                ApplyVideoTiming(state, geometry);

                long videoEnd = output.Position;
                try
                {
                    List<Mp4ByteRange> mediaRanges = regions
                        .Select(region => new Mp4ByteRange(region.PayloadOffset, region.PayloadLength))
                        .ToList();
                    audioTrack = Mp4MultiTrackAudioRecoveryService.Recover(
                        source,
                        mediaRanges,
                        state.VideoSourceRanges,
                        referenceProfile,
                        progress);
                    if (audioTrack is not null)
                    {
                        progress?.Invoke($"{audioTrack.Codec} ses payload'ı yeni mdat içine aktarılıyor...");
                        Mp4MultiTrackAudioRecoveryService.WritePayload(source, output, audioTrack);
                        ValidateAudioTrack(audioTrack);
                    }
                }
                catch (Exception audioError)
                {
                    output.SetLength(videoEnd);
                    output.Position = videoEnd;
                    audioTrack = null;
                    progress?.Invoke($"Ses izi güvenli doğrulamayı geçemedi; video-only onarıma devam ediliyor: {audioError.Message}");
                }

                payloadBytes = checked(output.Position - (long)mediaOffset);
                progress?.Invoke(audioTrack is null
                    ? $"{geometry.Width}×{geometry.Height} video izi doğrulandı; video moov/trak/stbl tabloları üretiliyor..."
                    : $"{geometry.Width}×{geometry.Height} video + {audioTrack.Codec} ses doğrulandı; multi-track moov tabloları üretiliyor...");
                byte[] streamingMoov = BuildMoov(state, geometry, mediaOffset, audioTrack);
                moovOffset = checked(mediaOffset + (ulong)payloadBytes);

                PatchMdatSize(output, ftyp.Length, checked((ulong)payloadBytes + 16UL));
                output.Position = checked((long)moovOffset);
                output.Write(streamingMoov);
                output.Flush(true);
            }

            // Fast-start/progressive layout: ftyp -> moov -> mdat. The first pass above intentionally
            // streams payload without buffering the whole recovered video in RAM. We then move only the
            // already-normalized mdat payload once and rebuild offsets against the final moov size.
            progress?.Invoke("MP4 progressive-download düzeni hazırlanıyor (ftyp → moov → mdat)...");
            ulong streamedMediaOffset = mediaOffset;
            byte[] progressiveMoov = BuildProgressiveMoov(
                state,
                geometry,
                ftyp.Length,
                videoPayloadBytes,
                audioTrack,
                out ulong progressiveMediaOffset);
            string fastStartTempPath = destinationPath + ".nsx-faststart.tmp";
            TryDelete(fastStartTempPath);
            using (var payloadSource = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferSize, FileOptions.SequentialScan))
            using (var progressiveOutput = new FileStream(fastStartTempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, IoBufferSize, FileOptions.SequentialScan))
            {
                progressiveOutput.Write(ftyp);
                progressiveOutput.Write(progressiveMoov);
                WriteMdatHeader(progressiveOutput, checked((ulong)payloadBytes + 16UL));
                payloadSource.Position = checked((long)streamedMediaOffset);
                CopyExactly(payloadSource, progressiveOutput, payloadBytes);
                progressiveOutput.Flush(true);
            }
            File.Move(fastStartTempPath, destinationPath, true);
            moovOffset = checked((ulong)ftyp.Length);
            mediaOffset = progressiveMediaOffset;

            progress?.Invoke("Yeni multi-track moov ve sample tabloları progressive düzende yazıldı; kapsayıcı doğrulanıyor...");
            ValidateOutput(destinationPath, state, mediaOffset, videoPayloadBytes, payloadBytes, moovOffset, audioTrack);

            string codec = state.Codec == CodecKind.H264 ? "H.264/AVC" : "H.265/HEVC";
            string skipped = state.SkippedBytes > 0
                ? $" {RecoveryFileItem.FormatBytes(state.SkippedBytes)} video dışı/bozuk alan ayrıştırıldı."
                : string.Empty;
            string referenceText = state.ReferenceParameterSetsUsed || state.ReferenceTimingUsed
                ? $" Referans profil uygulandı ({state.ReferenceProfile!.CodecText}, {geometry.Timescale:N0} timescale; ses profili: {state.ReferenceProfile.AudioCodec})."
                : string.Empty;
            string audioText = audioTrack is null
                ? " Güvenilir ses sample sınırı bulunamadığı için yanlış ses track'i üretilmedi."
                : $" {audioTrack.Codec} ses track'i yeniden kuruldu ({audioTrack.SampleCount:N0} sample, {audioTrack.SampleRate:N0} Hz, {audioTrack.Channels} kanal; {audioTrack.RecoveryMode}).";
            return new MediaRepairResult(
                true,
                $"Kayıp moov yeniden oluşturuldu. {codec}, {geometry.Width}×{geometry.Height}, " +
                $"{state.Samples.Count:N0} video sample ve {state.Samples.Count(x => x.Key):N0} keyframe doğrulandı.{audioText}{referenceText}{skipped}",
                new FileInfo(destinationPath).Length);
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath + ".nsx-faststart.tmp");
            TryDelete(destinationPath);
            return new MediaRepairResult(false, $"Kayıp moov yeniden oluşturulamadı: {ex.Message}");
        }
    }

    private static List<MdatRegion> FindMdatRegions(FileStream source)
    {
        var result = new List<MdatRegion>();
        byte[] buffer = new byte[SearchBlockSize + 32];
        int carry = 0;
        long position = 0;
        while (position < source.Length)
        {
            int request = (int)Math.Min(SearchBlockSize, source.Length - position);
            source.Position = position;
            int read = source.Read(buffer, carry, request);
            if (read <= 0) break;
            int count = carry + read;
            for (int i = 4; i + 4 <= count; i++)
            {
                if (buffer[i] != 'm' || buffer[i + 1] != 'd' || buffer[i + 2] != 'a' || buffer[i + 3] != 't')
                    continue;
                long boxOffset = position - carry + i - 4;
                if (TryReadMdat(source, boxOffset, out MdatRegion region) && region.PayloadLength >= 1024 &&
                    !result.Any(x => Math.Abs(x.BoxOffset - region.BoxOffset) < 16))
                    result.Add(region);
            }
            carry = Math.Min(32, count);
            if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return result.OrderBy(x => x.PayloadOffset).ToList();
    }

    private static bool TryReadMdat(FileStream source, long boxOffset, out MdatRegion region)
    {
        region = default;
        if (boxOffset < 0 || boxOffset + 8 > source.Length) return false;
        Span<byte> header = stackalloc byte[16];
        source.Position = boxOffset;
        if (source.Read(header[..8]) != 8 || header[4] != 'm' || header[5] != 'd' || header[6] != 'a' || header[7] != 't')
            return false;

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        int headerSize = 8;
        long boxSize;
        if (size32 == 1)
        {
            if (source.Read(header[..8]) != 8) return false;
            ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
            if (size64 < 16 || size64 > long.MaxValue) return false;
            boxSize = (long)size64;
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            boxSize = source.Length - boxOffset;
        }
        else
        {
            if (size32 < 8) return false;
            boxSize = size32;
        }

        boxSize = Math.Min(boxSize, source.Length - boxOffset);
        long payloadSize = boxSize - headerSize;
        if (payloadSize <= 0) return false;
        region = new MdatRegion(boxOffset, boxOffset + headerSize, payloadSize);
        return true;
    }

    private static ReferenceVideoProfile? NormalizeReferenceProfile(ReferenceVideoProfile? profile)
    {
        if (profile is null || profile.ContainerKind != "ISO-BMFF")
            return null;
        if (profile.VideoCodec is not (ReferenceVideoCodecKind.H264 or ReferenceVideoCodecKind.H265))
            return null;
        if (profile.Sps is null || profile.Pps is null ||
            (profile.VideoCodec == ReferenceVideoCodecKind.H265 && profile.Vps is null))
            return null;
        return profile;
    }

    private static NalCandidate? FindCodecAnchor(
        FileStream source,
        IReadOnlyList<MdatRegion> regions,
        ReferenceVideoProfile? referenceProfile)
    {
        NalCandidate? h264 = null;
        NalCandidate? h265 = null;
        int[] prefixSizes = GetCandidatePrefixSizes(referenceProfile);

        foreach (MdatRegion region in regions)
        {
            long end = region.PayloadOffset + region.PayloadLength;
            byte[] buffer = new byte[SearchBlockSize + 16];
            int carry = 0;
            long position = region.PayloadOffset;
            while (position < end)
            {
                int request = (int)Math.Min(SearchBlockSize, end - position);
                source.Position = position;
                int read = source.Read(buffer, carry, request);
                if (read <= 0) break;
                int count = carry + read;
                for (int i = 0; i + 3 <= count; i++)
                {
                    foreach (int prefixSize in prefixSizes)
                    {
                        if (TryReadNalFromBuffer(buffer.AsSpan(0, count), i, position - carry, end, prefixSize, CodecKind.H264, out NalCandidate h264Nal) &&
                            h264 is null && h264Nal.Type == 7 &&
                            ValidateAnchorRun(source, h264Nal.PrefixOffset, end, CodecKind.H264, prefixSize))
                            h264 = h264Nal;

                        if (TryReadNalFromBuffer(buffer.AsSpan(0, count), i, position - carry, end, prefixSize, CodecKind.H265, out NalCandidate h265Nal) &&
                            h265 is null && h265Nal.Type == 32 &&
                            ValidateAnchorRun(source, h265Nal.PrefixOffset, end, CodecKind.H265, prefixSize))
                            h265 = h265Nal;
                    }
                    if (h264 is not null && h265 is not null)
                        return h264.Value.PrefixOffset <= h265.Value.PrefixOffset ? h264 : h265;
                }
                carry = Math.Min(16, count);
                if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
                position += read;
            }
        }

        NalCandidate? parameterAnchor = h264 ?? h265;
        if (parameterAnchor is not null || referenceProfile is null)
            return parameterAnchor;

        CodecKind referenceCodec = referenceProfile.VideoCodec == ReferenceVideoCodecKind.H265
            ? CodecKind.H265
            : CodecKind.H264;
        return FindReferenceGuidedAnchor(source, regions, referenceCodec, prefixSizes);
    }

    private static NalCandidate? FindReferenceGuidedAnchor(
        FileStream source,
        IReadOnlyList<MdatRegion> regions,
        CodecKind codec,
        IReadOnlyList<int> prefixSizes)
    {
        NalCandidate? fallback = null;
        foreach (MdatRegion region in regions)
        {
            long end = region.PayloadOffset + region.PayloadLength;
            byte[] buffer = new byte[SearchBlockSize + 16];
            int carry = 0;
            long position = region.PayloadOffset;
            while (position < end)
            {
                int request = (int)Math.Min(SearchBlockSize, end - position);
                source.Position = position;
                int read = source.Read(buffer, carry, request);
                if (read <= 0) break;
                int count = carry + read;
                for (int i = 0; i + 3 <= count; i++)
                {
                    foreach (int prefixSize in prefixSizes)
                    {
                        if (!TryReadNalFromBuffer(buffer.AsSpan(0, count), i, position - carry, end, prefixSize, codec, out NalCandidate candidate))
                            continue;
                        if (!ValidateReferenceRun(source, candidate.PrefixOffset, end, codec, prefixSize, out bool hasRandomAccess))
                            continue;
                        if (hasRandomAccess)
                            return candidate;
                        fallback ??= candidate;
                    }
                }
                carry = Math.Min(16, count);
                if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
                position += read;
            }
        }
        return fallback;
    }

    private static bool ValidateReferenceRun(
        FileStream source,
        long position,
        long end,
        CodecKind codec,
        int prefixSize,
        out bool hasRandomAccess)
    {
        hasRandomAccess = false;
        int count = 0;
        int vcl = 0;
        while (count < 24 && TryReadNal(source, position, end, codec, prefixSize, out NalCandidate nal))
        {
            bool isVcl = codec == CodecKind.H264 ? nal.Type is 1 or 2 or 3 or 4 or 5 : nal.Type <= 31;
            if (isVcl) vcl++;
            if (codec == CodecKind.H264 ? nal.Type == 5 : nal.Type is >= 16 and <= 23)
                hasRandomAccess = true;
            position += nal.PrefixSize + nal.Length;
            count++;
        }
        return count >= 3 && vcl >= 2;
    }

    private static int[] GetCandidatePrefixSizes(ReferenceVideoProfile? profile)
    {
        var result = new List<int>(4);
        void Add(int value)
        {
            if (value is >= 1 and <= 4 && !result.Contains(value))
                result.Add(value);
        }
        if (profile is not null) Add(profile.NalLengthSize);
        Add(4); Add(2); Add(1); Add(3);
        return result.ToArray();
    }

    private static bool ValidateAnchorRun(FileStream source, long position, long end, CodecKind codec, int prefixSize)
    {
        bool sps = false, pps = false, vps = codec == CodecKind.H264;
        int count = 0;
        while (count < 16 && TryReadNal(source, position, end, codec, prefixSize, out NalCandidate nal))
        {
            if (codec == CodecKind.H264) { sps |= nal.Type == 7; pps |= nal.Type == 8; }
            else { vps |= nal.Type == 32; sps |= nal.Type == 33; pps |= nal.Type == 34; }
            position += nal.PrefixSize + nal.Length;
            count++;
        }
        return count >= 3 && sps && pps && vps;
    }

    private static void ScanVideo(
        FileStream source,
        FileStream payload,
        IReadOnlyList<MdatRegion> regions,
        NalCandidate anchor,
        ScanState state,
        Action<string>? progress)
    {
        bool started = false;
        long nextProgress = 256L * 1024 * 1024;
        int prefixSize = anchor.PrefixSize;
        foreach (MdatRegion region in regions)
        {
            long start = region.PayloadOffset;
            long end = region.PayloadOffset + region.PayloadLength;
            long position = started ? start : Math.Max(start, anchor.PrefixOffset);
            if (!started && anchor.PrefixOffset >= start && anchor.PrefixOffset < end) started = true;
            else if (!started) continue;

            while (position + prefixSize + 1 <= end)
            {
                if (!TryReadNal(source, position, end, state.Codec, prefixSize, out NalCandidate nal))
                {
                    FinalizeSample(state, payload);
                    long next = FindNextNalRun(source, position + 1, Math.Min(end, position + ResyncWindow), state.Codec, prefixSize);
                    if (next < 0)
                    {
                        state.SkippedBytes += end - position;
                        break;
                    }
                    state.SkippedBytes += next - position;
                    position = next;
                    continue;
                }

                ProcessNal(source, payload, nal, state);
                position += nal.PrefixSize + nal.Length;
                if (payload.Length >= nextProgress)
                {
                    progress?.Invoke($"moov reconstruction • {RecoveryFileItem.FormatBytes(payload.Length)} video payload ve {state.Samples.Count:N0} sample çıkarıldı...");
                    nextProgress += 256L * 1024 * 1024;
                }
            }
        }
    }

    private static long FindNextNalRun(FileStream source, long start, long end, CodecKind codec, int prefixSize)
    {
        byte[] buffer = new byte[SearchBlockSize + 16];
        int carry = 0;
        long position = start;
        while (position < end)
        {
            int request = (int)Math.Min(SearchBlockSize, end - position);
            source.Position = position;
            int read = source.Read(buffer, carry, request);
            if (read <= 0) break;
            int count = carry + read;
            for (int i = 0; i + prefixSize + 1 <= count; i++)
            {
                long absolute = position - carry + i;
                if (ValidateNalSequence(source, absolute, end, codec, prefixSize, 3)) return absolute;
            }
            carry = Math.Min(16, count);
            if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return -1;
    }

    private static bool ValidateNalSequence(FileStream source, long position, long end, CodecKind codec, int prefixSize, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!TryReadNal(source, position, end, codec, prefixSize, out NalCandidate nal)) return false;
            position += nal.PrefixSize + nal.Length;
        }
        return true;
    }

    private static bool TryReadNalFromBuffer(
        ReadOnlySpan<byte> data,
        int offset,
        long absoluteBase,
        long absoluteEnd,
        int prefixSize,
        CodecKind codec,
        out NalCandidate nal)
    {
        nal = default;
        if (prefixSize is < 1 or > 4 || offset < 0 || offset + prefixSize + 1 > data.Length)
            return false;
        uint length = ReadLengthPrefix(data.Slice(offset, prefixSize));
        int minimum = codec == CodecKind.H264 ? 1 : 2;
        if (length < minimum)
            return false;
        long absolute = absoluteBase + offset;
        if (length > absoluteEnd - absolute - prefixSize)
            return false;
        byte first = data[offset + prefixSize];
        if ((first & 0x80) != 0)
            return false;
        int type;
        if (codec == CodecKind.H264)
        {
            type = first & 0x1F;
            if (type is <= 0 or > 12) return false;
        }
        else
        {
            if (offset + prefixSize + 2 > data.Length) return false;
            byte second = data[offset + prefixSize + 1];
            type = (first >> 1) & 0x3F;
            if (type > 40 || (second & 0x07) == 0) return false;
        }
        nal = new NalCandidate(absolute, prefixSize, length, codec, type);
        return true;
    }

    private static bool TryReadNal(FileStream source, long offset, long end, CodecKind codec, int prefixSize, out NalCandidate nal)
    {
        nal = default;
        if (prefixSize is < 1 or > 4 || offset < 0 || offset + prefixSize + 1 > end) return false;
        Span<byte> head = stackalloc byte[6];
        source.Position = offset;
        int request = (int)Math.Min(prefixSize + 2, end - offset);
        if (source.Read(head[..request]) != request || request < prefixSize + 1) return false;
        uint length = ReadLengthPrefix(head[..prefixSize]);
        int minimum = codec == CodecKind.H264 ? 1 : 2;
        if (length < minimum || length > end - offset - prefixSize) return false;
        byte first = head[prefixSize];
        if ((first & 0x80) != 0) return false;

        int type;
        if (codec == CodecKind.H264)
        {
            type = first & 0x1F;
            if (type is <= 0 or > 12) return false;
        }
        else
        {
            if (request < prefixSize + 2) return false;
            type = (first >> 1) & 0x3F;
            if (type > 40 || (head[prefixSize + 1] & 0x07) == 0) return false;
        }
        nal = new NalCandidate(offset, prefixSize, length, codec, type);
        return true;
    }

    private static uint ReadLengthPrefix(ReadOnlySpan<byte> prefix)
    {
        uint value = 0;
        foreach (byte b in prefix)
            value = (value << 8) | b;
        return value;
    }

    private static void ProcessNal(FileStream source, FileStream payload, NalCandidate nal, ScanState state)
    {
        bool vcl = nal.Codec == CodecKind.H264 ? nal.Type is 1 or 2 or 3 or 4 or 5 : nal.Type <= 31;
        bool key = nal.Codec == CodecKind.H264 ? nal.Type == 5 : nal.Type is >= 16 and <= 23;
        bool newAccessUnit = false;

        if (nal.Codec == CodecKind.H264)
        {
            if (nal.Type == 9 && state.CurrentHasVcl) newAccessUnit = true;
            else if (vcl && state.CurrentHasVcl && TryReadH264FirstMb(source, nal, out uint firstMb) && firstMb == 0)
                newAccessUnit = true;
        }
        else
        {
            if (nal.Type == 35 && state.CurrentHasVcl) newAccessUnit = true;
            else if (vcl && state.CurrentHasVcl && TryReadHevcFirstSlice(source, nal, out bool firstSlice) && firstSlice)
                newAccessUnit = true;
        }

        if (newAccessUnit) FinalizeSample(state, payload);
        if (state.CurrentSize == 0) state.CurrentPayloadStart = payload.Position;

        CaptureParameterSet(source, nal, state);
        AddVideoSourceRange(state, nal.PrefixOffset, checked((long)nal.PrefixSize + nal.Length));
        CopyNal(source, payload, nal);
        ulong size = (ulong)state.CurrentSize + 4UL + nal.Length;
        if (size > uint.MaxValue)
            throw new InvalidDataException("Tek video sample 4 GB sınırını aşıyor; güvenli stsz tablosu oluşturulamadı.");
        state.CurrentSize = (uint)size;
        state.CurrentHasVcl |= vcl;
        state.CurrentKey |= key;
        state.AcceptedNals++;
    }


    private static void AddVideoSourceRange(ScanState state, long offset, long length)
    {
        if (length <= 0)
            return;
        if (state.VideoSourceRanges.Count == 0)
        {
            state.VideoSourceRanges.Add(new Mp4ByteRange(offset, length));
            return;
        }

        Mp4ByteRange last = state.VideoSourceRanges[^1];
        long end = checked(offset + length);
        long lastEnd = last.End;
        bool touches = offset <= lastEnd || (lastEnd < long.MaxValue && offset == lastEnd + 1);
        if (touches)
        {
            long mergedEnd = Math.Max(lastEnd, end);
            state.VideoSourceRanges[^1] = new Mp4ByteRange(last.Offset, mergedEnd - last.Offset);
        }
        else
        {
            state.VideoSourceRanges.Add(new Mp4ByteRange(offset, length));
        }
    }

    private static void FinalizeSample(ScanState state, FileStream payload)
    {
        if (state.CurrentSize == 0) return;
        if (state.CurrentHasVcl)
            state.Samples.Add(new Sample(state.CurrentSize, state.CurrentKey));
        else if (state.CurrentPayloadStart >= 0 && state.CurrentPayloadStart <= payload.Length)
        {
            payload.SetLength(state.CurrentPayloadStart);
            payload.Position = state.CurrentPayloadStart;
        }
        state.CurrentSize = 0;
        state.CurrentHasVcl = false;
        state.CurrentKey = false;
        state.CurrentPayloadStart = -1;
    }

    private static void CaptureParameterSet(FileStream source, NalCandidate nal, ScanState state)
    {
        bool needed = nal.Codec == CodecKind.H264 ? nal.Type is 7 or 8 : nal.Type is 32 or 33 or 34;
        if (!needed || nal.Length > MaxParameterSetBytes) return;
        byte[] bytes = new byte[(int)nal.Length];
        source.Position = nal.PrefixOffset + nal.PrefixSize;
        source.ReadExactly(bytes);
        if (nal.Codec == CodecKind.H264)
        {
            if (nal.Type == 7 && state.Sps is null) state.Sps = bytes;
            else if (nal.Type == 8 && state.Pps is null) state.Pps = bytes;
        }
        else
        {
            if (nal.Type == 32 && state.Vps is null) state.Vps = bytes;
            else if (nal.Type == 33 && state.Sps is null) state.Sps = bytes;
            else if (nal.Type == 34 && state.Pps is null) state.Pps = bytes;
        }
    }

    private static void CopyNal(FileStream source, FileStream payload, NalCandidate nal)
    {
        Span<byte> normalizedLength = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(normalizedLength, nal.Length);
        payload.Write(normalizedLength);

        source.Position = nal.PrefixOffset + nal.PrefixSize;
        long remaining = nal.Length;
        byte[] buffer = new byte[IoBufferSize];
        while (remaining > 0)
        {
            int read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("NAL verisi beklenenden önce sona erdi.");
            payload.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static bool TryReadH264FirstMb(FileStream source, NalCandidate nal, out uint firstMb)
    {
        firstMb = 0;
        int size = (int)Math.Min(nal.Length, 1024u);
        if (size <= 1) return false;
        byte[] data = new byte[size];
        source.Position = nal.PrefixOffset + nal.PrefixSize;
        if (source.Read(data, 0, data.Length) != data.Length) return false;
        var bits = new BitReader(RemoveEmulationPrevention(data.AsSpan(1)));
        return bits.TryReadUE(out firstMb);
    }

    private static bool TryReadHevcFirstSlice(FileStream source, NalCandidate nal, out bool firstSlice)
    {
        firstSlice = false;
        if (nal.Length < 3) return false;
        Span<byte> data = stackalloc byte[3];
        source.Position = nal.PrefixOffset + nal.PrefixSize;
        if (source.Read(data) != 3) return false;
        firstSlice = (data[2] & 0x80) != 0;
        return true;
    }

    private static void ApplyReferenceFallback(ScanState state)
    {
        ReferenceVideoProfile? reference = state.ReferenceProfile;
        if (reference is null || !ReferenceCodecMatches(state.Codec, reference.VideoCodec))
            return;

        bool used = false;
        if (state.Sps is null)
        {
            state.Sps = reference.Sps?.ToArray();
            state.Pps ??= reference.Pps?.ToArray();
            if (state.Codec == CodecKind.H265)
                state.Vps ??= reference.Vps?.ToArray();
            used = state.Sps is not null && state.Pps is not null && (state.Codec == CodecKind.H264 || state.Vps is not null);
        }
        else
        {
            byte[]? currentSps = state.Sps;
            byte[]? referenceSps = reference.Sps;
            if (currentSps is not null && referenceSps is not null && currentSps.AsSpan().SequenceEqual(referenceSps))
            {
                if (state.Pps is null && reference.Pps is not null) { state.Pps = reference.Pps.ToArray(); used = true; }
                if (state.Codec == CodecKind.H265 && state.Vps is null && reference.Vps is not null) { state.Vps = reference.Vps.ToArray(); used = true; }
            }
        }

        state.ReferenceParameterSetsUsed |= used;
    }

    private static bool ReferenceCodecMatches(CodecKind codec, ReferenceVideoCodecKind reference) =>
        codec == CodecKind.H264 ? reference == ReferenceVideoCodecKind.H264 : reference == ReferenceVideoCodecKind.H265;

    private static bool CanUseReferenceTiming(ScanState state)
    {
        ReferenceVideoProfile? reference = state.ReferenceProfile;
        byte[]? currentSps = state.Sps;
        byte[]? referenceSps = reference?.Sps;
        if (reference is null || !ReferenceCodecMatches(state.Codec, reference.VideoCodec) || referenceSps is null || currentSps is null)
            return false;
        return currentSps.AsSpan().SequenceEqual(referenceSps);
    }

    private static void ValidateState(ScanState state, long payloadBytes)
    {
        if (state.Sps is null || state.Pps is null) throw new InvalidDataException("SPS/PPS parametre setleri tamamlanamadı.");
        if (state.Codec == CodecKind.H265 && state.Vps is null) throw new InvalidDataException("HEVC VPS parametre seti bulunamadı.");
        if (state.Samples.Count < 8) throw new InvalidDataException("Güvenilir moov üretmek için yeterli video sample bulunamadı.");
        if (!state.Samples.Any(x => x.Key)) throw new InvalidDataException("Doğrulanabilir IDR/IRAP keyframe bulunamadı.");
        if (payloadBytes < 4096) throw new InvalidDataException("Yeniden oluşturulan video payload yetersiz.");
    }

    private static Geometry ResolveGeometry(ScanState state)
    {
        byte[] sps = state.Sps ?? throw new InvalidDataException("SPS bulunamadı.");

        ReferenceVideoProfile? reference = CanUseReferenceTiming(state) ? state.ReferenceProfile : null;
        uint timescale = reference is { VideoTimescale: > 0 } ? reference.VideoTimescale : DefaultTimescale;
        int samplesPerChunk = reference is not null
            ? Math.Clamp(reference.PreferredSamplesPerChunk, 1, 4096)
            : Math.Max(1, state.Samples.Count);

        if (state.Codec == CodecKind.H264)
        {
            int width;
            int height;
            double? fps;
            if (!TryParseH264Sps(sps, out width, out height, out fps))
            {
                if (reference is null || reference.Width <= 0 || reference.Height <= 0)
                    throw new InvalidDataException("H.264 SPS içinden çözünürlük doğrulanamadı.");
                width = reference.Width;
                height = reference.Height;
            }

            uint delta = reference is { VideoSampleDelta: > 0 }
                ? reference.VideoSampleDelta
                : fps is > 1 and < 240
                    ? Math.Max(1u, (uint)Math.Round(timescale / fps.Value))
                    : DefaultSampleDelta;
            return new Geometry(width, height, timescale, delta, samplesPerChunk);
        }

        int hevcWidth;
        int hevcHeight;
        if (!TryParseH265Sps(sps, out hevcWidth, out hevcHeight))
        {
            if (reference is null || reference.Width <= 0 || reference.Height <= 0)
                throw new InvalidDataException("H.265 SPS içinden çözünürlük doğrulanamadı.");
            hevcWidth = reference.Width;
            hevcHeight = reference.Height;
        }
        uint hevcDelta = reference is { VideoSampleDelta: > 0 } ? reference.VideoSampleDelta : DefaultSampleDelta;
        return new Geometry(hevcWidth, hevcHeight, timescale, hevcDelta, samplesPerChunk);
    }

    private static void ApplyVideoTiming(ScanState state, Geometry geometry)
    {
        ReferenceVideoProfile? reference = CanUseReferenceTiming(state) ? state.ReferenceProfile : null;
        var timeCursor = new TimeRunCursor(reference?.VideoTimeToSampleRuns, geometry.SampleDelta);
        var compositionCursor = new CompositionRunCursor(reference?.VideoCompositionOffsetRuns);

        for (int i = 0; i < state.Samples.Count; i++)
        {
            Sample sample = state.Samples[i];
            state.Samples[i] = sample with
            {
                Duration = timeCursor.Next(),
                CompositionOffset = compositionCursor.Next()
            };
        }

        state.ReferenceTimingUsed = reference is not null &&
            (reference.VideoTimeToSampleRuns.Length > 0 ||
             reference.VideoCompositionOffsetRuns.Length > 0 ||
             reference.VideoSampleDelta > 0);
    }

    private static void ValidateAudioTrack(Mp4RecoveredAudioTrack track)
    {
        if (track.SampleCount <= 0 || track.SampleCount > uint.MaxValue)
            throw new InvalidDataException("Ses track sample sayısı MP4 tablo sınırlarının dışında.");
        if (track.Timescale == 0 || track.SampleRate <= 0 || track.Channels <= 0)
            throw new InvalidDataException("Ses track zaman tabanı/kanal bilgisi doğrulanamadı.");
        if (track.SampleEntryBox.Length < 16 || track.SampleEntryBox.Length > 1024 * 1024)
            throw new InvalidDataException("Ses sample-entry kutusu geçersiz.");
        if (track.FixedSampleSize > 0)
        {
            if (track.FixedSampleCount <= 0 || track.FixedPayloadRanges.Count == 0)
                throw new InvalidDataException("PCM/fixed-size ses sample geometrisi boş.");
            long expected = checked(track.FixedSampleCount * track.FixedSampleSize);
            if (expected != track.OutputPayloadBytes)
                throw new InvalidDataException("Fixed-size ses stsz toplamı mdat ses payload'ıyla uyuşmuyor.");
        }
        else
        {
            long sum = 0;
            foreach (Mp4AudioSourceSample sample in track.Samples)
            {
                if (sample.Size == 0 || sample.Duration == 0)
                    throw new InvalidDataException("Ses sample boyutu veya süresi sıfır.");
                sum = checked(sum + sample.Size);
            }
            if (sum != track.OutputPayloadBytes)
                throw new InvalidDataException("Ses stsz toplamı mdat ses payload'ıyla uyuşmuyor.");
        }
    }

    private static bool TryParseH264Sps(byte[] sps, out int width, out int height, out double? fps)
    {
        width = 0; height = 0; fps = null;
        if (sps.Length < 4) return false;
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(1)));
        if (!bits.TryReadBits(8, out uint profile) || !bits.TrySkip(16) || !bits.TryReadUE(out _)) return false;

        uint chromaFormat = 1;
        bool separateColour = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (!bits.TryReadUE(out chromaFormat)) return false;
            if (chromaFormat == 3 && !bits.TryReadBit(out separateColour)) return false;
            if (!bits.TryReadUE(out _) || !bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadBit(out bool scaling)) return false;
            if (scaling)
            {
                int count = chromaFormat != 3 ? 8 : 12;
                for (int i = 0; i < count; i++)
                {
                    if (!bits.TryReadBit(out bool present)) return false;
                    if (present && !SkipScalingList(bits, i < 6 ? 16 : 64)) return false;
                }
            }
        }

        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint pocType)) return false;
        if (pocType == 0)
        {
            if (!bits.TryReadUE(out _)) return false;
        }
        else if (pocType == 1)
        {
            if (!bits.TrySkip(1) || !bits.TryReadSE(out _) || !bits.TryReadSE(out _) || !bits.TryReadUE(out uint cycle)) return false;
            for (uint i = 0; i < cycle; i++) if (!bits.TryReadSE(out _)) return false;
        }

        if (!bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadUE(out uint widthMbs) ||
            !bits.TryReadUE(out uint heightMaps) || !bits.TryReadBit(out bool frameOnly)) return false;
        if (!frameOnly && !bits.TrySkip(1)) return false;
        if (!bits.TrySkip(1) || !bits.TryReadBit(out bool crop)) return false;

        uint left = 0, right = 0, top = 0, bottom = 0;
        if (crop && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;

        int baseWidth = checked((int)((widthMbs + 1) * 16));
        int baseHeight = checked((int)((2 - (frameOnly ? 1 : 0)) * (heightMaps + 1) * 16));
        uint chromaArray = separateColour ? 0u : chromaFormat;
        int subWidth = chromaArray is 1 or 2 ? 2 : 1;
        int subHeight = chromaArray == 1 ? 2 : 1;
        int cropX = chromaArray == 0 ? 1 : subWidth;
        int cropY = chromaArray == 0 ? 2 - (frameOnly ? 1 : 0) : subHeight * (2 - (frameOnly ? 1 : 0));
        width = baseWidth - checked((int)((left + right) * (uint)cropX));
        height = baseHeight - checked((int)((top + bottom) * (uint)cropY));
        if (width <= 0 || height <= 0 || width > ushort.MaxValue || height > ushort.MaxValue) return false;

        if (bits.TryReadBit(out bool vui) && vui) fps = TryReadH264VuiRate(bits);
        return true;
    }

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

    private static bool TryParseH265Sps(byte[] sps, out int width, out int height)
    {
        width = 0; height = 0;
        if (sps.Length < 6) return false;
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(2)));
        if (!bits.TrySkip(4) || !bits.TryReadBits(3, out uint layers) || !bits.TrySkip(1) || !SkipHevcProfile(bits, (int)layers)) return false;
        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint chroma)) return false;
        if (chroma == 3 && !bits.TrySkip(1)) return false;
        if (!bits.TryReadUE(out uint picWidth) || !bits.TryReadUE(out uint picHeight) || !bits.TryReadBit(out bool window)) return false;
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (window && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;
        int subWidth = chroma is 1 or 2 ? 2 : 1;
        int subHeight = chroma == 1 ? 2 : 1;
        width = checked((int)picWidth - (int)((left + right) * (uint)subWidth));
        height = checked((int)picHeight - (int)((top + bottom) * (uint)subHeight));
        return width > 0 && height > 0 && width <= ushort.MaxValue && height <= ushort.MaxValue;
    }

    private static bool SkipHevcProfile(BitReader bits, int maxLayers)
    {
        if (!bits.TrySkip(96)) return false;
        bool[] profile = new bool[maxLayers];
        bool[] level = new bool[maxLayers];
        for (int i = 0; i < maxLayers; i++)
            if (!bits.TryReadBit(out profile[i]) || !bits.TryReadBit(out level[i])) return false;
        if (maxLayers > 0)
            for (int i = maxLayers; i < 8; i++) if (!bits.TrySkip(2)) return false;
        for (int i = 0; i < maxLayers; i++)
        {
            if (profile[i] && !bits.TrySkip(88)) return false;
            if (level[i] && !bits.TrySkip(8)) return false;
        }
        return true;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> source)
    {
        byte[] result = new byte[source.Length];
        int write = 0, zeros = 0;
        for (int i = 0; i < source.Length; i++)
        {
            byte value = source[i];
            if (zeros >= 2 && value == 0x03) { zeros = 0; continue; }
            result[write++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.AsSpan(0, write).ToArray();
    }

    private static byte[] ReadOrCreateFtyp(FileStream source, CodecKind codec, ReferenceVideoProfile? referenceProfile)
    {
        long offset = FindFtyp(source);
        if (offset >= 0)
        {
            Span<byte> sizeBytes = stackalloc byte[4];
            source.Position = offset;
            if (source.Read(sizeBytes) == 4)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);
                if (size is >= 16 and <= 1024 * 1024 && offset + size <= source.Length)
                {
                    byte[] bytes = new byte[(int)size];
                    source.Position = offset;
                    source.ReadExactly(bytes);
                    return bytes;
                }
            }
        }

        if (referenceProfile?.FtypBox is { Length: >= 16 and <= 1048576 } referenceFtyp &&
            referenceFtyp.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            return referenceFtyp.ToArray();

        string sample = codec == CodecKind.H264 ? "avc1" : "hvc1";
        return Box("ftyp", writer =>
        {
            WriteAscii(writer, "isom");
            WriteU32(writer, 0x200);
            WriteAscii(writer, "isom");
            WriteAscii(writer, "iso2");
            WriteAscii(writer, sample);
            WriteAscii(writer, "mp41");
        });
    }

    private static long FindFtyp(FileStream source)
    {
        byte[] buffer = new byte[SearchBlockSize + 16];
        int carry = 0;
        long position = 0;
        while (position < source.Length)
        {
            int request = (int)Math.Min(SearchBlockSize, source.Length - position);
            source.Position = position;
            int read = source.Read(buffer, carry, request);
            if (read <= 0) break;
            int count = carry + read;
            for (int i = 4; i + 4 <= count; i++)
            {
                if (buffer[i] != 'f' || buffer[i + 1] != 't' || buffer[i + 2] != 'y' || buffer[i + 3] != 'p') continue;
                uint size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i - 4, 4));
                long absolute = position - carry + i - 4;
                if (size is >= 16 and <= 1024 * 1024 && absolute + size <= source.Length) return absolute;
            }
            carry = Math.Min(16, count);
            if (carry > 0) buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }
        return -1;
    }

    private static byte[] BuildProgressiveMoov(
        ScanState state,
        Geometry geometry,
        int ftypLength,
        long videoPayloadBytes,
        Mp4RecoveredAudioTrack? audioTrack,
        out ulong mediaOffset)
    {
        // moov size is normally offset-independent. A stco→co64 boundary can however change
        // the box size, so converge on the final offset instead of assuming one pass.
        ulong candidateOffset = checked((ulong)ftypLength + 16UL);
        byte[] moov = Array.Empty<byte>();
        for (int pass = 0; pass < 6; pass++)
        {
            if (audioTrack is not null)
                audioTrack.OutputPayloadOffset = checked(candidateOffset + (ulong)videoPayloadBytes);

            moov = BuildMoov(state, geometry, candidateOffset, audioTrack);
            ulong nextOffset = checked((ulong)ftypLength + (ulong)moov.Length + 16UL);
            if (nextOffset == candidateOffset)
            {
                mediaOffset = candidateOffset;
                return moov;
            }
            candidateOffset = nextOffset;
        }

        if (audioTrack is not null)
            audioTrack.OutputPayloadOffset = checked(candidateOffset + (ulong)videoPayloadBytes);
        moov = BuildMoov(state, geometry, candidateOffset, audioTrack);
        ulong verifiedOffset = checked((ulong)ftypLength + (ulong)moov.Length + 16UL);
        if (verifiedOffset != candidateOffset)
            throw new InvalidDataException("Progressive MP4 moov/media offset dengesi kurulamadı.");

        mediaOffset = candidateOffset;
        return moov;
    }

    private static byte[] BuildMoov(
        ScanState state,
        Geometry geometry,
        ulong mediaOffset,
        Mp4RecoveredAudioTrack? audioTrack)
    {
        ulong videoDuration = GetVideoDuration(state, geometry.SampleDelta);
        uint movieTimescale = geometry.Timescale;
        ulong audioMovieDuration = audioTrack is null
            ? 0
            : ScaleMediaDuration(audioTrack.MediaDuration, audioTrack.Timescale, movieTimescale);
        ulong movieDuration = Math.Max(videoDuration, audioMovieDuration);
        uint nextTrackId = audioTrack is null ? 2u : 3u;

        return Box("moov", writer =>
        {
            writer.Write(BuildMvhd(movieDuration, movieTimescale, nextTrackId));
            writer.Write(BuildVideoTrak(state, geometry, videoDuration, mediaOffset));
            if (audioTrack is not null)
                writer.Write(BuildAudioTrak(audioTrack, movieTimescale));
        });
    }

    private static ulong GetVideoDuration(ScanState state, uint fallbackDelta)
    {
        ulong duration = 0;
        foreach (Sample sample in state.Samples)
            duration = checked(duration + Math.Max(1u, sample.Duration == 0 ? fallbackDelta : sample.Duration));
        return duration;
    }

    private static ulong ScaleMediaDuration(ulong duration, uint sourceTimescale, uint targetTimescale)
    {
        if (duration == 0 || sourceTimescale == 0 || targetTimescale == 0 || sourceTimescale == targetTimescale)
            return duration;
        ulong whole = duration / sourceTimescale;
        ulong remainder = duration % sourceTimescale;
        ulong scaledWhole = checked(whole * targetTimescale);
        ulong scaledRemainder = checked((remainder * targetTimescale + sourceTimescale / 2u) / sourceTimescale);
        return checked(scaledWhole + scaledRemainder);
    }

    private static byte[] BuildMvhd(ulong duration, uint timescale, uint nextTrackId) => Box("mvhd", writer =>
    {
        FullHeader(writer, 1, 0);
        WriteU64(writer, 0); WriteU64(writer, 0); WriteU32(writer, timescale); WriteU64(writer, duration);
        WriteU32(writer, 0x00010000); WriteU16(writer, 0x0100); WriteU16(writer, 0);
        WriteU32(writer, 0); WriteU32(writer, 0); UnityMatrix(writer);
        for (int i = 0; i < 6; i++) WriteU32(writer, 0);
        WriteU32(writer, nextTrackId);
    });

    private static byte[] BuildVideoTrak(ScanState state, Geometry geometry, ulong duration, ulong mediaOffset) => Box("trak", writer =>
    {
        writer.Write(Box("tkhd", box =>
        {
            FullHeader(box, 1, 7);
            WriteU64(box, 0); WriteU64(box, 0); WriteU32(box, 1); WriteU32(box, 0); WriteU64(box, duration);
            WriteU32(box, 0); WriteU32(box, 0); WriteU16(box, 0); WriteU16(box, 0); WriteU16(box, 0); WriteU16(box, 0);
            UnityMatrix(box);
            WriteU32(box, checked((uint)geometry.Width << 16));
            WriteU32(box, checked((uint)geometry.Height << 16));
        }));

        writer.Write(Box("mdia", mdia =>
        {
            mdia.Write(Box("mdhd", box =>
            {
                FullHeader(box, 1, 0); WriteU64(box, 0); WriteU64(box, 0); WriteU32(box, geometry.Timescale); WriteU64(box, duration);
                WriteU16(box, 0x55C4); WriteU16(box, 0);
            }));
            mdia.Write(Box("hdlr", box =>
            {
                FullHeader(box, 0, 0); WriteU32(box, 0); WriteAscii(box, "vide"); WriteU32(box, 0); WriteU32(box, 0); WriteU32(box, 0);
                WriteAscii(box, "NSX Recovered Video\0");
            }));
            mdia.Write(Box("minf", minf =>
            {
                minf.Write(Box("vmhd", box =>
                {
                    FullHeader(box, 0, 1); WriteU16(box, 0); WriteU16(box, 0); WriteU16(box, 0); WriteU16(box, 0);
                }));
                minf.Write(BuildDinf());
                minf.Write(BuildVideoStbl(state, geometry, mediaOffset));
            }));
        }));
    });

    private static byte[] BuildAudioTrak(Mp4RecoveredAudioTrack audio, uint movieTimescale) => Box("trak", writer =>
    {
        ulong mediaDuration = audio.MediaDuration;
        ulong movieDuration = ScaleMediaDuration(mediaDuration, audio.Timescale, movieTimescale);
        writer.Write(Box("tkhd", box =>
        {
            FullHeader(box, 1, 7);
            WriteU64(box, 0); WriteU64(box, 0); WriteU32(box, 2); WriteU32(box, 0); WriteU64(box, movieDuration);
            WriteU32(box, 0); WriteU32(box, 0); WriteU16(box, 0); WriteU16(box, 0); WriteU16(box, 0x0100); WriteU16(box, 0);
            UnityMatrix(box);
            WriteU32(box, 0); WriteU32(box, 0);
        }));

        writer.Write(Box("mdia", mdia =>
        {
            mdia.Write(Box("mdhd", box =>
            {
                FullHeader(box, 1, 0); WriteU64(box, 0); WriteU64(box, 0); WriteU32(box, audio.Timescale); WriteU64(box, mediaDuration);
                WriteU16(box, 0x55C4); WriteU16(box, 0);
            }));
            mdia.Write(Box("hdlr", box =>
            {
                FullHeader(box, 0, 0); WriteU32(box, 0); WriteAscii(box, "soun"); WriteU32(box, 0); WriteU32(box, 0); WriteU32(box, 0);
                WriteAscii(box, "NSX Recovered Audio\0");
            }));
            mdia.Write(Box("minf", minf =>
            {
                minf.Write(Box("smhd", box =>
                {
                    FullHeader(box, 0, 0); WriteU16(box, 0); WriteU16(box, 0);
                }));
                minf.Write(BuildDinf());
                minf.Write(BuildAudioStbl(audio));
            }));
        }));
    });

    private static byte[] BuildDinf() => Box("dinf", dinf => dinf.Write(Box("dref", dref =>
    {
        FullHeader(dref, 0, 0); WriteU32(dref, 1); dref.Write(Box("url ", url => FullHeader(url, 0, 1)));
    })));

    private static byte[] BuildVideoStbl(ScanState state, Geometry geometry, ulong mediaOffset) => Box("stbl", writer =>
    {
        writer.Write(Box("stsd", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, 1); box.Write(BuildSampleEntry(state, geometry));
        }));

        List<(uint Count, uint Delta)> timeRuns = BuildVideoTimeRuns(state, geometry.SampleDelta);
        writer.Write(Box("stts", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, checked((uint)timeRuns.Count));
            foreach ((uint count, uint delta) in timeRuns) { WriteU32(box, count); WriteU32(box, delta); }
        }));

        List<(uint Count, int Offset)> compositionRuns = BuildCompositionRuns(state);
        writer.Write(Box("ctts", box =>
        {
            bool signed = compositionRuns.Any(run => run.Offset < 0);
            FullHeader(box, signed ? (byte)1 : (byte)0, 0);
            WriteU32(box, checked((uint)compositionRuns.Count));
            foreach ((uint count, int offset) in compositionRuns)
            {
                WriteU32(box, count);
                if (signed) WriteU32(box, unchecked((uint)offset));
                else WriteU32(box, checked((uint)Math.Max(0, offset)));
            }
        }));

        List<(ulong Offset, int Count)> chunks = BuildChunkLayout(state.Samples, mediaOffset, geometry.SamplesPerChunk);
        writer.Write(BuildStsc(chunks));
        writer.Write(Box("stsz", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, 0); WriteU32(box, checked((uint)state.Samples.Count));
            foreach (Sample sample in state.Samples) WriteU32(box, sample.Size);
        }));
        writer.Write(BuildChunkOffsetBox(chunks));

        uint[] sync = state.Samples.Select((sample, index) => (sample, index))
            .Where(pair => pair.sample.Key)
            .Select(pair => checked((uint)pair.index + 1))
            .ToArray();
        writer.Write(Box("stss", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, checked((uint)sync.Length)); foreach (uint n in sync) WriteU32(box, n);
        }));
    });

    private static byte[] BuildAudioStbl(Mp4RecoveredAudioTrack audio) => Box("stbl", writer =>
    {
        writer.Write(Box("stsd", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, 1); box.Write(audio.SampleEntryBox);
        }));

        List<(uint Count, uint Delta)> timeRuns = BuildAudioTimeRuns(audio);
        writer.Write(Box("stts", box =>
        {
            FullHeader(box, 0, 0); WriteU32(box, checked((uint)timeRuns.Count));
            foreach ((uint count, uint delta) in timeRuns) { WriteU32(box, count); WriteU32(box, delta); }
        }));

        List<(ulong Offset, int Count)> chunks = BuildAudioChunkLayout(audio);
        writer.Write(BuildStsc(chunks));
        writer.Write(Box("stsz", box =>
        {
            FullHeader(box, 0, 0);
            if (audio.FixedSampleSize > 0)
            {
                WriteU32(box, audio.FixedSampleSize);
                WriteU32(box, checked((uint)audio.FixedSampleCount));
            }
            else
            {
                WriteU32(box, 0);
                WriteU32(box, checked((uint)audio.Samples.Count));
                foreach (Mp4AudioSourceSample sample in audio.Samples) WriteU32(box, sample.Size);
            }
        }));
        writer.Write(BuildChunkOffsetBox(chunks));
    });

    private static List<(uint Count, uint Delta)> BuildVideoTimeRuns(ScanState state, uint fallbackDelta)
    {
        var runs = new List<(uint Count, uint Delta)>();
        foreach (Sample sample in state.Samples)
        {
            uint delta = Math.Max(1u, sample.Duration == 0 ? fallbackDelta : sample.Duration);
            AddTimeRun(runs, delta);
        }
        return runs;
    }

    private static List<(uint Count, int Offset)> BuildCompositionRuns(ScanState state)
    {
        var runs = new List<(uint Count, int Offset)>();
        foreach (Sample sample in state.Samples)
        {
            if (runs.Count > 0 && runs[^1].Offset == sample.CompositionOffset && runs[^1].Count < uint.MaxValue)
            {
                (uint count, int offset) = runs[^1];
                runs[^1] = (count + 1, offset);
            }
            else
            {
                runs.Add((1, sample.CompositionOffset));
            }
        }
        return runs;
    }

    private static List<(uint Count, uint Delta)> BuildAudioTimeRuns(Mp4RecoveredAudioTrack audio)
    {
        var runs = new List<(uint Count, uint Delta)>();
        if (audio.FixedSampleSize > 0)
        {
            if (audio.FixedSampleCount <= 0 || audio.FixedSampleCount > uint.MaxValue)
                throw new InvalidDataException("Fixed-size ses sample sayısı stts sınırının dışında.");
            runs.Add((checked((uint)audio.FixedSampleCount), Math.Max(1u, audio.FixedSampleDelta)));
            return runs;
        }

        foreach (Mp4AudioSourceSample sample in audio.Samples)
            AddTimeRun(runs, Math.Max(1u, sample.Duration));
        return runs;
    }

    private static void AddTimeRun(List<(uint Count, uint Delta)> runs, uint delta)
    {
        if (runs.Count > 0 && runs[^1].Delta == delta && runs[^1].Count < uint.MaxValue)
        {
            (uint count, uint existingDelta) = runs[^1];
            runs[^1] = (count + 1, existingDelta);
        }
        else
        {
            runs.Add((1, delta));
        }
    }

    private static byte[] BuildStsc(IReadOnlyList<(ulong Offset, int Count)> chunks) => Box("stsc", box =>
    {
        FullHeader(box, 0, 0);
        if (chunks.Count == 0)
        {
            WriteU32(box, 0);
            return;
        }

        var entries = new List<(uint FirstChunk, uint SamplesPerChunk)>();
        int previous = -1;
        for (int i = 0; i < chunks.Count; i++)
        {
            int count = chunks[i].Count;
            if (count <= 0)
                throw new InvalidDataException("Chunk sample sayısı geçersiz.");
            if (count == previous)
                continue;
            entries.Add((checked((uint)i + 1), checked((uint)count)));
            previous = count;
        }

        WriteU32(box, checked((uint)entries.Count));
        foreach ((uint firstChunk, uint samplesPerChunk) in entries)
        {
            WriteU32(box, firstChunk); WriteU32(box, samplesPerChunk); WriteU32(box, 1);
        }
    });

    internal static string ChunkOffsetBoxTypeForTest(ulong offset)
    {
        byte[] box = BuildChunkOffsetBox(new[] { (offset, 1) });
        return Encoding.ASCII.GetString(box, 4, 4);
    }

    private static byte[] BuildChunkOffsetBox(IReadOnlyList<(ulong Offset, int Count)> chunks)
    {
        bool use64 = chunks.Any(chunk => chunk.Offset > uint.MaxValue);
        return use64
            ? Box("co64", box =>
            {
                FullHeader(box, 0, 0); WriteU32(box, checked((uint)chunks.Count));
                foreach ((ulong offset, _) in chunks) WriteU64(box, offset);
            })
            : Box("stco", box =>
            {
                FullHeader(box, 0, 0); WriteU32(box, checked((uint)chunks.Count));
                foreach ((ulong offset, _) in chunks) WriteU32(box, checked((uint)offset));
            });
    }

    private static List<(ulong Offset, int Count)> BuildChunkLayout(IReadOnlyList<Sample> samples, ulong mediaOffset, int preferredSamplesPerChunk)
    {
        var chunks = new List<(ulong Offset, int Count)>();
        if (samples.Count == 0)
            return chunks;
        int perChunk = Math.Clamp(preferredSamplesPerChunk, 1, samples.Count);
        ulong offset = mediaOffset;
        int sampleIndex = 0;
        while (sampleIndex < samples.Count)
        {
            int count = Math.Min(perChunk, samples.Count - sampleIndex);
            chunks.Add((offset, count));
            for (int i = 0; i < count; i++)
                offset = checked(offset + samples[sampleIndex + i].Size);
            sampleIndex += count;
        }
        return chunks;
    }

    private static List<(ulong Offset, int Count)> BuildAudioChunkLayout(Mp4RecoveredAudioTrack audio)
    {
        var chunks = new List<(ulong Offset, int Count)>();
        int preferred = Math.Clamp(audio.PreferredSamplesPerChunk, 1, 4096);
        ulong offset = audio.OutputPayloadOffset;

        if (audio.FixedSampleSize > 0)
        {
            long remaining = audio.FixedSampleCount;
            while (remaining > 0)
            {
                int count = checked((int)Math.Min(preferred, remaining));
                chunks.Add((offset, count));
                offset = checked(offset + (ulong)audio.FixedSampleSize * (uint)count);
                remaining -= count;
            }
            return chunks;
        }

        int sampleIndex = 0;
        while (sampleIndex < audio.Samples.Count)
        {
            int count = Math.Min(preferred, audio.Samples.Count - sampleIndex);
            chunks.Add((offset, count));
            for (int i = 0; i < count; i++)
                offset = checked(offset + audio.Samples[sampleIndex + i].Size);
            sampleIndex += count;
        }
        return chunks;
    }

    private static byte[] BuildSampleEntry(ScanState state, Geometry geometry)
    {
        string type = state.Codec == CodecKind.H264 ? "avc1" : "hvc1";
        return Box(type, writer =>
        {
            writer.Write(new byte[6]); WriteU16(writer, 1); WriteU16(writer, 0); WriteU16(writer, 0);
            WriteU32(writer, 0); WriteU32(writer, 0); WriteU32(writer, 0);
            WriteU16(writer, checked((ushort)geometry.Width)); WriteU16(writer, checked((ushort)geometry.Height));
            WriteU32(writer, 0x00480000); WriteU32(writer, 0x00480000); WriteU32(writer, 0); WriteU16(writer, 1);
            writer.Write(new byte[32]); WriteU16(writer, 0x18); WriteU16(writer, 0xFFFF);
            writer.Write(state.Codec == CodecKind.H264 ? BuildAvcC(state) : BuildHvcC(state));
        });
    }

    private static byte[] BuildAvcC(ScanState state)
    {
        byte[] sps = state.Sps ?? throw new InvalidDataException("avcC için SPS eksik.");
        byte[] pps = state.Pps ?? throw new InvalidDataException("avcC için PPS eksik.");
        if (sps.Length < 4 || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue) throw new InvalidDataException("avcC parametre seti geçersiz.");
        return Box("avcC", writer =>
        {
            writer.Write((byte)1); writer.Write(sps[1]); writer.Write(sps[2]); writer.Write(sps[3]); writer.Write((byte)0xFF); writer.Write((byte)0xE1);
            WriteU16(writer, (ushort)sps.Length); writer.Write(sps); writer.Write((byte)1); WriteU16(writer, (ushort)pps.Length); writer.Write(pps);
        });
    }

    private static byte[] BuildHvcC(ScanState state)
    {
        byte[] vps = state.Vps ?? throw new InvalidDataException("hvcC için VPS eksik.");
        byte[] sps = state.Sps ?? throw new InvalidDataException("hvcC için SPS eksik.");
        byte[] pps = state.Pps ?? throw new InvalidDataException("hvcC için PPS eksik.");
        HevcProfile profile = ParseHevcProfile(sps);
        return Box("hvcC", writer =>
        {
            writer.Write((byte)1);
            writer.Write((byte)((profile.Space << 6) | (profile.Tier << 5) | profile.Idc));
            WriteU32(writer, profile.Compat); WriteU48(writer, profile.Constraints); writer.Write(profile.Level);
            WriteU16(writer, 0xF000); writer.Write((byte)0xFC); writer.Write((byte)0xFD); writer.Write((byte)0xF8); writer.Write((byte)0xF8);
            WriteU16(writer, 0); writer.Write((byte)0x0F); writer.Write((byte)3);
            HvcArray(writer, 32, vps); HvcArray(writer, 33, sps); HvcArray(writer, 34, pps);
        });
    }

    private static void HvcArray(BinaryWriter writer, int type, byte[] nal)
    {
        if (nal.Length > ushort.MaxValue) throw new InvalidDataException("HEVC parametre seti çok büyük.");
        writer.Write((byte)(0x80 | (type & 0x3F))); WriteU16(writer, 1); WriteU16(writer, (ushort)nal.Length); writer.Write(nal);
    }

    private static HevcProfile ParseHevcProfile(byte[] sps)
    {
        if (sps.Length < 6) return new HevcProfile(0, 0, 1, 0, 0, 120);
        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(2)));
        if (!bits.TrySkip(4) || !bits.TryReadBits(3, out _) || !bits.TrySkip(1) ||
            !bits.TryReadBits(2, out uint space) || !bits.TryReadBits(1, out uint tier) || !bits.TryReadBits(5, out uint idc) ||
            !bits.TryReadBits(32, out uint compat) || !bits.TryReadBits64(48, out ulong constraints) || !bits.TryReadBits(8, out uint level))
            return new HevcProfile(0, 0, 1, 0, 0, 120);
        return new HevcProfile((byte)space, (byte)tier, (byte)idc, compat, constraints, (byte)level);
    }

    private static byte[] Box(string type, Action<BinaryWriter> body)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteU32(writer, 0); WriteAscii(writer, type); body(writer); writer.Flush();
        if (stream.Length > uint.MaxValue) throw new InvalidDataException($"{type} kutusu 32-bit boyutu aşıyor.");
        stream.Position = 0; WriteU32(writer, (uint)stream.Length); writer.Flush();
        return stream.ToArray();
    }

    private static void CopyExactly(Stream source, Stream destination, long bytesToCopy)
    {
        if (bytesToCopy < 0)
            throw new ArgumentOutOfRangeException(nameof(bytesToCopy));

        byte[] buffer = new byte[IoBufferSize];
        long remaining = bytesToCopy;
        while (remaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, remaining);
            int read = source.Read(buffer, 0, requested);
            if (read <= 0)
                throw new EndOfStreamException("Progressive MP4 payload kopyası beklenenden erken sona erdi.");
            destination.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void WriteMdatHeader(Stream output, ulong totalSize)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(header[..4], 1);
        header[4] = (byte)'m'; header[5] = (byte)'d'; header[6] = (byte)'a'; header[7] = (byte)'t';
        BinaryPrimitives.WriteUInt64BigEndian(header[8..], totalSize);
        output.Write(header);
    }

    private static void PatchMdatSize(FileStream output, int ftypLength, ulong totalSize)
    {
        long returnPosition = output.Position;
        output.Position = checked(ftypLength + 8L);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, totalSize);
        output.Write(size);
        output.Position = returnPosition;
    }

    private static void FullHeader(BinaryWriter writer, byte version, uint flags)
    {
        writer.Write(version); writer.Write((byte)(flags >> 16)); writer.Write((byte)(flags >> 8)); writer.Write((byte)flags);
    }

    private static void UnityMatrix(BinaryWriter writer)
    {
        WriteU32(writer, 0x00010000); WriteU32(writer, 0); WriteU32(writer, 0);
        WriteU32(writer, 0); WriteU32(writer, 0x00010000); WriteU32(writer, 0);
        WriteU32(writer, 0); WriteU32(writer, 0); WriteU32(writer, 0x40000000);
    }

    private static void WriteAscii(BinaryWriter writer, string text) => writer.Write(Encoding.ASCII.GetBytes(text));
    private static void WriteU16(BinaryWriter writer, ushort value)
    {
        Span<byte> data = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(data, value); writer.Write(data);
    }
    private static void WriteU32(BinaryWriter writer, uint value)
    {
        Span<byte> data = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(data, value); writer.Write(data);
    }
    private static void WriteU64(BinaryWriter writer, ulong value)
    {
        Span<byte> data = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(data, value); writer.Write(data);
    }
    private static void WriteU48(BinaryWriter writer, ulong value)
    {
        Span<byte> data = stackalloc byte[6];
        for (int i = 5; i >= 0; i--) { data[i] = (byte)(value & 0xFF); value >>= 8; }
        writer.Write(data);
    }

    private static void ValidateOutput(
        string path,
        ScanState state,
        ulong mediaOffset,
        long videoPayloadBytes,
        long payloadBytes,
        ulong moovOffset,
        Mp4RecoveredAudioTrack? audioTrack)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 32 || mediaOffset >= (ulong)stream.Length || moovOffset + 8 > (ulong)stream.Length)
            throw new InvalidDataException("Yeniden oluşturulan MP4 sınırları geçersiz.");
        if (payloadBytes < 0 || (ulong)payloadBytes > (ulong)stream.Length - mediaOffset)
            throw new InvalidDataException("mdat payload boyutu dosyayla uyuşmuyor.");
        if (moovOffset >= mediaOffset || mediaOffset < 16)
            throw new InvalidDataException("Progressive MP4 kutu sıralaması geçersiz.");

        stream.Position = checked((long)mediaOffset - 16L);
        Span<byte> mdatHeader = stackalloc byte[16];
        if (stream.Read(mdatHeader) != 16 ||
            mdatHeader[4] != 'm' || mdatHeader[5] != 'd' || mdatHeader[6] != 'a' || mdatHeader[7] != 't')
            throw new InvalidDataException("Progressive MP4 mdat başlığı doğrulanamadı.");
        ulong mdatSize = BinaryPrimitives.ReadUInt64BigEndian(mdatHeader[8..]);
        if (BinaryPrimitives.ReadUInt32BigEndian(mdatHeader[..4]) != 1 || mdatSize != checked((ulong)payloadBytes + 16UL))
            throw new InvalidDataException("Progressive MP4 mdat boyutu geçersiz.");

        stream.Position = (long)moovOffset;
        Span<byte> header = stackalloc byte[8];
        if (stream.Read(header) != 8 || header[4] != 'm' || header[5] != 'o' || header[6] != 'o' || header[7] != 'v')
            throw new InvalidDataException("Yeni moov beklenen konumda bulunamadı.");
        uint moovSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        if (moovSize < 8 || moovOffset + moovSize > (ulong)stream.Length)
            throw new InvalidDataException("Yeni moov kutu boyutu geçersiz.");

        int probeLength = (int)Math.Min((long)moovSize, 64L * 1024 * 1024);
        byte[] probe = new byte[probeLength];
        stream.Position = (long)moovOffset; stream.ReadExactly(probe);
        if (!Contains(probe, "trak") || !Contains(probe, "stbl") || !Contains(probe, "stts") ||
            !Contains(probe, "ctts") || !Contains(probe, "stsc") || !Contains(probe, "stsz") ||
            (!Contains(probe, "stco") && !Contains(probe, "co64")) || !Contains(probe, "stss"))
            throw new InvalidDataException("Video moov/sample tabloları doğrulanamadı.");

        ulong videoSum = 0;
        foreach (Sample sample in state.Samples) videoSum = checked(videoSum + sample.Size);
        if (videoSum != (ulong)videoPayloadBytes)
            throw new InvalidDataException("Video stsz toplamı video mdat payload ile uyuşmuyor.");

        if (audioTrack is null)
        {
            if (videoPayloadBytes != payloadBytes)
                throw new InvalidDataException("Audio track yokken mdat payload içinde açıklanamayan veri kaldı.");
            if (CountAscii(probe, "trak") != 1)
                throw new InvalidDataException("Video-only moov beklenmeyen track sayısı içeriyor.");
            return;
        }

        if (CountAscii(probe, "trak") < 2 || !Contains(probe, "soun") || !Contains(probe, audioTrack.SampleEntryType))
            throw new InvalidDataException("Multi-track moov içinde ses trak/sample-entry doğrulanamadı.");
        if (audioTrack.OutputPayloadOffset != checked(mediaOffset + (ulong)videoPayloadBytes))
            throw new InvalidDataException("Ses payload başlangıcı video payload sonuyla uyuşmuyor.");
        if (checked(videoPayloadBytes + audioTrack.OutputPayloadBytes) != payloadBytes)
            throw new InvalidDataException("Video+ses payload toplamı mdat boyutuyla uyuşmuyor.");
    }

    private static bool Contains(byte[] data, string value) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(value)) >= 0;

    private static int CountAscii(byte[] data, string value)
    {
        byte[] needle = Encoding.ASCII.GetBytes(value);
        if (needle.Length == 0 || data.Length < needle.Length)
            return 0;
        int count = 0;
        int position = 0;
        while (position <= data.Length - needle.Length)
        {
            int found = data.AsSpan(position).IndexOf(needle);
            if (found < 0)
                break;
            count++;
            position += found + needle.Length;
        }
        return count;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed class TimeRunCursor
    {
        private readonly Mp4TimeToSampleRun[] _runs;
        private readonly uint _fallback;
        private int _index;
        private uint _remaining;

        public TimeRunCursor(Mp4TimeToSampleRun[]? runs, uint fallback)
        {
            _runs = runs?.Where(run => run.Count > 0 && run.Delta > 0).ToArray() ?? Array.Empty<Mp4TimeToSampleRun>();
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
                _index = (_index + 1) % _runs.Length;
                _remaining = _runs[_index].Count;
            }
            _remaining--;
            return _runs[_index].Delta;
        }
    }

    private sealed class CompositionRunCursor
    {
        private readonly Mp4CompositionOffsetRun[] _runs;
        private int _index;
        private uint _remaining;

        public CompositionRunCursor(Mp4CompositionOffsetRun[]? runs)
        {
            _runs = runs?.Where(run => run.Count > 0).ToArray() ?? Array.Empty<Mp4CompositionOffsetRun>();
            if (_runs.Length > 0)
                _remaining = _runs[0].Count;
        }

        public int Next()
        {
            if (_runs.Length == 0)
                return 0;
            if (_remaining == 0)
            {
                _index = (_index + 1) % _runs.Length;
                _remaining = _runs[_index].Count;
            }
            _remaining--;
            return _runs[_index].Offset;
        }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _position;
        public BitReader(byte[] data) => _data = data;

        public bool TryReadBit(out bool value)
        {
            value = false;
            if (_position >= _data.Length * 8) return false;
            int index = _position >> 3, shift = 7 - (_position & 7);
            value = ((_data[index] >> shift) & 1) != 0; _position++; return true;
        }
        public bool TrySkip(int count)
        {
            if (count < 0 || _position + count > _data.Length * 8) return false;
            _position += count; return true;
        }
        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (count < 0 || count > 32 || _position + count > _data.Length * 8) return false;
            for (int i = 0; i < count; i++) { if (!TryReadBit(out bool bit)) return false; value = (value << 1) | (bit ? 1u : 0u); }
            return true;
        }
        public bool TryReadBits64(int count, out ulong value)
        {
            value = 0;
            if (count < 0 || count > 64 || _position + count > _data.Length * 8) return false;
            for (int i = 0; i < count; i++) { if (!TryReadBit(out bool bit)) return false; value = (value << 1) | (bit ? 1UL : 0UL); }
            return true;
        }
        public bool TryReadUE(out uint value)
        {
            value = 0; int zeros = 0;
            while (true)
            {
                if (!TryReadBit(out bool bit)) return false;
                if (bit) break;
                if (++zeros > 31) return false;
            }
            if (zeros == 0) return true;
            if (!TryReadBits(zeros, out uint suffix)) return false;
            value = ((1u << zeros) - 1) + suffix; return true;
        }
        public bool TryReadSE(out int value)
        {
            value = 0;
            if (!TryReadUE(out uint code)) return false;
            value = (code & 1) == 0 ? -(int)(code / 2) : (int)((code + 1) / 2); return true;
        }
    }
}
