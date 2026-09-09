using System.Buffers;
using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

public sealed record VideoFragmentBoundaryResult(
    long StartOffset,
    long Length,
    bool IsExact,
    string Family,
    string Evidence);

public static class VideoFragmentBoundaryService
{
    private const int SearchBlockBytes = 1024 * 1024;
    private const long MaxSequentialScanBytes = 128L * 1024 * 1024;
    private const int MinimumGenericBoundaryBytes = 512;
    private const long PtsClock = 90_000L;
    private const long PtsWrap = 1L << 33;

    private const uint BoxFtyp = 0x66747970;
    private const uint BoxStyp = 0x73747970;
    private const uint BoxMoof = 0x6D6F6F66;
    private const uint BoxMdat = 0x6D646174;
    private const uint BoxMoov = 0x6D6F6F76;
    private const uint BoxSidx = 0x73696478;
    private const uint BoxFree = 0x66726565;
    private const uint BoxSkip = 0x736B6970;
    private const uint BoxEmsg = 0x656D7367;
    private const uint BoxPrft = 0x70726674;

    private const ulong EbmlHeaderId = 0x1A45DFA3;
    private const ulong SegmentId = 0x18538067;
    private const ulong ClusterId = 0x1F43B675;

    public static VideoFragmentBoundaryResult? Detect(
        RawDeviceReader reader,
        SignatureKind kind,
        long startOffset,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (startOffset < 0 || volumeLength <= startOffset)
            return null;

        var source = new RawFragmentSource(reader, startOffset, volumeLength - startOffset);
        VideoFragmentBoundaryResult? result = DetectCore(source, kind, cancellationToken);
        return result is null
            ? null
            : result with { StartOffset = checked(startOffset + result.StartOffset) };
    }

    public static VideoFragmentBoundaryResult? Detect(
        ReadOnlyMemory<byte> data,
        SignatureKind kind,
        CancellationToken cancellationToken = default)
    {
        if (data.Length == 0)
            return null;

        return DetectCore(new MemoryFragmentSource(data), kind, cancellationToken);
    }

    private static VideoFragmentBoundaryResult? DetectCore(
        IFragmentSource source,
        SignatureKind kind,
        CancellationToken cancellationToken) => kind switch
        {
            SignatureKind.MpegTs => DetectTransport(source, cancellationToken),
            SignatureKind.IsoBmff or SignatureKind.IsoBmffFragment => DetectIsoBmff(source, cancellationToken),
            SignatureKind.H264AnnexB => DetectAnnexB(source, h265: false, cancellationToken),
            SignatureKind.H265AnnexB => DetectAnnexB(source, h265: true, cancellationToken),
            SignatureKind.H264LengthPrefixed => DetectLengthPrefixed(source, h265: false, cancellationToken),
            SignatureKind.H265LengthPrefixed => DetectLengthPrefixed(source, h265: true, cancellationToken),
            SignatureKind.Matroska or SignatureKind.MatroskaCluster => DetectMatroska(source, cancellationToken),
            SignatureKind.MpegProgramStream or SignatureKind.MpegVideoStream => DetectMpeg(source, cancellationToken),
            _ => null
        };

