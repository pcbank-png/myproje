using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

internal static class ModernCodecReconstructionService
{
    private const int IvfHeaderSize = 32;
    private const int FrameHeaderSize = 12;
    private const int CopyBufferSize = 1024 * 1024;
    private const int ResyncWindow = 8 * 1024 * 1024;
    private const uint MaxFrameBytes = 64 * 1024 * 1024;
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase) { "IVF", "AV1" };

    private readonly record struct IvfHeader(ushort Width, ushort Height, uint Rate, uint Scale);
    private readonly record struct Av1Frame(long HeaderOffset, uint Size, ulong Timestamp, bool KeyEvidence, bool SequenceEvidence);

    public static bool Supports(string extension) => Supported.Contains(extension);

    public static MediaRepairResult Repair(string extension, string sourcePath, string destinationPath, Action<string>? progress)
    {
        try
        {
            if (!Supports(extension))
                return new MediaRepairResult(false, "Bu modern video türü için onarım desteği bulunmuyor.");
            return RepairAv1Ivf(sourcePath, destinationPath, progress);
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, "AV1 video yapısı yeniden oluşturulamadı: " + ex.Message);
        }
    }

    public static RawFileAnalysis? AnalyzeAv1Ivf(RawDeviceReader reader, long start, long volumeLength, CancellationToken cancellationToken)
    {
        if (!TryReadIvfHeader(reader, start, volumeLength, out IvfHeader header)) return null;
        long position = start + IvfHeaderSize;
        long lastGoodEnd = position;
        int frames = 0;
        bool sequenceHeader = false;
        bool keyEvidence = false;
        while (position + FrameHeaderSize <= volumeLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] frameHeader = reader.ReadBytes(position, FrameHeaderSize);
            if (frameHeader.Length != FrameHeaderSize) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(frameHeader.AsSpan(0, 4));
            if (size == 0 || size > MaxFrameBytes || position + FrameHeaderSize + size > volumeLength) break;
            byte[] payload = reader.ReadBytes(position + FrameHeaderSize, checked((int)size));
            if (!TryValidateAv1Frame(payload, out bool hasSequence, out bool hasFrame, out bool isKey) || !hasFrame) break;
            sequenceHeader |= hasSequence;
            keyEvidence |= isKey;
            frames++;
            position += FrameHeaderSize + size;
            lastGoodEnd = position;
        }
        if (frames == 0 || !sequenceHeader || header.Width == 0 || header.Height == 0) return null;
        return new RawFileAnalysis("IVF", lastGoodEnd - start, frames >= 2 && keyEvidence ? "Çok İyi" : "İyi");
    }

    private static MediaRepairResult RepairAv1Ivf(string sourcePath, string destinationPath, Action<string>? progress)
    {
        progress?.Invoke("AV1 video başlığı ve kare yapısı analiz ediliyor...");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);
        if (!TryReadIvfHeader(input, out IvfHeader header)) throw new InvalidDataException("AV1/IVF başlığı doğrulanamadı.");
        List<Av1Frame> frames = ScanFrames(input, out long skippedBytes);
        if (frames.Count == 0 || !frames.Any(frame => frame.KeyEvidence))
            throw new InvalidDataException("Doğrulanabilir AV1 kare zinciri ve başlangıç karesi bulunamadı.");

        string? parent = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        progress?.Invoke("AV1 kareleri yeniden sıralanıyor ve video yapısı oluşturuluyor...");
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            WriteIvfHeader(output, header, checked((uint)frames.Count));
            byte[] buffer = new byte[CopyBufferSize];
            Span<byte> frameHeader = stackalloc byte[FrameHeaderSize];
            ulong timestamp = 0;
            foreach (Av1Frame frame in frames.OrderByDescending(frame => frame.SequenceEvidence).ThenBy(frame => frame.Timestamp).ThenBy(frame => frame.HeaderOffset))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(frameHeader[..4], frame.Size);
                BinaryPrimitives.WriteUInt64LittleEndian(frameHeader[4..], timestamp++);
                output.Write(frameHeader);
                CopyRange(input, output, frame.HeaderOffset + FrameHeaderSize, frame.Size, buffer);
            }
            output.Flush(true);
        }
        ValidateOutput(destinationPath, header, frames.Count);
        string skipped = skippedBytes > 0 ? $" {skippedBytes:N0} baytlık bozuk veya başka dosyaya ait alan atlandı." : string.Empty;
        return new MediaRepairResult(true, $"AV1 video yapısı yeniden oluşturuldu; {frames.Count:N0} kare ve OBU sınırları doğrulandı.{skipped}", new FileInfo(destinationPath).Length);
    }

    private static List<Av1Frame> ScanFrames(FileStream input, out long skippedBytes)
    {
        var frames = new List<Av1Frame>();
        long position = IvfHeaderSize;
        skippedBytes = 0;
        bool sequenceSeen = false;
        while (position + FrameHeaderSize <= input.Length)
        {
            if (TryReadFrame(input, position, out Av1Frame frame, out bool hasSequence))
            {
                sequenceSeen |= hasSequence;
                if (sequenceSeen) frames.Add(frame);
                position += FrameHeaderSize + frame.Size;
                continue;
            }
            long next = FindNextFrame(input, position + 1, Math.Min(input.Length, position + ResyncWindow), !sequenceSeen);
            if (next < 0) break;
            skippedBytes += next - position;
            position = next;
        }
        return frames;
    }

    private static long FindNextFrame(FileStream input, long start, long end, bool requireSequence)
    {
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        for (long position = start; position + FrameHeaderSize <= end; position++)
        {
            input.Position = position;
            if (input.Read(header) != FrameHeaderSize) break;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
            if (size == 0 || size > MaxFrameBytes || position + FrameHeaderSize + size > input.Length) continue;
            if (TryReadFrame(input, position, out _, out bool hasSequence) && (!requireSequence || hasSequence)) return position;
        }
        return -1;
    }

    private static bool TryReadFrame(FileStream input, long position, out Av1Frame frame, out bool hasSequence)
    {
        frame = default;
        hasSequence = false;
        Span<byte> header = stackalloc byte[FrameHeaderSize];
        input.Position = position;
        if (input.Read(header) != FrameHeaderSize) return false;
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        ulong timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header[4..]);
        if (size == 0 || size > MaxFrameBytes || position + FrameHeaderSize + size > input.Length) return false;
        byte[] payload = new byte[checked((int)size)];
        input.ReadExactly(payload);
        if (!TryValidateAv1Frame(payload, out hasSequence, out bool hasFrame, out bool key) || !hasFrame) return false;
        frame = new Av1Frame(position, size, timestamp, key || hasSequence, hasSequence);
        return true;
    }

    internal static bool TryValidateAv1Frame(ReadOnlySpan<byte> payload, out bool hasSequenceHeader, out bool hasFrame, out bool keyEvidence)
    {
        hasSequenceHeader = false;
        hasFrame = false;
        keyEvidence = false;
        int position = 0;
        int count = 0;
        while (position < payload.Length)
        {
            byte header = payload[position++];
            if ((header & 0x80) != 0) return false;
            int type = (header >> 3) & 0x0F;
            bool extension = (header & 0x04) != 0;
            bool hasSize = (header & 0x02) != 0;
            if (type == 0 || type == 15 || !hasSize) return false;
            if (extension)
            {
                if (position >= payload.Length || (payload[position++] & 0x07) != 0) return false;
            }
            if (!TryReadLeb128(payload, ref position, out ulong length) || length > int.MaxValue || length > (ulong)(payload.Length - position)) return false;
            int obuLength = (int)length;
            if (type == 1)
            {
                if (obuLength < 2) return false;
                hasSequenceHeader = true;
                keyEvidence = true;
            }
            else if (type is 3 or 6)
            {
                if (obuLength == 0) return false;
                hasFrame = true;
                if ((payload[position] & 0x80) == 0) keyEvidence = true;
            }
            position += obuLength;
            if (++count > 65536) return false;
        }
        return count > 0 && position == payload.Length;
    }

    private static bool TryReadLeb128(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (int i = 0; i < 8; i++)
        {
            if (position >= data.Length) return false;
            byte current = data[position++];
            value |= (ulong)(current & 0x7F) << (i * 7);
            if ((current & 0x80) == 0) return true;
        }
        return false;
    }

    private static bool TryReadIvfHeader(FileStream input, out IvfHeader header)
    {
        Span<byte> data = stackalloc byte[IvfHeaderSize];
        input.Position = 0;
        header = default;
        return input.Read(data) == data.Length && TryParseIvfHeader(data, out header);
    }

    private static bool TryReadIvfHeader(RawDeviceReader reader, long start, long volumeLength, out IvfHeader header)
    {
        header = default;
        if (start < 0 || start + IvfHeaderSize > volumeLength) return false;
        byte[] data = reader.ReadBytes(start, IvfHeaderSize);
        return data.Length == IvfHeaderSize && TryParseIvfHeader(data, out header);
    }

    private static bool TryParseIvfHeader(ReadOnlySpan<byte> data, out IvfHeader header)
    {
        header = default;
        if (data.Length < IvfHeaderSize || !data[..4].SequenceEqual("DKIF"u8) ||
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..6]) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]) != IvfHeaderSize ||
            !data[8..12].SequenceEqual("AV01"u8)) return false;
        ushort width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]);
        ushort height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]);
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(data[16..20]);
        uint scale = BinaryPrimitives.ReadUInt32LittleEndian(data[20..24]);
        if (width == 0 || height == 0 || rate == 0 || scale == 0) return false;
        header = new IvfHeader(width, height, rate, scale);
        return true;
    }

    private static void WriteIvfHeader(Stream output, IvfHeader header, uint frameCount)
    {
        Span<byte> data = stackalloc byte[IvfHeaderSize];
        "DKIF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data[6..8], IvfHeaderSize);
        "AV01"u8.CopyTo(data[8..12]);
        BinaryPrimitives.WriteUInt16LittleEndian(data[12..14], header.Width);
        BinaryPrimitives.WriteUInt16LittleEndian(data[14..16], header.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..20], header.Rate);
        BinaryPrimitives.WriteUInt32LittleEndian(data[20..24], header.Scale);
        BinaryPrimitives.WriteUInt32LittleEndian(data[24..28], frameCount);
        output.Write(data);
    }

    private static void ValidateOutput(string path, IvfHeader expected, int expectedFrames)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!TryReadIvfHeader(input, out IvfHeader actual) || actual != expected) throw new InvalidDataException("Yeniden oluşturulan AV1/IVF başlığı doğrulanamadı.");
        Span<byte> header = stackalloc byte[IvfHeaderSize];
        input.Position = 0;
        input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[24..28]) != expectedFrames) throw new InvalidDataException("AV1 kare sayısı doğrulanamadı.");
        List<Av1Frame> frames = ScanFrames(input, out long skipped);
        if (frames.Count != expectedFrames || skipped != 0) throw new InvalidDataException("Yeniden oluşturulan AV1 kare zinciri doğrulanamadı.");
    }

    private static void CopyRange(Stream input, Stream output, long offset, uint length, byte[] buffer)
    {
        input.Position = offset;
        long remaining = length;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0) throw new EndOfStreamException("AV1 kare verisi beklenenden önce sona erdi.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
