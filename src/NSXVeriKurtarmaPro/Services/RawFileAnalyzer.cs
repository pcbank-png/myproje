using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record RawFileAnalysis(
    string Extension,
    long Length,
    string RecoveryState,
    bool PrependStandardMp4Header = false,
    RecoveryTransformKind TransformKind = RecoveryTransformKind.None,
    byte[]? AppendData = null);

public static class RawFileAnalyzer
{
    private const long MaxJpegSize = 512L * 1024 * 1024;
    private const long MaxPngSize = 1024L * 1024 * 1024;
    private const long MaxGifSize = 1024L * 1024 * 1024;
    private const long MaxTiffSize = 8L * 1024 * 1024 * 1024;
    private const long MaxImageContainerSize = 8L * 1024 * 1024 * 1024;
    private const long MaxPdfSize = 2L * 1024 * 1024 * 1024;
    private const long MaxZipSize = 8L * 1024 * 1024 * 1024;
    private const long MaxRarSize = 16L * 1024 * 1024 * 1024;
    private const long MaxOleSize = 8L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> AllowedIsoTopLevelBoxes = new(StringComparer.Ordinal)
    {
        "ftyp", "free", "skip", "wide", "mdat", "moov", "uuid", "meta", "pdin",
        "moof", "mfra", "sidx", "styp", "emsg", "prft"
    };

