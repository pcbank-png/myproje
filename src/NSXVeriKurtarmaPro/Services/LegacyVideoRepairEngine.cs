using System.Buffers.Binary;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Conservative reconstruction for legacy/global video containers that already have
/// raw-carving support. The engine never invents codec parameters that cannot be
/// derived from the surviving stream/container structure.
/// </summary>
internal static class LegacyVideoRepairEngine
{
    private const int CopyBufferSize = 1024 * 1024;
    private const long GenericResyncWindow = 16L * 1024 * 1024;
    private const long DvResyncWindow = 4L * 1024 * 1024;
    private const long NsvResyncWindow = 512L * 1024;

    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "WTV", "DV", "OGV", "RM", "RMVB", "NSV", "ROQ",
        "BIK", "BINK", "BK2", "BIK2", "SMK"
    };

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

    private static readonly HashSet<ushort> RoqChunkTypes =
    [
        0x1001, // INFO
        0x1002, // QUAD_CODEBOOK
        0x1011, // QUAD_VQ
        0x1020, // SOUND_MONO
        0x1021, // SOUND_STEREO
        0x1030  // PACKET
    ];

    private static readonly HashSet<string> RealMediaObjectIds = new(StringComparer.Ordinal)
    {
        ".RMF", "PROP", "CONT", "MDPR", "DATA", "INDX"
    };

    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    public static bool Supports(string extension) => Supported.Contains(extension);

    public static MediaRepairResult Repair(
        string extension,
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        try
        {
            return extension.ToUpperInvariant() switch
            {
                "WTV" => RepairWtv(sourcePath, destinationPath, progress),
                "DV" => RepairDv(sourcePath, destinationPath, progress),
                "OGV" => RepairOggVideo(sourcePath, destinationPath, progress),
                "RM" or "RMVB" => RepairRealMedia(sourcePath, destinationPath, progress),
                "NSV" => RepairNsv(sourcePath, destinationPath, progress),
                "ROQ" => RepairRoq(sourcePath, destinationPath, progress),
                "BIK" or "BINK" or "BK2" or "BIK2" => RepairBink(sourcePath, destinationPath, progress),
                "SMK" => RepairSmacker(sourcePath, destinationPath, progress),
                _ => new MediaRepairResult(false, "Bu video türü için gelişmiş onarım desteği bulunmuyor.")
            };
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, "Video yapısı yeniden oluşturulamadı: " + ex.Message);
        }
    }

    private static MediaRepairResult RepairWtv(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("WTV kayıt yapısı doğrulanıyor...");
        using var input = OpenRead(sourcePath);
        if (input.Length < 0x2000)
            throw new InvalidDataException("WTV yapısı için yeterli veri bulunamadı.");

        byte[] header = new byte[0x100];
        ReadExactly(input, 0, header);
        if (!header.AsSpan(0, 16).SequenceEqual(WtvRootGuid) ||
            !header.AsSpan(16, 16).SequenceEqual(WtvSubGuid))
            throw new InvalidDataException("WTV kök imzası doğrulanamadı.");

        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30, 4));
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38, 4));
        if (rootSize is 0 or > 0x1000 || rootSector == 0)
            throw new InvalidDataException("WTV kayıt dizini doğrulanamadı.");

        long rootOffset = checked((long)rootSector << 12);
        if (rootOffset + rootSize > input.Length)
            throw new InvalidDataException("WTV kayıt dizini dosya sınırlarıyla uyuşmuyor.");

        byte[] root = new byte[Math.Min((int)rootSize, 256)];
        ReadExactly(input, rootOffset, root);
        if (root.All(static value => value == 0))
            throw new InvalidDataException("WTV kayıt dizini okunamıyor.");

        // WTV file-system sectors are 4 KiB. Unknown/reserved header fields are never
        // rewritten here; only a physically incomplete tail sector may be discarded.
        long usableLength = input.Length - (input.Length % 0x1000L);
        if (usableLength < rootOffset + rootSize)
            throw new InvalidDataException("WTV kayıt yapısındaki medya verisi eksik.");

        progress?.Invoke("WTV medya yapısı yeniden oluşturuluyor...");
        EnsureParentDirectory(destinationPath);
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            CopyRange(input, output, 0, usableLength);
            output.Flush(true);
        }

        ValidateWtvOutput(destinationPath);
        return new MediaRepairResult(true, "WTV kayıt yapısı doğrulandı; okunabilir veri sınırı yeniden oluşturuldu.", usableLength);
    }

    private static MediaRepairResult RepairDv(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("DV video kareleri analiz ediliyor...");
        using var input = OpenRead(sourcePath);
        long first = FindNextDvFrame(input, 0, Math.Min(input.Length, GenericResyncWindow), null, null);
        if (first < 0 || !TryReadDvSequenceStart(input, first, out bool pal, out byte channelMarker))
            throw new InvalidDataException("Geçerli DV video başlangıcı bulunamadı.");

        int baseFrameSize = pal ? 144000 : 120000;
        int frameSize = DetermineDvFrameSize(input, first, baseFrameSize, channelMarker);
        if (frameSize <= 0)
            throw new InvalidDataException("DV frame boyutu belirlenemedi.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        long position = first;
        long skipped = first;
        int frames = 0;
        byte[] frame = new byte[frameSize];

        while (position + frameSize <= input.Length)
        {
            if (TryReadDvSequenceStart(input, position, out bool currentPal, out byte currentChannel) &&
                currentPal == pal && currentChannel == channelMarker &&
                ValidateDvFrameGeometry(input, position, frameSize, pal))
            {
                ReadExactly(input, position, frame);
                output.Write(frame, 0, frame.Length);
                frames++;
                position += frameSize;
                continue;
            }

            long next = FindNextDvFrame(
                input,
                position + 1,
                Math.Min(input.Length, position + DvResyncWindow),
                pal,
                channelMarker);
            if (next < 0)
                break;

            skipped += next - position;
            position = next;
        }

        if (frames == 0)
            throw new InvalidDataException("Doğrulanabilir DV frame bulunamadı.");

        output.Flush(true);
        string skippedText = skipped > 0 ? $" {skipped:N0} baytlık hasarlı bölüm güvenli biçimde atlandı." : string.Empty;
        return new MediaRepairResult(true, $"DV video akışı yeniden hizalandı; {frames:N0} kare korundu.{skippedText}", output.Length);
    }

    private static MediaRepairResult RepairOggVideo(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("Ogg/Theora video yapısı analiz ediliyor...");
        using var input = OpenRead(sourcePath);

        HashSet<uint> recognizedSerials = [];
        uint? videoSerial = null;
        DiscoverOggStreams(input, recognizedSerials, ref videoSerial);
        if (!videoSerial.HasValue)
            throw new InvalidDataException("Theora video akışı tanımlanamadı.");

        progress?.Invoke("Ogg/Theora akış yapısı yeniden oluşturuluyor...");
        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        var sequenceBySerial = new Dictionary<uint, uint>();
        var firstSeen = new HashSet<uint>();
        var lastPageOffsetBySerial = new Dictionary<uint, long>();
        long position = 0;
        long rejected = 0;
        int pages = 0;
        int videoPages = 0;

        while (position + 27 <= input.Length)
        {
            if (!TryReadOggPage(input, position, out OggPage page))
            {
                long next = FindNextAscii(input, "OggS"u8, position + 1, Math.Min(input.Length, position + GenericResyncWindow));
                if (next < 0)
                    break;
                rejected += next - position;
                position = next;
                continue;
            }

            if (!recognizedSerials.Contains(page.Serial))
            {
                position += page.TotalLength;
                continue;
            }

            byte[] bytes = new byte[page.TotalLength];
            ReadExactly(input, position, bytes);
            uint sequence = sequenceBySerial.TryGetValue(page.Serial, out uint current) ? current : 0;
            sequenceBySerial[page.Serial] = sequence + 1;

            byte flags = bytes[5];
            flags = (byte)(flags & ~0x06); // BOS/EOS yeniden kurulacak.
            if (firstSeen.Add(page.Serial))
                flags |= 0x02;
            bytes[5] = flags;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18, 4), sequence);
            WriteOggCrc(bytes);

            long outputOffset = output.Position;
            output.Write(bytes, 0, bytes.Length);
            lastPageOffsetBySerial[page.Serial] = outputOffset;
            pages++;
            if (page.Serial == videoSerial.Value)
                videoPages++;
            position += page.TotalLength;
        }

        if (videoPages < 2)
            throw new InvalidDataException("Yeterli Theora video sayfası yeniden oluşturulamadı.");

        foreach ((uint serial, long pageOffset) in lastPageOffsetBySerial)
            PatchOggEndOfStream(output, pageOffset, serial);

        output.Flush(true);
        string rejectText = rejected > 0 ? $" {rejected:N0} baytlık hasarlı bölüm güvenli biçimde atlandı." : string.Empty;
        return new MediaRepairResult(true, $"Ogg/Theora video yapısı yeniden oluşturuldu; {pages:N0} veri sayfası doğrulandı.{rejectText}", output.Length);
    }

    private static MediaRepairResult RepairRealMedia(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("RealMedia video yapısı analiz ediliyor...");
        using var input = OpenRead(sourcePath);

        var mdprObjects = new List<byte[]>();
        var contentObjects = new List<byte[]>();
        var streamIds = new HashSet<ushort>();
        bool videoDescriptor = false;
        long dataOffset = -1;
        long position = 0;
        long rejected = 0;

        while (position + 10 <= input.Length)
        {
            if (!TryReadRmObject(input, position, allowTruncatedData: true, out RmObject obj))
            {
                long next = FindNextRmObject(input, position + 1, Math.Min(input.Length, position + GenericResyncWindow));
                if (next < 0)
                    break;
                rejected += next - position;
                position = next;
                continue;
            }

            if (obj.Id == "MDPR")
            {
                if (obj.Size > int.MaxValue)
                    throw new InvalidDataException("RealMedia MDPR nesnesi aşırı büyük.");
                byte[] bytes = new byte[(int)obj.Size];
                ReadExactly(input, position, bytes);
                if (bytes.Length >= 12)
                    streamIds.Add(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10, 2)));
                videoDescriptor |= ContainsAscii(bytes, "video/x-pn-realvideo") ||
                                   ContainsAscii(bytes, "RV10") || ContainsAscii(bytes, "RV20") ||
                                   ContainsAscii(bytes, "RV30") || ContainsAscii(bytes, "RV40");
                mdprObjects.Add(bytes);
            }
            else if (obj.Id == "CONT" && obj.Size <= 1024 * 1024)
            {
                byte[] bytes = new byte[(int)obj.Size];
                ReadExactly(input, position, bytes);
                contentObjects.Add(bytes);
            }
            else if (obj.Id == "DATA")
            {
                dataOffset = position;
                break;
            }

            position += obj.Size;
        }

        if (!videoDescriptor || mdprObjects.Count == 0 || dataOffset < 0)
            throw new InvalidDataException("RealMedia video MDPR/DATA yapısı birlikte doğrulanamadı.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        WriteSyntheticRmfHeader(output, checked((uint)(contentObjects.Count + mdprObjects.Count + 1)));
        foreach (byte[] cont in contentObjects)
            output.Write(cont, 0, cont.Length);
        foreach (byte[] mdpr in mdprObjects)
            output.Write(mdpr, 0, mdpr.Length);

        progress?.Invoke("RealMedia medya akışı yeniden oluşturuluyor...");
        long dataBytes = WriteRepairedRmDataObject(input, dataOffset, output, streamIds, out int packetCount, out long dataRejected);
        rejected += dataRejected;
        if (packetCount == 0)
            throw new InvalidDataException("Geçerli RealMedia DATA paketi bulunamadı.");

        output.Flush(true);
        string rejectedText = rejected > 0 ? $" {rejected:N0} baytlık hasarlı bölüm güvenli biçimde atlandı." : string.Empty;
        return new MediaRepairResult(true, $"RealMedia video yapısı yeniden oluşturuldu; {packetCount:N0} veri paketi korundu.{rejectedText}", output.Length);
    }

    private static MediaRepairResult RepairNsv(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("NSV ses ve video akışı analiz ediliyor...");
        using var input = OpenRead(sourcePath);
        long firstSync = FindNextNsvSync(input, 0, Math.Min(input.Length, GenericResyncWindow), requireStreamHeader: true);
        if (firstSync < 0 || !TryReadNsvUnit(input, firstSync, out NsvUnit firstUnit) || !firstUnit.HasStreamHeader)
            throw new InvalidDataException("NSV video başlangıç bilgisi doğrulanamadı.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        WriteSyntheticNsvFileHeader(output);

        long position = firstSync;
        long rejected = firstSync;
        int units = 0;
        int streamHeaders = 0;
        byte frameRateCode = firstUnit.FrameRateCode;

        while (position + 7 <= input.Length)
        {
            if (!TryReadNsvUnit(input, position, out NsvUnit unit))
            {
                long next = FindNextNsvSync(input, position + 1, Math.Min(input.Length, position + NsvResyncWindow), requireStreamHeader: false);
                if (next < 0)
                    break;
                rejected += next - position;
                position = next;
                continue;
            }

            CopyRange(input, output, position, unit.TotalLength);
            units++;
            if (unit.HasStreamHeader)
                streamHeaders++;
            position += unit.TotalLength;
        }

        if (units < 2 || streamHeaders == 0)
            throw new InvalidDataException("Yeterli NSV medya bloğu bulunamadı.");

        uint durationMs = EstimateNsvDurationMilliseconds(units, frameRateCode);
        output.Position = 8;
        Span<byte> patch = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(patch[..4], checked((uint)Math.Min(uint.MaxValue, output.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(patch[4..8], durationMs);
        output.Write(patch);
        output.Flush(true);

        string rejectedText = rejected > 0 ? $" {rejected:N0} baytlık hasarlı bölüm güvenli biçimde atlandı." : string.Empty;
        return new MediaRepairResult(true, $"NSV medya yapısı yeniden oluşturuldu; {units:N0} veri bloğu korundu.{rejectedText}", output.Length);
    }

    private static MediaRepairResult RepairRoq(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("RoQ medya yapısı analiz ediliyor...");
        using var input = OpenRead(sourcePath);
        long headerOffset = FindRoqHeader(input, 0, Math.Min(input.Length, GenericResyncWindow));
        if (headerOffset < 0)
            throw new InvalidDataException("RoQ zamanlama bilgisi güvenilir biçimde doğrulanamadı.");

        byte[] header = new byte[8];
        ReadExactly(input, headerOffset, header);
        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        output.Write(header, 0, header.Length);

        long position = headerOffset + 8;
        long rejected = headerOffset;
        int chunks = 0;
        int videoChunks = 0;
        bool info = false;

        while (position + 8 <= input.Length)
        {
            if (!TryReadRoqChunk(input, position, out RoqChunk chunk))
            {
                long next = FindNextRoqChunk(input, position + 1, Math.Min(input.Length, position + GenericResyncWindow));
                if (next < 0)
                    break;
                rejected += next - position;
                position = next;
                continue;
            }

            if (chunk.Type == 0x1001)
            {
                if (chunk.Size < 8)
                {
                    position++;
                    continue;
                }
                byte[] infoBytes = new byte[8];
                ReadExactly(input, position + 8, infoBytes);
                ushort width = BinaryPrimitives.ReadUInt16LittleEndian(infoBytes.AsSpan(0, 2));
                ushort height = BinaryPrimitives.ReadUInt16LittleEndian(infoBytes.AsSpan(2, 2));
                if (width == 0 || height == 0 || width > 16384 || height > 16384)
                {
                    position++;
                    continue;
                }
                info = true;
            }
            if (chunk.Type is 0x1002 or 0x1011)
                videoChunks++;

            CopyRange(input, output, position, chunk.TotalLength);
            chunks++;
            position += chunk.TotalLength;
        }

        if (!info || videoChunks == 0)
            throw new InvalidDataException("RoQ görüntü bilgisi ve video verisi birlikte doğrulanamadı.");

        output.Flush(true);
        string rejectedText = rejected > 0 ? $" {rejected:N0} baytlık hasarlı bölüm güvenli biçimde atlandı." : string.Empty;
        return new MediaRepairResult(true, $"RoQ medya akışı yeniden hizalandı; {chunks:N0} veri bloğu korundu.{rejectedText}", output.Length);
    }

    private static MediaRepairResult RepairBink(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("Bink video yapısı ve kare sınırları doğrulanıyor...");
        using var input = OpenRead(sourcePath);
        if (!TryParseBinkHeader(input, out BinkHeader bink))
            throw new InvalidDataException("Bink/Bink2 video yapısı ve kare tablosu doğrulanamadı.");

        long usableEnd = Math.Min(input.Length, bink.DeclaredFileSize);
        if (usableEnd <= bink.FirstFrameOffset)
            throw new InvalidDataException("Bink video kare verisi eksik.");

        uint largest = 0;
        for (int i = 0; i < bink.FrameOffsets.Count; i++)
        {
            long start = bink.FrameOffsets[i] & ~1L;
            long end = i + 1 < bink.FrameOffsets.Count
                ? bink.FrameOffsets[i + 1] & ~1L
                : usableEnd;
            if (start < bink.FirstFrameOffset || end <= start || end > usableEnd)
                throw new InvalidDataException("Bink kare tablosundaki veri sırası doğrulanamadı.");
            largest = Math.Max(largest, checked((uint)Math.Min(uint.MaxValue, end - start)));
        }

        EnsureParentDirectory(destinationPath);
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            CopyRange(input, output, 0, usableEnd);
            output.Position = 4;
            Span<byte> patch = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(patch[..4], checked((uint)(usableEnd - 8)));
            BinaryPrimitives.WriteUInt32LittleEndian(patch[4..8], bink.FrameCount);
            BinaryPrimitives.WriteUInt32LittleEndian(patch[8..12], largest);
            output.Write(patch);
            output.Flush(true);
        }

        return new MediaRepairResult(true, $"Bink video yapısı doğrulandı; {bink.FrameCount:N0} kare için dosya sınırları yeniden düzenlendi.", usableEnd);
    }

    private static MediaRepairResult RepairSmacker(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("Smacker video yapısı ve kare sınırları doğrulanıyor...");
        using var input = OpenRead(sourcePath);
        if (!TryComputeSmackerLength(input, out long totalLength, out uint frames))
            throw new InvalidDataException("Smacker video yapısı ve kare tablosu doğrulanamadı.");

        EnsureParentDirectory(destinationPath);
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            CopyRange(input, output, 0, totalLength);
            output.Flush(true);
        }

        return new MediaRepairResult(true, $"Smacker video yapısı doğrulandı; {frames:N0} kare için okunabilir dosya sınırı yeniden oluşturuldu.", totalLength);
    }

    // --------------------------- WTV ---------------------------

    private static void ValidateWtvOutput(string path)
    {
        using var input = OpenRead(path);
        byte[] header = new byte[0x100];
        ReadExactly(input, 0, header);
        if (!header.AsSpan(0, 16).SequenceEqual(WtvRootGuid) || !header.AsSpan(16, 16).SequenceEqual(WtvSubGuid))
            throw new InvalidDataException("Onarılan WTV imzası doğrulanamadı.");
        uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30, 4));
        uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x38, 4));
        long rootOffset = checked((long)rootSector << 12);
        if (rootSize is 0 or > 0x1000 || rootSector == 0 || rootOffset + rootSize > input.Length || input.Length % 0x1000L != 0)
            throw new InvalidDataException("Onarılan WTV kayıt yapısı doğrulanamadı.");
    }

    // --------------------------- DV ---------------------------

    private static bool TryReadDvSequenceStart(Stream input, long offset, out bool pal, out byte channelMarker)
    {
        pal = false;
        channelMarker = 0;
        if (offset < 0 || offset + 6L * 80 > input.Length)
            return false;
        byte[] data = new byte[6 * 80];
        ReadExactly(input, offset, data);
        if (!GlobalVideoRawAnalyzer.LooksLikeDvStart(data))
            return false;
        pal = (data[3] & 0x80) != 0;
        channelMarker = (byte)(data[1] & 0x08);
        return (data[1] >> 4) == 0;
    }

    private static int DetermineDvFrameSize(Stream input, long start, int baseFrameSize, byte channelMarker)
    {
        foreach (int candidate in new[] { baseFrameSize, baseFrameSize * 2, baseFrameSize * 4 })
        {
            long next = start + candidate;
            if (next + 6L * 80 > input.Length)
                continue;
            if (TryReadDvSequenceStart(input, next, out _, out byte nextChannel) && nextChannel == channelMarker)
                return candidate;
        }
        return baseFrameSize;
    }

    private static bool ValidateDvFrameGeometry(Stream input, long frameStart, int frameSize, bool pal)
    {
        int sequencesPerChannel = pal ? 12 : 10;
        int sequenceCount = frameSize / 12000;
        if (sequenceCount < sequencesPerChannel || frameSize % 12000 != 0)
            return false;
        byte[] id = new byte[3];
        for (int sequence = 0; sequence < sequenceCount; sequence++)
        {
            long offset = frameStart + sequence * 12000L;
            if (offset + 3 > input.Length)
                return false;
            ReadExactly(input, offset, id);
            if (id[0] != 0x1F || (id[1] >> 4) != sequence % sequencesPerChannel)
                return false;
        }
        return true;
    }

    private static long FindNextDvFrame(Stream input, long start, long end, bool? pal, byte? channel)
    {
        long position = Math.Max(0, start);
        end = Math.Min(end, input.Length);
        byte[] buffer = new byte[256 * 1024];
        while (position < end)
        {
            int request = (int)Math.Min(buffer.Length, end - position);
            input.Position = position;
            int read = input.Read(buffer, 0, request);
            if (read <= 0)
                break;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != 0x1F)
                    continue;
                long candidate = position + i;
                if (candidate + 6L * 80 > input.Length)
                    return -1;
                if (!TryReadDvSequenceStart(input, candidate, out bool candidatePal, out byte candidateChannel))
                    continue;
                if (pal.HasValue && candidatePal != pal.Value)
                    continue;
                if (channel.HasValue && candidateChannel != channel.Value)
                    continue;
                return candidate;
            }
            position += read;
        }
        return -1;
    }

    // --------------------------- OGG / OGV ---------------------------

    private static void DiscoverOggStreams(Stream input, HashSet<uint> recognizedSerials, ref uint? videoSerial)
    {
        long position = 0;
        int pages = 0;
        while (position + 27 <= input.Length && pages < 4096)
        {
            if (!TryReadOggPage(input, position, out OggPage page))
            {
                long next = FindNextAscii(input, "OggS"u8, position + 1, Math.Min(input.Length, position + GenericResyncWindow));
                if (next < 0)
                    break;
                position = next;
                continue;
            }

            int inspectLength = Math.Min(page.PayloadLength, 64 * 1024);
            if (inspectLength > 0)
            {
                byte[] payload = new byte[inspectLength];
                ReadExactly(input, position + page.HeaderLength, payload);
                bool theora = payload.Length >= 7 && payload[0] == 0x80 && Encoding.ASCII.GetString(payload, 1, 6) == "theora";
                bool vorbis = payload.Length >= 7 && payload[0] == 0x01 && Encoding.ASCII.GetString(payload, 1, 6) == "vorbis";
                bool opus = StartsWithAscii(payload, "OpusHead");
                bool speex = StartsWithAscii(payload, "Speex   ");
                if (theora)
                {
                    recognizedSerials.Add(page.Serial);
                    videoSerial ??= page.Serial;
                }
                else if (vorbis || opus || speex)
                {
                    recognizedSerials.Add(page.Serial);
                }
            }

            position += page.TotalLength;
            pages++;
            if (videoSerial.HasValue && recognizedSerials.Count >= 2 && pages > 128)
                break;
        }
    }

    private static bool TryReadOggPage(Stream input, long offset, out OggPage page)
    {
        page = default;
        if (offset < 0 || offset + 27 > input.Length)
            return false;
        byte[] header = new byte[27];
        ReadExactly(input, offset, header);
        if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8) || header[4] != 0)
            return false;
        int segmentCount = header[26];
        if (offset + 27L + segmentCount > input.Length)
            return false;
        byte[] lacing = new byte[segmentCount];
        if (segmentCount > 0)
            ReadExactly(input, offset + 27, lacing);
        int payload = 0;
        foreach (byte value in lacing)
            payload += value;
        int total = checked(27 + segmentCount + payload);
        if (total < 27 || offset + total > input.Length)
            return false;
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14, 4));
        page = new OggPage(total, 27 + segmentCount, payload, serial);
        return true;
    }

    private static void WriteOggCrc(byte[] page)
    {
        page[22] = page[23] = page[24] = page[25] = 0;
        uint crc = 0;
        foreach (byte value in page)
            crc = (crc << 8) ^ OggCrcTable[((crc >> 24) ^ value) & 0xFF];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22, 4), crc);
    }

    private static uint[] BuildOggCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 0x80000000U) != 0 ? (value << 1) ^ 0x04C11DB7U : value << 1;
            table[i] = value;
        }
        return table;
    }

    private static void PatchOggEndOfStream(FileStream output, long pageOffset, uint expectedSerial)
    {
        if (!TryReadOggPage(output, pageOffset, out OggPage page) || page.Serial != expectedSerial)
            throw new InvalidDataException("Ogg EOS patch sayfası doğrulanamadı.");
        byte[] bytes = new byte[page.TotalLength];
        ReadExactly(output, pageOffset, bytes);
        bytes[5] |= 0x04;
        WriteOggCrc(bytes);
        output.Position = pageOffset;
        output.Write(bytes, 0, bytes.Length);
    }

    // --------------------------- REALMEDIA ---------------------------

    private static bool TryReadRmObject(Stream input, long offset, bool allowTruncatedData, out RmObject obj)
    {
        obj = default;
        if (offset < 0 || offset + 10 > input.Length)
            return false;
        byte[] header = new byte[10];
        ReadExactly(input, offset, header);
        string id = Encoding.ASCII.GetString(header, 0, 4);
        if (!RealMediaObjectIds.Contains(id))
            return false;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8, 2));
        if (size < 10 || (version != 0 && version != 2))
            return false;
        if (offset + size > input.Length && !(allowTruncatedData && id == "DATA"))
            return false;
        obj = new RmObject(id, size, version);
        return true;
    }

    private static long FindNextRmObject(Stream input, long start, long end)
    {
        string[] tags = ["PROP", "CONT", "MDPR", "DATA", "INDX"];
        long best = -1;
        foreach (string tag in tags)
        {
            long found = FindNextAscii(input, Encoding.ASCII.GetBytes(tag), start, end);
            if (found >= 0 && (best < 0 || found < best) && TryReadRmObject(input, found, true, out _))
                best = found;
        }
        return best;
    }

    private static void WriteSyntheticRmfHeader(Stream output, uint headerCount)
    {
        Span<byte> rmf = stackalloc byte[18];
        ".RMF"u8.CopyTo(rmf);
        BinaryPrimitives.WriteUInt32BigEndian(rmf[4..8], 18);
        BinaryPrimitives.WriteUInt16BigEndian(rmf[8..10], 0);
        BinaryPrimitives.WriteUInt32BigEndian(rmf[10..14], 0);
        BinaryPrimitives.WriteUInt32BigEndian(rmf[14..18], headerCount);
        output.Write(rmf);
    }

    private static long WriteRepairedRmDataObject(
        Stream input,
        long dataOffset,
        FileStream output,
        HashSet<ushort> streamIds,
        out int packetCount,
        out long rejectedBytes)
    {
        packetCount = 0;
        rejectedBytes = 0;
        if (!TryReadRmObject(input, dataOffset, true, out RmObject data) || data.Id != "DATA")
            throw new InvalidDataException("RealMedia DATA nesnesi doğrulanamadı.");
        int dataHeaderLength = data.Version == 2 ? 30 : 18;
        if (dataOffset + dataHeaderLength > input.Length)
            throw new InvalidDataException("RealMedia veri bölümü eksik.");

        long objectStart = output.Position;
        byte[] dataHeader = new byte[dataHeaderLength];
        ReadExactly(input, dataOffset, dataHeader);
        output.Write(dataHeader, 0, dataHeader.Length);
        long sourcePosition = dataOffset + dataHeaderLength;
        long declaredEnd = Math.Min(input.Length, dataOffset + data.Size);

        while (sourcePosition + 12 <= declaredEnd)
        {
            if (!TryReadRmPacket(input, sourcePosition, declaredEnd, streamIds, out int packetLength))
            {
                long next = FindNextRmPacket(input, sourcePosition + 1, Math.Min(declaredEnd, sourcePosition + 1024 * 1024), streamIds);
                if (next < 0)
                    break;
                rejectedBytes += next - sourcePosition;
                sourcePosition = next;
                continue;
            }
            CopyRange(input, output, sourcePosition, packetLength);
            packetCount++;
            sourcePosition += packetLength;
        }

        long objectSize = output.Position - objectStart;
        output.Position = objectStart + 4;
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(sizeBytes, checked((uint)objectSize));
        output.Write(sizeBytes);
        output.Position = objectStart + 10;
        Span<byte> countBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(countBytes, checked((uint)packetCount));
        output.Write(countBytes);
        int nextHeaderOffset = data.Version == 2 ? 26 : 14;
        output.Position = objectStart + nextHeaderOffset;
        Span<byte> zero = stackalloc byte[4];
        output.Write(zero);
        output.Position = objectStart + objectSize;
        return objectSize;
    }

    private static bool TryReadRmPacket(Stream input, long offset, long end, HashSet<ushort> streamIds, out int length)
    {
        length = 0;
        if (offset + 12 > end)
            return false;
        byte[] header = new byte[12];
        ReadExactly(input, offset, header);
        ushort version = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
        ushort packetLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        ushort streamId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
        if (version > 1 || packetLength < 12 || offset + packetLength > end)
            return false;
        if (streamIds.Count > 0 && !streamIds.Contains(streamId))
            return false;
        length = packetLength;
        return true;
    }

    private static long FindNextRmPacket(Stream input, long start, long end, HashSet<ushort> streamIds)
    {
        for (long position = start; position + 12 <= end; position++)
        {
            if (TryReadRmPacket(input, position, end, streamIds, out _))
                return position;
        }
        return -1;
    }

    // --------------------------- NSV ---------------------------

    private static void WriteSyntheticNsvFileHeader(Stream output)
    {
        Span<byte> header = stackalloc byte[28];
        "NSVf"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 28);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], 0); // final file size patched later
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..16], 0); // duration patched later
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 0); // info strings
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..24], 0); // table entries
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], 0); // used entries
        output.Write(header);
    }

    private static bool TryReadNsvUnit(Stream input, long offset, out NsvUnit unit)
    {
        unit = default;
        if (offset < 0 || offset + 7 > input.Length)
            return false;

        byte[] tag = new byte[4];
        int markerLength;
        bool streamHeader;
        byte frameRate = 0;
        if (offset + 4 <= input.Length)
        {
            ReadExactly(input, offset, tag);
            streamHeader = tag.AsSpan().SequenceEqual("NSVs"u8);
        }
        else
        {
            streamHeader = false;
        }

        if (streamHeader)
        {
            if (offset + 24 > input.Length)
                return false;
            byte[] stream = new byte[19];
            ReadExactly(input, offset, stream);
            uint vtag = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(4, 4));
            ushort width = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(12, 2));
            ushort height = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(14, 2));
            frameRate = stream[16];
            if (vtag == 0 || width == 0 || height == 0 || width > 16384 || height > 16384 || frameRate == 0)
                return false;
            markerLength = 19;
        }
        else
        {
            byte[] beef = new byte[2];
            ReadExactly(input, offset, beef);
            if (beef[0] != 0xEF || beef[1] != 0xBE)
                return false;
            markerLength = 2;
        }

        byte[] av = new byte[5];
        ReadExactly(input, offset + markerLength, av);
        uint videoSize = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(av.AsSpan(1, 2)) << 4) | (uint)(av[0] >> 4);
        ushort audioSize = BinaryPrimitives.ReadUInt16LittleEndian(av.AsSpan(3, 2));
        int auxCount = av[0] & 0x0F;
        long total = markerLength + 5L + videoSize + audioSize;
        if (auxCount > 15 || (videoSize == 0 && audioSize == 0) || total <= markerLength + 5 || offset + total > input.Length)
            return false;

        unit = new NsvUnit(total, streamHeader, frameRate);
        return true;
    }

    private static long FindNextNsvSync(Stream input, long start, long end, bool requireStreamHeader)
    {
        long nsvs = FindNextAscii(input, "NSVs"u8, start, end);
        if (requireStreamHeader)
            return nsvs >= 0 && TryReadNsvUnit(input, nsvs, out NsvUnit header) && header.HasStreamHeader ? nsvs : -1;

        long beef = FindNextBytes(input, [0xEF, 0xBE], start, end);
        long candidate = nsvs < 0 ? beef : beef < 0 ? nsvs : Math.Min(nsvs, beef);
        while (candidate >= 0)
        {
            if (TryReadNsvUnit(input, candidate, out _))
                return candidate;
            long nextStart = candidate + 1;
            nsvs = FindNextAscii(input, "NSVs"u8, nextStart, end);
            beef = FindNextBytes(input, [0xEF, 0xBE], nextStart, end);
            candidate = nsvs < 0 ? beef : beef < 0 ? nsvs : Math.Min(nsvs, beef);
        }
        return -1;
    }

    private static uint EstimateNsvDurationMilliseconds(int units, byte code)
    {
        double fps;
        if ((code & 0x80) == 0)
        {
            fps = code;
        }
        else
        {
            int t = (code & 0x7F) >> 2;
            double num = t < 16 ? 1.0 : t - 15.0;
            double den = t < 16 ? t + 1.0 : 1.0;
            if ((code & 1) != 0)
            {
                num *= 1000.0;
                den *= 1001.0;
            }
            num *= (code & 3) switch { 3 => 24.0, 2 => 25.0, _ => 30.0 };
            fps = num / den;
        }
        if (fps <= 0.1 || double.IsNaN(fps) || double.IsInfinity(fps))
            return 0;
        double ms = units * 1000.0 / fps;
        return (uint)Math.Clamp(Math.Round(ms), 0, uint.MaxValue);
    }

    // --------------------------- ROQ ---------------------------

    private static long FindRoqHeader(Stream input, long start, long end)
    {
        byte[] signature = [0x84, 0x10, 0xFF, 0xFF, 0xFF, 0xFF];
        long position = FindNextBytes(input, signature, start, end);
        while (position >= 0)
        {
            if (position + 8 <= input.Length)
            {
                byte[] header = new byte[8];
                ReadExactly(input, position, header);
                ushort fps = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
                if (fps > 0)
                    return position;
            }
            position = FindNextBytes(input, signature, position + 1, end);
        }
        return -1;
    }

    private static bool TryReadRoqChunk(Stream input, long offset, out RoqChunk chunk)
    {
        chunk = default;
        if (offset < 0 || offset + 8 > input.Length)
            return false;
        byte[] header = new byte[8];
        ReadExactly(input, offset, header);
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0, 2));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(2, 4));
        if (!RoqChunkTypes.Contains(type) || size > 256 * 1024 * 1024U)
            return false;
        long total = 8L + size;
        if (offset + total > input.Length)
            return false;
        chunk = new RoqChunk(type, size, total);
        return true;
    }

    private static long FindNextRoqChunk(Stream input, long start, long end)
    {
        for (long position = start; position + 8 <= end; position++)
        {
            if (TryReadRoqChunk(input, position, out _))
                return position;
        }
        return -1;
    }

    // --------------------------- BINK ---------------------------

    private static bool TryParseBinkHeader(Stream input, out BinkHeader header)
    {
        header = default!;
        if (input.Length < 52)
            return false;
        byte[] first = new byte[44];
        ReadExactly(input, 0, first);
        bool bink1 = first[0] == (byte)'B' && first[1] == (byte)'I' && first[2] == (byte)'K';
        bool bink2 = first[0] == (byte)'K' && first[1] == (byte)'B' && first[2] == (byte)'2';
        if (!bink1 && !bink2)
            return false;
        byte revision = first[3];
        ulong declared64 = (ulong)BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(4, 4)) + 8UL;
        if (declared64 > uint.MaxValue)
            return false;
        uint declared = (uint)declared64;
        uint frames = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(8, 4));
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(20, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(24, 4));
        uint fpsNum = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(28, 4));
        uint fpsDen = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(32, 4));
        uint audioTracks = BinaryPrimitives.ReadUInt32LittleEndian(first.AsSpan(40, 4));
        if (declared < 52 || frames is 0 or > 1_000_000 || width is 0 or > 7680 || height is 0 or > 4800 || fpsNum == 0 || fpsDen == 0 || audioTracks > 256)
            return false;

        long indexStart = 44;
        bool newField = (bink1 && revision == (byte)'k') || (bink2 && revision is (byte)'i' or (byte)'j' or (byte)'k');
        if (newField)
            indexStart += 4;
        indexStart += audioTracks * 4L; // max decoded sizes
        indexStart += audioTracks * 4L; // sample-rate/flags
        indexStart += audioTracks * 4L; // track IDs
        long indexEnd = indexStart + frames * 4L;
        if (indexEnd > input.Length || indexEnd > int.MaxValue)
            return false;

        var offsets = new List<uint>(checked((int)frames));
        byte[] table = new byte[checked((int)(frames * 4L))];
        ReadExactly(input, indexStart, table);
        uint previous = 0;
        for (int i = 0; i < frames; i++)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 4, 4));
            uint position = raw & ~1U;
            if (position < indexEnd || (i > 0 && position <= previous) || position >= declared || position >= input.Length)
                return false;
            offsets.Add(raw);
            previous = position;
        }

        header = new BinkHeader(declared, frames, offsets, offsets[0] & ~1U);
        return true;
    }

    // --------------------------- SMACKER ---------------------------

    private static bool TryComputeSmackerLength(Stream input, out long totalLength, out uint frameCount)
    {
        totalLength = 0;
        frameCount = 0;
        if (input.Length < 104)
            return false;
        byte[] header = new byte[104];
        ReadExactly(input, 0, header);
        if (!(header.AsSpan(0, 4).SequenceEqual("SMK2"u8) || header.AsSpan(0, 4).SequenceEqual("SMK4"u8)))
            return false;
        uint width = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        uint height = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint logicalFrames = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        uint treesSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(52, 4));
        if (width is 0 or > 32768 || height is 0 or > 32768 || logicalFrames is 0 or > 5_000_000 || (flags & ~0x07U) != 0)
            return false;
        long physicalFrames = logicalFrames + ((flags & 1) != 0 ? 1L : 0L);
        long sizeTableBytes = physicalFrames * 4L;
        long typeTableBytes = physicalFrames;
        long frameDataStart = 104L + sizeTableBytes + typeTableBytes + treesSize;
        if (frameDataStart > input.Length || sizeTableBytes > int.MaxValue)
            return false;
        byte[] sizes = new byte[checked((int)sizeTableBytes)];
        ReadExactly(input, 104, sizes);
        long payload = 0;
        for (int i = 0; i < physicalFrames; i++)
        {
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(sizes.AsSpan(i * 4, 4));
            uint size = stored & 0xFFFFFFFCU;
            if (size == 0)
                return false;
            payload = checked(payload + size);
        }
        totalLength = checked(frameDataStart + payload);
        if (totalLength > input.Length)
            return false;
        frameCount = logicalFrames;
        return true;
    }

    // --------------------------- Generic helpers ---------------------------

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void CopyRange(Stream input, Stream output, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset + length > input.Length)
            throw new InvalidDataException("Kopyalama aralığı kaynak dosyanın dışında.");
        input.Position = offset;
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, request);
            if (read <= 0)
                throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void ReadExactly(Stream input, long offset, Span<byte> buffer)
    {
        input.Position = offset;
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer[total..]);
            if (read <= 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static void ReadExactly(Stream input, long offset, byte[] buffer) => ReadExactly(input, offset, buffer.AsSpan());

    private static long FindNextAscii(Stream input, ReadOnlySpan<byte> signature, long start, long end) =>
        FindNextBytes(input, signature.ToArray(), start, end);

    private static long FindNextBytes(Stream input, byte[] signature, long start, long end)
    {
        if (signature.Length == 0)
            return -1;
        start = Math.Max(0, start);
        end = Math.Min(end, input.Length);
        if (start >= end)
            return -1;
        int overlap = signature.Length - 1;
        byte[] buffer = new byte[256 * 1024 + overlap];
        int carry = 0;
        long position = start;
        while (position < end)
        {
            int request = (int)Math.Min(256 * 1024, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            int index = buffer.AsSpan(0, count).IndexOf(signature);
            if (index >= 0)
                return position - carry + index;
            carry = Math.Min(overlap, count);
            if (carry > 0)
                Buffer.BlockCopy(buffer, count - carry, buffer, 0, carry);
            position += read;
        }
        return -1;
    }

    private static bool StartsWithAscii(byte[] data, string text) =>
        data.AsSpan().StartsWith(Encoding.ASCII.GetBytes(text));

    private static bool ContainsAscii(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private readonly record struct OggPage(int TotalLength, int HeaderLength, int PayloadLength, uint Serial);
    private readonly record struct RmObject(string Id, uint Size, ushort Version);
    private readonly record struct NsvUnit(long TotalLength, bool HasStreamHeader, byte FrameRateCode);
    private readonly record struct RoqChunk(ushort Type, uint Size, long TotalLength);
    private sealed record BinkHeader(uint DeclaredFileSize, uint FrameCount, List<uint> FrameOffsets, uint FirstFrameOffset);
}
