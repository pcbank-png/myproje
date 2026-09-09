using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Completes common raw-carving candidates directly from the block that Deep Scan has
/// already read. No additional source-media seek/read is performed here. This keeps the
/// unified scan sequential while retaining strong structure checks for the common formats.
/// </summary>
internal static class FastBufferedCandidateAnalyzer
{
    private const int MaxBufferedHeaderValidationBytes = 2 * 1024 * 1024;

    public static RawFileAnalysis? TryAnalyze(
        SignatureKind kind,
        ReadOnlySpan<byte> available,
        long remainingSourceBytes)
    {
        if (available.Length < 4 || remainingSourceBytes <= 0)
            return null;

        return kind switch
        {
            SignatureKind.Jpeg => TryAnalyzeJpeg(available, remainingSourceBytes),
            SignatureKind.Png => TryAnalyzePng(available, remainingSourceBytes),
            SignatureKind.Bmp => TryAnalyzeBmp(available, remainingSourceBytes),
            SignatureKind.WebP or SignatureKind.Riff => TryAnalyzeRiff(available, remainingSourceBytes),
            SignatureKind.Pdf => TryAnalyzePdf(available, remainingSourceBytes),
            _ => null
        };
    }

    private static RawFileAnalysis? TryAnalyzeJpeg(ReadOnlySpan<byte> data, long remainingSourceBytes)
    {
        if (data.Length < 16 || data[0] != 0xFF || data[1] != 0xD8 || data[2] != 0xFF)
            return null;

        int headerWindow = Math.Min(data.Length, MaxBufferedHeaderValidationBytes);
        if (!JpegAdvancedReconstructionService.ValidateHeaderForRegression(data[..headerWindow]))
            return null;

        int position = FindJpegEntropyStart(data);
        if (position < 0)
            return null;

        while (position + 1 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            int markerStart = position++;
            while (position < data.Length && data[position] == 0xFF)
                position++;
            if (position >= data.Length)
                break;

            byte marker = data[position++];
            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7)
                continue;

            if (marker == 0xD9)
            {
                long length = position;
                return length <= remainingSourceBytes
                    ? new RawFileAnalysis("JPG", length, "Çok İyi")
                    : null;
            }

            if (marker == 0x01 || marker == 0xD8)
                continue;

            if (!MarkerHasLength(marker) || position + 2 > data.Length)
                return null;

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
                return null;
            position += segmentLength;

            // Progressive JPEG can contain more than one SOS. Continue scanning the next
            // entropy segment without leaving the already-read source block.
            _ = markerStart;
        }

        return null;
    }

    private static int FindJpegEntropyStart(ReadOnlySpan<byte> data)
    {
        int position = 2;
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
                return -1;

            byte marker = data[position++];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker == 0xD9 || position + 2 > data.Length)
                return -1;

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2 || position + segmentLength > data.Length)
                return -1;

            if (marker == 0xDA)
                return position + segmentLength;

            position += segmentLength;
        }

        return -1;
    }

    private static bool MarkerHasLength(byte marker) =>
        marker is not (0x00 or 0x01 or 0xD8 or 0xD9) &&
        marker is not (>= 0xD0 and <= 0xD7);

    private static RawFileAnalysis? TryAnalyzePng(ReadOnlySpan<byte> data, long remainingSourceBytes)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 33 || !data[..8].SequenceEqual(png))
            return null;

        int position = 8;
        bool seenIhdr = false;
        bool seenIdat = false;
        int chunkCount = 0;
        while (position + 12 <= data.Length && chunkCount++ < 100000)
        {
            uint payload = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));
            long chunkLength = 12L + payload;
            if (chunkLength < 12 || position + chunkLength > data.Length)
                return null;

            ReadOnlySpan<byte> type = data.Slice(position + 4, 4);
            if (!seenIhdr)
            {
                if (!type.SequenceEqual("IHDR"u8) || payload != 13)
                    return null;
                seenIhdr = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return null;
            }

            if (type.SequenceEqual("IDAT"u8))
                seenIdat = true;

            position += checked((int)chunkLength);
            if (type.SequenceEqual("IEND"u8))
            {
                if (payload != 0 || !seenIhdr || !seenIdat || position > remainingSourceBytes)
                    return null;
                return new RawFileAnalysis("PNG", position, "Çok İyi");
            }
        }

        return null;
    }

    private static RawFileAnalysis? TryAnalyzeBmp(ReadOnlySpan<byte> data, long remainingSourceBytes)
    {
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
            return null;

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));
        if (declared < 54 || declared > data.Length || declared > remainingSourceBytes)
            return null;

        if (!BmpStructureValidator.LooksLikePrefix(data[..Math.Min(data.Length, 160)]))
            return null;

        return new RawFileAnalysis("BMP", declared, "Çok İyi");
    }

    private static RawFileAnalysis? TryAnalyzeRiff(ReadOnlySpan<byte> data, long remainingSourceBytes)
    {
        if (data.Length < 12 || !data[..4].SequenceEqual("RIFF"u8))
            return null;

        long declared = 8L + BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
        if (declared < 12 || declared > data.Length || declared > remainingSourceBytes)
            return null;

        ReadOnlySpan<byte> type = data.Slice(8, 4);
        if (type.SequenceEqual("WEBP"u8))
            return new RawFileAnalysis("WEBP", declared, "Çok İyi");

        int inspectLength = (int)Math.Min(declared, 1024L * 1024);
        ReadOnlySpan<byte> inspect = data[..inspectLength];
        if (type.SequenceEqual("WAVE"u8) &&
            ContainsAscii(inspect, "fmt "u8) && ContainsAscii(inspect, "data"u8))
            return new RawFileAnalysis("WAV", declared, "Çok İyi");

        if (type.SequenceEqual("AVI "u8) &&
            ContainsAscii(inspect, "vids"u8) && ContainsAscii(inspect, "movi"u8))
            return new RawFileAnalysis("AVI", declared, "Çok İyi");

        return null;
    }

    private static RawFileAnalysis? TryAnalyzePdf(ReadOnlySpan<byte> data, long remainingSourceBytes)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual("%PDF"u8))
            return null;

        int eof = data.LastIndexOf("%%EOF"u8);
        if (eof < 8)
            return null;

        int length = eof + 5;
        while (length < data.Length && length - eof < 8 &&
               data[length] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t')
            length++;

        return length <= remainingSourceBytes
            ? new RawFileAnalysis("PDF", length, "Çok İyi")
            : null;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, ReadOnlySpan<byte> needle) =>
        data.IndexOf(needle) >= 0;
}
