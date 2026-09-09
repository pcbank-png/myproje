using System.Buffers.Binary;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class PriorityVideoRepairEngine
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int TransportFastResyncWindow = 16 * 1024 * 1024;
    private const int TransportUltraResyncWindow = 256 * 1024 * 1024;

    private static readonly HashSet<string> TransportFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "MTS", "M2TS", "TS", "M2T", "TP", "TRP", "TOD"
    };

    private static readonly HashSet<string> MpegFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "MPG", "MPEG", "MPE", "MPV", "M1V", "M2V", "VOB", "EVO", "MOD"
    };

    private static readonly HashSet<string> AviFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "AVI", "DIVX", "XVID"
    };

    private static readonly HashSet<string> NalFamily = new(StringComparer.OrdinalIgnoreCase)
    {
        "H264", "AVC", "H265", "HEVC"
    };

    public static bool Supports(string extension) =>
        TransportFamily.Contains(extension) ||
        MpegFamily.Contains(extension) ||
        AviFamily.Contains(extension) ||
        NalFamily.Contains(extension);

    public static MediaRepairResult Repair(
        string extension,
        string sourcePath,
        string destinationPath,
        Action<string>? progress,
        ReferenceVideoProfile? referenceProfile = null)
    {
        try
        {
            if (TransportFamily.Contains(extension))
                return AvchdTransportReconstructionService.Reconstruct(sourcePath, destinationPath, progress, referenceProfile);

            if (MpegFamily.Contains(extension))
                return RepairMpeg(sourcePath, destinationPath, progress);

            if (AviFamily.Contains(extension))
                return RepairAvi(sourcePath, destinationPath, progress);

            if (NalFamily.Contains(extension))
                return RepairAnnexB(sourcePath, destinationPath, extension is "H265" or "HEVC", progress);

            return new MediaRepairResult(false, "Bu video türü için yapısal onarım motoru bulunmuyor.");
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, $"Video onarılamadı: {ex.Message}");
        }
    }

    private static MediaRepairResult RepairTransportStream(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("MTS/M2TS/TS paket geometrisi ve senkron yapısı analiz ediliyor...");

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        TransportGeometry geometry = DetectTransportGeometry(input)
            ?? throw new InvalidDataException("Geçerli 188/192 bayt transport-stream paket dizisi bulunamadı.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        byte[] packet = new byte[geometry.PacketSize];
        long position = geometry.PacketStart;
        long skippedBytes = geometry.PacketStart;
        long copiedPackets = 0;
        long rejectedPackets = 0;
        int videoPesPackets = 0;
        var continuityCounters = new Dictionary<int, int>();
        long nextProgress = 64L * 1024 * 1024;

        while (position + geometry.PacketSize <= input.Length)
        {
            input.Position = position;
            if (!ReadExactly(input, packet))
                break;

            if (!IsValidTransportPacket(packet, geometry.SyncOffset, out bool hasVideoPes))
            {
                long next = FindTransportStart(
                    input,
                    position + 1,
                    Math.Min(input.Length, position + TransportFastResyncWindow),
                    geometry.PacketSize,
                    geometry.SyncOffset,
                    8);

                if (next < 0)
                {
                    next = FindTransportStart(
                        input,
                        Math.Min(input.Length, position + TransportFastResyncWindow),
                        Math.Min(input.Length, position + TransportUltraResyncWindow),
                        geometry.PacketSize,
                        geometry.SyncOffset,
                        12);
                }

                if (next < 0)
                    break;

                skippedBytes += next - position;
                rejectedPackets++;
                position = next;
                continue;
            }

            NormalizeTransportContinuity(packet, geometry.SyncOffset, continuityCounters);
            output.Write(packet, 0, packet.Length);
            copiedPackets++;
            if (hasVideoPes)
                videoPesPackets++;

            position += geometry.PacketSize;

            if (output.Length >= nextProgress)
            {
                progress?.Invoke($"Transport stream yeniden hizalanıyor • {RecoveryFileItem.FormatBytes(output.Length)} doğrulandı...");
                nextProgress += 64L * 1024 * 1024;
            }
        }

        output.Flush(true);

        if (copiedPackets < 20 || output.Length < geometry.PacketSize * 20L)
            throw new InvalidDataException("Yeterli sayıda sağlam transport-stream paketi doğrulanamadı.");

        if (videoPesPackets == 0 && !ContainsVideoElementaryEvidence(destinationPath, 32L * 1024 * 1024))
            throw new InvalidDataException("Transport stream içinde doğrulanabilir video PES/H.264/H.265 akışı bulunamadı.");

        ValidateTransportStream(destinationPath, geometry.PacketSize, geometry.SyncOffset);

        string packetType = geometry.PacketSize == 192 ? "192 bayt MTS/M2TS" : "188 bayt TS/MTS";
        string recoveryText = skippedBytes > 0 || rejectedPackets > 0
            ? $" {RecoveryFileItem.FormatBytes(skippedBytes)} bozuk/uyumsuz alan atlandı ve akış yeniden senkronlandı."
            : string.Empty;

        return new MediaRepairResult(
            true,
            $"{packetType} akışı paket seviyesinde yeniden hizalandı; {copiedPackets:N0} paket ve video akışı doğrulandı.{recoveryText}",
            output.Length);
    }

    private static TransportGeometry? DetectTransportGeometry(FileStream input)
    {
        if (input.Length < 188 * 8L)
            return null;

        const int blockSize = 4 * 1024 * 1024;
        const int requiredTail = 192 * 8 + 8;
        byte[] buffer = new byte[blockSize + requiredTail];
        int carry = 0;
        long position = 0;

        while (position < input.Length)
        {
            int request = (int)Math.Min(blockSize, input.Length - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            ReadOnlySpan<byte> data = buffer.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                if (data[i] == 0x47 && HasTransportSyncAt(data, i, 188, 0, 8))
                    return new TransportGeometry(188, 0, position - carry + i);

                if (i + 4 < count && data[i + 4] == 0x47 && HasTransportSyncAt(data, i, 192, 4, 8))
                    return new TransportGeometry(192, 4, position - carry + i);
            }

            carry = Math.Min(requiredTail, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return null;
    }

    private static bool HasTransportSyncAt(ReadOnlySpan<byte> data, int start, int packetSize, int syncOffset, int packetCount)
    {
        int last = start + syncOffset + packetSize * (packetCount - 1);
        if (start < 0 || last >= data.Length)
            return false;

        for (int i = 0; i < packetCount; i++)
        {
            if (data[start + syncOffset + i * packetSize] != 0x47)
                return false;
        }

        return true;
    }

    private static long FindTransportStart(ReadOnlySpan<byte> data, int packetSize, int syncOffset, int packetCount)
    {
        int needed = syncOffset + packetSize * (packetCount - 1) + 1;
        for (int start = 0; start + needed <= data.Length; start++)
        {
            bool ok = true;
            for (int i = 0; i < packetCount; i++)
            {
                if (data[start + syncOffset + i * packetSize] != 0x47)
                {
                    ok = false;
                    break;
                }
            }

            if (ok)
                return start;
        }

        return -1;
    }

    private static long FindTransportStart(
        FileStream input,
        long searchStart,
        long searchEnd,
        int packetSize,
        int syncOffset,
        int packetCount)
    {
        if (searchStart >= searchEnd)
            return -1;

        const int blockSize = 4 * 1024 * 1024;
        int overlap = syncOffset + packetSize * packetCount + 8;
        byte[] buffer = new byte[blockSize + overlap];
        int carry = 0;
        long position = searchStart;

        while (position < searchEnd)
        {
            int request = (int)Math.Min(blockSize, searchEnd - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            long local = FindTransportStart(buffer.AsSpan(0, count), packetSize, syncOffset, packetCount);
            if (local >= 0)
                return position - carry + local;

            carry = Math.Min(overlap, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return -1;
    }

    private static bool IsValidTransportPacket(byte[] packet, int syncOffset, out bool hasVideoPes)
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

    private static void NormalizeTransportContinuity(
        byte[] packet,
        int syncOffset,
        Dictionary<int, int> continuityCounters)
    {
        if (syncOffset < 0 || syncOffset + 4 > packet.Length || packet[syncOffset] != 0x47)
            return;

        int pid = ((packet[syncOffset + 1] & 0x1F) << 8) | packet[syncOffset + 2];
        int adaptationControl = (packet[syncOffset + 3] >> 4) & 0x03;
        bool hasPayload = adaptationControl is 1 or 3;
        if (!hasPayload)
            return;

        int original = packet[syncOffset + 3] & 0x0F;
        int counter = continuityCounters.TryGetValue(pid, out int expected) ? expected : original;
        packet[syncOffset + 3] = (byte)((packet[syncOffset + 3] & 0xF0) | (counter & 0x0F));
        continuityCounters[pid] = (counter + 1) & 0x0F;
    }

    private static void ValidateTransportStream(string path, int packetSize, int syncOffset)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < packetSize * 20L || input.Length % packetSize != 0)
            throw new InvalidDataException("Onarılan transport-stream paket uzunluğu tutarsız.");

        byte[] packet = new byte[packetSize];
        int checkedPackets = 0;
        int videoPes = 0;
        while (checkedPackets < 4096 && input.Position + packetSize <= input.Length)
        {
            if (!ReadExactly(input, packet))
                break;
            if (!IsValidTransportPacket(packet, syncOffset, out bool hasVideo))
                throw new InvalidDataException("Onarılan transport-stream içinde bozuk paket bulundu.");
            if (hasVideo)
                videoPes++;
            checkedPackets++;
        }

        if (checkedPackets < 20 || (videoPes == 0 && !ContainsVideoElementaryEvidence(path, 32L * 1024 * 1024)))
            throw new InvalidDataException("Onarılan transport-stream video akışı olarak doğrulanamadı.");
    }

    private static MediaRepairResult RepairMpeg(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("MPG/MPEG pack, PES ve video sequence yapısı analiz ediliyor...");

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 16)
            throw new InvalidDataException("MPEG verisi çok kısa.");

        byte[] packHeader = [0x00, 0x00, 0x01, 0xBA];
        byte[] sequenceHeader = [0x00, 0x00, 0x01, 0xB3];
        long searchEnd = input.Length;
        long packStart = FindPattern(input, packHeader, 0, searchEnd);
        long sequenceStart = FindPattern(input, sequenceHeader, 0, searchEnd);

        bool programStream = packStart >= 0 && (sequenceStart < 0 || packStart <= sequenceStart);
        long start = programStream ? packStart : sequenceStart;
        if (start < 0)
            throw new InvalidDataException("MPEG pack veya video sequence başlangıcı bulunamadı.");

        byte[] endMarker = programStream
            ? [0x00, 0x00, 0x01, 0xB9]
            : [0x00, 0x00, 0x01, 0xB7];

        long lastEnd = FindLastPattern(input, endMarker, start, input.Length);
        long length = lastEnd >= start ? lastEnd + endMarker.Length - start : input.Length - start;
        if (length <= 0)
            throw new InvalidDataException("MPEG dosya sınırı çıkarılamadı.");

        EnsureParentDirectory(destinationPath);
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            CopyRange(input, output, start, length);
            if (lastEnd < start)
                output.Write(endMarker, 0, endMarker.Length);
            output.Flush(true);
        }

        progress?.Invoke("MPEG video akışı ve bitiş yapısı tekrar doğrulanıyor...");
        ValidateMpeg(destinationPath, programStream);
        long size = new FileInfo(destinationPath).Length;

        return new MediaRepairResult(
            true,
            programStream
                ? "MPG/MPEG program stream başlangıç-bitiş sınırları temizlendi; pack/PES/video akışı doğrulandı."
                : "MPEG elementary video başlangıç-bitiş sınırları temizlendi; sequence/video akışı doğrulandı.",
            size);
    }

    private static void ValidateMpeg(string path, bool programStream)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] data = ReadPrefix(input, (int)Math.Min(input.Length, 32L * 1024 * 1024));
        if (data.Length < 16)
            throw new InvalidDataException("Onarılan MPEG dosyası çok kısa.");

        bool hasSequence = IndexOf(data, [0x00, 0x00, 0x01, 0xB3]) >= 0;
        bool hasVideoPes = HasMpegVideoPes(data);
        if (programStream)
        {
            if (!StartsWith(data, [0x00, 0x00, 0x01, 0xBA]) || (!hasSequence && !hasVideoPes))
                throw new InvalidDataException("Onarılan program stream içinde geçerli video akışı doğrulanamadı.");
        }
        else if (!StartsWith(data, [0x00, 0x00, 0x01, 0xB3]))
        {
            throw new InvalidDataException("Onarılan MPEG elementary stream sequence header ile başlamıyor.");
        }
    }

    private static MediaRepairResult RepairAvi(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("AVI/OpenDML RIFF/hdrl/movi/AVIX yapısı analiz ediliyor...");

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        long start = FindRiffAviStart(input, input.Length);
        if (start < 0)
            throw new InvalidDataException("RIFF AVI başlangıcı bulunamadı.");

        long available = input.Length - start;
        List<AviRiffSegment> segments = ReadAviRiffSegments(input, start, available);

        if (segments.Count == 0)
        {
            long structuralLength = FindAviStructuralLength(input, start, available);
            if (structuralLength < 1024)
                throw new InvalidDataException("AVI yapısal dosya sınırı çıkarılamadı.");
            if (structuralLength - 8 > uint.MaxValue)
                throw new InvalidDataException("Tek RIFF segmenti 4 GB sınırını aşıyor ve güvenli OpenDML AVIX segment sınırı bulunamadı.");

            segments.Add(new AviRiffSegment(start, structuralLength, "AVI "));
        }

        EnsureParentDirectory(destinationPath);
        using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
        {
            byte[] patchedSize = new byte[4];
            foreach (AviRiffSegment segment in segments)
            {
                if (segment.Length < 12 || segment.Length - 8 > uint.MaxValue)
                    throw new InvalidDataException("AVI/OpenDML RIFF segment boyutu güvenli 32-bit RIFF aralığında değil.");

                long outputSegmentStart = output.Position;
                CopyRange(input, output, segment.Offset, segment.Length);

                output.Position = outputSegmentStart + 4;
                BinaryPrimitives.WriteUInt32LittleEndian(patchedSize, (uint)(segment.Length - 8));
                output.Write(patchedSize, 0, patchedSize.Length);
                output.Position = outputSegmentStart + segment.Length;
            }

            output.Flush(true);
        }

        progress?.Invoke("AVI/OpenDML RIFF segmentleri ve video/movi yapısı tekrar doğrulanıyor...");
        ValidateAvi(destinationPath);
        long size = new FileInfo(destinationPath).Length;

        return new MediaRepairResult(
            true,
            segments.Count > 1
                ? $"AVI/OpenDML video {segments.Count:N0} RIFF/AVIX segmentiyle yeniden kuruldu; segment boyutları ve hdrl/movi/video akışı doğrulandı."
                : "AVI RIFF dosya sınırı yeniden kuruldu; RIFF boyutu düzeltildi ve hdrl/movi/video akışı doğrulandı.",
            size);
    }

    private static List<AviRiffSegment> ReadAviRiffSegments(FileStream input, long start, long available)
    {
        var segments = new List<AviRiffSegment>();
        long end = start + available;
        long position = start;
        bool first = true;
        Span<byte> header = stackalloc byte[12];

        while (position + 12 <= end)
        {
            input.Position = position;
            if (input.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8))
                break;

            string formType = Encoding.ASCII.GetString(header[8..12]);
            if ((first && formType != "AVI ") || (!first && formType != "AVIX"))
                break;

            uint payload = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            long declaredLength = 8L + payload;
            long segmentLength;

            if (declaredLength >= 12 && position + declaredLength <= end)
            {
                segmentLength = declaredLength;
            }
            else
            {
                long next = FindNextRiffAvixStart(input, position + 12, end);
                segmentLength = next > position ? next - position : end - position;
            }

            if (segmentLength < 12 || segmentLength - 8 > uint.MaxValue)
                break;

            segments.Add(new AviRiffSegment(position, segmentLength, formType));
            position += segmentLength;
            first = false;

            if (position + 12 <= end)
            {
                input.Position = position;
                if (input.Read(header) != 12 || !header[..4].SequenceEqual("RIFF"u8))
                    break;
                if (Encoding.ASCII.GetString(header[8..12]) != "AVIX")
                    break;
            }
        }

        return segments;
    }

    private static long FindNextRiffAvixStart(FileStream input, long start, long end)
    {
        byte[] signature = Encoding.ASCII.GetBytes("RIFF");
        Span<byte> header = stackalloc byte[12];
        long position = start;

        while (position + 12 <= end)
        {
            long candidate = FindPattern(input, signature, position, end);
            if (candidate < 0)
                return -1;

            input.Position = candidate;
            if (input.Read(header) == 12 && Encoding.ASCII.GetString(header[8..12]) == "AVIX")
                return candidate;

            position = candidate + 1;
        }

        return -1;
    }

    private static long FindRiffAviStart(FileStream input, long maxSearch)
    {
        byte[] signature = Encoding.ASCII.GetBytes("RIFF");
        Span<byte> header = stackalloc byte[12];
        long position = 0;
        while (position + 12 <= maxSearch)
        {
            long candidate = FindPattern(input, signature, position, maxSearch);
            if (candidate < 0)
                return -1;

            input.Position = candidate;
            if (input.Read(header) == 12 && header[8] == (byte)'A' && header[9] == (byte)'V' && header[10] == (byte)'I' && header[11] == (byte)' ')
                return candidate;

            position = candidate + 1;
        }

        return -1;
    }

    private static long FindAviStructuralLength(FileStream input, long start, long available)
    {
        long end = start + available;
        long position = start + 12;
        long lastGood = position;
        bool seenMovi = false;
        Span<byte> header = stackalloc byte[12];
        while (position + 8 <= end)
        {
            input.Position = position;
            if (input.Read(header[..8]) != 8)
                break;

            string id = Encoding.ASCII.GetString(header[..4]);
            if (!IsFourCc(id))
                break;

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            long total = 8L + size + (size & 1);
            if (total < 8 || position + total > end)
                break;

            if (id is "LIST" or "RIFF")
            {
                input.Position = position;
                if (input.Read(header) != 12 || size < 4)
                    break;
                if (Encoding.ASCII.GetString(header[8..12]) == "movi")
                    seenMovi = true;
            }

            lastGood = position + total;
            position += total;

            if (id == "idx1")
                break;
        }

        return seenMovi ? lastGood - start : 0;
    }

    private static void ValidateAvi(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 12)
            throw new InvalidDataException("Onarılan AVI dosyası çok kısa.");

        List<AviRiffSegment> segments = ReadAviRiffSegments(input, 0, input.Length);
        if (segments.Count == 0 || segments[0].Offset != 0 || segments[0].FormType != "AVI ")
            throw new InvalidDataException("Onarılan dosya RIFF AVI/OpenDML olarak doğrulanamadı.");

        long covered = 0;
        byte[] sizeBytes = new byte[4];
        foreach (AviRiffSegment segment in segments)
        {
            if (segment.Offset != covered)
                throw new InvalidDataException("AVI/OpenDML RIFF segmentleri arasında doğrulanamayan boşluk bulundu.");

            input.Position = segment.Offset + 4;
            if (input.Read(sizeBytes, 0, sizeBytes.Length) != 4)
                throw new InvalidDataException("AVI RIFF segment boyutu okunamadı.");

            uint declared = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes.AsSpan());
            if (8L + declared != segment.Length)
                throw new InvalidDataException("AVI/OpenDML RIFF segment boyutu gerçek segment uzunluğuyla eşleşmiyor.");

            covered += segment.Length;
        }

        if (covered != input.Length)
            throw new InvalidDataException("AVI/OpenDML sonunda doğrulanamayan veri bulundu.");

        byte[] prefix = ReadPrefix(input, (int)Math.Min(input.Length, 32L * 1024 * 1024));
        if (IndexOf(prefix, Encoding.ASCII.GetBytes("hdrl")) < 0 ||
            IndexOf(prefix, Encoding.ASCII.GetBytes("movi")) < 0 ||
            IndexOf(prefix, Encoding.ASCII.GetBytes("vids")) < 0)
            throw new InvalidDataException("Onarılan AVI içinde hdrl/movi/video stream yapısı eksik.");
    }

    private static MediaRepairResult RepairAnnexB(
        string sourcePath,
        string destinationPath,
        bool h265,
        Action<string>? progress)
    {
        progress?.Invoke(h265
            ? "H.265/HEVC NAL akışı başlangıç kodları ve parametre setleri analiz ediliyor..."
            : "H.264/AVC NAL akışı başlangıç kodları ve parametre setleri analiz ediliyor...");

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        if (input.Length < 8)
            throw new InvalidDataException("NAL video akışı çok kısa.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        long search = 0;
        long nalCount = 0;
        long keyFrames = 0;
        bool hasVps = false;
        bool hasSps = false;
        bool hasPps = false;
        long copiedPayload = 0;
        byte[] annexBPrefix = [0x00, 0x00, 0x00, 0x01];
        byte[] header = new byte[2];
        long nextProgress = 256L * 1024 * 1024;

        while (search < input.Length)
        {
            long current = FindNextAnnexBStart(input, search, input.Length, out int prefixLength);
            if (current < 0)
                break;

            long payloadStart = current + prefixLength;
            if (payloadStart >= input.Length)
                break;

            input.Position = payloadStart;
            int headerRead = input.Read(header, 0, header.Length);
            if (headerRead <= 0)
                break;

            bool valid;
            int nalType;
            if (!h265)
            {
                valid = (header[0] & 0x80) == 0;
                nalType = header[0] & 0x1F;
                valid &= nalType is >= 1 and <= 12;
            }
            else
            {
                valid = headerRead >= 2 && (header[0] & 0x80) == 0 && (header[1] & 0x07) != 0;
                nalType = (header[0] >> 1) & 0x3F;
                valid &= nalType <= 40;
            }

            long next = FindNextAnnexBStart(input, payloadStart + (h265 ? 2 : 1), input.Length, out _);
            long payloadLength = (next >= 0 ? next : input.Length) - payloadStart;
            if (payloadLength <= 0)
            {
                search = current + Math.Max(1, prefixLength);
                continue;
            }

            if (valid)
            {
                output.Write(annexBPrefix, 0, annexBPrefix.Length);
                CopyRange(input, output, payloadStart, payloadLength);
                copiedPayload += payloadLength;
                nalCount++;

                if (!h265)
                {
                    if (nalType == 5) keyFrames++;
                    if (nalType == 7) hasSps = true;
                    if (nalType == 8) hasPps = true;
                }
                else
                {
                    if (nalType is 19 or 20 or 21) keyFrames++;
                    if (nalType == 32) hasVps = true;
                    if (nalType == 33) hasSps = true;
                    if (nalType == 34) hasPps = true;
                }

                if (output.Length >= nextProgress)
                {
                    progress?.Invoke($"NAL video akışı yeniden kuruluyor • {RecoveryFileItem.FormatBytes(output.Length)} doğrulandı...");
                    nextProgress += 256L * 1024 * 1024;
                }
            }

            if (next < 0)
                break;
            search = next;
        }

        output.Flush(true);

        if (nalCount < 8 || copiedPayload < 256L * 1024 || keyFrames == 0)
            throw new InvalidDataException("Yeterli sayıda doğrulanabilir video NAL birimi veya anahtar kare bulunamadı.");

        if ((!h265 && (!hasSps || !hasPps)) ||
            (h265 && (!hasVps || !hasSps || !hasPps)))
            throw new InvalidDataException(h265
                ? "HEVC VPS/SPS/PPS parametre setlerinin tamamı bulunamadı. Güvenli onarım doğrulanamadı."
                : "H.264 SPS/PPS parametre setlerinin tamamı bulunamadı. Güvenli onarım doğrulanamadı.");

        return new MediaRepairResult(
            true,
            h265
                ? $"HEVC Annex-B akışı {nalCount:N0} NAL birimiyle yeniden kuruldu; VPS/SPS/PPS ve {keyFrames:N0} anahtar kare doğrulandı."
                : $"H.264 Annex-B akışı {nalCount:N0} NAL birimiyle yeniden kuruldu; SPS/PPS ve {keyFrames:N0} IDR kare doğrulandı.",
            output.Length);
    }

    private static long FindNextAnnexBStart(FileStream input, long start, long end, out int prefixLength)
    {
        byte[] prefix3 = [0x00, 0x00, 0x01];
        byte[] prefix4 = [0x00, 0x00, 0x00, 0x01];
        long four = FindPattern(input, prefix4, start, end);
        long three = FindPattern(input, prefix3, start, end);

        if (four >= 0 && (three < 0 || four <= three))
        {
            prefixLength = 4;
            return four;
        }

        if (three >= 0)
        {
            prefixLength = 3;
            return three;
        }

        prefixLength = 0;
        return -1;
    }

    private static bool ContainsVideoElementaryEvidence(string path, long maxBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] data = ReadPrefix(input, (int)Math.Min(input.Length, maxBytes));
        return HasMpegVideoPes(data) ||
               IndexOf(data, [0x00, 0x00, 0x01, 0x67]) >= 0 ||
               IndexOf(data, [0x00, 0x00, 0x00, 0x01, 0x67]) >= 0 ||
               ContainsHevcParameterSet(data);
    }

    private static bool ContainsHevcParameterSet(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 6 <= data.Length; i++)
        {
            int nalOffset;
            if (i + 5 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
                nalOffset = i + 3;
            else if (i + 6 <= data.Length && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1)
                nalOffset = i + 4;
            else
                continue;

            int type = (data[nalOffset] >> 1) & 0x3F;
            if (type is 32 or 33 or 34)
                return true;
        }

        return false;
    }

    private static bool HasMpegVideoPes(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 4 <= data.Length; i++)
        {
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01 && data[i + 3] is >= 0xE0 and <= 0xEF)
                return true;
        }
        return false;
    }

    private static long FindPattern(FileStream input, byte[] pattern, long start, long end)
    {
        if (pattern.Length == 0 || start >= end)
            return -1;

        const int blockSize = 1024 * 1024;
        byte[] buffer = new byte[blockSize + 32];
        int carry = 0;
        long position = start;

        while (position < end)
        {
            int request = (int)Math.Min(blockSize, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            int index = IndexOf(buffer.AsSpan(0, count), pattern);
            if (index >= 0)
                return position - carry + index;

            carry = Math.Min(pattern.Length - 1, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return -1;
    }

    private static long FindLastPattern(FileStream input, byte[] pattern, long start, long end)
    {
        if (pattern.Length == 0 || start >= end)
            return -1;

        const int blockSize = 1024 * 1024;
        byte[] buffer = new byte[blockSize + 32];
        int carry = 0;
        long position = start;
        long last = -1;

        while (position < end)
        {
            int request = (int)Math.Min(blockSize, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            int search = 0;
            while (search <= count - pattern.Length)
            {
                int index = IndexOf(buffer.AsSpan(search, count - search), pattern);
                if (index < 0)
                    break;
                int absoluteIndex = search + index;
                last = position - carry + absoluteIndex;
                search = absoluteIndex + 1;
            }

            carry = Math.Min(pattern.Length - 1, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return last;
    }

    private static void CopyRange(FileStream input, FileStream output, long start, long length)
    {
        input.Position = start;
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read <= 0)
                throw new EndOfStreamException("Video verisi beklenenden önce sona erdi.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static byte[] ReadPrefix(FileStream input, int length)
    {
        byte[] data = new byte[length];
        input.Position = 0;
        int total = 0;
        while (total < data.Length)
        {
            int read = input.Read(data, total, data.Length - total);
            if (read <= 0)
                break;
            total += read;
        }

        return total == data.Length ? data : data.AsSpan(0, total).ToArray();
    }

    private static bool ReadExactly(Stream input, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = input.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
                return false;
            total += read;
        }
        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) =>
        data.Length >= pattern.Length && data[..pattern.Length].SequenceEqual(pattern);

    private static int IndexOf(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern) =>
        data.IndexOf(pattern);

    private static bool IsFourCc(string value) =>
        value.Length == 4 && value.All(ch => ch is >= ' ' and <= '~');

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private readonly record struct TransportGeometry(int PacketSize, int SyncOffset, long PacketStart);
    private readonly record struct AviRiffSegment(long Offset, long Length, string FormType);
}
