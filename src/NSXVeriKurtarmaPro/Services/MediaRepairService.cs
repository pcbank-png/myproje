using System.Buffers.Binary;
using System.IO;
using System.Text;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record MediaRepairResult(
    bool Success,
    string Message,
    long OutputBytes = 0,
    int? VideoHealthScore = null,
    string? VideoHealthGrade = null,
    string? VideoHealthSummary = null);

public sealed class MediaRepairService
{
    private static readonly HashSet<string> Mp4Family = new(StringComparer.OrdinalIgnoreCase)
    {
        "MP4", "MOV", "QT", "3GP", "3G2", "M4V", "F4V"
    };

    private static readonly HashSet<string> ContainerBoxes = new(StringComparer.Ordinal)
    {
        "moov", "trak", "mdia", "minf", "stbl", "edts", "dinf", "udta", "meta",
        "moof", "traf", "mfra"
    };

    public MediaRepairResult Repair(
        RecoveryFileItem item,
        string sourcePath,
        string destinationPath,
        Action<string>? progress = null,
        ReferenceVideoProfile? referenceProfile = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destinationPath);

        return item.Category switch
        {
            "Fotoğraf" => RepairImage(item, sourcePath, destinationPath, progress),
            "Video" => RepairVideo(item, sourcePath, destinationPath, progress, referenceProfile),
            _ => new MediaRepairResult(false, "Bu dosya türü için onarma desteklenmiyor.")
        };
    }

    private static MediaRepairResult RepairImage(
        RecoveryFileItem item,
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        try
        {
            var repaired = GlobalImageCodec.Repair(
                sourcePath,
                destinationPath,
                item.Extension,
                progress);

            return new MediaRepairResult(
                true,
                $"Resim gerçekten yeniden oluşturuldu ve doğrulandı " +
                $"({repaired.Width}×{repaired.Height}, {repaired.Frames:N0} kare, {repaired.Extension.ToUpperInvariant()}).",
                repaired.Bytes);
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, $"Resim onarılamadı: {ex.Message}");
        }
    }

    public static bool CanRepairVideo(string extension)
    {
        string ext = FileTypeHelper.Normalize(extension);
        return Mp4Family.Contains(ext) || PriorityVideoRepairEngine.Supports(ext) || LegacyVideoRepairEngine.Supports(ext) || ExtendedVideoRepairEngine.Supports(ext) || ModernCodecReconstructionService.Supports(ext);
    }

    private static MediaRepairResult RepairVideo(
        RecoveryFileItem item,
        string sourcePath,
        string destinationPath,
        Action<string>? progress,
        ReferenceVideoProfile? referenceProfile)
    {
        string extension = FileTypeHelper.Normalize(item.Extension);
        MediaRepairResult structuralResult;

        if (Mp4Family.Contains(extension))
        {
            structuralResult = RepairMp4Family(sourcePath, destinationPath, progress, referenceProfile);
        }
        else if (PriorityVideoRepairEngine.Supports(extension))
        {
            structuralResult = PriorityVideoRepairEngine.Repair(extension, sourcePath, destinationPath, progress, referenceProfile);
        }
        else if (LegacyVideoRepairEngine.Supports(extension))
        {
            structuralResult = LegacyVideoRepairEngine.Repair(extension, sourcePath, destinationPath, progress);
        }
        else if (ExtendedVideoRepairEngine.Supports(extension))
        {
            structuralResult = ExtendedVideoRepairEngine.Repair(extension, sourcePath, destinationPath, progress);
        }
        else if (ModernCodecReconstructionService.Supports(extension))
        {
            structuralResult = ModernCodecReconstructionService.Repair(extension, sourcePath, destinationPath, progress);
        }
        else
        {
            return new MediaRepairResult(
                false,
                "Bu video kapsayıcısında güvenli yapısal onarım henüz desteklenmiyor. Yanlış başarı raporu verilmedi.");
        }

        if (!structuralResult.Success)
            return structuralResult;

        progress?.Invoke("Yapısal onarım tamamlandı • gerçek kare decode doğrulaması başlatılıyor...");
        VideoDecodeHealthResult health = VideoDecodeValidationService.Validate(destinationPath, progress);
        if (!health.Success)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(
                false,
                $"Yapısal onarım üretildi ancak gerçek video decode doğrulaması geçilemedi. {health.Summary}");
        }

        progress?.Invoke("Gerçek video decode geçti • ses track'i ve A/V sync forensic doğrulaması yapılıyor...");
        bool referenceExpectsAudio = ReferenceExpectsAudio(referenceProfile);
        AudioVideoSyncForensicResult audioSync = AudioVideoSyncForensicService.Analyze(
            destinationPath, extension, health, progress, referenceExpectsAudio);

        progress?.Invoke("Video + ses doğrulaması tamamlandı • birleşik forensic sağlık skoru hesaplanıyor...");
        ForensicVideoHealthResult forensic = ForensicVideoHealthService.Evaluate(
            item, destinationPath, health, audioSync, progress);
        if (!forensic.Success)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(
                false,
                $"Yapısal onarım ve decoder çıktısı üretildi ancak forensic sağlık eşiği geçilemedi. {forensic.Summary}",
                0,
                forensic.Score,
                forensic.Grade,
                forensic.Summary);
        }

        string timelineText = health.TimelineValidated
            ? $" Gerçek decode: {health.PassedSamples}/{health.TotalSamples} zaman noktası."
            : " Gerçek decoder doğrulandı; bu akışta çoklu zaman noktası seek testi uygulanamadı.";

        return new MediaRepairResult(
            true,
            structuralResult.Message + timelineText + " " + forensic.Summary,
            structuralResult.OutputBytes,
            forensic.Score,
            forensic.Grade,
            forensic.Summary);
    }

    private static bool ReferenceExpectsAudio(ReferenceVideoProfile? profile) =>
        profile is not null &&
        !string.IsNullOrWhiteSpace(profile.AudioCodec) &&
        !string.Equals(profile.AudioCodec, "Yok/Bilinmiyor", StringComparison.OrdinalIgnoreCase);

    private static MediaRepairResult RepairMp4Family(
        string sourcePath,
        string destinationPath,
        Action<string>? progress,
        ReferenceVideoProfile? referenceProfile)
    {
        string? structuralFailure = null;

        try
        {
            progress?.Invoke("MP4/MOV kutu yapısı (ftyp/moov/mdat) analiz ediliyor...");
            using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long startOffset = FindFtypBoxStart(input);
            if (startOffset < 0)
                throw new InvalidDataException("ftyp kutusu bulunamadı.");

            List<Mp4Atom> atoms = ReadTopLevelAtoms(input, startOffset, allowTruncatedMdat: true);
            Mp4Atom? ftyp = atoms.FirstOrDefault(a => a.Type == "ftyp");
            Mp4Atom? moov = atoms.FirstOrDefault(a => a.Type == "moov");
            Mp4Atom? firstMdat = atoms.FirstOrDefault(a => a.Type == "mdat");

            if (ftyp is null || firstMdat is null)
                throw new InvalidDataException("ftyp/mdat yapısı doğrulanamadı.");
            if (moov is null)
                throw new InvalidDataException("moov kutusu bulunamadı.");

            progress?.Invoke("moov içindeki parça ve zaman tabloları doğrulanıyor...");
            byte[] moovBytes = ReadAtomBytes(input, moov);
            if (!ContainsChildBox(moovBytes, "mvhd") || !ContainsChildBox(moovBytes, "trak"))
                throw new InvalidDataException("moov yapısı eksik; mvhd/trak tabloları doğrulanamadı.");

            long delta = -startOffset;
            if (delta != 0)
            {
                progress?.Invoke("Medya chunk ofsetleri yeni dosya konumuna göre düzeltiliyor...");
                PatchChunkOffsets(moovBytes, delta);
            }

            progress?.Invoke("Geçerli video atomları temiz bir kapsayıcıya yeniden yazılıyor...");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using (var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (Mp4Atom atom in atoms)
                {
                    if (ReferenceEquals(atom, moov)) output.Write(moovBytes, 0, moovBytes.Length);
                    else if (atom.IsTruncatedMdat) CopyTruncatedMdatWithFixedHeader(input, output, atom);
                    else CopyAtom(input, output, atom);
                }
                output.Flush(true);
            }

            progress?.Invoke("Onarılan video yapısı tekrar doğrulanıyor...");
            ValidateMp4(destinationPath);
            long size = new FileInfo(destinationPath).Length;
            string tail = atoms.Any(a => a.IsTruncatedMdat)
                ? " Truncated mdat boyutu gerçek veriye göre düzeltildi."
                : string.Empty;
            return new MediaRepairResult(true, "Video kapsayıcı yapısı yeniden yazıldı; moov/mdat ve chunk tabloları doğrulandı." + tail, size);
        }
        catch (Exception ex)
        {
            structuralFailure = ex.Message;
            TryDelete(destinationPath);
        }

        progress?.Invoke("Mevcut moov kullanılamıyor; mdat içinden yeni moov/trak/sample tabloları oluşturuluyor...");
        MediaRepairResult reconstructed = Mp4MoovReconstructionService.Reconstruct(sourcePath, destinationPath, progress, referenceProfile);
        if (reconstructed.Success)
            return reconstructed;

        string prefix = string.IsNullOrWhiteSpace(structuralFailure) ? string.Empty : $"Mevcut kapsayıcı onarımı: {structuralFailure} ";
        return new MediaRepairResult(false, $"Video onarılamadı. {prefix}{reconstructed.Message}");
    }

    private static long FindFtypBoxStart(Stream stream)
    {
        if (stream.Length < 12)
            return -1;

        const int blockSize = 4 * 1024 * 1024;
        const int overlap = 32;
        byte[] buffer = new byte[blockSize + overlap];
        int carry = 0;
        long position = 0;

        while (position < stream.Length)
        {
            int request = (int)Math.Min(blockSize, stream.Length - position);
            stream.Position = position;
            int read = stream.Read(buffer, carry, request);
            if (read <= 0)
                break;

            int count = carry + read;
            for (int i = 0; i + 12 <= count; i++)
            {
                if (buffer[i + 4] != (byte)'f' || buffer[i + 5] != (byte)'t' ||
                    buffer[i + 6] != (byte)'y' || buffer[i + 7] != (byte)'p')
                    continue;

                uint size = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(i, 4));
                long absolute = position - carry + i;
                if (size is >= 12 and <= 1024 * 1024 && absolute + size <= stream.Length)
                    return absolute;
            }

            carry = Math.Min(overlap, count);
            if (carry > 0)
                buffer.AsSpan(count - carry, carry).CopyTo(buffer);
            position += read;
        }

        return -1;
    }

    private static List<Mp4Atom> ReadTopLevelAtoms(Stream stream, long startOffset, bool allowTruncatedMdat)
    {
        var atoms = new List<Mp4Atom>();
        long length = stream.Length;
        long offset = startOffset;
        Span<byte> header = stackalloc byte[16];

        while (offset + 8 <= length)
        {
            stream.Position = offset;
            if (stream.Read(header[..8]) != 8)
                break;

            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            if (!IsBoxType(type))
                break;

            int headerSize = 8;
            long atomSize = size32;
            if (size32 == 1)
            {
                if (stream.Read(header[..8]) != 8)
                    break;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(header[..8]);
                if (size64 > long.MaxValue)
                    break;
                atomSize = (long)size64;
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                atomSize = length - offset;
            }

            if (atomSize < headerSize)
                break;

            bool truncated = offset + atomSize > length;
            if (truncated)
            {
                if (!allowTruncatedMdat || type != "mdat")
                    break;
                atomSize = length - offset;
                if (atomSize <= headerSize)
                    break;
            }

            atoms.Add(new Mp4Atom(offset, atomSize, type, headerSize, truncated && type == "mdat"));
            offset += atomSize;
            if (truncated)
                break;
        }

        return atoms;
    }

    private static bool IsBoxType(string type) =>
        type.Length == 4 && type.All(ch => ch is >= ' ' and <= '~');

    private static byte[] ReadAtomBytes(Stream input, Mp4Atom atom)
    {
        if (atom.Length > int.MaxValue)
            throw new InvalidDataException($"{atom.Type} kutusu işlenemeyecek kadar büyük.");

        byte[] bytes = new byte[(int)atom.Length];
        input.Position = atom.Offset;
        input.ReadExactly(bytes);
        return bytes;
    }

    private static bool ContainsChildBox(byte[] parentAtom, string type)
    {
        return FindBoxRecursive(parentAtom, 8, parentAtom.Length, type);
    }

    private static bool FindBoxRecursive(byte[] data, int start, int end, string target)
    {
        int offset = start;
        while (offset + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int header = 8;
            long size = size32;
            if (size32 == 1)
            {
                if (offset + 16 > end)
                    return false;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                if (size64 > int.MaxValue)
                    return false;
                size = (long)size64;
                header = 16;
            }
            else if (size32 == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
                return false;
            if (type == target)
                return true;

            if (ContainerBoxes.Contains(type))
            {
                int childStart = offset + header + (type == "meta" ? 4 : 0);
                if (childStart < offset + size && FindBoxRecursive(data, childStart, (int)(offset + size), target))
                    return true;
            }

            offset += (int)size;
        }

        return false;
    }

    private static void PatchChunkOffsets(byte[] moovAtom, long delta)
    {
        PatchBoxesRecursive(moovAtom, 8, moovAtom.Length, delta);
    }

    private static void PatchBoxesRecursive(byte[] data, int start, int end, long delta)
    {
        int offset = start;
        while (offset + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int header = 8;
            long size = size32;
            if (size32 == 1)
            {
                if (offset + 16 > end)
                    return;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                if (size64 > int.MaxValue)
                    return;
                size = (long)size64;
                header = 16;
            }
            else if (size32 == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
                return;

            if (type == "stco")
            {
                int body = offset + header;
                if (body + 8 <= offset + size)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
                    int entries = body + 8;
                    for (uint i = 0; i < count && entries + 4 <= offset + size; i++, entries += 4)
                    {
                        uint current = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entries, 4));
                        long patched = current + delta;
                        if (patched < 0 || patched > uint.MaxValue)
                            throw new InvalidDataException("stco ofseti güvenli aralığın dışına çıktı.");
                        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entries, 4), (uint)patched);
                    }
                }
            }
            else if (type == "co64")
            {
                int body = offset + header;
                if (body + 8 <= offset + size)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
                    int entries = body + 8;
                    for (uint i = 0; i < count && entries + 8 <= offset + size; i++, entries += 8)
                    {
                        ulong current = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(entries, 8));
                        long patched = checked((long)current + delta);
                        if (patched < 0)
                            throw new InvalidDataException("co64 ofseti negatif konuma düştü.");
                        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(entries, 8), (ulong)patched);
                    }
                }
            }
            else if (type == "tfhd")
            {
                int body = offset + header;
                if (body + 16 <= offset + size)
                {
                    uint flags = (uint)((data[body + 1] << 16) | (data[body + 2] << 8) | data[body + 3]);
                    if ((flags & 0x000001) != 0)
                    {
                        int baseOffsetPos = body + 8;
                        ulong current = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(baseOffsetPos, 8));
                        long patched = checked((long)current + delta);
                        if (patched < 0)
                            throw new InvalidDataException("tfhd base_data_offset negatif konuma düştü.");
                        BinaryPrimitives.WriteUInt64BigEndian(data.AsSpan(baseOffsetPos, 8), (ulong)patched);
                    }
                }
            }
            else if (ContainerBoxes.Contains(type))
            {
                int childStart = offset + header + (type == "meta" ? 4 : 0);
                if (childStart < offset + size)
                    PatchBoxesRecursive(data, childStart, (int)(offset + size), delta);
            }

            offset += (int)size;
        }
    }

    private static void CopyTruncatedMdatWithFixedHeader(Stream input, Stream output, Mp4Atom atom)
    {
        input.Position = atom.Offset;
        byte[] header = new byte[atom.HeaderSize];
        input.ReadExactly(header);

        if (atom.HeaderSize == 8)
        {
            BinaryPrimitives.WriteUInt32BigEndian(
                header.AsSpan(0, 4),
                atom.Length > uint.MaxValue ? 0u : (uint)atom.Length);
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 1);
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), (ulong)atom.Length);
        }

        output.Write(header, 0, header.Length);
        CopyBytes(input, output, atom.Length - atom.HeaderSize);
    }

    private static void CopyAtom(Stream input, Stream output, Mp4Atom atom)
    {
        input.Position = atom.Offset;
        CopyBytes(input, output, atom.Length);
    }

    private static void CopyBytes(Stream input, Stream output, long length)
    {
        byte[] buffer = new byte[1024 * 1024];
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

    private static void ValidateMp4(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<Mp4Atom> atoms = ReadTopLevelAtoms(stream, 0, allowTruncatedMdat: false);
        Mp4Atom? moov = atoms.FirstOrDefault(a => a.Type == "moov");
        if (atoms.FirstOrDefault(a => a.Type == "ftyp") is null ||
            moov is null ||
            atoms.FirstOrDefault(a => a.Type == "mdat") is null)
            throw new InvalidDataException("Doğrulama sonrası temel MP4 kutuları eksik.");

        byte[] moovBytes = ReadAtomBytes(stream, moov);
        if (!ContainsChildBox(moovBytes, "mvhd") || !ContainsChildBox(moovBytes, "trak"))
            throw new InvalidDataException("Doğrulama sonrası moov tabloları eksik.");

        List<ulong> chunkOffsets = [];
        CollectChunkOffsetsRecursive(moovBytes, 8, moovBytes.Length, chunkOffsets);
        if (chunkOffsets.Count > 0)
        {
            if (chunkOffsets.Any(offset => offset >= (ulong)stream.Length))
                throw new InvalidDataException("Chunk tablosunda dosya sınırı dışında medya ofseti bulundu.");

            List<(ulong Start, ulong End)> mediaRanges = atoms
                .Where(a => a.Type == "mdat")
                .Select(a => (Start: (ulong)(a.Offset + a.HeaderSize), End: (ulong)(a.Offset + a.Length)))
                .ToList();

            int insideMedia = chunkOffsets.Count(offset => mediaRanges.Any(range => offset >= range.Start && offset < range.End));
            if (insideMedia == 0)
                throw new InvalidDataException("Chunk tabloları mdat medya alanına işaret etmiyor.");
        }
    }

    private static void CollectChunkOffsetsRecursive(byte[] data, int start, int end, List<ulong> result)
    {
        int offset = start;
        while (offset + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            int header = 8;
            long size = size32;
            if (size32 == 1)
            {
                if (offset + 16 > end)
                    return;
                ulong size64 = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
                if (size64 > int.MaxValue)
                    return;
                size = (long)size64;
                header = 16;
            }
            else if (size32 == 0)
            {
                size = end - offset;
            }

            if (size < header || offset + size > end)
                return;

            if (type == "stco")
            {
                int body = offset + header;
                if (body + 8 <= offset + size)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
                    int entries = body + 8;
                    for (uint i = 0; i < count && entries + 4 <= offset + size; i++, entries += 4)
                        result.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(entries, 4)));
                }
            }
            else if (type == "co64")
            {
                int body = offset + header;
                if (body + 8 <= offset + size)
                {
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4, 4));
                    int entries = body + 8;
                    for (uint i = 0; i < count && entries + 8 <= offset + size; i++, entries += 8)
                        result.Add(BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(entries, 8)));
                }
            }
            else if (ContainerBoxes.Contains(type))
            {
                int childStart = offset + header + (type == "meta" ? 4 : 0);
                if (childStart < offset + size)
                    CollectChunkOffsetsRecursive(data, childStart, (int)(offset + size), result);
            }

            offset += (int)size;
        }
    }

    private static int FindSequence(byte[] data, byte[] pattern, int start)
    {
        for (int i = Math.Max(0, start); i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] == pattern[j])
                    continue;
                match = false;
                break;
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static int FindLastSequence(byte[] data, byte[] pattern)
    {
        for (int i = data.Length - pattern.Length; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] == pattern[j])
                    continue;
                match = false;
                break;
            }
            if (match)
                return i;
        }
        return -1;
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

    private sealed record Mp4Atom(long Offset, long Length, string Type, int HeaderSize, bool IsTruncatedMdat);
}