    private static VideoFragmentBoundaryResult? DetectAnnexB(
        IFragmentSource source,
        bool h265,
        CancellationToken cancellationToken)
    {
        long scanLimit = Math.Min(source.Length, MaxSequentialScanBytes);
        if (scanLimit < 8)
            return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(SearchBlockBytes + 8);
        try
        {
            long cursor = 0;
            long lastMarker = -1;
            NalMarker? previous = null;
            long lastCompleteEnd = 0;
            var state = new NalBoundaryState(h265);

            while (cursor < scanLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int request = (int)Math.Min(SearchBlockBytes + 8L, scanLimit - cursor);
                int read = ReadAtMost(source, cursor, buffer.AsSpan(0, request));
                if (read <= 0)
                    break;

                int safeEnd = read - 3;
                for (int index = 0; index < safeEnd; index++)
                {
                    int prefixLength = GetAnnexPrefixLength(buffer.AsSpan(0, read), index);
                    if (prefixLength == 0 || index + prefixLength >= read)
                        continue;

                    long absolute = cursor + index;
                    if (absolute <= lastMarker)
                        continue;

                    int nalType = GetNalType(buffer[index + prefixLength], h265);
                    if (!IsPlausibleNalType(nalType, h265))
                        continue;

                    var current = new NalMarker(absolute, prefixLength, nalType);
                    if (previous is NalMarker completed)
                    {
                        long boundary = state.Process(
                            completed,
                            absolute,
                            HashNalPrefix(source, completed, absolute));
                        lastCompleteEnd = absolute;
                        if (boundary > 0)
                        {
                            return CreateResult(
                                boundary,
                                isExact: true,
                                h265 ? "H265-ANNEXB" : "H264-ANNEXB",
                                "parameter-set/IDR or end-of-sequence boundary",
                                source.Length);
                        }
                    }

                    previous = current;
                    lastMarker = absolute;
                    index += prefixLength - 1;
                }

                if (read < request)
                    break;

                long advance = Math.Max(1, read - 8L);
                cursor += advance;
            }

            if (lastCompleteEnd < MinimumGenericBoundaryBytes)
                return null;

            return CreateResult(
                lastCompleteEnd,
                isExact: false,
                h265 ? "H265-ANNEXB" : "H264-ANNEXB",
                "last complete NAL boundary",
                source.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static VideoFragmentBoundaryResult? DetectLengthPrefixed(
        IFragmentSource source,
        bool h265,
        CancellationToken cancellationToken)
    {
        long scanLimit = Math.Min(source.Length, MaxSequentialScanBytes);
        long position = 0;
        long lastCompleteEnd = 0;
        var state = new NalBoundaryState(h265);
        Span<byte> prefix = stackalloc byte[6];

        while (position + 6 <= source.Length && position < scanLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReadExact(source, position, prefix))
                break;

            uint nalLength = BinaryPrimitives.ReadUInt32BigEndian(prefix[..4]);
            if (nalLength < (h265 ? 2u : 1u))
                break;

            long end;
            try
            {
                end = checked(position + 4L + nalLength);
            }
            catch (OverflowException)
            {
                break;
            }

            if (end > source.Length)
                break;

            int nalType = GetNalType(prefix[4], h265);
            if (!IsPlausibleNalType(nalType, h265))
                break;

            var marker = new NalMarker(position, 4, nalType);
            long boundary = state.Process(marker, end, HashNalPrefix(source, marker, end));
            if (boundary > 0)
            {
                return CreateResult(
                    boundary,
                    isExact: true,
                    h265 ? "H265-LENGTH" : "H264-LENGTH",
                    "parameter-set/IDR or end-of-sequence boundary",
                    source.Length);
            }

            lastCompleteEnd = end;
            position = end;
        }

        if (lastCompleteEnd < MinimumGenericBoundaryBytes)
            return null;

        return CreateResult(
            lastCompleteEnd,
            isExact: lastCompleteEnd == source.Length,
            h265 ? "H265-LENGTH" : "H264-LENGTH",
            "last complete length-prefixed NAL",
            source.Length);
    }

    private static VideoFragmentBoundaryResult? DetectTransport(
        IFragmentSource source,
        CancellationToken cancellationToken)
    {
        int probeLength = (int)Math.Min(source.Length, 192L * 12);
        if (probeLength < 188 * 5)
            return null;

        byte[] probe = new byte[probeLength];
        if (ReadAtMost(source, 0, probe) < probeLength)
            return null;

        if (!TryDetectTransportGeometry(probe, out int packetSize, out int syncOffset))
            return null;

        long scanLimit = Math.Min(source.Length, MaxSequentialScanBytes);
        long packetPosition = 0;
        long lastValidEnd = 0;
        ulong? initialPat = null;
        ulong? pendingPat = null;
        long pendingPatOffset = -1;
        int pendingPatCount = 0;
        var lastPtsByPid = new Dictionary<int, long>();

        int packetsPerBlock = Math.Max(1, SearchBlockBytes / packetSize);
        byte[] block = ArrayPool<byte>.Shared.Rent(packetsPerBlock * packetSize);
        try
        {
            while (packetPosition + packetSize <= scanLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int packetCount = (int)Math.Min(
                    packetsPerBlock,
                    (scanLimit - packetPosition) / packetSize);
                int request = packetCount * packetSize;
                int read = ReadAtMost(source, packetPosition, block.AsSpan(0, request));
                int readablePackets = read / packetSize;
                if (readablePackets <= 0)
                    break;

                for (int packetIndex = 0; packetIndex < readablePackets; packetIndex++)
                {
                    long absolutePacket = packetPosition + packetIndex * (long)packetSize;
                    ReadOnlySpan<byte> packet = block.AsSpan(packetIndex * packetSize, packetSize);
                    if (packet[syncOffset] != 0x47)
                    {
                        return lastValidEnd >= packetSize * 5L
                            ? CreateResult(lastValidEnd, false, packetSize == 192 ? "M2TS" : "TS",
                                "last complete transport packet before sync loss", source.Length)
                            : null;
                    }

                    ReadOnlySpan<byte> tsPacket = packet.Slice(syncOffset, 188);
                    int pid = ((tsPacket[1] & 0x1F) << 8) | tsPacket[2];
                    bool payloadUnitStart = (tsPacket[1] & 0x40) != 0;
                    if (!TryGetTransportPayload(tsPacket, out ReadOnlySpan<byte> payload))
                    {
                        lastValidEnd = absolutePacket + packetSize;
                        continue;
                    }

                    if (pid == 0 && payloadUnitStart && TryReadPatFingerprint(payload, out ulong patFingerprint))
                    {
                        if (initialPat is null)
                        {
                            initialPat = patFingerprint;
                        }
                        else if (patFingerprint != initialPat.Value && absolutePacket >= packetSize * 16L)
                        {
                            if (pendingPat == patFingerprint)
                            {
                                pendingPatCount++;
                            }
                            else
                            {
                                pendingPat = patFingerprint;
                                pendingPatOffset = absolutePacket;
                                pendingPatCount = 1;
                            }

                            if (pendingPatCount >= 2)
                            {
                                return CreateResult(
                                    pendingPatOffset,
                                    isExact: true,
                                    packetSize == 192 ? "M2TS" : "TS",
                                    "repeated foreign PAT/program boundary",
                                    source.Length);
                            }
                        }
                        else
                        {
                            pendingPat = null;
                            pendingPatOffset = -1;
                            pendingPatCount = 0;
                        }
                    }

                    if (payloadUnitStart && TryReadPesPts(payload, out int streamId, out long pts) &&
                        streamId is >= 0xE0 and <= 0xEF)
                    {
                        if (lastPtsByPid.TryGetValue(pid, out long previousPts) &&
                            LooksLikePtsReset(previousPts, pts))
                        {
                            return CreateResult(
                                absolutePacket,
                                isExact: true,
                                packetSize == 192 ? "M2TS" : "TS",
                                "video PTS reset boundary",
                                source.Length);
                        }

                        lastPtsByPid[pid] = pts;
                    }

                    lastValidEnd = absolutePacket + packetSize;
                }

                packetPosition += readablePackets * (long)packetSize;
                if (readablePackets < packetCount)
                    break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(block);
        }

        if (lastValidEnd < packetSize * 5L)
            return null;

        return CreateResult(
            lastValidEnd,
            isExact: lastValidEnd == source.Length,
            packetSize == 192 ? "M2TS" : "TS",
            "last complete transport packet",
            source.Length);
    }

    private static VideoFragmentBoundaryResult? DetectIsoBmff(
        IFragmentSource source,
        CancellationToken cancellationToken)
    {
        if (!TryReadIsoBox(source, 0, out IsoBox first))
            return null;

        if (first.OpenEnded)
        {
            long nextFile = FindNextIsoFileStart(source, first.HeaderSize, cancellationToken);
            long openEnd = nextFile > 0 ? nextFile : Math.Min(source.Length, MaxSequentialScanBytes);
            return openEnd >= first.HeaderSize
                ? CreateResult(openEnd, nextFile > 0, "ISO-BMFF", "open-ended box capped at next ftyp", source.Length)
                : null;
        }

        if (first.Type is BoxMdat or BoxMoov)
        {
            return CreateResult(
                first.Size,
                isExact: true,
                "ISO-BMFF",
                first.Type == BoxMdat ? "declared mdat box" : "declared moov box",
                source.Length);
        }

        long position = 0;
        long lastValidEnd = 0;
        bool sawMoof = false;
        bool sawMdat = false;
        int boxCount = 0;

        while (position + 8 <= source.Length && boxCount < 100_000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadIsoBox(source, position, out IsoBox box))
                break;

            if (position > 0 && box.Type == BoxFtyp)
            {
                return CreateResult(position, true, "ISO-BMFF", "next independent ftyp boundary", source.Length);
            }

            if (box.OpenEnded)
            {
                long nextFile = FindNextIsoFileStart(source, position + box.HeaderSize, cancellationToken);
                long end = nextFile > position ? nextFile : Math.Min(source.Length, MaxSequentialScanBytes);
                return CreateResult(end, nextFile > position, "ISO-BMFF", "open-ended box boundary", source.Length);
            }

            long next;
            try
            {
                next = checked(position + box.Size);
            }
            catch (OverflowException)
            {
                break;
            }

            lastValidEnd = next;
            boxCount++;

            if (box.Type == BoxMoof)
                sawMoof = true;
            else if (box.Type == BoxMdat)
            {
                sawMdat = true;
                if (sawMoof || first.Type is BoxStyp or BoxMoof)
                {
                    return CreateResult(
                        next,
                        isExact: true,
                        "ISO-BMFF",
                        "complete moof/mdat fragment pair",
                        source.Length);
                }
            }

            if (first.Type == BoxFtyp && box.Type == BoxMdat)
            {
                return CreateResult(next, true, "ISO-BMFF", "ftyp container through first mdat", source.Length);
            }

            position = next;
            if (position >= Math.Min(source.Length, MaxSequentialScanBytes) && !sawMdat)
                break;

            if (position + 8 <= source.Length &&
                TryReadIsoBox(source, position, out IsoBox nextBox) &&
                sawMdat && nextBox.Type is BoxMoof or BoxStyp or BoxFtyp)
            {
                return CreateResult(position, true, "ISO-BMFF", "next fragment/file box boundary", source.Length);
            }
        }

        if (lastValidEnd < 8)
            return null;

        return CreateResult(
            lastValidEnd,
            isExact: lastValidEnd == source.Length,
            "ISO-BMFF",
            "last complete top-level box",
            source.Length);
    }

    private static VideoFragmentBoundaryResult? DetectMatroska(
        IFragmentSource source,
        CancellationToken cancellationToken)
    {
        if (!TryReadEbmlElement(source, 0, out EbmlElement first))
            return null;

        long position;
        long lastCompleteEnd;
        bool sawCluster;
        long scanLimit = Math.Min(source.Length, MaxSequentialScanBytes);

        if (first.Id == EbmlHeaderId)
        {
            position = first.End;
            lastCompleteEnd = first.End;
            sawCluster = false;

            if (position < source.Length && TryReadEbmlElement(source, position, out EbmlElement segment) &&
                segment.Id == SegmentId)
            {
                if (!segment.UnknownSize)
                {
                    return CreateResult(segment.End, true, "MATROSKA", "declared Segment boundary", source.Length);
                }

                position = segment.PayloadOffset;
                lastCompleteEnd = position;
            }
        }
        else if (first.Id == ClusterId)
        {
            // Metadata-loss carving can start directly at a surviving Cluster.
            // Treat it as a fragment root and walk only complete consecutive EBML elements.
            position = 0;
            lastCompleteEnd = 0;
            sawCluster = true;
        }
        else
        {
            return null;
        }

        while (position + 2 <= scanLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long nextHeader = FindPattern(source, position, scanLimit, [0x1A, 0x45, 0xDF, 0xA3], cancellationToken);
            if (nextHeader == position && position > 0)
            {
                return CreateResult(position, true, "MATROSKA", "next EBML document boundary", source.Length);
            }

            if (TryReadEbmlElement(source, position, out EbmlElement element))
            {
                if (element.Id == EbmlHeaderId && position > 0)
                    return CreateResult(position, true, "MATROSKA", "next EBML document boundary", source.Length);

                if (element.Id == ClusterId)
                    sawCluster = true;

                if (!element.UnknownSize)
                {
                    lastCompleteEnd = element.End;
                    position = element.End;
                    continue;
                }
            }

            long nextCluster = FindPattern(source, position + 1, scanLimit, [0x1F, 0x43, 0xB6, 0x75], cancellationToken);
            long nextEbml = FindPattern(source, position + 1, scanLimit, [0x1A, 0x45, 0xDF, 0xA3], cancellationToken);
            if (nextEbml >= 0 && (nextCluster < 0 || nextEbml < nextCluster))
                return CreateResult(nextEbml, true, "MATROSKA", "next EBML document boundary", source.Length);

            if (nextCluster < 0)
                break;

            sawCluster = true;
            position = nextCluster;
        }

        if (!sawCluster || lastCompleteEnd < MinimumGenericBoundaryBytes)
            return null;

        return CreateResult(
            lastCompleteEnd,
            isExact: lastCompleteEnd == source.Length,
            "MATROSKA",
            "last complete Cluster/EBML element",
            source.Length);
    }

    private static VideoFragmentBoundaryResult? DetectMpeg(
        IFragmentSource source,
        CancellationToken cancellationToken)
    {
        long scanLimit = Math.Min(source.Length, MaxSequentialScanBytes);
        if (scanLimit < 8)
            return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(SearchBlockBytes + 8);
        try
        {
            long cursor = 0;
            long lastCodePosition = -1;
            long lastCompleteEnd = 0;
            bool sawPicture = false;
            ulong firstSequenceFingerprint = 0;
            Span<byte> lengthBytes = stackalloc byte[2];

            while (cursor < scanLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int request = (int)Math.Min(SearchBlockBytes + 8L, scanLimit - cursor);
                int read = ReadAtMost(source, cursor, buffer.AsSpan(0, request));
                if (read <= 0)
                    break;

                for (int index = 0; index + 4 <= read; index++)
                {
                    if (buffer[index] != 0x00 || buffer[index + 1] != 0x00 || buffer[index + 2] != 0x01)
                        continue;

                    long absolute = cursor + index;
                    if (absolute <= lastCodePosition)
                        continue;
                    lastCodePosition = absolute;

                    byte code = buffer[index + 3];
                    if (code is 0xB7 or 0xB9)
                    {
                        return CreateResult(
                            absolute + 4,
                            isExact: true,
                            "MPEG",
                            code == 0xB7 ? "sequence end code" : "program end code",
                            source.Length);
                    }

                    if (code == 0x00)
                        sawPicture = true;

                    if (code == 0xB3)
                    {
                        ulong fingerprint = HashRange(source, absolute + 4, Math.Min(64, source.Length - absolute - 4));
                        if (firstSequenceFingerprint == 0)
                        {
                            firstSequenceFingerprint = fingerprint;
                        }
                        else if (sawPicture && fingerprint != 0 && fingerprint != firstSequenceFingerprint)
                        {
                            return CreateResult(
                                absolute,
                                isExact: true,
                                "MPEG",
                                "new sequence-header fingerprint boundary",
                                source.Length);
                        }
                    }

                    if (code is >= 0xBD and <= 0xEF && absolute + 6 <= source.Length)
                    {
                        if (ReadExact(source, absolute + 4, lengthBytes))
                        {
                            int packetLength = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
                            if (packetLength > 0)
                            {
                                long end = absolute + 6L + packetLength;
                                if (end <= source.Length)
                                    lastCompleteEnd = Math.Max(lastCompleteEnd, end);
                            }
                        }
                    }
                    else if (code == 0xBA)
                    {
                        int packLength = TryReadMpegPackLength(source, absolute);
                        if (packLength > 0 && absolute + packLength <= source.Length)
                            lastCompleteEnd = Math.Max(lastCompleteEnd, absolute + packLength);
                    }

                    index += 3;
                }

                if (read < request)
                    break;
                cursor += Math.Max(1, read - 8L);
            }

            long conservativeEnd = Math.Max(lastCompleteEnd, lastCodePosition);
            if (conservativeEnd < MinimumGenericBoundaryBytes)
                return null;

            return CreateResult(
                conservativeEnd,
                isExact: conservativeEnd == source.Length,
                "MPEG",
                "last complete PES/pack/GOP boundary",
                source.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int TryReadMpegPackLength(IFragmentSource source, long offset)
    {
        Span<byte> header = stackalloc byte[14];
        if (!ReadExact(source, offset, header[..12]))
            return 0;

        if ((header[4] & 0xC0) == 0x40)
        {
            if (!ReadExact(source, offset, header))
                return 0;
            return 14 + (header[13] & 0x07);
        }

        if ((header[4] & 0xF0) == 0x20)
            return 12;

        return 0;
    }

    private static bool TryDetectTransportGeometry(ReadOnlySpan<byte> data, out int packetSize, out int syncOffset)
    {
        foreach ((int size, int offset) in new[] { (188, 0), (192, 4) })
        {
            bool valid = true;
            for (int packet = 0; packet < 5; packet++)
            {
                int index = offset + packet * size;
                if (index >= data.Length || data[index] != 0x47)
                {
                    valid = false;
                    break;
                }
            }

            if (valid)
            {
                packetSize = size;
                syncOffset = offset;
                return true;
            }
        }

        packetSize = 0;
        syncOffset = 0;
        return false;
    }

    private static bool TryGetTransportPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (packet.Length < 188 || packet[0] != 0x47)
            return false;

        int adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl is 0 or 2)
            return false;

        int offset = 4;
        if (adaptationControl == 3)
        {
            int adaptationLength = packet[4];
            offset = 5 + adaptationLength;
        }

        if (offset < 0 || offset >= packet.Length)
            return false;

        payload = packet[offset..];
        return payload.Length > 0;
    }

    private static bool TryReadPatFingerprint(ReadOnlySpan<byte> payload, out ulong fingerprint)
    {
        fingerprint = 0;
        if (payload.Length < 8)
            return false;

        int pointer = payload[0];
        int sectionStart = 1 + pointer;
        if (sectionStart + 8 > payload.Length || payload[sectionStart] != 0x00)
            return false;

        int sectionLength = ((payload[sectionStart + 1] & 0x0F) << 8) | payload[sectionStart + 2];
        int sectionEnd = sectionStart + 3 + sectionLength;
        if (sectionLength < 9 || sectionEnd > payload.Length)
            return false;

        int payloadEnd = sectionEnd - 4;
        if (payloadEnd < sectionStart + 8)
            return false;

        fingerprint = HashBytes(payload.Slice(sectionStart, payloadEnd - sectionStart));
        return fingerprint != 0;
    }

    private static bool TryReadPesPts(ReadOnlySpan<byte> payload, out int streamId, out long pts)
    {
        streamId = -1;
        pts = -1;
        if (payload.Length < 14 || payload[0] != 0x00 || payload[1] != 0x00 || payload[2] != 0x01)
            return false;

        streamId = payload[3];
        if ((payload[6] & 0xC0) != 0x80)
            return false;

        int flags = (payload[7] >> 6) & 0x03;
        if (flags is not (2 or 3) || payload[8] < 5)
            return false;

        ReadOnlySpan<byte> value = payload.Slice(9, 5);
        if ((value[0] & 0x01) == 0 || (value[2] & 0x01) == 0 || (value[4] & 0x01) == 0)
            return false;

        pts = ((long)(value[0] & 0x0E) << 29) |
              ((long)value[1] << 22) |
              ((long)(value[2] & 0xFE) << 14) |
              ((long)value[3] << 7) |
              ((long)(value[4] & 0xFE) >> 1);
        return true;
    }

    private static bool LooksLikePtsReset(long previous, long current)
    {
        if (previous < 10 * PtsClock || current >= 2 * PtsClock)
            return false;

        if (previous > PtsWrap - 10 * PtsClock)
            return false;

        return previous - current > 5 * PtsClock;
    }

    private static bool TryReadIsoBox(IFragmentSource source, long position, out IsoBox box)
    {
        box = default;
        if (position < 0 || position + 8 > source.Length)
            return false;

        Span<byte> header = stackalloc byte[16];
        if (!ReadExact(source, position, header[..8]))
            return false;

        uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        if (!IsPlausibleBoxType(type))
            return false;

        int headerSize = 8;
        long size;
        bool openEnded = false;
        if (size32 == 1)
        {
            if (!ReadExact(source, position, header))
                return false;
            ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            if (size64 > long.MaxValue)
                return false;
            size = (long)size64;
            headerSize = 16;
        }
        else if (size32 == 0)
        {
            size = source.Length - position;
            openEnded = true;
        }
        else
        {
            size = size32;
        }

        if (size < headerSize || size > source.Length - position)
            return false;

        box = new IsoBox(position, size, headerSize, type, openEnded);
        return true;
    }

    private static long FindNextIsoFileStart(
        IFragmentSource source,
        long start,
        CancellationToken cancellationToken)
    {
        long limit = Math.Min(source.Length, MaxSequentialScanBytes);
        long search = start;
        while (search < limit)
        {
            long typeOffset = FindPattern(source, search, limit, [(byte)'f', (byte)'t', (byte)'y', (byte)'p'], cancellationToken);
            if (typeOffset < 4)
                return -1;

            long boxOffset = typeOffset - 4;
            if (TryReadIsoBox(source, boxOffset, out IsoBox box) && box.Type == BoxFtyp)
                return boxOffset;

            search = typeOffset + 1;
        }

        return -1;
    }

    private static bool IsPlausibleBoxType(uint type)
    {
        byte a = (byte)(type >> 24);
        byte b = (byte)(type >> 16);
        byte c = (byte)(type >> 8);
        byte d = (byte)type;
        return IsFourCcByte(a) && IsFourCcByte(b) && IsFourCcByte(c) && IsFourCcByte(d);
    }

    private static bool IsFourCcByte(byte value) => value is >= 0x20 and <= 0x7E;

    private static bool TryReadEbmlElement(IFragmentSource source, long position, out EbmlElement element)
    {
        element = default;
        if (!TryReadEbmlVint(source, position, isSize: false, out ulong id, out int idLength, out _))
            return false;
        if (!TryReadEbmlVint(source, position + idLength, isSize: true, out ulong size, out int sizeLength, out bool unknown))
            return false;

        long payloadOffset;
        try
        {
            payloadOffset = checked(position + idLength + sizeLength);
        }
        catch (OverflowException)
        {
            return false;
        }

        long end;
        if (unknown)
        {
            end = source.Length;
        }
        else
        {
            if (size > long.MaxValue)
                return false;
            try
            {
                end = checked(payloadOffset + (long)size);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (end > source.Length)
                return false;
        }

        element = new EbmlElement(position, id, payloadOffset, end, unknown);
        return true;
    }

    private static bool TryReadEbmlVint(
        IFragmentSource source,
        long position,
        bool isSize,
        out ulong value,
        out int length,
        out bool unknown)
    {
        value = 0;
        length = 0;
        unknown = false;
        Span<byte> bytes = stackalloc byte[8];
        if (!ReadExact(source, position, bytes[..1]) || bytes[0] == 0)
            return false;

        byte marker = 0x80;
        while (length < 8 && (bytes[0] & marker) == 0)
        {
            marker >>= 1;
            length++;
        }
        length++;

        if (length is < 1 or > 8 || (!isSize && length > 4))
            return false;
        if (!ReadExact(source, position, bytes[..length]))
            return false;

        value = isSize ? (ulong)(bytes[0] & (marker - 1)) : bytes[0];
        for (int index = 1; index < length; index++)
            value = (value << 8) | bytes[index];

        if (isSize)
        {
            ulong allOnes = (1UL << (7 * length)) - 1;
            unknown = value == allOnes;
        }

        return true;
    }

    private static long FindPattern(
        IFragmentSource source,
        long start,
        long limit,
        byte[] pattern,
        CancellationToken cancellationToken)
    {
        if (pattern.Length == 0 || start < 0 || start >= limit)
            return -1;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(SearchBlockBytes + pattern.Length);
        try
        {
            long cursor = start;
            while (cursor < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int request = (int)Math.Min(SearchBlockBytes + pattern.Length - 1L, limit - cursor);
                int read = ReadAtMost(source, cursor, buffer.AsSpan(0, request));
                if (read < pattern.Length)
                    return -1;

                int found = buffer.AsSpan(0, read).IndexOf(pattern);
                if (found >= 0)
                    return cursor + found;

                if (read < request)
                    return -1;
                cursor += Math.Max(1, read - pattern.Length + 1L);
            }

            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int GetAnnexPrefixLength(ReadOnlySpan<byte> data, int index)
    {
        if (index + 3 <= data.Length && data[index] == 0x00 && data[index + 1] == 0x00)
        {
            if (data[index + 2] == 0x01)
                return 3;
            if (index + 4 <= data.Length && data[index + 2] == 0x00 && data[index + 3] == 0x01)
                return 4;
        }
        return 0;
    }

    private static int GetNalType(byte firstHeaderByte, bool h265) =>
        h265 ? (firstHeaderByte >> 1) & 0x3F : firstHeaderByte & 0x1F;

    private static bool IsPlausibleNalType(int nalType, bool h265) =>
        h265 ? nalType is >= 0 and <= 40 : nalType is >= 1 and <= 12;

    private static ulong HashNalPrefix(IFragmentSource source, NalMarker marker, long end)
    {
        long payloadStart = marker.Offset + marker.PrefixLength;
        long count = Math.Min(256, Math.Max(0, end - payloadStart));
        return HashRange(source, payloadStart, count);
    }

    private static ulong HashRange(IFragmentSource source, long position, long count)
    {
        if (count <= 0 || position < 0 || position >= source.Length)
            return 0;

        int length = (int)Math.Min(256, Math.Min(count, source.Length - position));
        Span<byte> bytes = stackalloc byte[256];
        int read = ReadAtMost(source, position, bytes[..length]);
        return read > 0 ? HashBytes(bytes[..read]) : 0;
    }

    private static ulong HashBytes(ReadOnlySpan<byte> bytes)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static VideoFragmentBoundaryResult? CreateResult(
        long length,
        bool isExact,
        string family,
        string evidence,
        long sourceLength)
    {
        if (length <= 0 || length > sourceLength)
            return null;
        return new VideoFragmentBoundaryResult(0, length, isExact, family, evidence);
    }

    private static int ReadAtMost(IFragmentSource source, long offset, Span<byte> destination)
    {
        if (destination.Length == 0 || offset < 0 || offset >= source.Length)
            return 0;

        int target = (int)Math.Min(destination.Length, source.Length - offset);
        int total = 0;
        while (total < target)
        {
            int read = source.Read(offset + total, destination.Slice(total, target - total));
            if (read <= 0)
                break;
            total += read;
        }
        return total;
    }

    private static bool ReadExact(IFragmentSource source, long offset, Span<byte> destination) =>
        ReadAtMost(source, offset, destination) == destination.Length;

    private interface IFragmentSource
    {
        long Length { get; }
        int Read(long offset, Span<byte> destination);
    }

    private sealed class RawFragmentSource : IFragmentSource
    {
        private readonly RawDeviceReader _reader;
        private readonly long _baseOffset;

        public RawFragmentSource(RawDeviceReader reader, long baseOffset, long length)
        {
            _reader = reader;
            _baseOffset = baseOffset;
            Length = length;
        }

        public long Length { get; }

        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= Length || destination.Length == 0)
                return 0;
            int count = (int)Math.Min(destination.Length, Length - offset);
            return _reader.Read(checked(_baseOffset + offset), destination[..count]);
        }
    }

    private sealed class MemoryFragmentSource : IFragmentSource
    {
        private readonly ReadOnlyMemory<byte> _memory;

        public MemoryFragmentSource(ReadOnlyMemory<byte> memory)
        {
            _memory = memory;
        }

        public long Length => _memory.Length;

        public int Read(long offset, Span<byte> destination)
        {
            if (offset < 0 || offset >= _memory.Length || destination.Length == 0)
                return 0;
            int count = Math.Min(destination.Length, _memory.Length - (int)offset);
            _memory.Span.Slice((int)offset, count).CopyTo(destination);
            return count;
        }
    }

    private readonly record struct NalMarker(long Offset, int PrefixLength, int Type);

    private sealed class NalBoundaryState
    {
        private readonly bool _h265;
        private bool _seenVcl;
        private bool _pendingSps;
        private bool _pendingPps;
        private long _pendingStart = -1;
        private ulong _initialParameterHash;
        private ulong _pendingParameterHash;

        public NalBoundaryState(bool h265)
        {
            _h265 = h265;
        }

        public long Process(NalMarker marker, long end, ulong hash)
        {
            bool parameter = IsParameter(marker.Type);
            bool vcl = IsVcl(marker.Type);
            bool randomAccess = IsRandomAccess(marker.Type);
            bool endMarker = IsEndMarker(marker.Type);

            if (endMarker && _seenVcl)
                return end;

            if (parameter)
            {
                if (!_seenVcl)
                {
                    _initialParameterHash = CombineHash(_initialParameterHash, hash);
                }
                else
                {
                    if (_pendingStart < 0)
                        _pendingStart = marker.Offset;
                    _pendingParameterHash = CombineHash(_pendingParameterHash, hash);
                    _pendingSps |= IsSps(marker.Type);
                    _pendingPps |= IsPps(marker.Type);
                }
                return -1;
            }

            if (vcl)
            {
                if (_pendingStart >= 0 && randomAccess && _pendingSps && _pendingPps)
                {
                    if (_initialParameterHash != 0 &&
                        _pendingParameterHash != 0 &&
                        _pendingParameterHash != _initialParameterHash)
                    {
                        return _pendingStart;
                    }
                }

                _seenVcl = true;
                ClearPending();
            }

            return -1;
        }

        private bool IsParameter(int type) => _h265 ? type is 32 or 33 or 34 : type is 7 or 8;
        private bool IsSps(int type) => _h265 ? type == 33 : type == 7;
        private bool IsPps(int type) => _h265 ? type == 34 : type == 8;
        private bool IsVcl(int type) => _h265 ? type is >= 0 and <= 31 : type is >= 1 and <= 5;
        private bool IsRandomAccess(int type) => _h265 ? type is 19 or 20 or 21 : type == 5;
        private bool IsEndMarker(int type) => _h265 ? type is 36 or 37 : type is 10 or 11;

        private void ClearPending()
        {
            _pendingStart = -1;
            _pendingParameterHash = 0;
            _pendingSps = false;
            _pendingPps = false;
        }

        private static ulong CombineHash(ulong current, ulong next)
        {
            if (next == 0)
                return current;
            return current == 0 ? next : unchecked((current * 1099511628211UL) ^ next);
        }
    }

    private readonly record struct IsoBox(long Offset, long Size, int HeaderSize, uint Type, bool OpenEnded);
    private readonly record struct EbmlElement(long Offset, ulong Id, long PayloadOffset, long End, bool UnknownSize);
}