    public static RawFileAnalysis? Analyze(
        RawDeviceReader reader,
        SignatureKind kind,
        long startOffset,
        long volumeLength,
        CancellationToken cancellationToken,
        bool preserveTransportBoundaries = false)
    {
        if (cancellationToken.IsCancellationRequested) return null;

        return kind switch
        {
            SignatureKind.Jpeg => AnalyzeJpeg(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Png => AnalyzePng(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Gif => AnalyzeGif(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Bmp => AnalyzeBmp(reader, startOffset, volumeLength),
            SignatureKind.TiffLittleEndian => AnalyzeTiff(reader, startOffset, volumeLength, littleEndian: true, cancellationToken),
            SignatureKind.TiffBigEndian => AnalyzeTiff(reader, startOffset, volumeLength, littleEndian: false, cancellationToken),
            SignatureKind.FujiRaf => AnalyzeFujiRaf(reader, startOffset, volumeLength),
            SignatureKind.SigmaX3f => AnalyzeSigmaX3f(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.WebP => AnalyzeWebP(reader, startOffset, volumeLength),
            SignatureKind.Ico => AnalyzeIco(reader, startOffset, volumeLength),
            SignatureKind.Jpeg2000 => AnalyzeJpeg2000(reader, startOffset, volumeLength),
            SignatureKind.Jpeg2000Codestream => AnalyzeFooterBased(reader, startOffset, volumeLength, MaxImageContainerSize, "J2K", [0xFF, 0xD9], 0, cancellationToken),
            SignatureKind.Psd => AnalyzePsd(reader, startOffset, volumeLength),
            SignatureKind.Dds => AnalyzeDds(reader, startOffset, volumeLength),
            SignatureKind.Exr => AnalyzeExr(reader, startOffset, volumeLength),
            SignatureKind.Riff => AnalyzeRiff(reader, startOffset, volumeLength),
            SignatureKind.Asf => AnalyzeAsf(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.AsfDataObject => AnalyzeAsfDataObject(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.IsoBmff => AnalyzeIsoBmff(reader, startOffset, volumeLength, cancellationToken, allowRebuild: false),
            SignatureKind.IsoBmffFragment => AnalyzeIsoBmff(reader, startOffset, volumeLength, cancellationToken, allowRebuild: true),
            SignatureKind.MpegTs => AnalyzeMpegTs(reader, startOffset, volumeLength, cancellationToken, preserveTransportBoundaries),
            SignatureKind.MpegProgramStream => AnalyzeMpegProgramStream(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.MpegVideoStream => AnalyzeMpegVideoStream(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.H264LengthPrefixed =>
                AnalyzeLengthPrefixedNal(reader, startOffset, volumeLength, h265: false, cancellationToken) ??
                AnalyzeLengthPrefixedNal(reader, startOffset, volumeLength, h265: true, cancellationToken),
            SignatureKind.H265LengthPrefixed =>
                AnalyzeLengthPrefixedNal(reader, startOffset, volumeLength, h265: true, cancellationToken) ??
                AnalyzeLengthPrefixedNal(reader, startOffset, volumeLength, h265: false, cancellationToken),
            SignatureKind.H264AnnexB => AnalyzeAnnexBNal(reader, startOffset, volumeLength, h265: false, cancellationToken),
            SignatureKind.H265AnnexB => AnalyzeAnnexBNal(reader, startOffset, volumeLength, h265: true, cancellationToken),
            SignatureKind.Matroska => AnalyzeMatroska(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.MatroskaCluster => AnalyzeMatroskaCluster(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Flv => AnalyzeFlv(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.OggVideo => AnalyzeOggVideo(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.RealMedia => AnalyzeRealMedia(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Mxf => GlobalVideoRawAnalyzer.AnalyzeMxf(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.MxfEssence => GlobalVideoRawAnalyzer.AnalyzeMxf(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Dv => GlobalVideoRawAnalyzer.AnalyzeDv(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Wtv => GlobalVideoRawAnalyzer.AnalyzeWtv(reader, startOffset, volumeLength),
            SignatureKind.Nsv => GlobalVideoRawAnalyzer.AnalyzeNsv(reader, startOffset, volumeLength),
            SignatureKind.Roq => GlobalVideoRawAnalyzer.AnalyzeRoq(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Bink => GlobalVideoRawAnalyzer.AnalyzeBink(reader, startOffset, volumeLength),
            SignatureKind.Smacker => GlobalVideoRawAnalyzer.AnalyzeSmacker(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Av1Ivf => ModernCodecReconstructionService.AnalyzeAv1Ivf(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Pdf => AnalyzePdf(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Zip => AnalyzeZip(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Mp3 => AnalyzeMp3(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.AacAdts => AnalyzeAacAdts(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Aiff => AnalyzeAiff(reader, startOffset, volumeLength),
            SignatureKind.Au => AnalyzeAu(reader, startOffset, volumeLength),
            SignatureKind.Midi => AnalyzeMidi(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.SevenZip => AnalyzeSevenZip(reader, startOffset, volumeLength),
            SignatureKind.Cab => AnalyzeCab(reader, startOffset, volumeLength),
            SignatureKind.PortableExecutable => AnalyzePortableExecutable(reader, startOffset, volumeLength),
            SignatureKind.SqliteDatabase => SqliteRecoveryService.AnalyzeDatabase(reader, startOffset, volumeLength),
            SignatureKind.SqliteWal => SqliteRecoveryService.AnalyzeWal(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.SqliteJournal => SqliteRecoveryService.AnalyzeJournal(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.Rar => AnalyzeRar(reader, startOffset, volumeLength, cancellationToken),
            SignatureKind.OleCompound => AnalyzeOleCompound(reader, startOffset, volumeLength, cancellationToken),
            _ => null
        };
    }

    private static RawFileAnalysis? AnalyzeJpeg(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken) =>
        JpegAdvancedReconstructionService.Analyze(reader, start, volumeLength, cancellationToken);

    private static RawFileAnalysis? AnalyzePng(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        byte[] signature = new byte[8];
        if (!reader.ReadExact(start, signature)) return null;

        byte[] expected = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!signature.AsSpan().SequenceEqual(expected)) return null;

        long position = start + 8;
        long maxEnd = Math.Min(volumeLength, start + MaxPngSize);
        byte[] chunkHeader = new byte[8];
        int chunkCount = 0;
        bool seenIhdr = false;
        bool seenIdat = false;
        Span<byte> ihdr = stackalloc byte[13];

        while (position + 12 <= maxEnd && chunkCount < 500000)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            if (!reader.ReadExact(position, chunkHeader)) return null;
            uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(chunkHeader, 4, 4);

            long chunkLength = 12L + dataLength;
            if (chunkLength < 12 || position + chunkLength > maxEnd)
                return null;

            if (chunkCount == 0)
            {
                if (type != "IHDR" || dataLength != 13)
                    return null;

                if (!reader.ReadExact(position + 8, ihdr) || !ValidatePngIhdr(ihdr))
                    return null;

                seenIhdr = true;
            }
            else if (type == "IHDR")
            {
                return null;
            }

            if (type == "IDAT")
            {
                if (!seenIhdr) return null;
                seenIdat = true;
            }

            position += chunkLength;
            chunkCount++;

            if (type == "IEND")
            {
                if (dataLength != 0 || !seenIhdr || !seenIdat)
                    return null;

                return new RawFileAnalysis("PNG", position - start, "Çok İyi");
            }
        }

        return null;
    }

    private static RawFileAnalysis? AnalyzeGif(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[13];
        if (!reader.ReadExact(start, header)) return null;

        string signature = Encoding.ASCII.GetString(header[..6]);
        if (signature is not "GIF87a" and not "GIF89a")
            return null;

        int logicalWidth = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        int logicalHeight = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        if (logicalWidth <= 0 || logicalHeight <= 0)
            return null;

        long maxEnd = Math.Min(volumeLength, start + MaxGifSize);
        long position = start + 13;
        byte packed = header[10];
        bool seenImage = false;
        if ((packed & 0x80) != 0)
        {
            int tableBytes = 3 * (1 << ((packed & 0x07) + 1));
            position += tableBytes;
        }

        Span<byte> one = stackalloc byte[1];
        Span<byte> imageDescriptor = stackalloc byte[9];

        while (position < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (!reader.ReadExact(position, one)) return null;

            byte marker = one[0];
            if (marker == 0x3B)
                return seenImage
                    ? new RawFileAnalysis("GIF", position + 1 - start, "Çok İyi")
                    : null;

            if (marker == 0x21)
            {
                position += 2;
                if (!SkipGifSubBlocks(reader, ref position, maxEnd, cancellationToken))
                    return null;
                continue;
            }

            if (marker == 0x2C)
            {
                if (!reader.ReadExact(position + 1, imageDescriptor)) return null;

                int imageWidth = BinaryPrimitives.ReadUInt16LittleEndian(imageDescriptor.Slice(4, 2));
                int imageHeight = BinaryPrimitives.ReadUInt16LittleEndian(imageDescriptor.Slice(6, 2));
                if (imageWidth <= 0 || imageHeight <= 0)
                    return null;

                seenImage = true;
                position += 10;

                byte imagePacked = imageDescriptor[8];
                if ((imagePacked & 0x80) != 0)
                {
                    int localTableBytes = 3 * (1 << ((imagePacked & 0x07) + 1));
                    position += localTableBytes;
                }

                if (position >= maxEnd || !reader.ReadExact(position, one)) return null;
                position++;
                if (!SkipGifSubBlocks(reader, ref position, maxEnd, cancellationToken))
                    return null;
                continue;
            }

            return null;
        }

        return null;
    }

    private static bool IsJpegStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or
        0xC5 or 0xC6 or 0xC7 or
        0xC9 or 0xCA or 0xCB or
        0xCD or 0xCE or 0xCF;

    private static bool ValidatePngIhdr(ReadOnlySpan<byte> ihdr)
    {
        if (ihdr.Length != 13) return false;

        uint width = BinaryPrimitives.ReadUInt32BigEndian(ihdr.Slice(0, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(ihdr.Slice(4, 4));
        byte bitDepth = ihdr[8];
        byte colorType = ihdr[9];
        byte compression = ihdr[10];
        byte filter = ihdr[11];
        byte interlace = ihdr[12];

        if (width == 0 || height == 0 || compression != 0 || filter != 0 || interlace > 1)
            return false;

        return colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            4 => bitDepth is 8 or 16,
            6 => bitDepth is 8 or 16,
            _ => false
        };
    }

    private static bool SkipGifSubBlocks(
        RawDeviceReader reader,
        ref long position,
        long maxEnd,
        CancellationToken cancellationToken)
    {
        Span<byte> sizeByte = stackalloc byte[1];
        while (position < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (!reader.ReadExact(position, sizeByte)) return false;

            int length = sizeByte[0];
            position++;
            if (length == 0)
                return true;

            if (position + length > maxEnd)
                return false;

            position += length;
        }

        return false;
    }

    private static RawFileAnalysis? AnalyzeFujiRaf(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] header = new byte[112];
        if (!reader.ReadExact(start, header))
            return null;
        if (!header.AsSpan(0, 16).SequenceEqual("FUJIFILMCCD-RAW "u8))
            return null;

        uint jpegOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(84, 4));
        uint jpegLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(88, 4));
        uint rawOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(92, 4));
        uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(96, 4));

        ulong jpegEnd = (ulong)jpegOffset + jpegLength;
        ulong rawEnd = (ulong)rawOffset + rawLength;
        ulong end = Math.Max(jpegEnd, rawEnd);
        long available = volumeLength - start;
        if (end < 112 || end > (ulong)Math.Min(MaxImageContainerSize, available))
            return null;

        bool hasPreview = jpegOffset >= 112 && jpegLength >= 1024;
        bool hasRaw = rawOffset >= 112 && rawLength >= 4096;
        if (!hasPreview && !hasRaw)
            return null;

        return new RawFileAnalysis("RAF", (long)end, hasPreview && hasRaw ? "Çok İyi" : "İyi");
    }

    private static RawFileAnalysis? AnalyzeSigmaX3f(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[64];
        if (!reader.ReadExact(start, header) || !header.AsSpan(0, 4).SequenceEqual("FOVb"u8))
            return null;

        long maxEnd = Math.Min(volumeLength, start + MaxImageContainerSize);
        // X3F keeps its section directory near EOF. Search backwards in bounded windows for SECd.
        const int windowSize = 1024 * 1024;
        byte[] window = new byte[windowSize];
        long cursor = maxEnd;
        long minimum = Math.Max(start + 64, maxEnd - 64L * 1024 * 1024);
        while (cursor > minimum)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;
            long windowStart = Math.Max(minimum, cursor - windowSize);
            int wanted = checked((int)(cursor - windowStart));
            int read = reader.ReadBestEffort(windowStart, window.AsSpan(0, wanted), out _);
            for (int i = read - 4; i >= 0; i--)
            {
                if (window[i] == (byte)'S' && window[i + 1] == (byte)'E' &&
                    window[i + 2] == (byte)'C' && window[i + 3] == (byte)'d')
                {
                    long candidateEnd = cursor;
                    if (candidateEnd - start >= 4096)
                        return new RawFileAnalysis("X3F", candidateEnd - start, "İyi");
                }
            }
            cursor = windowStart;
        }

        return null;
    }

    private static RawFileAnalysis? AnalyzeTiff(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        bool littleEndian,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[16];
        header.Clear();
        if (!reader.ReadExact(start, header[..8])) return null;
        _ = reader.ReadBestEffort(start + 8, header[8..], out _);

        if (littleEndian)
        {
            if (header[0] != (byte)'I' || header[1] != (byte)'I') return null;
        }
        else
        {
            if (header[0] != (byte)'M' || header[1] != (byte)'M') return null;
        }

        ushort magic = ReadUInt16(header.Slice(2, 2), littleEndian);
        bool bigTiff = magic == 43;
        bool olympusRaw = littleEndian && magic is 0x4F52 or 0x5352;
        bool panasonicRaw = littleEndian && magic == 0x0055;
        if (magic is not 42 and not 43 && !olympusRaw && !panasonicRaw)
            return null;

        string? rawExtension = null;
        if (olympusRaw) rawExtension = "ORF";
        else if (panasonicRaw) rawExtension = "RW2";
        else if (littleEndian && header[8] == (byte)'C' && header[9] == (byte)'R' && header[10] == 0x02)
            rawExtension = "CR2";
        string? cameraMake = null;

        long maxRelative = Math.Min(MaxTiffSize, volumeLength - start);
        if (maxRelative < 8) return null;

        ulong firstIfd;
        int countSize;
        int entrySize;
        int inlineSize;
        int nextOffsetSize;

        if (!bigTiff)
        {
            firstIfd = ReadUInt32(header.Slice(4, 4), littleEndian);
            countSize = 2;
            entrySize = 12;
            inlineSize = 4;
            nextOffsetSize = 4;
        }
        else
        {
            if (!reader.ReadExact(start, header)) return null;
            ushort offsetSize = ReadUInt16(header.Slice(4, 2), littleEndian);
            ushort reserved = ReadUInt16(header.Slice(6, 2), littleEndian);
            if (offsetSize != 8 || reserved != 0) return null;

            firstIfd = ReadUInt64(header.Slice(8, 8), littleEndian);
            countSize = 8;
            entrySize = 20;
            inlineSize = 8;
            nextOffsetSize = 8;
        }

        if (firstIfd == 0 || firstIfd >= (ulong)maxRelative)
            return null;

        long maxReferencedEnd = bigTiff ? 16 : 8;
        var pendingIfds = new Queue<ulong>();
        var visitedIfds = new HashSet<ulong>();
        pendingIfds.Enqueue(firstIfd);
        int parsedIfds = 0;
        Span<byte> countBuffer = stackalloc byte[8];
        Span<byte> nextBuffer = stackalloc byte[8];

        while (pendingIfds.Count > 0 && parsedIfds < 2048)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            ulong ifdOffset = pendingIfds.Dequeue();
            if (ifdOffset == 0 || ifdOffset >= (ulong)maxRelative || !visitedIfds.Add(ifdOffset))
                continue;

            if (!reader.ReadExact(start + (long)ifdOffset, countBuffer[..countSize]))
                return null;

            ulong entryCount = bigTiff
                ? ReadUInt64(countBuffer, littleEndian)
                : ReadUInt16(countBuffer[..2], littleEndian);

            if (entryCount > 100000)
                return null;

            ulong tableBytes = entryCount * (ulong)entrySize;
            ulong tableEnd = ifdOffset + (ulong)countSize + tableBytes + (ulong)nextOffsetSize;
            if (tableEnd > (ulong)maxRelative || tableEnd > (ulong)long.MaxValue)
                return null;

            maxReferencedEnd = Math.Max(maxReferencedEnd, (long)tableEnd);

            var offsetsByTag = new Dictionary<ushort, ulong[]>();
            var countsByTag = new Dictionary<ushort, ulong[]>();
            byte[] entryBuffer = new byte[entrySize];

            for (ulong i = 0; i < entryCount; i++)
            {
                if (cancellationToken.IsCancellationRequested) return null;

                long entryOffset = start + (long)ifdOffset + countSize + checked((long)i * entrySize);
                if (!reader.ReadExact(entryOffset, entryBuffer))
                    return null;

                ReadOnlySpan<byte> entry = entryBuffer;
                ushort tag = ReadUInt16(entry[..2], littleEndian);
                ushort type = ReadUInt16(entry.Slice(2, 2), littleEndian);
                ulong count = bigTiff
                    ? ReadUInt64(entry.Slice(4, 8), littleEndian)
                    : ReadUInt32(entry.Slice(4, 4), littleEndian);

                int typeSize = GetTiffTypeSize(type);
                if (typeSize <= 0 || count == 0)
                    continue;

                if (count > ulong.MaxValue / (ulong)typeSize)
                    return null;

                ulong valueBytes = count * (ulong)typeSize;
                int valueFieldOffset = bigTiff ? 12 : 8;
                ulong valueOrOffset = bigTiff
                    ? ReadUInt64(entry.Slice(valueFieldOffset, 8), littleEndian)
                    : ReadUInt32(entry.Slice(valueFieldOffset, 4), littleEndian);

                if (valueBytes > (ulong)inlineSize)
                {
                    if (valueOrOffset >= (ulong)maxRelative || valueBytes > (ulong)maxRelative - valueOrOffset)
                        return null;
                    maxReferencedEnd = Math.Max(maxReferencedEnd, (long)(valueOrOffset + valueBytes));
                }

                if (tag == 50706)
                {
                    rawExtension = "DNG";
                }
                else if (tag is 271 or 272 or 50708)
                {
                    string? text = ReadTiffAsciiValue(
                        reader, start, maxRelative, entry, type, count, littleEndian, bigTiff);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        cameraMake = string.IsNullOrWhiteSpace(cameraMake)
                            ? text
                            : cameraMake + " " + text;
                    }
                }

                if (tag is 273 or 324 or 513)
                {
                    ulong[] values = ReadTiffUnsignedValues(
                        reader, start, maxRelative, entry, type, count, littleEndian, bigTiff);
                    if (values.Length > 0) offsetsByTag[tag] = values;
                }
                else if (tag is 279 or 325 or 514)
                {
                    ulong[] values = ReadTiffUnsignedValues(
                        reader, start, maxRelative, entry, type, count, littleEndian, bigTiff);
                    if (values.Length > 0) countsByTag[tag] = values;
                }
                else if (tag is 330 or 34665 or 34853)
                {
                    ulong[] values = ReadTiffUnsignedValues(
                        reader, start, maxRelative, entry, type, count, littleEndian, bigTiff);
                    foreach (ulong nested in values)
                    {
                        if (nested > 0 && nested < (ulong)maxRelative)
                            pendingIfds.Enqueue(nested);
                    }
                }
            }

            ApplyTiffDataRanges(offsetsByTag, countsByTag, 273, 279, maxRelative, ref maxReferencedEnd);
            ApplyTiffDataRanges(offsetsByTag, countsByTag, 324, 325, maxRelative, ref maxReferencedEnd);
            ApplyTiffDataRanges(offsetsByTag, countsByTag, 513, 514, maxRelative, ref maxReferencedEnd);

            long nextFieldOffset = start + (long)ifdOffset + countSize + checked((long)tableBytes);
            if (!reader.ReadExact(nextFieldOffset, nextBuffer[..nextOffsetSize]))
                return null;

            ulong nextIfd = bigTiff
                ? ReadUInt64(nextBuffer, littleEndian)
                : ReadUInt32(nextBuffer[..4], littleEndian);
            if (nextIfd > 0 && nextIfd < (ulong)maxRelative)
                pendingIfds.Enqueue(nextIfd);

            parsedIfds++;
        }

        if (parsedIfds == 0 || maxReferencedEnd <= (bigTiff ? 16 : 8))
            return null;

        long length = Math.Min(maxReferencedEnd, maxRelative);
        rawExtension ??= ClassifyTiffCameraRaw(cameraMake);
        string extension = rawExtension ?? "TIFF";
        return new RawFileAnalysis(extension, length, parsedIfds > 1 ? "Çok İyi" : "İyi");
    }

    private static string? ReadTiffAsciiValue(
        RawDeviceReader reader,
        long start,
        long maxRelative,
        ReadOnlySpan<byte> entry,
        ushort type,
        ulong count,
        bool littleEndian,
        bool bigTiff)
    {
        if (type != 2 || count == 0 || count > 4096)
            return null;

        int inlineSize = bigTiff ? 8 : 4;
        int valueFieldOffset = bigTiff ? 12 : 8;
        int byteCount = checked((int)count);
        byte[] data;
        if (byteCount <= inlineSize)
        {
            data = entry.Slice(valueFieldOffset, inlineSize).ToArray();
        }
        else
        {
            ulong valueOffset = bigTiff
                ? ReadUInt64(entry.Slice(valueFieldOffset, 8), littleEndian)
                : ReadUInt32(entry.Slice(valueFieldOffset, 4), littleEndian);
            if (valueOffset >= (ulong)maxRelative || (ulong)byteCount > (ulong)maxRelative - valueOffset)
                return null;
            data = reader.ReadBytes(start + (long)valueOffset, byteCount);
            if (data.Length != byteCount)
                return null;
        }

        string value = Encoding.ASCII.GetString(data, 0, Math.Min(byteCount, data.Length)).TrimEnd('\0', ' ');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    internal static string? ClassifyTiffCameraRaw(string? makeAndModel)
    {
        if (string.IsNullOrWhiteSpace(makeAndModel))
            return null;

        string text = makeAndModel.ToUpperInvariant();
        if (text.Contains("NIKON")) return "NEF";
        if (text.Contains("SONY")) return "ARW";
        if (text.Contains("CANON")) return "CR2";
        if (text.Contains("OLYMPUS") || text.Contains("OM DIGITAL")) return "ORF";
        if (text.Contains("PANASONIC")) return "RW2";
        if (text.Contains("PENTAX") || text.Contains("RICOH")) return "PEF";
        if (text.Contains("SAMSUNG")) return "SRW";
        if (text.Contains("HASSELBLAD")) return "3FR";
        if (text.Contains("PHASE ONE")) return "IIQ";
        if (text.Contains("KODAK")) return "KDC";
        if (text.Contains("LEICA")) return "RWL";
        return null;
    }

    private static ulong[] ReadTiffUnsignedValues(
        RawDeviceReader reader,
        long start,
        long maxRelative,
        ReadOnlySpan<byte> entry,
        ushort type,
        ulong count,
        bool littleEndian,
        bool bigTiff)
    {
        if (count == 0 || count > 1_000_000)
            return [];

        int typeSize = GetTiffTypeSize(type);
        if (typeSize is not 2 and not 4 and not 8)
            return [];

        int inlineSize = bigTiff ? 8 : 4;
        int valueFieldOffset = bigTiff ? 12 : 8;
        ulong totalBytes = count * (ulong)typeSize;
        if (totalBytes > int.MaxValue)
            return [];

        byte[] data;
        if (totalBytes <= (ulong)inlineSize)
        {
            data = entry.Slice(valueFieldOffset, inlineSize).ToArray();
        }
        else
        {
            ulong valueOffset = bigTiff
                ? ReadUInt64(entry.Slice(valueFieldOffset, 8), littleEndian)
                : ReadUInt32(entry.Slice(valueFieldOffset, 4), littleEndian);

            if (valueOffset >= (ulong)maxRelative || totalBytes > (ulong)maxRelative - valueOffset)
                return [];

            data = reader.ReadBytes(start + (long)valueOffset, (int)totalBytes);
            if (data.Length != (int)totalBytes)
                return [];
        }

        var result = new ulong[(int)count];
        for (int i = 0; i < result.Length; i++)
        {
            ReadOnlySpan<byte> value = data.AsSpan(i * typeSize, typeSize);
            result[i] = typeSize switch
            {
                2 => ReadUInt16(value, littleEndian),
                4 => ReadUInt32(value, littleEndian),
                8 => ReadUInt64(value, littleEndian),
                _ => 0
            };
        }

        return result;
    }

    private static void ApplyTiffDataRanges(
        IReadOnlyDictionary<ushort, ulong[]> offsetsByTag,
        IReadOnlyDictionary<ushort, ulong[]> countsByTag,
        ushort offsetTag,
        ushort countTag,
        long maxRelative,
        ref long maxReferencedEnd)
    {
        if (!offsetsByTag.TryGetValue(offsetTag, out ulong[]? offsets) ||
            !countsByTag.TryGetValue(countTag, out ulong[]? counts) ||
            offsets.Length == 0 || counts.Length == 0)
            return;

        int length = Math.Min(offsets.Length, counts.Length);
        for (int i = 0; i < length; i++)
        {
            ulong offset = offsets[i];
            ulong size = counts[i];
            if (offset >= (ulong)maxRelative || size > (ulong)maxRelative - offset)
                continue;

            maxReferencedEnd = Math.Max(maxReferencedEnd, (long)(offset + size));
        }
    }

    private static int GetTiffTypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 or 16 or 17 or 18 => 8,
        _ => 0
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data)
            : BinaryPrimitives.ReadUInt16BigEndian(data);

    private static uint ReadUInt32(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data)
            : BinaryPrimitives.ReadUInt32BigEndian(data);

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt64LittleEndian(data)
            : BinaryPrimitives.ReadUInt64BigEndian(data);

    private static RawFileAnalysis? AnalyzeAsf(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> headerGuid =
        [
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
        ];
        ReadOnlySpan<byte> filePropertiesGuid =
        [
            0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11,
            0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
        ];
        ReadOnlySpan<byte> videoMediaGuid =
        [
            0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11,
            0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
        ];
        ReadOnlySpan<byte> audioMediaGuid =
        [
            0x40, 0x9E, 0x69, 0xF8, 0x4D, 0x5B, 0xCF, 0x11,
            0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
        ];
        ReadOnlySpan<byte> dataObjectGuid =
        [
            0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
        ];

        Span<byte> header = stackalloc byte[30];
        if (!reader.ReadExact(start, header) || !header[..16].SequenceEqual(headerGuid))
            return null;

        ulong headerSize = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16, 8));
        if (headerSize < 30 || headerSize > 64UL * 1024 * 1024 || headerSize > (ulong)(volumeLength - start))
            return null;

        byte[] headerData = reader.ReadBytes(start, checked((int)headerSize));
        if (headerData.Length != (int)headerSize)
            return null;

        bool hasVideo = ContainsBytes(headerData, videoMediaGuid);
        bool hasAudio = ContainsBytes(headerData, audioMediaGuid);
        if (!hasVideo && !hasAudio)
            return null;

        string extension = hasVideo ? "WMV" : "WMA";

        if (cancellationToken.IsCancellationRequested) return null;

        long maxLength = volumeLength - start;
        int index = IndexOf(headerData, filePropertiesGuid);
        if (index >= 0 && index + 48 <= headerData.Length)
        {
            ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(headerData.AsSpan(index + 40, 8));
            if (fileSize >= headerSize && fileSize <= (ulong)maxLength && fileSize <= (ulong)long.MaxValue)
                return new RawFileAnalysis(extension, (long)fileSize, "Çok İyi");
        }

        long dataOffset = start + (long)headerSize;
        Span<byte> objectHeader = stackalloc byte[24];
        if (dataOffset + objectHeader.Length > volumeLength ||
            !reader.ReadExact(dataOffset, objectHeader) ||
            !objectHeader[..16].SequenceEqual(dataObjectGuid))
            return null;

        ulong dataObjectSize = BinaryPrimitives.ReadUInt64LittleEndian(objectHeader[16..24]);
        if (dataObjectSize < 50 || dataObjectSize > (ulong)(volumeLength - dataOffset) || dataObjectSize > (ulong)long.MaxValue)
            return null;

        long recoveredLength = (long)headerSize + (long)dataObjectSize;
        return recoveredLength > (long)headerSize
            ? new RawFileAnalysis(extension, recoveredLength, "Yeniden İnşa")
            : null;
    }

    private static RawFileAnalysis? AnalyzeAsfDataObject(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> dataObjectGuid =
        [
            0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
        ];
        Span<byte> header = stackalloc byte[50];
        if (!reader.ReadExact(start, header) || !header[..16].SequenceEqual(dataObjectGuid))
            return null;
        ulong size = BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(16, 8));
        if (size < 50 || size > (ulong)(volumeLength - start) || size > (ulong)long.MaxValue)
            return null;
        if (cancellationToken.IsCancellationRequested)
            return null;
        int inspect = (int)Math.Min((long)size - 50, 4L * 1024 * 1024);
        if (inspect <= 0)
            return null;
        byte[] payload = reader.ReadBytes(start + 50, inspect);
        bool videoEvidence = ContainsAscii(payload, "WVC1") || ContainsAscii(payload, "WMV3") ||
                             ContainsBytes(payload, new byte[] { 0x00, 0x00, 0x01, 0x0F });
        if (!videoEvidence)
            return null;
        return new RawFileAnalysis("WMV", (long)size, "Yeniden İnşa");
    }

    private static RawFileAnalysis? AnalyzeBmp(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        long availableBytes = volumeLength - start;
        if (availableBytes < 26) return null;

        int headerLength = (int)Math.Min(256L, availableBytes);
        byte[] header = ReadBestEffortBytes(reader, start, headerLength);
        if (!BmpStructureValidator.TryValidate(header, availableBytes, out long size))
            return null;

        return new RawFileAnalysis("BMP", size, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzeWebP(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] header = new byte[12];
        if (!reader.ReadExact(start, header) ||
            !header.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
            !header.AsSpan(8, 4).SequenceEqual("WEBP"u8))
            return null;

        uint riffSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        long length = 8L + riffSize;
        if (length < 20 || length > MaxImageContainerSize || start + length > volumeLength)
            return null;

        return new RawFileAnalysis("WEBP", length, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzeIco(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        if (!IconContainerValidator.TryAnalyze(
                reader,
                start,
                volumeLength,
                out string extension,
                out long containerLength))
        {
            return null;
        }

        return new RawFileAnalysis(extension, containerLength, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzeJpeg2000(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] signature = new byte[12];
        if (!reader.ReadExact(start, signature) ||
            !signature.AsSpan().SequenceEqual(new byte[] { 0x00, 0x00, 0x00, 0x0C, (byte)'j', (byte)'P', 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A }))
            return null;

        long position = start;
        long maxEnd = Math.Min(volumeLength, start + MaxImageContainerSize);
        int boxes = 0;
        bool seenSignature = false;
        bool seenFileType = false;
        bool seenCodestream = false;

        while (position + 8 <= maxEnd && boxes++ < 100000)
        {
            byte[] boxHeader = new byte[16];
            if (!reader.ReadExact(position, boxHeader.AsSpan(0, 8)))
                return null;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(boxHeader.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(boxHeader, 4, 4);
            long headerSize = 8;
            long boxSize;

            if (size32 == 1)
            {
                if (!reader.ReadExact(position + 8, boxHeader.AsSpan(8, 8)))
                    return null;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(boxHeader.AsSpan(8, 8));
                if (size64 > long.MaxValue) return null;
                boxSize = (long)size64;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                // Box-to-EOF is valid in a normal file, but a raw device has no file EOF.
                // Treating the rest of the volume as this image would be a false recovery.
                return null;
            }
            else
            {
                boxSize = size32;
            }

            if (boxSize < headerSize || position + boxSize > maxEnd)
                return null;

            if (type == "jP  ") seenSignature = true;
            else if (type == "ftyp") seenFileType = true;
            else if (type == "jp2c") seenCodestream = true;

            position += boxSize;
            if (size32 == 0) break;
        }

        long length = position - start;
        return seenSignature && seenFileType && seenCodestream && length >= 32
            ? new RawFileAnalysis("JP2", length, "Çok İyi")
            : null;
    }

    private static RawFileAnalysis? AnalyzePsd(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] header = new byte[26];
        if (!reader.ReadExact(start, header) || !header.AsSpan(0, 4).SequenceEqual("8BPS"u8))
            return null;

        ushort version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (version is not (1 or 2) || header.AsSpan(6, 6).IndexOfAnyExcept((byte)0) >= 0)
            return null;

        int channels = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(12, 2));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(14, 4));
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(18, 4));
        int depth = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(22, 2));
        if (channels is < 1 or > 56 || width == 0 || height == 0 || width > 300000 || height > 300000 || depth is not (1 or 8 or 16 or 32))
            return null;

        long position = start + 26;
        for (int section = 0; section < 3; section++)
        {
            int lengthBytes = section == 2 && version == 2 ? 8 : 4;
            byte[] lenBuffer = new byte[8];
            if (!reader.ReadExact(position, lenBuffer.AsSpan(0, lengthBytes))) return null;
            ulong sectionLength = lengthBytes == 8
                ? BinaryPrimitives.ReadUInt64BigEndian(lenBuffer)
                : BinaryPrimitives.ReadUInt32BigEndian(lenBuffer.AsSpan(0, 4));
            if (sectionLength > (ulong)MaxImageContainerSize) return null;
            position = checked(position + lengthBytes + (long)sectionLength);
            if (position > volumeLength) return null;
        }

        byte[] compressionBuffer = new byte[2];
        if (!reader.ReadExact(position, compressionBuffer)) return null;
        ushort compression = BinaryPrimitives.ReadUInt16BigEndian(compressionBuffer);
        position += 2;

        long rows = checked((long)channels * height);
        long rowBytes = depth == 1 ? ((long)width + 7) / 8 : checked((long)width * depth / 8);

        if (compression == 0)
        {
            long dataBytes = checked(rows * rowBytes);
            long length = checked(position - start + dataBytes);
            return length <= MaxImageContainerSize && start + length <= volumeLength
                ? new RawFileAnalysis(version == 2 ? "PSB" : "PSD", length, "Çok İyi")
                : null;
        }

        if (compression == 1)
        {
            int countBytes = version == 2 ? 4 : 2;
            long tableBytes = checked(rows * countBytes);
            if (tableBytes > 256L * 1024 * 1024 || position + tableBytes > volumeLength)
                return null;

            byte[] table = new byte[checked((int)tableBytes)];
            if (!reader.ReadExact(position, table)) return null;
            long compressedBytes = 0;
            for (long i = 0; i < rows; i++)
            {
                int off = checked((int)(i * countBytes));
                compressedBytes = checked(compressedBytes + (countBytes == 4
                    ? BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(off, 4))
                    : BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(off, 2))));
            }

            long length = checked(position - start + tableBytes + compressedBytes);
            return length <= MaxImageContainerSize && start + length <= volumeLength
                ? new RawFileAnalysis(version == 2 ? "PSB" : "PSD", length, "Çok İyi")
                : null;
        }

        // ZIP/ZIP-prediction PSD image data has no reliable outer byte length. Guessing a
        // boundary would risk consuming unrelated sectors, so it is intentionally rejected.
        return null;
    }

    private static RawFileAnalysis? AnalyzeDds(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] header = new byte[148];
        if (!reader.ReadExact(start, header.AsSpan(0, 128)) || !header.AsSpan(0, 4).SequenceEqual("DDS "u8))
            return null;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)) != 124 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76, 4)) != 32)
            return null;

        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        uint mipCount = Math.Max(1, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(28, 4)));
        uint fourCc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));
        uint rgbBits = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88, 4));
        uint caps2 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(112, 4));
        if ((caps2 & 0x0000FE00) != 0) // cubemap/volume texture: size needs face/depth accounting
            return null;
        if (width == 0 || height == 0 || width > 131072 || height > 131072 || mipCount > 32)
            return null;

        long headerBytes = 128;
        int blockBytes = fourCc switch
        {
            0x31545844 => 8,  // DXT1
            0x33545844 or 0x35545844 => 16, // DXT3/DXT5
            _ => 0
        };

        if (fourCc == 0x30315844) // DX10
        {
            if (!reader.ReadExact(start + 128, header.AsSpan(128, 20))) return null;
            headerBytes = 148;
            uint dxgi = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(128, 4));
            uint arraySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(140, 4));
            if (arraySize != 1) return null;
            blockBytes = dxgi switch
            {
                71 or 72 or 80 or 81 => 8,
                74 or 75 or 77 or 78 or 83 or 84 or 95 or 96 or 98 or 99 => 16,
                _ => 0
            };
            if (blockBytes == 0) return null;
        }

        long dataBytes = 0;
        uint w = width, h = height;
        for (uint level = 0; level < mipCount; level++)
        {
            long levelBytes;
            if (blockBytes > 0)
                levelBytes = checked(Math.Max(1L, (w + 3L) / 4) * Math.Max(1L, (h + 3L) / 4) * blockBytes);
            else if (rgbBits is >= 8 and <= 128)
                levelBytes = checked((long)w * h * ((rgbBits + 7) / 8));
            else
                return null;

            dataBytes = checked(dataBytes + levelBytes);
            w = Math.Max(1, w / 2);
            h = Math.Max(1, h / 2);
        }

        long length = checked(headerBytes + dataBytes);
        return length <= MaxImageContainerSize && start + length <= volumeLength
            ? new RawFileAnalysis("DDS", length, "İyi")
            : null;
    }

    private static RawFileAnalysis? AnalyzeExr(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        byte[] prefix = new byte[8];
        if (!reader.ReadExact(start, prefix) || !prefix.AsSpan(0, 4).SequenceEqual(new byte[] { 0x76, 0x2F, 0x31, 0x01 }))
            return null;

        uint versionField = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4, 4));
        if ((versionField & 0x000000FF) is 0 or > 2 ||
            (versionField & (0x00000200u | 0x00000800u | 0x00001000u)) != 0)
            return null; // tiled, deep/non-image and multipart EXR need a different chunk model

        long position = start + 8;
        int minY = 0, maxY = -1;
        byte compression = 0;
        bool tiled = false;

        for (int attr = 0; attr < 4096; attr++)
        {
            string name = ReadNullTerminatedAscii(reader, ref position, volumeLength, 255);
            if (name.Length == 0) break;
            string type = ReadNullTerminatedAscii(reader, ref position, volumeLength, 255);
            byte[] sizeBytes = new byte[4];
            if (!reader.ReadExact(position, sizeBytes)) return null;
            position += 4;
            int valueSize = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
            if (valueSize < 0 || valueSize > 64 * 1024 * 1024 || position + valueSize > volumeLength) return null;

            if (name == "dataWindow" && type == "box2i" && valueSize == 16)
            {
                byte[] box = new byte[16];
                if (!reader.ReadExact(position, box)) return null;
                minY = BinaryPrimitives.ReadInt32LittleEndian(box.AsSpan(4, 4));
                maxY = BinaryPrimitives.ReadInt32LittleEndian(box.AsSpan(12, 4));
            }
            else if (name == "compression" && type == "compression" && valueSize == 1)
            {
                byte[] c = new byte[1];
                if (!reader.ReadExact(position, c)) return null;
                compression = c[0];
            }
            else if (name == "tiles")
            {
                tiled = true;
            }

            position += valueSize;
        }

        if (tiled || maxY < minY)
            return null;

        int linesPerChunk = compression switch
        {
            0 or 1 or 2 => 1,
            3 or 5 => 16,
            4 or 6 or 7 or 8 or 9 => 32,
            10 => 32,
            11 => 256,
            _ => 1
        };
        long height = (long)maxY - minY + 1;
        long chunkCount = (height + linesPerChunk - 1) / linesPerChunk;
        if (chunkCount <= 0 || chunkCount > 10_000_000) return null;
        long tableBytes = checked(chunkCount * 8);
        if (position + tableBytes > volumeLength || tableBytes > 256L * 1024 * 1024) return null;

        byte[] offsets = new byte[checked((int)tableBytes)];
        if (!reader.ReadExact(position, offsets)) return null;
        long maxEnd = position + tableBytes;
        for (long i = 0; i < chunkCount; i++)
        {
            long off = BinaryPrimitives.ReadInt64LittleEndian(offsets.AsSpan(checked((int)(i * 8)), 8));
            if (off < 0 || start + off + 8 > volumeLength) return null;
            byte[] chunkHeader = new byte[8];
            if (!reader.ReadExact(start + off, chunkHeader)) return null;
            int dataSize = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            if (dataSize < 0) return null;
            long chunkEnd = checked(off + 8L + dataSize);
            if (chunkEnd > maxEnd - start) maxEnd = start + chunkEnd;
        }

        long length = maxEnd - start;
        return length > tableBytes && length <= MaxImageContainerSize && start + length <= volumeLength
            ? new RawFileAnalysis("EXR", length, "Çok İyi")
            : null;
    }

    private static string ReadNullTerminatedAscii(RawDeviceReader reader, ref long position, long volumeLength, int maxBytes)
    {
        var bytes = new List<byte>(Math.Min(64, maxBytes));
        byte[] one = new byte[1];
        while (bytes.Count < maxBytes && position < volumeLength)
        {
            if (!reader.ReadExact(position, one)) return string.Empty;
            position++;
            if (one[0] == 0) return Encoding.ASCII.GetString(bytes.ToArray());
            if (one[0] < 0x20 || one[0] > 0x7E) return string.Empty;
            bytes.Add(one[0]);
        }
        return string.Empty;
    }

    private static RawFileAnalysis? AnalyzeRiff(
        RawDeviceReader reader,
        long start,
        long volumeLength)
    {
        Span<byte> header = stackalloc byte[12];
        if (!reader.ReadExact(start, header)) return null;

        if (header[0] != (byte)'R' || header[1] != (byte)'I' ||
            header[2] != (byte)'F' || header[3] != (byte)'F')
            return null;

        uint riffPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        long declaredLength = 8L + riffPayloadSize;
        string type = Encoding.ASCII.GetString(header[8..12]);

        if (type == "WEBP")
        {
            if (declaredLength < 12 || start + declaredLength > volumeLength)
                return null;
            return new RawFileAnalysis("WEBP", declaredLength, "Çok İyi");
        }

        if (type == "WAVE")
        {
            if (declaredLength < 44 || start + declaredLength > volumeLength)
                return null;

            int inspect = (int)Math.Min(declaredLength, 1024L * 1024);
            byte[] wave = ReadBestEffortBytes(reader, start, inspect);
            if (!ContainsAscii(wave, "fmt ") || !ContainsAscii(wave, "data"))
                return null;

            return new RawFileAnalysis("WAV", declaredLength, "Çok İyi");
        }

        if (type != "AVI ")
            return null;

        long available = volumeLength - start;
        if (available < 64)
            return null;

        int inspectLength = (int)Math.Min(available, 8L * 1024 * 1024);
        byte[] aviHeader = ReadBestEffortBytes(reader, start, inspectLength);
        if (!ContainsAscii(aviHeader, "vids") || !ContainsAscii(aviHeader, "movi"))
            return null;

        if (declaredLength >= 12 && declaredLength <= available)
        {
            long openDmlLength = ExtendAviOpenDmlLength(reader, start, declaredLength, available);
            return new RawFileAnalysis("AVI", openDmlLength, "Çok İyi");
        }

        // RIFF uzunluğu silinme/bozulma sırasında zarar görmüş olabilir. İç LIST/chunk
        // sınırları halen okunabiliyorsa dosyanın güvenilir yapısal sonunu onlardan çıkar.
        long structuralLength = EstimateAviStructuralLength(reader, start, available);
        if (structuralLength < 1024 * 1024)
            return null;

        structuralLength = ExtendAviOpenDmlLength(reader, start, structuralLength, available);
        return new RawFileAnalysis("AVI", structuralLength, "Yeniden İnşa");
    }

    private static long ExtendAviOpenDmlLength(RawDeviceReader reader, long start, long firstSegmentLength, long available)
    {
        long total = firstSegmentLength;
        Span<byte> header = stackalloc byte[12];

        while (total + 12 <= available)
        {
            long segmentStart = start + total;
            if (!reader.ReadExact(segmentStart, header) || !header[..4].SequenceEqual("RIFF"u8))
                break;

            string formType = Encoding.ASCII.GetString(header[8..12]);
            if (formType != "AVIX")
                break;

            uint payload = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            long segmentLength = 8L + payload;
            if (segmentLength < 12 || total + segmentLength > available)
                break;

            total += segmentLength;
        }

        return Math.Min(total, available);
    }

    private static long EstimateAviStructuralLength(RawDeviceReader reader, long start, long available)
    {
        long maxEnd = start + available;
        long position = start + 12;
        long lastGoodEnd = position;
        bool seenMovi = false;
        Span<byte> chunkHeader = stackalloc byte[12];
        while (position + 8 <= maxEnd)
        {
            if (!reader.ReadExact(position, chunkHeader[..8]))
                break;

            string id = Encoding.ASCII.GetString(chunkHeader[..4]);
            if (!LooksLikeFourCc(id))
                break;

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            long total = 8L + size + (size & 1);
            if (total < 8 || position + total > maxEnd)
                break;

            if (id is "LIST" or "RIFF")
            {
                if (size < 4 || !reader.ReadExact(position, chunkHeader[..12]))
                    break;

                string listType = Encoding.ASCII.GetString(chunkHeader[8..12]);
                if (listType == "movi")
                    seenMovi = true;
            }
            else if (id == "idx1")
            {
                // idx1 geçerli son chunk ise dosya sınırı oldukça güvenilirdir.
                lastGoodEnd = position + total;
                break;
            }

            lastGoodEnd = position + total;
            position += total;
        }

        if (!seenMovi || lastGoodEnd <= start + 12)
            return 0;

        return lastGoodEnd - start;
    }

    private static RawFileAnalysis? AnalyzeIsoBmff(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken,
        bool allowRebuild)
    {
        Span<byte> first = stackalloc byte[24];
        if (!reader.ReadExact(start, first[..16])) return null;

        string firstType = Encoding.ASCII.GetString(first[4..8]);
        bool startsWithFtyp = firstType == "ftyp";
        bool startsWithFragment = firstType is "styp" or "moof" or "mdat" or "moov";
        if (!startsWithFtyp && (!allowRebuild || !startsWithFragment))
            return null;

        string extension = "MP4";
        if (startsWithFtyp)
        {
            uint firstBoxSize = BinaryPrimitives.ReadUInt32BigEndian(first[0..4]);
            if (firstBoxSize < 12) return null;
            string majorBrand = Encoding.ASCII.GetString(first[8..12]);
            extension = ClassifyIsoBrand(majorBrand);
            if (firstBoxSize <= 4096 && firstBoxSize <= volumeLength - start)
            {
                byte[] ftyp = ReadBestEffortBytes(reader, start, (int)firstBoxSize);
                string detectedImage = HeifRecoveryService.DetectExtensionFromFtyp(ftyp);
                if (detectedImage is "HEIC" or "HEIF" or "AVIF")
                    extension = detectedImage;
            }
        }

        long maxEnd = volumeLength;
        long position = start;
        bool seenFtyp = false;
        bool seenMediaData = false;
        bool seenMoov = false;
        bool seenMoof = false;
        bool seenStyp = false;
        long moovOffset = -1;
        long moovLength = 0;
        long firstMdatPayloadOffset = -1;
        long firstMdatPayloadLength = 0;
        Span<byte> boxHeader = stackalloc byte[16];

        while (position + 8 <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            if (!reader.ReadExact(position, boxHeader[..8])) break;

            ulong boxSize = BinaryPrimitives.ReadUInt32BigEndian(boxHeader[0..4]);
            string boxType = Encoding.ASCII.GetString(boxHeader[4..8]);
            int headerSize = 8;

            if (boxSize == 1)
            {
                if (!reader.ReadExact(position, boxHeader)) break;
                boxSize = BinaryPrimitives.ReadUInt64BigEndian(boxHeader[8..16]);
                headerSize = 16;
            }
            else if (boxSize == 0)
            {
                if (boxType != "mdat")
                    break;

                seenMediaData = true;
                long nextFile = FindNextIsoFileStart(reader, position + headerSize, maxEnd, cancellationToken);
                if (nextFile <= position)
                    return null;

                if (firstMdatPayloadOffset < 0)
                {
                    firstMdatPayloadOffset = position + headerSize;
                    firstMdatPayloadLength = Math.Max(0, nextFile - firstMdatPayloadOffset);
                }

                position = nextFile;
                break;
            }

            if (boxSize < (ulong)headerSize)
                break;
            if (!AllowedIsoTopLevelBoxes.Contains(boxType) && !LooksLikeFourCc(boxType))
                break;
            if (boxSize > (ulong)(maxEnd - position))
            {
                if (allowRebuild && position == start && boxType == "mdat")
                {
                    // mdat boyut alanı silinme/format sonrası bozulmuş olabilir. Sonraki
                    // güvenilir ftyp başlangıcına kadar olan alanı video parçası kabul et.
                    seenMediaData = true;
                    long nextFile = FindNextIsoFileStart(reader, position + headerSize, maxEnd, cancellationToken);
                    if (nextFile > position + 1024L * 1024)
                    {
                        if (firstMdatPayloadOffset < 0)
                        {
                            firstMdatPayloadOffset = position + headerSize;
                            firstMdatPayloadLength = Math.Max(0, nextFile - firstMdatPayloadOffset);
                        }
                        position = nextFile;
                    }
                }
                break;
            }

            if (boxType == "ftyp")
            {
                seenFtyp = true;
                if (position == start && boxSize >= 12 && reader.ReadExact(position, first[..12]))
                    extension = ClassifyIsoBrand(Encoding.ASCII.GetString(first[8..12]));
            }
            else if (boxType == "mdat")
            {
                seenMediaData = true;
                if (firstMdatPayloadOffset < 0)
                {
                    firstMdatPayloadOffset = position + headerSize;
                    firstMdatPayloadLength = Math.Max(0, (long)boxSize - headerSize);
                }
            }
            else if (boxType == "moov")
            {
                seenMoov = true;
                moovOffset = position;
                moovLength = (long)boxSize;
            }
            else if (boxType == "moof") seenMoof = true;
            else if (boxType == "styp") seenStyp = true;

            position += (long)boxSize;
        }

        long length = position - start;
        if (length < 24)
            return null;

        bool imageContainer = extension is "HEIC" or "HEIF" or "AVIF" or "CR3";
        if (imageContainer)
        {
            if (!seenFtyp)
                return null;

            if (extension is "HEIC" or "HEIF" or "AVIF")
            {
                HeifStructureResult heif = HeifRecoveryService.Analyze(reader, start, length, cancellationToken);
                if (!heif.IsValid)
                    return null;
                return new RawFileAnalysis(heif.Extension, length, heif.RecoveryState);
            }

            return new RawFileAnalysis(extension, length, seenMoov ? "Çok İyi" : "İyi");
        }

        bool hasVideoTrack = false;
        if (seenMoov && moovOffset >= start && moovLength > 0)
        {
            int inspectLength = (int)Math.Min(moovLength, 8L * 1024 * 1024);
            byte[] moovData = ReadBestEffortBytes(reader, moovOffset, inspectLength);
            hasVideoTrack = ContainsIsoVideoEvidence(moovData);
        }
        else if (seenMoof || seenStyp)
        {
            int inspectLength = (int)Math.Min(length, 8L * 1024 * 1024);
            byte[] fragmentData = ReadBestEffortBytes(reader, start, inspectLength);
            hasVideoTrack = ContainsIsoVideoEvidence(fragmentData);
        }

        bool hasVideoPayload = firstMdatPayloadOffset >= start && firstMdatPayloadLength > 0 &&
                               ContainsLikelyIsoVideoPayload(
                                   reader,
                                   firstMdatPayloadOffset,
                                   firstMdatPayloadLength,
                                   cancellationToken);

        if (seenFtyp && seenMediaData)
        {
            // M4A/M4B are audio-only ISO-BMFF containers. The previous video-centric gate
            // deliberately rejected them even though their box boundaries were fully valid.
            if (extension is "M4A" or "M4B")
                return new RawFileAnalysis(extension, length, seenMoov ? "Çok İyi" : "İyi");

            if (hasVideoTrack)
                return new RawFileAnalysis(extension, length, seenMoov || seenMoof ? "Çok İyi" : "İyi");

            // Deleted MP4/MOV files frequently lose or TRIM the small moov/sample-table
            // region while ftyp + mdat payload remains physically present. Rejecting those
            // candidates made NVMe recovery report zero videos even when media payload was
            // still readable. Keep them as partial video candidates; final verification and
            // confidence scoring remain responsible for separating usable content.
            if (hasVideoPayload || allowRebuild)
                return new RawFileAnalysis(extension, length, "Video Parçası");

            // ftyp + a structurally bounded mdat is already strong ISO-BMFF evidence. Audio-only
            // M4A/M4B brands are classified separately below, so generic MP4/MOV containers are
            // retained with deliberately low recovery state instead of being silently dropped.
            if (extension is "MP4" or "M4V" or "MOV" or "QT" or "3GP" or "3G2" or "F4V")
                return new RawFileAnalysis(extension, length, "Kısmi");

            return null;
        }

        if (!allowRebuild || !seenMediaData)
            return null;

        // Silinmiş video başlığının ilk kısmı ezilmiş olabilir. mdat/moof/styp/moov
        // kutuları halen tutarlıysa bunu kullanıcıya yeniden inşa adayı olarak gösteririz.
        if (seenMoov && (hasVideoTrack || hasVideoPayload))
            return new RawFileAnalysis("MP4", length, "Yeniden İnşa", PrependStandardMp4Header: true);

        if ((seenMoof || seenStyp) && (hasVideoTrack || hasVideoPayload))
            return new RawFileAnalysis("MP4", length, "Video Parçası");

        // Standalone mdat is valuable after partial TRIM/formatting. The previous ordering
        // required hasVideoTrack before reaching this branch, which made it effectively
        // unreachable when moov had been deleted. A >=1 MB bounded mdat is retained as a
        // fragment candidate; payload evidence raises its confidence later.
        if (firstType == "mdat" && length >= 1024L * 1024)
            return new RawFileAnalysis("MP4", length, "Video Parçası");

        return null;
    }

    private static bool ContainsLikelyIsoVideoPayload(
        RawDeviceReader reader,
        long payloadOffset,
        long payloadLength,
        CancellationToken cancellationToken)
    {
        if (payloadOffset < 0 || payloadLength < 32)
            return false;

        int inspectLength = (int)Math.Min(payloadLength, 2L * 1024 * 1024);
        if (inspectLength < 32)
            return false;

        byte[] data = ReadBestEffortBytes(reader, payloadOffset, inspectLength);
        if (data.Length < 32)
            return false;

        ReadOnlySpan<byte> span = data;
        int strongNalEvidence = 0;
        int validNalCount = 0;

        // MP4 commonly stores H.264/H.265 samples as 4-byte big-endian length-prefixed NALs.
        // Search a bounded prefix instead of assuming the very first mdat sample is video;
        // audio/video interleaving can put audio packets first.
        int searchLimit = Math.Min(span.Length - 8, 512 * 1024);
        for (int start = 0; start <= searchLimit; start++)
        {
            if ((start & 0x3FFF) == 0 && cancellationToken.IsCancellationRequested)
                return false;

            int position = start;
            int localValid = 0;
            int localStrong = 0;
            for (int sample = 0; sample < 4 && position + 5 <= span.Length; sample++)
            {
                uint nalLength = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(position, 4));
                if (nalLength == 0 || nalLength > 4 * 1024 * 1024 || nalLength > span.Length - position - 4)
                    break;

                int payload = position + 4;
                byte firstNalByte = span[payload];
                int h264Type = firstNalByte & 0x1F;
                int h265Type = (firstNalByte >> 1) & 0x3F;
                bool h264Valid = h264Type is >= 1 and <= 12;
                bool h265Valid = h265Type is >= 0 and <= 40;
                if (!h264Valid && !h265Valid)
                    break;

                localValid++;
                if (h264Type is 5 or 7 or 8 || h265Type is 19 or 20 or 21 or 32 or 33 or 34)
                    localStrong++;

                position = checked(position + 4 + (int)nalLength);
            }

            if (localValid >= 3 && localStrong >= 1)
                return true;

            validNalCount = Math.Max(validNalCount, localValid);
            strongNalEvidence = Math.Max(strongNalEvidence, localStrong);
        }

        // Annex-B payloads occur in camera exports and damaged/transcoded MP4 fragments.
        for (int i = 0; i + 6 < span.Length; i++)
        {
            if ((i & 0xFFFF) == 0 && cancellationToken.IsCancellationRequested)
                return false;

            int prefix = span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 1
                ? 3
                : i + 4 < span.Length && span[i] == 0 && span[i + 1] == 0 && span[i + 2] == 0 && span[i + 3] == 1
                    ? 4
                    : 0;
            if (prefix == 0)
                continue;

            byte nal = span[i + prefix];
            int h264Type = nal & 0x1F;
            int h265Type = (nal >> 1) & 0x3F;
            if (h264Type is 5 or 7 or 8 || h265Type is 19 or 20 or 21 or 32 or 33 or 34)
                return true;
        }

        return validNalCount >= 3 && strongNalEvidence >= 1;
    }

    private static long FindNextIsoFileStart(
        RawDeviceReader reader,
        long searchStart,
        long maxEnd,
        CancellationToken cancellationToken)
    {
        const int blockSize = 4 * 1024 * 1024;
        const int overlap = 16;
        byte[] block = new byte[blockSize + overlap];
        int carry = 0;
        long position = searchStart;

        while (position < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return -1;

            int request = (int)Math.Min(blockSize, maxEnd - position);
            int read = reader.ReadBestEffort(position, block.AsSpan(carry, request), out _);
            if (read <= 0)
                break;

            int count = carry + read;
            for (int i = 0; i + 12 <= count; i++)
            {
                if (block[i + 4] != (byte)'f' ||
                    block[i + 5] != (byte)'t' ||
                    block[i + 6] != (byte)'y' ||
                    block[i + 7] != (byte)'p')
                    continue;

                uint size = BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(i, 4));
                if (size is >= 12 and <= 1024 * 1024)
                    return position - carry + i;
            }

            carry = Math.Min(overlap, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);

            position += read;
        }

        return -1;
    }

    private static RawFileAnalysis? AnalyzeMpegProgramStream(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        RawFileAnalysis? analysis = AnalyzeFooterBased(
            reader,
            start,
            volumeLength,
            volumeLength - start,
            "MPG",
            [0x00, 0x00, 0x01, 0xB9],
            0,
            cancellationToken);

        if (analysis is null || analysis.Length <= 0)
            return null;

        int inspectLength = (int)Math.Min(analysis.Length, 8L * 1024 * 1024);
        byte[] data = ReadBestEffortBytes(reader, start, inspectLength);
        if (!ContainsMpegVideoEvidence(data))
            return null;

        return analysis;
    }

    private static RawFileAnalysis? AnalyzeMpegVideoStream(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        RawFileAnalysis? analysis = AnalyzeFooterBased(
            reader,
            start,
            volumeLength,
            volumeLength - start,
            "MPEG",
            [0x00, 0x00, 0x01, 0xB7],
            0,
            cancellationToken);

        if (analysis is null || analysis.Length <= 0)
            return null;

        int inspectLength = (int)Math.Min(analysis.Length, 8L * 1024 * 1024);
        byte[] data = ReadBestEffortBytes(reader, start, inspectLength);
        if (!StartsWithBytes(data, [0x00, 0x00, 0x01, 0xB3]) || !ContainsMpegVideoEvidence(data))
            return null;

        return new RawFileAnalysis("MPEG", analysis.Length, "İyi");
    }

    private static bool ContainsMpegVideoEvidence(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8)
            return false;

        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00 || data[i + 2] != 0x01)
                continue;

            byte code = data[i + 3];
            if (code == 0xB3 || code is >= 0xE0 and <= 0xEF)
                return true;
        }

        return false;
    }

    private static RawFileAnalysis? AnalyzeMpegTs(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken,
        bool preserveBoundaries = false)
    {
        int packetSize;
        int syncOffset;
        Span<byte> probe = stackalloc byte[192 * 8];
        if (!reader.ReadExact(start, probe))
            return null;

        if (HasTransportSync(probe, 188, 0, 6))
        {
            packetSize = 188;
            syncOffset = 0;
        }
        else if (HasTransportSync(probe, 192, 4, 6))
        {
            packetSize = 192;
            syncOffset = 4;
        }
        else
        {
            return null;
        }

        const int packetsPerBlock = 4096;
        const long resyncWindow = 256L * 1024 * 1024;
        int blockBytes = packetSize * packetsPerBlock;
        byte[] block = new byte[blockBytes];
        long maxEnd = volumeLength;
        long position = start;
        long lastGoodEnd = start;
        long verifiedPackets = 0;
        long skippedCorruptBytes = 0;
        int videoPesPackets = 0;
        bool stop = false;

        while (!stop && position + packetSize <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            int request = (int)Math.Min(blockBytes, maxEnd - position);
            int read = reader.ReadBestEffort(position, block.AsSpan(0, request), out _);
            if (read < packetSize)
                break;

            int completePackets = read / packetSize;
            bool resynced = false;

            for (int i = 0; i < completePackets; i++)
            {
                int packetOffset = i * packetSize;
                if (!IsValidTransportPacket(block.AsSpan(packetOffset, packetSize), syncOffset, out bool hasVideoPes))
                {
                    if (preserveBoundaries)
                    {
                        stop = true;
                        break;
                    }
                    long badPosition = position + packetOffset;
                    long next = FindNextTransportSync(
                        reader,
                        badPosition + 1,
                        Math.Min(maxEnd, badPosition + resyncWindow),
                        packetSize,
                        syncOffset,
                        cancellationToken);

                    if (next < 0)
                    {
                        stop = true;
                        break;
                    }

                    skippedCorruptBytes += next - badPosition;
                    position = next;
                    resynced = true;
                    break;
                }

                verifiedPackets++;
                if (hasVideoPes)
                    videoPesPackets++;

                lastGoodEnd = position + packetOffset + packetSize;
            }

            if (stop)
                break;
            if (resynced)
                continue;

            long advance = completePackets * (long)packetSize;
            if (advance <= 0)
                break;

            position += advance;
            if (read < request)
                break;
        }

        long spanLength = lastGoodEnd - start;
        if (verifiedPackets < 20 || spanLength < packetSize * 20L || videoPesPackets == 0)
            return null;

        return new RawFileAnalysis(
            packetSize == 192 ? "MTS" : "TS",
            spanLength,
            skippedCorruptBytes > 0 ? "Yeniden İnşa" : "İyi");
    }

    private static bool IsValidTransportPacket(
        ReadOnlySpan<byte> packet,
        int syncOffset,
        out bool hasVideoPes)
    {
        hasVideoPes = false;
        if (syncOffset < 0 || syncOffset + 4 > packet.Length || packet[syncOffset] != 0x47)
            return false;

        byte b1 = packet[syncOffset + 1];
        byte b3 = packet[syncOffset + 3];
        if ((b1 & 0x80) != 0)
            return false;

        int adaptationControl = (b3 >> 4) & 0x03;
        if (adaptationControl == 0)
            return false;

        bool payloadUnitStart = (b1 & 0x40) != 0;
        bool hasPayload = adaptationControl is 1 or 3;
        if (!payloadUnitStart || !hasPayload)
            return true;

        int payloadIndex = syncOffset + 4;
        if (adaptationControl == 3)
        {
            if (payloadIndex >= packet.Length)
                return false;

            int adaptationLength = packet[payloadIndex];
            payloadIndex += 1 + adaptationLength;
            if (payloadIndex > packet.Length)
                return false;
        }

        if (payloadIndex + 4 <= packet.Length &&
            packet[payloadIndex] == 0x00 &&
            packet[payloadIndex + 1] == 0x00 &&
            packet[payloadIndex + 2] == 0x01 &&
            packet[payloadIndex + 3] is >= 0xE0 and <= 0xEF)
            hasVideoPes = true;

        return true;
    }

    private static long FindNextTransportSync(
        RawDeviceReader reader,
        long searchStart,
        long searchEnd,
        int packetSize,
        int syncOffset,
        CancellationToken cancellationToken)
    {
        if (searchStart >= searchEnd)
            return -1;

        const int blockSize = 1024 * 1024;
        int overlap = syncOffset + packetSize * 8 + 8;
        byte[] buffer = new byte[blockSize + overlap];
        int carry = 0;
        long position = searchStart;

        while (position < searchEnd)
        {
            if (cancellationToken.IsCancellationRequested)
                return -1;

            int request = (int)Math.Min(blockSize, searchEnd - position);
            int read = reader.ReadBestEffort(position, buffer.AsSpan(carry, request), out _);
            if (read <= 0)
                break;

            int count = carry + read;
            int needed = syncOffset + packetSize * 7 + 1;
            for (int i = 0; i + needed <= count; i++)
            {
                bool valid = true;
                for (int packet = 0; packet < 8; packet++)
                {
                    if (buffer[i + syncOffset + packet * packetSize] != 0x47)
                    {
                        valid = false;
                        break;
                    }
                }

                if (valid)
                    return position - carry + i;
            }

            carry = Math.Min(overlap, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return -1;
    }

    private static bool HasTransportSync(ReadOnlySpan<byte> data, int packetSize, int syncOffset, int packetCount)
    {
        for (int i = 0; i < packetCount; i++)
        {
            int index = syncOffset + i * packetSize;
            if (index >= data.Length || data[index] != 0x47)
                return false;
        }

        return true;
    }

    private static bool ContainsIsoVideoEvidence(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20)
            return false;

        for (int i = 4; i + 16 <= data.Length; i++)
        {
            if (data[i] == (byte)'h' && data[i + 1] == (byte)'d' &&
                data[i + 2] == (byte)'l' && data[i + 3] == (byte)'r' &&
                data[i + 12] == (byte)'v' && data[i + 13] == (byte)'i' &&
                data[i + 14] == (byte)'d' && data[i + 15] == (byte)'e')
                return true;
        }

        return ContainsAscii(data, "avc1") ||
               ContainsAscii(data, "avc3") ||
               ContainsAscii(data, "hvc1") ||
               ContainsAscii(data, "hev1") ||
               ContainsAscii(data, "dvhe") ||
               ContainsAscii(data, "dvh1") ||
               ContainsAscii(data, "vp08") ||
               ContainsAscii(data, "vp09") ||
               ContainsAscii(data, "av01") ||
               ContainsAscii(data, "mp4v") ||
               ContainsAscii(data, "encv");
    }

    private static string ClassifyIsoBrand(string majorBrand)
    {
        string brand = majorBrand.TrimEnd('\0', ' ');

        if (brand.StartsWith("qt", StringComparison.OrdinalIgnoreCase))
            return "MOV";

        if (brand.StartsWith("3gp", StringComparison.OrdinalIgnoreCase))
            return "3GP";

        if (brand.StartsWith("3g2", StringComparison.OrdinalIgnoreCase))
            return "3G2";

        if (brand.StartsWith("M4A", StringComparison.OrdinalIgnoreCase))
            return "M4A";

        if (brand.StartsWith("M4B", StringComparison.OrdinalIgnoreCase))
            return "M4B";

        if (brand.StartsWith("M4V", StringComparison.OrdinalIgnoreCase))
            return "M4V";

        if (brand.StartsWith("F4V", StringComparison.OrdinalIgnoreCase))
            return "F4V";

        if (brand.Equals("crx", StringComparison.OrdinalIgnoreCase))
            return "CR3";

        if (brand is "heic" or "heix" or "hevc" or "hevx")
            return "HEIC";

        if (brand is "mif1" or "msf1")
            return "HEIF";

        if (brand is "avif" or "avis")
            return "AVIF";

        return "MP4";
    }


    private static RawFileAnalysis? AnalyzeAnnexBNal(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        bool h265,
        CancellationToken cancellationToken)
    {
        long maxEnd = volumeLength;

        if (!TryReadAnnexBNalHeader(reader, start, h265, out int firstPrefix, out int firstType))
            return null;

        long current = start;
        int currentPrefix = firstPrefix;
        int currentType = firstType;
        int nalCount = 0;
        int keyFrames = 0;
        int parameterSets = 0;
        long lastBoundary = start;

        while (current < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            CountNalType(currentType, h265, ref keyFrames, ref parameterSets);
            nalCount++;

            long searchStart = current + currentPrefix + (h265 ? 2 : 1);
            long next = FindNextAnnexBStart(reader, searchStart, maxEnd, cancellationToken);
            if (next < 0)
                break;

            if (!TryReadAnnexBNalHeader(reader, next, h265, out int nextPrefix, out int nextType))
            {
                lastBoundary = next;
                break;
            }

            lastBoundary = next;
            current = next;
            currentPrefix = nextPrefix;
            currentType = nextType;
        }

        long length = lastBoundary - start;
        if (nalCount < 8 || length < 256L * 1024 || keyFrames == 0)
            return null;

        string extension = h265 ? "H265" : "H264";
        string state = parameterSets > 0 ? "İyi" : "Video Akışı";
        return new RawFileAnalysis(extension, length, state);
    }

    private static bool TryReadAnnexBNalHeader(
        RawDeviceReader reader,
        long offset,
        bool h265,
        out int prefixLength,
        out int nalType)
    {
        prefixLength = 0;
        nalType = -1;

        Span<byte> probe = stackalloc byte[7];
        if (!reader.ReadExact(offset, probe))
            return false;

        if (probe[0] == 0x00 && probe[1] == 0x00 && probe[2] == 0x00 && probe[3] == 0x01)
            prefixLength = 4;
        else if (probe[0] == 0x00 && probe[1] == 0x00 && probe[2] == 0x01)
            prefixLength = 3;
        else
            return false;

        byte b0 = probe[prefixLength];
        if ((b0 & 0x80) != 0)
            return false;

        if (!h265)
        {
            nalType = b0 & 0x1F;
            return nalType is >= 1 and <= 12;
        }

        byte b1 = probe[prefixLength + 1];
        if ((b1 & 0x07) == 0)
            return false;

        nalType = (b0 >> 1) & 0x3F;
        return nalType <= 40;
    }

    private static void CountNalType(int type, bool h265, ref int keyFrames, ref int parameterSets)
    {
        if (!h265)
        {
            if (type == 5) keyFrames++;
            if (type is 7 or 8) parameterSets++;
            return;
        }

        if (type is 19 or 20 or 21) keyFrames++;
        if (type is 32 or 33 or 34) parameterSets++;
    }

    private static long FindNextAnnexBStart(
        RawDeviceReader reader,
        long searchStart,
        long searchEnd,
        CancellationToken cancellationToken)
    {
        const int blockSize = 1024 * 1024;
        byte[] block = new byte[blockSize + 4];
        int carry = 0;
        long position = searchStart;

        while (position < searchEnd)
        {
            if (cancellationToken.IsCancellationRequested) return -1;

            int request = (int)Math.Min(blockSize, searchEnd - position);
            int read = reader.ReadBestEffort(position, block.AsSpan(carry, request), out _);
            if (read <= 0) break;

            int count = carry + read;
            for (int i = 0; i + 3 < count; i++)
            {
                if (block[i] != 0x00 || block[i + 1] != 0x00)
                    continue;

                if (block[i + 2] == 0x01 ||
                    (block[i + 2] == 0x00 && i + 3 < count && block[i + 3] == 0x01))
                    return position - carry + i;
            }

            carry = Math.Min(4, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);

            position += read;
        }

        return -1;
    }

    private static RawFileAnalysis? AnalyzePdf(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[8];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual("%PDF"u8))
            return null;

        long maxEnd = Math.Min(volumeLength, start + MaxPdfSize);
        const int blockSize = 1024 * 1024;
        const long eofTailSearch = 4L * 1024 * 1024;
        ReadOnlySpan<byte> footer = "%%EOF"u8;
        byte[] block = new byte[blockSize + 4];
        long offset = start;
        int carry = 0;
        long lastEofEnd = -1;

        while (offset < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (lastEofEnd > 0 && offset - carry > lastEofEnd + eofTailSearch)
                break;

            int request = (int)Math.Min(blockSize, maxEnd - offset);
            int read = reader.ReadBestEffort(offset, block.AsSpan(carry, request), out _);
            if (read <= 0) break;

            int count = carry + read;
            int search = 0;
            while (search <= count - footer.Length)
            {
                int match = IndexOf(block.AsSpan(search, count - search), footer);
                if (match < 0) break;

                int absoluteMatch = search + match;
                lastEofEnd = offset - carry + absoluteMatch + footer.Length;
                search = absoluteMatch + footer.Length;
            }

            carry = Math.Min(4, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);

            offset += read;
        }

        if (lastEofEnd <= start + 8)
            return null;

        long length = lastEofEnd - start;
        int tailLength = (int)Math.Min(128L * 1024, length);
        byte[] tail = ReadBestEffortBytes(reader, lastEofEnd - tailLength, tailLength);
        string state = ContainsAscii(tail, "startxref") ? "Çok İyi" : "İyi";
        return new RawFileAnalysis("PDF", length, state);
    }

    private static RawFileAnalysis? AnalyzeRar(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> signature = stackalloc byte[8];
        if (!reader.ReadExact(start, signature)) return null;

        bool rar4 = signature[..7].SequenceEqual(new byte[]
        {
            (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x00
        });
        bool rar5 = signature.SequenceEqual(new byte[]
        {
            (byte)'R', (byte)'a', (byte)'r', (byte)'!', 0x1A, 0x07, 0x01, 0x00
        });

        if (!rar4 && !rar5)
            return null;

        return rar4
            ? AnalyzeRar4(reader, start, volumeLength, cancellationToken)
            : AnalyzeRar5(reader, start, volumeLength, cancellationToken);
    }

    private static RawFileAnalysis? AnalyzeRar4(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long maxEnd = Math.Min(volumeLength, start + MaxRarSize);
        long position = start + 7;
        int blockCount = 0;
        Span<byte> basic = stackalloc byte[11];

        while (position + 7 <= maxEnd && blockCount < 2_000_000)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (!reader.ReadExact(position, basic[..7])) return null;

            byte type = basic[2];
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(basic.Slice(3, 2));
            ushort headerSize = BinaryPrimitives.ReadUInt16LittleEndian(basic.Slice(5, 2));
            if (headerSize < 7 || position + headerSize > maxEnd)
                return null;

            uint dataSize = 0;
            if ((flags & 0x8000) != 0)
            {
                if (!reader.ReadExact(position + 7, basic[..4])) return null;
                dataSize = BinaryPrimitives.ReadUInt32LittleEndian(basic[..4]);
            }

            long total = headerSize + (long)dataSize;
            if (total <= 0 || position + total > maxEnd)
                return null;

            position += total;
            blockCount++;

            if (type == 0x7B)
                return new RawFileAnalysis("RAR", position - start, "Çok İyi");
        }

        return null;
    }

    private static RawFileAnalysis? AnalyzeRar5(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long maxEnd = Math.Min(volumeLength, start + MaxRarSize);
        long position = start + 8;
        int blockCount = 0;

        while (position + 7 <= maxEnd && blockCount < 2_000_000)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            byte[] probe = reader.ReadBytes(position, (int)Math.Min(256L, maxEnd - position));
            if (probe.Length < 7) return null;

            int cursor = 4;
            if (!TryReadRar5VInt(probe, ref cursor, out ulong headerSize) ||
                headerSize == 0 || headerSize > 64UL * 1024 * 1024)
                return null;

            int headerDataStart = cursor;
            if (!TryReadRar5VInt(probe, ref cursor, out ulong headerType) ||
                !TryReadRar5VInt(probe, ref cursor, out ulong headerFlags))
                return null;

            ulong extraSize = 0;
            ulong dataSize = 0;
            if ((headerFlags & 0x0001) != 0 && !TryReadRar5VInt(probe, ref cursor, out extraSize))
                return null;
            if ((headerFlags & 0x0002) != 0 && !TryReadRar5VInt(probe, ref cursor, out dataSize))
                return null;

            ulong headerTotal = 4UL + (ulong)(headerDataStart - 4) + headerSize;
            if (headerTotal > (ulong)long.MaxValue ||
                dataSize > (ulong)long.MaxValue ||
                dataSize > (ulong)long.MaxValue - headerTotal)
                return null;

            long total = (long)(headerTotal + dataSize);
            if (total <= 0 || position + total > maxEnd)
                return null;

            position += total;
            blockCount++;

            if (headerType == 5)
                return new RawFileAnalysis("RAR", position - start, "Çok İyi");
        }

        return null;
    }

    private static bool TryReadRar5VInt(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        int shift = 0;

        for (int i = 0; i < 10; i++)
        {
            if (offset >= data.Length || shift >= 64)
                return false;

            byte current = data[offset++];
            value |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }

    internal static bool LooksLikeMp3Candidate(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 10 && data[..3].SequenceEqual("ID3"u8))
        {
            byte version = data[3];
            if (version is >= 2 and <= 4 &&
                data[6] < 0x80 && data[7] < 0x80 && data[8] < 0x80 && data[9] < 0x80)
                return true;
        }

        int cursor = 0;
        int frames = 0;
        while (cursor + 4 <= data.Length && frames < 3)
        {
            if (!TryGetMp3FrameLength(data.Slice(cursor, 4), out int frameLength) ||
                frameLength < 24 || cursor + frameLength > data.Length)
                return false;
            cursor += frameLength;
            frames++;
        }
        return frames >= 3;
    }

    internal static bool LooksLikeAacAdtsCandidate(ReadOnlySpan<byte> data)
    {
        int cursor = 0;
        int frames = 0;
        while (cursor + 7 <= data.Length && frames < 3)
        {
            if (!TryGetAdtsFrameLength(data.Slice(cursor, 7), out int frameLength) ||
                frameLength < 7 || cursor + frameLength > data.Length)
                return false;
            cursor += frameLength;
            frames++;
        }
        return frames >= 3;
    }

    private static RawFileAnalysis? AnalyzeMp3(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long position = start;
        Span<byte> id3 = stackalloc byte[10];
        if (position + 10 <= volumeLength && reader.ReadExact(position, id3) && id3[..3].SequenceEqual("ID3"u8))
        {
            byte version = id3[3];
            if (version is < 2 or > 4 || id3[6] >= 0x80 || id3[7] >= 0x80 || id3[8] >= 0x80 || id3[9] >= 0x80)
                return null;

            int tagSize = (id3[6] << 21) | (id3[7] << 14) | (id3[8] << 7) | id3[9];
            long tagLength = 10L + tagSize + ((version == 4 && (id3[5] & 0x10) != 0) ? 10L : 0L);
            if (tagLength < 10 || start + tagLength > volumeLength)
                return null;
            position = start + tagLength;
        }

        long lastGood = position;
        int frames = 0;
        Span<byte> header = stackalloc byte[4];
        while (position + 4 <= volumeLength && frames < 2_000_000)
        {
            if ((frames & 0x3FFF) == 0 && cancellationToken.IsCancellationRequested)
                return null;
            if (!reader.ReadExact(position, header) || !TryGetMp3FrameLength(header, out int frameLength))
                break;
            if (frameLength < 24 || position + frameLength > volumeLength)
                break;

            position += frameLength;
            lastGood = position;
            frames++;
        }

        if (frames < 5 || lastGood - start < 4096)
            return null;

        Span<byte> tail = stackalloc byte[3];
        if (lastGood + 128 <= volumeLength && reader.ReadExact(lastGood, tail) && tail.SequenceEqual("TAG"u8))
            lastGood += 128;

        return new RawFileAnalysis("MP3", lastGood - start, "İyi");
    }

    private static bool TryGetMp3FrameLength(ReadOnlySpan<byte> header, out int frameLength)
    {
        frameLength = 0;
        if (header.Length < 4 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
            return false;

        int versionBits = (header[1] >> 3) & 0x03;
        int layerBits = (header[1] >> 1) & 0x03;
        if (versionBits == 1 || layerBits != 1) // MPEG Layer III only
            return false;

        int bitrateIndex = (header[2] >> 4) & 0x0F;
        int sampleIndex = (header[2] >> 2) & 0x03;
        if (bitrateIndex is 0 or 15 || sampleIndex == 3)
            return false;

        bool mpeg1 = versionBits == 3;
        int bitrateKbps = bitrateIndex switch
        {
            1 => mpeg1 ? 32 : 8,
            2 => mpeg1 ? 40 : 16,
            3 => mpeg1 ? 48 : 24,
            4 => mpeg1 ? 56 : 32,
            5 => mpeg1 ? 64 : 40,
            6 => mpeg1 ? 80 : 48,
            7 => mpeg1 ? 96 : 56,
            8 => mpeg1 ? 112 : 64,
            9 => mpeg1 ? 128 : 80,
            10 => mpeg1 ? 160 : 96,
            11 => mpeg1 ? 192 : 112,
            12 => mpeg1 ? 224 : 128,
            13 => mpeg1 ? 256 : 144,
            14 => mpeg1 ? 320 : 160,
            _ => 0
        };
        if (bitrateKbps <= 0)
            return false;

        int baseRate = sampleIndex switch { 0 => 44100, 1 => 48000, 2 => 32000, _ => 0 };
        int sampleRate = versionBits switch
        {
            3 => baseRate,
            2 => baseRate / 2,
            0 => baseRate / 4,
            _ => 0
        };
        if (sampleRate <= 0)
            return false;

        int padding = (header[2] >> 1) & 0x01;
        frameLength = (mpeg1 ? 144000 : 72000) * bitrateKbps / sampleRate + padding;
        return frameLength is >= 24 and <= 8192;
    }

    private static RawFileAnalysis? AnalyzeAacAdts(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long position = start;
        long lastGood = start;
        int frames = 0;
        Span<byte> header = stackalloc byte[7];
        while (position + 7 <= volumeLength && frames < 2_000_000)
        {
            if ((frames & 0x3FFF) == 0 && cancellationToken.IsCancellationRequested)
                return null;
            if (!reader.ReadExact(position, header) || !TryGetAdtsFrameLength(header, out int frameLength))
                break;
            if (position + frameLength > volumeLength)
                break;
            position += frameLength;
            lastGood = position;
            frames++;
        }

        return frames >= 5 && lastGood - start >= 4096
            ? new RawFileAnalysis("AAC", lastGood - start, "İyi")
            : null;
    }

    private static bool TryGetAdtsFrameLength(ReadOnlySpan<byte> header, out int frameLength)
    {
        frameLength = 0;
        if (header.Length < 7 || header[0] != 0xFF || (header[1] & 0xF6) != 0xF0)
            return false;
        int sampleFrequencyIndex = (header[2] >> 2) & 0x0F;
        if (sampleFrequencyIndex == 0x0F)
            return false;
        frameLength = ((header[3] & 0x03) << 11) | (header[4] << 3) | ((header[5] & 0xE0) >> 5);
        return frameLength >= 7;
    }

    private static RawFileAnalysis? AnalyzeAiff(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> header = stackalloc byte[12];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual("FORM"u8))
            return null;
        string type = Encoding.ASCII.GetString(header[8..12]);
        if (type is not "AIFF" and not "AIFC")
            return null;
        uint payload = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        long length = 8L + payload;
        if (length < 54 || length > volumeLength - start)
            return null;
        int inspect = (int)Math.Min(length, 1024L * 1024);
        byte[] data = ReadBestEffortBytes(reader, start, inspect);
        if (!ContainsAscii(data, "COMM") || !ContainsAscii(data, "SSND"))
            return null;
        return new RawFileAnalysis("AIFF", length, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzeAu(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> header = stackalloc byte[24];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual(".snd"u8))
            return null;
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(header[12..16]);
        uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint channels = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (dataOffset < 24 || dataSize == uint.MaxValue || encoding == 0 || sampleRate == 0 || channels is 0 or > 32)
            return null;
        long length = (long)dataOffset + dataSize;
        return length >= 24 && length <= volumeLength - start
            ? new RawFileAnalysis("AU", length, "Çok İyi")
            : null;
    }

    private static RawFileAnalysis? AnalyzeMidi(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[14];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual("MThd"u8))
            return null;
        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(header[4..8]);
        if (headerLength < 6 || headerLength > 1024 || start + 8L + headerLength > volumeLength)
            return null;
        ushort trackCount = BinaryPrimitives.ReadUInt16BigEndian(header[10..12]);
        if (trackCount == 0 || trackCount > 4096)
            return null;

        long position = start + 8L + headerLength;
        Span<byte> chunk = stackalloc byte[8];
        int parsedTracks = 0;
        while (parsedTracks < trackCount && position + 8 <= volumeLength)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;
            if (!reader.ReadExact(position, chunk) || !chunk[..4].SequenceEqual("MTrk"u8))
                return null;
            uint size = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..8]);
            long next = position + 8L + size;
            if (next <= position || next > volumeLength)
                return null;
            position = next;
            parsedTracks++;
        }

        return parsedTracks == trackCount
            ? new RawFileAnalysis("MID", position - start, "Çok İyi")
            : null;
    }

    private static RawFileAnalysis? AnalyzeSevenZip(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> header = stackalloc byte[32];
        ReadOnlySpan<byte> signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        if (!reader.ReadExact(start, header) || !header[..6].SequenceEqual(signature))
            return null;
        ulong nextHeaderOffset = BinaryPrimitives.ReadUInt64LittleEndian(header[12..20]);
        ulong nextHeaderSize = BinaryPrimitives.ReadUInt64LittleEndian(header[20..28]);
        ulong length = 32UL + nextHeaderOffset + nextHeaderSize;
        if (length < 32 || length > (ulong)(volumeLength - start) || length > long.MaxValue)
            return null;
        return new RawFileAnalysis("7Z", (long)length, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzeCab(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> header = stackalloc byte[36];
        if (!reader.ReadExact(start, header) || !header[..4].SequenceEqual("MSCF"u8))
            return null;
        uint cabinetSize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..12]);
        uint filesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        ushort folders = BinaryPrimitives.ReadUInt16LittleEndian(header[26..28]);
        ushort files = BinaryPrimitives.ReadUInt16LittleEndian(header[28..30]);
        if (cabinetSize < 36 || cabinetSize > volumeLength - start || filesOffset >= cabinetSize || folders == 0 || files == 0)
            return null;
        return new RawFileAnalysis("CAB", cabinetSize, "Çok İyi");
    }

    private static RawFileAnalysis? AnalyzePortableExecutable(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> dos = stackalloc byte[64];
        if (!reader.ReadExact(start, dos) || dos[0] != (byte)'M' || dos[1] != (byte)'Z')
            return null;
        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..0x40]);
        if (peOffset < 64 || peOffset > 1024 * 1024 || start + peOffset + 24 > volumeLength)
            return null;

        byte[] coff = reader.ReadBytes(start + peOffset, 24);
        if (coff.Length != 24 || !coff.AsSpan(0, 4).SequenceEqual("PE\0\0"u8))
            return null;
        ushort sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(6, 2));
        ushort optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(20, 2));
        ushort characteristics = BinaryPrimitives.ReadUInt16LittleEndian(coff.AsSpan(22, 2));
        if (sectionCount is 0 or > 96 || optionalSize > 4096)
            return null;

        long sectionTable = start + peOffset + 24L + optionalSize;
        long sectionBytes = sectionCount * 40L;
        if (sectionTable < start || sectionTable + sectionBytes > volumeLength)
            return null;
        byte[] sections = reader.ReadBytes(sectionTable, checked((int)sectionBytes));
        if (sections.Length != sectionBytes)
            return null;

        long end = peOffset + 24L + optionalSize + sectionBytes;
        for (int i = 0; i < sectionCount; i++)
        {
            ReadOnlySpan<byte> section = sections.AsSpan(i * 40, 40);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(16, 4));
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(section.Slice(20, 4));
            if (rawSize == 0)
                continue;
            long sectionEnd = (long)rawPointer + rawSize;
            if (sectionEnd > end)
                end = sectionEnd;
        }

        if (end <= 64 || end > volumeLength - start || end > 8L * 1024 * 1024 * 1024)
            return null;
        string extension = (characteristics & 0x2000) != 0 ? "DLL" : "EXE";
        return new RawFileAnalysis(extension, end, "İyi");
    }

    private static RawFileAnalysis? AnalyzeOleCompound(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        const uint FreeSect = 0xFFFFFFFF;
        const uint EndOfChain = 0xFFFFFFFE;

        byte[] header = reader.ReadBytes(start, 512);
        if (header.Length != 512 ||
            !header.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
            return null;

        if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2)) != 0xFFFE)
            return null;

        ushort sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30, 2));
        if (sectorShift is not 9 and not 12)
            return null;

        int sectorSize = 1 << sectorShift;
        uint fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44, 4));
        uint firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48, 4));
        uint firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68, 4));
        uint difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72, 4));

        long maxRelative = Math.Min(MaxOleSize, volumeLength - start);
        if (maxRelative < sectorSize || fatSectorCount == 0)
            return null;

        long maxSectorId = maxRelative / sectorSize - 2;
        if (maxSectorId < 0)
            return null;

        var fatSectorIds = new List<uint>(checked((int)Math.Min(fatSectorCount, 1_000_000u)));
        for (int i = 0; i < 109 && fatSectorIds.Count < fatSectorCount; i++)
        {
            uint sector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + i * 4, 4));
            if (sector != FreeSect && sector != EndOfChain)
                fatSectorIds.Add(sector);
        }

        uint difatSector = firstDifatSector;
        var visitedDifat = new HashSet<uint>();
        int difatEntriesPerSector = sectorSize / 4 - 1;

        for (uint i = 0;
             i < difatSectorCount && difatSector != EndOfChain && difatSector != FreeSect;
             i++)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            if (difatSector > maxSectorId || !visitedDifat.Add(difatSector))
                return null;

            byte[] sectorData = reader.ReadBytes(
                start + (difatSector + 1L) * sectorSize,
                sectorSize);
            if (sectorData.Length != sectorSize)
                return null;

            for (int entry = 0; entry < difatEntriesPerSector && fatSectorIds.Count < fatSectorCount; entry++)
            {
                uint fatSector = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(entry * 4, 4));
                if (fatSector != FreeSect && fatSector != EndOfChain)
                    fatSectorIds.Add(fatSector);
            }

            difatSector = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(sectorSize - 4, 4));
        }

        if (fatSectorIds.Count < fatSectorCount)
            return null;

        int entriesPerFatSector = sectorSize / 4;
        long totalFatEntries = (long)fatSectorCount * entriesPerFatSector;
        if (totalFatEntries <= 0 || totalFatEntries > 32_000_000)
            return null;

        var fat = new uint[(int)totalFatEntries];
        long highestAllocatedSector = -1;

        for (int fatIndex = 0; fatIndex < fatSectorCount; fatIndex++)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            uint fatSector = fatSectorIds[fatIndex];
            if (fatSector > maxSectorId)
                return null;

            highestAllocatedSector = Math.Max(highestAllocatedSector, fatSector);
            byte[] fatData = reader.ReadBytes(start + (fatSector + 1L) * sectorSize, sectorSize);
            if (fatData.Length != sectorSize)
                return null;

            for (int i = 0; i < entriesPerFatSector; i++)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(fatData.AsSpan(i * 4, 4));
                int index = fatIndex * entriesPerFatSector + i;
                fat[index] = value;

                if (index <= maxSectorId && value != FreeSect)
                    highestAllocatedSector = Math.Max(highestAllocatedSector, index);
            }
        }

        foreach (uint sector in visitedDifat)
            highestAllocatedSector = Math.Max(highestAllocatedSector, sector);

        byte[] directory = ReadOleChain(
            reader,
            start,
            firstDirectorySector,
            sectorSize,
            fat,
            maxSectorId,
            32L * 1024 * 1024,
            cancellationToken);

        if (directory.Length < 128)
            return null;

        bool isDoc = false;
        bool isXls = false;
        bool isPpt = false;
        bool isMsg = false;
        bool hasMsiTables = false;
        bool hasMsiStringPool = false;

        for (int offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            ReadOnlySpan<byte> entry = directory.AsSpan(offset, 128);
            ushort nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(64, 2));
            byte objectType = entry[66];
            if (objectType == 0 || nameBytes < 2 || nameBytes > 64)
                continue;

            int usableNameBytes = Math.Max(0, nameBytes - 2);
            string name = Encoding.Unicode.GetString(entry.Slice(0, usableNameBytes));
            if (name.Equals("WordDocument", StringComparison.OrdinalIgnoreCase)) isDoc = true;
            if (name.Equals("Workbook", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Book", StringComparison.OrdinalIgnoreCase)) isXls = true;
            if (name.Equals("PowerPoint Document", StringComparison.OrdinalIgnoreCase)) isPpt = true;
            if (name.Equals("__properties_version1.0", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase)) isMsg = true;
            if (name.Equals("_Tables", StringComparison.OrdinalIgnoreCase)) hasMsiTables = true;
            if (name.Equals("_StringPool", StringComparison.OrdinalIgnoreCase)) hasMsiStringPool = true;
        }

        string? extension = isMsg ? "MSG"
            : hasMsiTables && hasMsiStringPool ? "MSI"
            : isDoc ? "DOC"
            : isXls ? "XLS"
            : isPpt ? "PPT"
            : null;
        if (extension is null || highestAllocatedSector < 0)
            return null;

        long length = checked((highestAllocatedSector + 2L) * sectorSize);
        if (length <= 512 || length > maxRelative)
            return null;

        return new RawFileAnalysis(extension, length, "İyi");
    }

    private static byte[] ReadOleChain(
        RawDeviceReader reader,
        long start,
        uint firstSector,
        int sectorSize,
        uint[] fat,
        long maxSectorId,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        const uint FreeSect = 0xFFFFFFFF;
        const uint EndOfChain = 0xFFFFFFFE;
        const uint FatSect = 0xFFFFFFFD;
        const uint DifSect = 0xFFFFFFFC;

        using var memory = new MemoryStream();
        uint sector = firstSector;
        var visited = new HashSet<uint>();

        while (sector != EndOfChain &&
               sector != FreeSect &&
               sector != FatSect &&
               sector != DifSect &&
               sector <= maxSectorId &&
               sector < fat.Length &&
               visited.Add(sector) &&
               memory.Length < maxBytes)
        {
            if (cancellationToken.IsCancellationRequested)
                return [];

            byte[] data = reader.ReadBytes(start + (sector + 1L) * sectorSize, sectorSize);
            if (data.Length != sectorSize)
                return [];

            memory.Write(data, 0, data.Length);
            sector = fat[(int)sector];
        }

        return memory.ToArray();
    }

    private static RawFileAnalysis? AnalyzeLengthPrefixedNal(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        bool h265,
        CancellationToken cancellationToken)
    {
        long maxEnd = volumeLength;
        long position = start;
        int nalCount = 0;
        int keyFrames = 0;
        int parameterSets = 0;
        long payloadBytes = 0;
        Span<byte> header = stackalloc byte[6];

        while (position + 6 <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            if (!reader.ReadExact(position, header))
                break;

            uint nalLength = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            if (nalLength < 2 || position + 4L + nalLength > maxEnd)
                break;

            byte b0 = header[4];
            byte b1 = header[5];

            if (!h265)
            {
                if ((b0 & 0x80) != 0) break;
                int type = b0 & 0x1F;
                if (type is < 1 or > 12) break;
                if (type == 5) keyFrames++;
                if (type is 7 or 8) parameterSets++;
            }
            else
            {
                if ((b0 & 0x80) != 0 || (b1 & 0x07) == 0) break;
                int type = (b0 >> 1) & 0x3F;
                if (type > 40) break;
                if (type is 19 or 20 or 21) keyFrames++;
                if (type is 32 or 33 or 34) parameterSets++;
            }

            nalCount++;
            payloadBytes += 4L + nalLength;
            position += 4L + nalLength;
        }

        if (nalCount < 8 || payloadBytes < 256L * 1024 || keyFrames == 0)
            return null;

        string extension = h265 ? "H265" : "H264";
        string state = parameterSets > 0 ? "İyi" : "Video Akışı";
        RecoveryTransformKind transform = h265
            ? RecoveryTransformKind.LengthPrefixedH265ToAnnexB
            : RecoveryTransformKind.LengthPrefixedH264ToAnnexB;

        return new RawFileAnalysis(extension, payloadBytes, state, false, transform);
    }

    private static bool LooksLikeFourCc(string value)
    {
        if (value.Length != 4) return false;
        foreach (char ch in value)
        {
            if (ch < 0x20 || ch > 0x7E)
                return false;
        }
        return true;
    }

    private static RawFileAnalysis? AnalyzeFlv(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[9];
        if (!reader.ReadExact(start, header))
            return null;

        if (header[0] != (byte)'F' || header[1] != (byte)'L' || header[2] != (byte)'V' || header[3] != 0x01)
            return null;

        byte flags = header[4];
        if ((flags & 0x01) == 0)
            return null;

        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[5..9]);
        if (dataOffset < 9 || start + dataOffset + 15 > volumeLength)
            return null;

        long maxEnd = volumeLength;
        long position = start + dataOffset;
        long lastGoodEnd = position;
        int tagCount = 0;
        int videoTags = 0;
        Span<byte> tagPrefix = stackalloc byte[15];

        while (position + 15 <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!reader.ReadExact(position, tagPrefix))
                break;

            byte tagType = (byte)(tagPrefix[4] & 0x1F);
            if (tagType is not (8 or 9 or 18))
                break;

            int dataSize = (tagPrefix[5] << 16) | (tagPrefix[6] << 8) | tagPrefix[7];
            long tagLength = 4L + 11L + dataSize;
            if (dataSize < 0 || position + tagLength > maxEnd)
                break;

            if (tagType == 9)
                videoTags++;

            position += tagLength;
            lastGoodEnd = position;
            tagCount++;
        }

        if (videoTags == 0 || tagCount < 2)
            return null;

        if (lastGoodEnd + 4 <= maxEnd)
            lastGoodEnd += 4;

        long length = lastGoodEnd - start;
        return length > dataOffset ? new RawFileAnalysis("FLV", length, "Çok İyi") : null;
    }

    private static RawFileAnalysis? AnalyzeOggVideo(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long maxEnd = volumeLength;
        long position = start;
        long lastGoodEnd = start;
        var activeSerials = new HashSet<uint>();
        var endedSerials = new HashSet<uint>();
        uint? videoSerial = null;
        uint? audioSerial = null;
        string audioExtension = "OGG";
        int pageCount = 0;
        Span<byte> header = stackalloc byte[27];

        while (position + 27 <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!reader.ReadExact(position, header) ||
                header[0] != (byte)'O' || header[1] != (byte)'g' || header[2] != (byte)'g' || header[3] != (byte)'S' ||
                header[4] != 0)
                break;

            byte headerType = header[5];
            uint serial = BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
            int segmentCount = header[26];
            if (segmentCount <= 0 || position + 27 + segmentCount > maxEnd)
                break;

            byte[] lacing = reader.ReadBytes(position + 27, segmentCount);
            if (lacing.Length != segmentCount)
                break;

            int payloadSize = 0;
            foreach (byte value in lacing)
                payloadSize += value;

            long pageLength = 27L + segmentCount + payloadSize;
            if (pageLength <= 27 || position + pageLength > maxEnd)
                break;

            if ((headerType & 0x02) != 0)
                activeSerials.Add(serial);

            if (payloadSize > 0 && pageCount < 64)
            {
                int inspect = Math.Min(payloadSize, 64 * 1024);
                byte[] payload = reader.ReadBytes(position + 27 + segmentCount, inspect);
                if (payload.Length > 0)
                {
                    if (videoSerial is null && ContainsAscii(payload, "theora"))
                    {
                        videoSerial = serial;
                    }
                    else if (audioSerial is null &&
                             (StartsWithBytes(payload, "OpusHead"u8) ||
                              ContainsAscii(payload, "vorbis") ||
                              ContainsAscii(payload, "Speex   ")))
                    {
                        audioSerial = serial;
                        audioExtension = StartsWithBytes(payload, "OpusHead"u8) ? "OPUS" : "OGG";
                    }
                }
            }

            if ((headerType & 0x04) != 0)
                endedSerials.Add(serial);

            position += pageLength;
            lastGoodEnd = position;
            pageCount++;

            bool primaryEnded = videoSerial.HasValue
                ? endedSerials.Contains(videoSerial.Value)
                : audioSerial.HasValue && endedSerials.Contains(audioSerial.Value);
            if (primaryEnded && activeSerials.Count > 0 && activeSerials.All(endedSerials.Contains))
                break;
        }

        if (pageCount < 2)
            return null;

        string? extension = null;
        if (videoSerial.HasValue && endedSerials.Contains(videoSerial.Value))
            extension = "OGV";
        else if (audioSerial.HasValue && endedSerials.Contains(audioSerial.Value))
            extension = audioExtension;

        long length = lastGoodEnd - start;
        return extension is not null && length > 0
            ? new RawFileAnalysis(extension, length, "Çok İyi")
            : null;
    }

    private static RawFileAnalysis? AnalyzeRealMedia(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> first = stackalloc byte[10];
        if (!reader.ReadExact(start, first) ||
            first[0] != (byte)'.' || first[1] != (byte)'R' || first[2] != (byte)'M' || first[3] != (byte)'F')
            return null;

        long maxEnd = volumeLength;
        long position = start;
        long lastGoodEnd = start;
        bool hasVideo = false;
        bool hasData = false;
        int objectCount = 0;
        Span<byte> objectHeader = stackalloc byte[10];

        while (position + 10 <= maxEnd)
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            if (!reader.ReadExact(position, objectHeader))
                break;

            string objectId = Encoding.ASCII.GetString(objectHeader[..4]);
            uint objectSize = BinaryPrimitives.ReadUInt32BigEndian(objectHeader[4..8]);
            if (objectSize < 10 || position + objectSize > maxEnd)
                break;

            if (objectId is not (".RMF" or "PROP" or "CONT" or "MDPR" or "DATA" or "INDX"))
                break;

            if (objectId == "MDPR")
            {
                int inspect = (int)Math.Min(objectSize, 256L * 1024);
                byte[] data = reader.ReadBytes(position, inspect);
                if (ContainsAscii(data, "video/x-pn-realvideo") ||
                    ContainsAscii(data, "RV10") || ContainsAscii(data, "RV20") ||
                    ContainsAscii(data, "RV30") || ContainsAscii(data, "RV40"))
                    hasVideo = true;
            }
            else if (objectId == "DATA")
            {
                hasData = true;
            }

            position += objectSize;
            lastGoodEnd = position;
            objectCount++;

            if (objectId == "DATA" && hasVideo)
                break;
        }

        if (!hasVideo || !hasData || objectCount < 2)
            return null;

        long length = lastGoodEnd - start;
        return length > 0 ? new RawFileAnalysis("RM", length, "İyi") : null;
    }

    private static RawFileAnalysis? AnalyzeMatroskaCluster(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        Span<byte> cluster = stackalloc byte[4];
        if (!reader.ReadExact(start, cluster) ||
            cluster[0] != 0x1F || cluster[1] != 0x43 || cluster[2] != 0xB6 || cluster[3] != 0x75)
            return null;
        int inspect = (int)Math.Min(4L * 1024 * 1024, volumeLength - start);
        if (inspect < 16)
            return null;
        byte[] data = reader.ReadBytes(start, inspect);
        bool videoEvidence = ContainsBytes(data, new byte[] { 0x9D, 0x01, 0x2A }) ||
                             ContainsBytes(data, new byte[] { 0x00, 0x00, 0x01, 0x67 }) ||
                             ContainsBytes(data, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 }) ||
                             ContainsBytes(data, new byte[] { 0x00, 0x00, 0x01, 0x40 }) ||
                             ContainsBytes(data, new byte[] { 0x00, 0x00, 0x00, 0x01, 0x40 });
        if (!videoEvidence)
            return null;
        long lastGoodEnd = EstimateUnknownMatroskaEnd(reader, start, start, volumeLength, cancellationToken);
        if (lastGoodEnd <= start)
            return null;
        return new RawFileAnalysis("MKV", lastGoodEnd - start, "Yeniden İnşa");
    }

    private static RawFileAnalysis? AnalyzeMatroska(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        int inspectLength = (int)Math.Min(512L * 1024, volumeLength - start);
        if (inspectLength < 16) return null;

        byte[] data = reader.ReadBytes(start, inspectLength);
        if (data.Length < 16 ||
            data[0] != 0x1A || data[1] != 0x45 ||
            data[2] != 0xDF || data[3] != 0xA3)
            return null;

        if (!TryReadEbmlVInt(data.AsSpan(4), out ulong ebmlHeaderSize, out int ebmlSizeLength, out bool unknownHeaderSize) ||
            unknownHeaderSize)
            return null;

        long afterEbmlHeader = 4L + ebmlSizeLength + (long)ebmlHeaderSize;
        if (afterEbmlHeader < 8 || afterEbmlHeader >= data.Length)
            return null;

        int segmentIndex = FindBytes(data, (int)afterEbmlHeader, [0x18, 0x53, 0x80, 0x67]);
        if (segmentIndex < 0 || segmentIndex + 5 >= data.Length)
            return null;

        if (!TryReadEbmlVInt(
                data.AsSpan(segmentIndex + 4),
                out ulong segmentSize,
                out int segmentSizeLength,
                out bool unknownSegmentSize))
            return null;

        bool hasVideoTrack =
            ContainsAscii(data, "V_MPEG") ||
            ContainsAscii(data, "V_VP8") ||
            ContainsAscii(data, "V_VP9") ||
            ContainsAscii(data, "V_AV1") ||
            ContainsAscii(data, "V_THEORA") ||
            ContainsAscii(data, "V_REAL") ||
            ContainsAscii(data, "V_MS/VFW/FOURCC");

        if (!hasVideoTrack)
            return null;

        string extension = ContainsAscii(data, "webm") ? "WEBM" : "MKV";
        long segmentDataStart = start + segmentIndex + 4L + segmentSizeLength;

        if (!unknownSegmentSize)
        {
            if (segmentSize > (ulong)long.MaxValue)
                return null;

            long totalLength = segmentDataStart - start + (long)segmentSize;
            if (totalLength <= 0 || start + totalLength > volumeLength)
                return null;

            return new RawFileAnalysis(extension, totalLength, "Çok İyi");
        }

        long lastGoodEnd = EstimateUnknownMatroskaEnd(
            reader,
            start,
            segmentDataStart,
            volumeLength,
            cancellationToken);

        if (lastGoodEnd <= segmentDataStart)
            return null;

        return new RawFileAnalysis(extension, lastGoodEnd - start, "Yeniden İnşa");
    }

    private static long EstimateUnknownMatroskaEnd(
        RawDeviceReader reader,
        long fileStart,
        long segmentDataStart,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> clusterId = [0x1F, 0x43, 0xB6, 0x75];
        ReadOnlySpan<byte> ebmlId = [0x1A, 0x45, 0xDF, 0xA3];
        const int blockSize = 4 * 1024 * 1024;
        byte[] buffer = new byte[blockSize + 16];
        long position = segmentDataStart;
        long lastGoodEnd = segmentDataStart;
        int completeClusters = 0;

        while (position < volumeLength)
        {
            if (cancellationToken.IsCancellationRequested)
                return 0;

            int request = (int)Math.Min(blockSize, volumeLength - position);
            int read = reader.ReadBestEffort(position, buffer.AsSpan(0, request), out _);
            if (read <= 0)
                break;

            ReadOnlySpan<byte> span = buffer.AsSpan(0, read);
            int search = 0;
            while (search + 5 <= span.Length)
            {
                int rel = span[search..].IndexOf(clusterId);
                if (rel < 0)
                    break;

                int cluster = search + rel;
                if (cluster + 5 >= span.Length)
                    break;

                if (TryReadEbmlVInt(span[(cluster + 4)..], out ulong clusterSize, out int sizeLength, out bool unknownCluster) &&
                    !unknownCluster && clusterSize <= (ulong)long.MaxValue)
                {
                    long clusterEnd;
                    try
                    {
                        clusterEnd = checked(position + cluster + 4L + sizeLength + (long)clusterSize);
                    }
                    catch (OverflowException)
                    {
                        break;
                    }

                    if (clusterEnd > segmentDataStart && clusterEnd <= volumeLength)
                    {
                        lastGoodEnd = Math.Max(lastGoodEnd, clusterEnd);
                        completeClusters++;
                    }
                }

                search = cluster + 4;
            }

            int nextEbml = span.IndexOf(ebmlId);
            if (position > fileStart && nextEbml >= 0)
            {
                long nextFile = position + nextEbml;
                if (nextFile > segmentDataStart && completeClusters > 0)
                    return Math.Min(lastGoodEnd, nextFile);
            }

            if (read < request)
                break;

            position += Math.Max(1, read - 16);

            if (completeClusters > 0 && position - lastGoodEnd > 64L * 1024 * 1024)
                break;
        }

        return completeClusters > 0 ? lastGoodEnd : 0;
    }

    private static bool TryReadEbmlVInt(
        ReadOnlySpan<byte> data,
        out ulong value,
        out int length,
        out bool unknown)
    {
        value = 0;
        length = 0;
        unknown = false;

        if (data.Length == 0 || data[0] == 0)
            return false;

        byte mask = 0x80;
        int size = 1;
        while (size <= 8 && (data[0] & mask) == 0)
        {
            mask >>= 1;
            size++;
        }

        if (size > 8 || data.Length < size)
            return false;

        ulong result = (ulong)(data[0] & (mask - 1));
        for (int i = 1; i < size; i++)
            result = (result << 8) | data[i];

        ulong allOnes = size == 8
            ? 0x00FFFFFFFFFFFFFFUL
            : (1UL << (7 * size)) - 1UL;

        value = result;
        length = size;
        unknown = result == allOnes;
        return true;
    }

    private static int FindBytes(byte[] data, int start, ReadOnlySpan<byte> pattern)
    {
        int max = data.Length - pattern.Length;
        for (int i = Math.Max(0, start); i <= max; i++)
        {
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern))
                return i;
        }

        return -1;
    }

    private static RawFileAnalysis? AnalyzeFooterBased(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        long maxSize,
        string extension,
        byte[] footer,
        int trailingAllowance,
        CancellationToken cancellationToken)
    {
        long maxEnd = Math.Min(volumeLength, start + maxSize);
        const int blockSize = 1024 * 1024;
        int overlap = Math.Max(footer.Length - 1, 0);
        byte[] block = new byte[blockSize + overlap];
        long offset = start;
        int carry = 0;

        while (offset < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            int request = (int)Math.Min(blockSize, maxEnd - offset);
            int read = reader.ReadBestEffort(offset, block.AsSpan(carry, request), out _);
            if (read <= 0) break;

            int count = carry + read;
            int match = IndexOf(block.AsSpan(0, count), footer);
            if (match >= 0)
            {
                long end = offset - carry + match + footer.Length + trailingAllowance;
                end = Math.Min(end, volumeLength);
                return new RawFileAnalysis(extension, end - start, "İyi");
            }

            carry = Math.Min(overlap, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);

            offset += read;
        }

        return null;
    }

    private static RawFileAnalysis? AnalyzeZip(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        long maxEnd = Math.Min(volumeLength, start + MaxZipSize);
        ReadOnlySpan<byte> eocdSignature = [(byte)'P', (byte)'K', 0x05, 0x06];
        const int blockSize = 1024 * 1024;
        byte[] block = new byte[blockSize + 3];
        long offset = start;
        int carry = 0;

        while (offset < maxEnd)
        {
            if (cancellationToken.IsCancellationRequested) return null;

            int request = (int)Math.Min(blockSize, maxEnd - offset);
            int read = reader.ReadBestEffort(offset, block.AsSpan(carry, request), out _);
            if (read <= 0) break;

            int count = carry + read;
            int searchOffset = 0;
            while (searchOffset <= count - eocdSignature.Length)
            {
                int relative = IndexOf(block.AsSpan(searchOffset, count - searchOffset), eocdSignature);
                if (relative < 0)
                    break;

                int match = searchOffset + relative;
                long eocdOffset = offset - carry + match;
                if (TryValidateZipEnd(reader, start, eocdOffset, maxEnd, out long candidateEnd))
                {
                    long length = candidateEnd - start;
                    string extension = DetectZipExtension(reader, start, length);
                    return new RawFileAnalysis(extension, length, "Çok İyi");
                }

                searchOffset = match + 1;
            }

            carry = Math.Min(3, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);

            offset += read;
        }

        ZipReconstructionResult? reconstructed = ZipOfficeReconstructionService.TryReconstruct(
            reader,
            start,
            maxEnd,
            cancellationToken);
        if (reconstructed is null)
            return null;

        return new RawFileAnalysis(
            reconstructed.Extension,
            reconstructed.SourceLength,
            reconstructed.Partial ? "Kısmi" : "Yeniden İnşa",
            AppendData: reconstructed.CentralDirectorySuffix);
    }

    private static bool TryValidateZipEnd(
        RawDeviceReader reader,
        long start,
        long eocdOffset,
        long maxEnd,
        out long archiveEnd)
    {
        archiveEnd = 0;
        Span<byte> eocd = stackalloc byte[22];
        if (!reader.ReadExact(eocdOffset, eocd) ||
            eocd[0] != (byte)'P' || eocd[1] != (byte)'K' || eocd[2] != 0x05 || eocd[3] != 0x06)
            return false;

        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(4, 2));
        ushort centralDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(6, 2));
        ushort entriesOnDisk = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(8, 2));
        ushort totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(10, 2));
        uint centralSize32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(12, 4));
        uint centralOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd.Slice(16, 4));
        ushort commentLength = BinaryPrimitives.ReadUInt16LittleEndian(eocd.Slice(20, 2));

        long end = eocdOffset + 22L + commentLength;
        if (end > maxEnd)
            return false;

        bool zip64 = entriesOnDisk == ushort.MaxValue ||
                     totalEntries == ushort.MaxValue ||
                     centralSize32 == uint.MaxValue ||
                     centralOffset32 == uint.MaxValue;

        ulong centralSize;
        ulong centralOffset;
        ulong entries;

        if (zip64)
        {
            if (!TryReadZip64DirectoryInfo(
                    reader,
                    start,
                    eocdOffset,
                    out centralOffset,
                    out centralSize,
                    out entries))
                return false;
        }
        else
        {
            if (diskNumber != 0 || centralDisk != 0 || entriesOnDisk != totalEntries)
                return false;

            centralSize = centralSize32;
            centralOffset = centralOffset32;
            entries = totalEntries;
        }

        ulong maxRelative = (ulong)(maxEnd - start);
        if (centralOffset > maxRelative ||
            centralSize > maxRelative ||
            centralOffset > maxRelative - centralSize)
            return false;

        long centralStart = start + (long)centralOffset;
        long centralEnd = centralStart + (long)centralSize;
        if (centralStart < start || centralEnd > eocdOffset)
            return false;

        if (entries > 0)
        {
            Span<byte> centralHeader = stackalloc byte[4];
            if (!reader.ReadExact(centralStart, centralHeader) ||
                centralHeader[0] != (byte)'P' || centralHeader[1] != (byte)'K' ||
                centralHeader[2] != 0x01 || centralHeader[3] != 0x02)
                return false;
        }

        if (eocdOffset - centralEnd > 4L * 1024 * 1024)
            return false;

        archiveEnd = end;
        return true;
    }

    private static bool TryReadZip64DirectoryInfo(
        RawDeviceReader reader,
        long start,
        long eocdOffset,
        out ulong centralOffset,
        out ulong centralSize,
        out ulong entries)
    {
        centralOffset = 0;
        centralSize = 0;
        entries = 0;

        if (eocdOffset - start < 20)
            return false;

        Span<byte> locator = stackalloc byte[20];
        if (!reader.ReadExact(eocdOffset - 20, locator) ||
            locator[0] != (byte)'P' || locator[1] != (byte)'K' ||
            locator[2] != 0x06 || locator[3] != 0x07)
            return false;

        uint locatorDisk = BinaryPrimitives.ReadUInt32LittleEndian(locator.Slice(4, 4));
        ulong zip64RelativeOffset = BinaryPrimitives.ReadUInt64LittleEndian(locator.Slice(8, 8));
        uint diskCount = BinaryPrimitives.ReadUInt32LittleEndian(locator.Slice(16, 4));
        if (locatorDisk != 0 ||
            diskCount != 1 ||
            zip64RelativeOffset > (ulong)(eocdOffset - start))
            return false;

        long zip64Offset = start + (long)zip64RelativeOffset;
        if (zip64Offset < start || zip64Offset + 56 > eocdOffset)
            return false;

        Span<byte> record = stackalloc byte[56];
        if (!reader.ReadExact(zip64Offset, record) ||
            record[0] != (byte)'P' || record[1] != (byte)'K' ||
            record[2] != 0x06 || record[3] != 0x06)
            return false;

        ulong recordSize = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(4, 8));
        if (recordSize < 44 || recordSize > 16UL * 1024 * 1024)
            return false;

        uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4));
        uint centralDisk = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(20, 4));
        ulong entriesOnDisk = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(24, 8));
        entries = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(32, 8));
        centralSize = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(40, 8));
        centralOffset = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(48, 8));

        return diskNumber == 0 && centralDisk == 0 && entriesOnDisk == entries;
    }


    private static string DetectZipExtension(RawDeviceReader reader, long start, long length)
    {
        int inspectLength = (int)Math.Min(length, 4L * 1024 * 1024);
        if (inspectLength <= 0) return "ZIP";

        byte[] bytes = ReadBestEffortBytes(reader, start, inspectLength);

        if (ContainsAscii(bytes, "word/") || ContainsAscii(bytes, "word\\"))
            return "DOCX";

        if (ContainsAscii(bytes, "xl/") || ContainsAscii(bytes, "xl\\"))
            return "XLSX";

        if (ContainsAscii(bytes, "ppt/") || ContainsAscii(bytes, "ppt\\"))
            return "PPTX";

        if (ContainsAscii(bytes, "application/epub+zip") ||
            ContainsAscii(bytes, "META-INF/container.xml"))
            return "EPUB";

        if (ContainsAscii(bytes, "application/vnd.oasis.opendocument.text"))
            return "ODT";
        if (ContainsAscii(bytes, "application/vnd.oasis.opendocument.spreadsheet"))
            return "ODS";
        if (ContainsAscii(bytes, "application/vnd.oasis.opendocument.presentation"))
            return "ODP";

        if (ContainsAscii(bytes, "AndroidManifest.xml") && ContainsAscii(bytes, "classes.dex"))
            return "APK";

        if (ContainsAscii(bytes, "META-INF/MANIFEST.MF"))
            return "JAR";

        return "ZIP";
    }

    private static bool ContainsBytes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) =>
        IndexOf(data, pattern) >= 0;

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string text)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(text);
        return IndexOf(data, pattern) >= 0;
    }

    private static byte[] ReadBestEffortBytes(RawDeviceReader reader, long offset, int count)
    {
        if (count <= 0) return [];

        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(offset, data, out _);
        if (read == data.Length) return data;
        if (read <= 0) return [];

        Array.Resize(ref data, read);
        return data;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return -1;

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
    private static bool StartsWithBytes(ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature) =>
        data.Length >= signature.Length && data[..signature.Length].SequenceEqual(signature);

}
