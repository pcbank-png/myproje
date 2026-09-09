using System.Buffers.Binary;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

public static class GlobalVideoRawAnalyzer
{
    private static readonly byte[] MxfHeaderPrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x02
    ];

    private static readonly byte[] MxfEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0D, 0x01, 0x03, 0x01
    ];

    private static readonly byte[] MxfAvidEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0E, 0x04, 0x03, 0x01
    ];

    private static readonly byte[] MxfCanopusEssencePrefix =
    [
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x0A,
        0x0E, 0x0F, 0x03, 0x01
    ];

    private static readonly byte[] WtvRootGuid =
    [
        0xB7, 0xD8, 0x00, 0x20, 0x37, 0x49, 0xDA, 0x11,
        0xA6, 0x4E, 0x00, 0x07, 0xE9, 0x5E, 0xAD, 0x8D
    ];

    private static readonly byte[] WtvSubGuid =
    [
        0x8C, 0xC3, 0xD2, 0xC2, 0x7E, 0x9A, 0xDA, 0x11,
        0x8B, 0xF7, 0x00, 0x07, 0xE9, 0x5E, 0xAD, 0x8D
    ];

    private static readonly byte[] MpegVideoSequenceSignature = new byte[] { 0x00, 0x00, 0x01, 0xB3 };
    private static readonly byte[] H264SpsSignature3 = new byte[] { 0x00, 0x00, 0x01, 0x67 };
    private static readonly byte[] H264SpsSignature4 = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 };
    private static readonly byte[] H265VpsSignature = new byte[] { 0x00, 0x00, 0x01, 0x40 };
    private static readonly byte[] Jpeg2000CodestreamSignature = new byte[] { 0xFF, 0x4F, 0xFF, 0x51 };
    private static readonly byte[] DnxHdSignature = new byte[] { 0x00, 0x00, 0x02, 0x80, 0x01 };

    public static RawFileAnalysis? AnalyzeMxf(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> first = stackalloc byte[16];
        if (!reader.ReadExact(start, first))
            return null;
        bool headerStart = first[..14].SequenceEqual(MxfHeaderPrefix) && first[14] is >= 0x01 and <= 0x04 && first[15] == 0x00;
        bool essenceStart = first[..12].SequenceEqual(MxfEssencePrefix) ||
                            first[..12].SequenceEqual(MxfAvidEssencePrefix) ||
                            first[..12].SequenceEqual(MxfCanopusEssencePrefix);
        if (!headerStart && !essenceStart)
            return null;

        long position = start;
        long lastGoodEnd = start;
        int klvCount = 0;
        int essenceCount = 0;
        bool videoEvidence = false;
        bool randomIndexPack = false;
        Span<byte> key = stackalloc byte[16];

        while (position + 17 <= volumeLength)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!reader.ReadExact(position, key) ||
                key[0] != 0x06 || key[1] != 0x0E || key[2] != 0x2B || key[3] != 0x34)
                break;

            if (!TryReadBerLength(reader, position + 16, volumeLength, out ulong valueLength, out int lengthBytes) ||
                valueLength > (ulong)long.MaxValue)
                break;

            long valueStart;
            long valueEnd;
            try
            {
                valueStart = checked(position + 16L + lengthBytes);
                valueEnd = checked(valueStart + (long)valueLength);
            }
            catch (OverflowException)
            {
                break;
            }

            if (valueEnd <= valueStart || valueEnd > volumeLength)
                break;

            bool essence = key[..12].SequenceEqual(MxfEssencePrefix) ||
                           key[..12].SequenceEqual(MxfAvidEssencePrefix) ||
                           key[..12].SequenceEqual(MxfCanopusEssencePrefix);

            if (essence)
            {
                essenceCount++;
                int inspect = (int)Math.Min(valueLength, 64UL * 1024);
                if (inspect > 0)
                {
                    byte[] payload = reader.ReadBytes(valueStart, inspect);
                    videoEvidence |= LooksLikeVideoEssence(payload);
                }
            }

            // Random Index Pack: ... 0D 01 02 01 01 11 01 00
            if (key[8] == 0x0D && key[9] == 0x01 && key[10] == 0x02 && key[11] == 0x01 &&
                key[12] == 0x01 && key[13] == 0x11 && key[14] == 0x01 && key[15] == 0x00)
                randomIndexPack = true;

            lastGoodEnd = valueEnd;
            position = valueEnd;
            klvCount++;

            if (randomIndexPack)
                break;
        }

        if (klvCount < 3 || essenceCount == 0 || !videoEvidence || lastGoodEnd <= start)
            return null;

        string state = essenceStart
            ? "Yeniden İnşa"
            : randomIndexPack ? "Çok İyi" : essenceCount >= 3 ? "İyi" : "Yeniden İnşa";

        return new RawFileAnalysis("MXF", lastGoodEnd - start, state);
    }

    public static RawFileAnalysis? AnalyzeDv(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        if (!LooksLikeDvSequenceStart(reader, start, volumeLength, out bool pal, out byte channelMarker))
            return null;

        int baseFrameSize = pal ? 144000 : 120000;
        int frameSize = DetermineDvFrameSize(reader, start, volumeLength, baseFrameSize, channelMarker);
        if (frameSize <= 0 || start + frameSize > volumeLength)
            return null;

        long position = start;
        int frames = 0;
        while (position + frameSize <= volumeLength)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!LooksLikeDvSequenceStart(reader, position, volumeLength, out bool framePal, out byte frameChannel) ||
                framePal != pal || frameChannel != channelMarker)
                break;

            if (!ValidateDvFrameGeometry(reader, position, frameSize, pal, volumeLength))
                break;

            frames++;
            position += frameSize;
        }

        if (frames == 0)
            return null;

        return new RawFileAnalysis("DV", (long)frames * frameSize, frames >= 2 ? "Çok İyi" : "İyi");
    }

    public static RawFileAnalysis? AnalyzeWtv(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        if (volumeLength - start < 0x1000)
            return null;

        byte[] header = reader.ReadBytes(start, 0x100);
        if (header.Length < 0x5C ||
            !header.AsSpan(0, 16).SequenceEqual(WtvRootGuid) ||
            !header.AsSpan(16, 16).SequenceEqual(WtvSubGuid))
            return null;

        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30, 4));
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38, 4));
        uint fileMetaUnits = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x58, 4));
        if (rootSize is 0 or > 0x1000 || fileMetaUnits == 0)
            return null;

        long length;
        try
        {
            length = checked((long)fileMetaUnits * 0x1000L);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (length < 0x40000 || length > volumeLength - start)
            return null;

        long rootOffset = (long)rootSector << 12;
        if (rootOffset <= 0 || rootOffset + rootSize > length)
            return null;

        byte[] root = reader.ReadBytes(start + rootOffset, (int)Math.Min(rootSize, 256u));
        if (root.Length < 16 || IsAllZero(root))
            return null;

        return new RawFileAnalysis("WTV", length, "İyi");
    }

    public static RawFileAnalysis? AnalyzeNsv(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        Span<byte> header = stackalloc byte[28];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual("NSVf"u8))
            return null;

        uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        if (chunkSize < 28 || fileSize < chunkSize || fileSize > (ulong)(volumeLength - start))
            return null;

        int inspectLength = (int)Math.Min((long)fileSize, 2L * 1024 * 1024);
        byte[] inspect = reader.ReadBytes(start, inspectLength);
        if (inspect.AsSpan().IndexOf("NSVs"u8) < 0)
            return null;

        return new RawFileAnalysis("NSV", fileSize, "İyi");
    }

    public static RawFileAnalysis? AnalyzeRoq(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        if (!reader.ReadExact(start, header) ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[..2]) != 0x1084 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[2..6]) != 0xFFFFFFFF ||
            BinaryPrimitives.ReadUInt16LittleEndian(header[6..8]) == 0)
            return null;

        long position = start + 8;
        long lastGood = position;
        bool hasInfo = false;
        int videoChunks = 0;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> info = stackalloc byte[8];

        while (position + 8 <= volumeLength)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!reader.ReadExact(position, chunk))
                break;

            ushort type = BinaryPrimitives.ReadUInt16LittleEndian(chunk[..2]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[2..6]);
            if (type is not (0x1001 or 0x1002 or 0x1011 or 0x1020 or 0x1021 or 0x1030))
                break;

            long next;
            try
            {
                next = checked(position + 8L + size);
            }
            catch (OverflowException)
            {
                break;
            }

            if (next > volumeLength)
                break;

            if (type == 0x1001 && size >= 8)
            {
                if (!reader.ReadExact(position + 8, info))
                    break;
                int width = BinaryPrimitives.ReadUInt16LittleEndian(info[..2]);
                int height = BinaryPrimitives.ReadUInt16LittleEndian(info[2..4]);
                hasInfo |= width is > 0 and <= 16384 && height is > 0 and <= 16384;
            }
            else if (type is 0x1002 or 0x1011)
            {
                videoChunks++;
            }

            lastGood = next;
            position = next;
        }

        if (!hasInfo || videoChunks == 0 || lastGood <= start + 8)
            return null;

        return new RawFileAnalysis("ROQ", lastGood - start, "İyi");
    }

    public static RawFileAnalysis? AnalyzeBink(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        Span<byte> header = stackalloc byte[44];
        if (!reader.ReadExact(start, header))
            return null;

        bool bink1 = header[0] == (byte)'B' && header[1] == (byte)'I' && header[2] == (byte)'K';
        bool bink2 = header[0] == (byte)'K' && header[1] == (byte)'B' && header[2] == (byte)'2';
        if (!bink1 && !bink2)
            return null;

        byte revision = header[3];
        if (!char.IsLetterOrDigit((char)revision))
            return null;

        ulong totalLength = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8UL;
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint largestFrame = BinaryPrimitives.ReadUInt32LittleEndian(header[12..16]);
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header[20..24]);
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]);
        uint fpsNum = BinaryPrimitives.ReadUInt32LittleEndian(header[28..32]);
        uint fpsDen = BinaryPrimitives.ReadUInt32LittleEndian(header[32..36]);

        if (totalLength < 44 || totalLength > (ulong)(volumeLength - start) ||
            frames is 0 or > 1_000_000 || largestFrame == 0 || largestFrame > totalLength ||
            width is 0 or > 32768 || height is 0 or > 32768 || fpsNum == 0 || fpsDen == 0)
            return null;

        return new RawFileAnalysis(bink2 ? "BK2" : "BIK", (long)totalLength, "Çok İyi");
    }

    public static RawFileAnalysis? AnalyzeSmacker(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        byte[] header = reader.ReadBytes(start, 104);
        if (header.Length != 104 ||
            !(header.AsSpan(0, 4).SequenceEqual("SMK2"u8) || header.AsSpan(0, 4).SequenceEqual("SMK4"u8)))
            return null;

        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint logicalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        uint treesSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(52, 4));

        if (width is 0 or > 32768 || height is 0 or > 32768 || logicalFrames is 0 or > 5_000_000 || (flags & ~0x07u) != 0)
            return null;

        long physicalFrames = logicalFrames + ((flags & 1) != 0 ? 1L : 0L);
        long tableStart = start + 104;
        long tableBytes;
        long frameDataStart;
        try
        {
            tableBytes = checked(physicalFrames * 5L);
            frameDataStart = checked(tableStart + tableBytes + treesSize);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (frameDataStart > volumeLength)
            return null;

        long frameSizeTableBytes = physicalFrames * 4L;
        long sizePosition = tableStart;
        long remaining = frameSizeTableBytes;
        long payloadBytes = 0;
        byte[] block = new byte[64 * 1024];

        while (remaining > 0)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            int request = (int)Math.Min(block.Length, remaining);
            request -= request % 4;
            if (request <= 0)
                break;

            int read = reader.ReadBestEffort(sizePosition, block.AsSpan(0, request), out _);
            if (read != request)
                return null;

            for (int i = 0; i < read; i += 4)
            {
                uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i, 4));
                uint frameSize = storedSize & 0xFFFFFFFCu;
                if (frameSize == 0)
                    return null;
                try
                {
                    payloadBytes = checked(payloadBytes + frameSize);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }

            sizePosition += read;
            remaining -= read;
        }

        long totalLength;
        try
        {
            totalLength = checked(frameDataStart - start + payloadBytes);
        }
        catch (OverflowException)
        {
            return null;
        }

        if (totalLength <= 104 || totalLength > volumeLength - start)
            return null;

        return new RawFileAnalysis("SMK", totalLength, "Çok İyi");
    }

    public static bool LooksLikeDvStart(ReadOnlySpan<byte> data)
    {
        if (data.Length < 6 * 80)
            return false;

        return data[0] == 0x1F && data[80] == 0x3F && data[160] == 0x3F &&
               data[240] == 0x56 && data[320] == 0x56 && data[400] == 0x56 &&
               data[2] == 0x00 && data[82] == 0x00 && data[162] == 0x01 &&
               data[242] == 0x00 && data[322] == 0x01 && data[402] == 0x02;
    }

    private static bool LooksLikeDvSequenceStart(
        RawDeviceReader reader,
        long offset,
        long volumeLength,
        out bool pal,
        out byte channelMarker)
    {
        pal = false;
        channelMarker = 0;
        if (offset < 0 || offset + 6L * 80 > volumeLength)
            return false;

        byte[] data = reader.ReadBytes(offset, 6 * 80);
        if (!LooksLikeDvStart(data))
            return false;

        pal = (data[3] & 0x80) != 0;
        channelMarker = (byte)(data[1] & 0x08);
        return (data[1] >> 4) == 0;
    }

    private static int DetermineDvFrameSize(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        int baseFrameSize,
        byte channelMarker)
    {
        int[] candidates = [baseFrameSize, baseFrameSize * 2, baseFrameSize * 4];
        foreach (int candidate in candidates)
        {
            long next = start + candidate;
            if (next + 6L * 80 > volumeLength)
                continue;

            if (LooksLikeDvSequenceStart(reader, next, volumeLength, out _, out byte nextChannel) &&
                nextChannel == channelMarker)
                return candidate;
        }

        return baseFrameSize;
    }

    private static bool ValidateDvFrameGeometry(
        RawDeviceReader reader,
        long frameStart,
        int frameSize,
        bool pal,
        long volumeLength)
    {
        int sequencesPerChannel = pal ? 12 : 10;
        int sequenceCount = frameSize / 12000;
        if (sequenceCount < sequencesPerChannel || frameSize % 12000 != 0)
            return false;

        Span<byte> id = stackalloc byte[3];
        for (int sequence = 0; sequence < sequenceCount; sequence++)
        {
            long sequenceStart = frameStart + sequence * 12000L;
            if (sequenceStart + 6L * 80 > volumeLength || !reader.ReadExact(sequenceStart, id) || id[0] != 0x1F)
                return false;

            int expectedSequence = sequence % sequencesPerChannel;
            if ((id[1] >> 4) != expectedSequence)
                return false;
        }

        return true;
    }

    private static bool TryReadBerLength(
        RawDeviceReader reader,
        long offset,
        long volumeLength,
        out ulong value,
        out int encodedBytes)
    {
        value = 0;
        encodedBytes = 0;
        Span<byte> one = stackalloc byte[1];
        if (offset >= volumeLength || !reader.ReadExact(offset, one))
            return false;

        byte first = one[0];
        if ((first & 0x80) == 0)
        {
            value = first;
            encodedBytes = 1;
            return true;
        }

        int count = first & 0x7F;
        if (count is 0 or > 8 || offset + 1L + count > volumeLength)
            return false;

        Span<byte> bytes = stackalloc byte[8];
        if (!reader.ReadExact(offset + 1, bytes[..count]))
            return false;

        ulong result = 0;
        for (int i = 0; i < count; i++)
            result = (result << 8) | bytes[i];

        value = result;
        encodedBytes = 1 + count;
        return true;
    }

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            if (value != 0)
                return false;
        }
        return true;
    }

    private static bool LooksLikeVideoEssence(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return false;

        if (data.IndexOf(MpegVideoSequenceSignature) >= 0 ||
            data.IndexOf(H264SpsSignature3) >= 0 ||
            data.IndexOf(H264SpsSignature4) >= 0 ||
            data.IndexOf(H265VpsSignature) >= 0 ||
            data.IndexOf(Jpeg2000CodestreamSignature) >= 0 ||
            data.IndexOf(DnxHdSignature) >= 0)
            return true;

        return LooksLikeDvStart(data);
    }
}
