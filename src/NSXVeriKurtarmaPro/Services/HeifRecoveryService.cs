using System.Buffers.Binary;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

public sealed record HeifStructureResult(
    bool IsValid,
    string Extension,
    string RecoveryState,
    int StructuralScore,
    string Summary);

/// <summary>
/// HEIC/HEIF/AVIF dosyalarını yalnız ftyp imzasıyla kabul etmez. ISO-BMFF üst
/// kutularını ve HEIF meta ağacındaki hdlr/pitm/iloc/iinf/iprp + mdat/idat
/// ilişkisini doğrular. Kaynağa hiçbir zaman yazmaz.
/// </summary>
public static class HeifRecoveryService
{
    private const int MaxMetaInspectBytes = 4 * 1024 * 1024;

    public static HeifStructureResult Analyze(
        RawDeviceReader reader,
        long start,
        long length,
        CancellationToken cancellationToken)
    {
        if (length < 24)
            return Invalid("HEIF container cok kisa");

        Span<byte> header = stackalloc byte[16];
        if (!reader.ReadExact(start, header[..12]) || !header.Slice(4, 4).SequenceEqual("ftyp"u8))
            return Invalid("ftyp bulunamadi");

        if (!TryReadBoxHeader(reader, start, start + length, out long ftypSize, out int ftypHeader, out string ftypType) ||
            ftypType != "ftyp" || ftypSize < ftypHeader + 8)
            return Invalid("ftyp yapisi gecersiz");

        int ftypReadLength = (int)Math.Min(ftypSize, 4096);
        byte[] ftyp = new byte[ftypReadLength];
        if (reader.ReadBestEffort(start, ftyp, out long ftypUnreadable) < Math.Min(16, ftypReadLength) ||
            ftypUnreadable >= ftypReadLength / 2)
            return Invalid("ftyp okunamadi");

        string extension = ClassifyBrands(ftyp);
        if (extension is not ("HEIC" or "HEIF" or "AVIF"))
            return Invalid("HEIF/AVIF brand bulunamadi", extension);

        bool seenMeta = false;
        bool seenMdat = false;
        MetaEvidence meta = default;
        int topLevelBoxes = 0;
        long position = start;
        long end = start + length;

        while (position + 8 <= end && topLevelBoxes < 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadBoxHeader(reader, position, end, out long boxSize, out int headerSize, out string boxType))
                break;

            if (boxSize <= 0 || position + boxSize > end)
                break;

            topLevelBoxes++;
            if (boxType == "meta")
            {
                seenMeta = true;
                meta = AnalyzeMeta(reader, position, boxSize, headerSize, start, length, cancellationToken);
            }
            else if (boxType == "mdat")
            {
                seenMdat = boxSize > headerSize;
            }

            position += boxSize;
        }

        bool hasPayload = seenMdat || meta.SeenIdat;
        int coreMetadata = (meta.SeenHandler ? 1 : 0) + (meta.SeenPrimaryItem ? 1 : 0) +
                           (meta.SeenItemLocation ? 1 : 0) + (meta.SeenItemInfo ? 1 : 0) +
                           (meta.SeenItemProperties ? 1 : 0);
        bool valid = seenMeta && hasPayload && coreMetadata >= 2 && meta.StructurallyValid;
        if (!valid)
        {
            string missing = !seenMeta ? "meta yok" : !hasPayload ? "mdat/idat yok" : "HEIF item metadata yetersiz";
            return Invalid(missing, extension);
        }

        int score = 55;
        score += Math.Min(25, coreMetadata * 5);
        if (meta.IlocExtentsChecked > 0)
            score += meta.IlocExtentsInvalid == 0 ? 12 : -Math.Min(20, meta.IlocExtentsInvalid * 5);
        if (seenMdat) score += 5;
        score = Math.Clamp(score, 0, 100);

        string state = score >= 88 ? "Çok İyi" : score >= 70 ? "İyi" : "Kısmi";
        string summary = $"HEIF meta dogrulandi • item metadata {coreMetadata}/5 • " +
                         $"iloc extent {meta.IlocExtentsChecked - meta.IlocExtentsInvalid:N0}/{meta.IlocExtentsChecked:N0} • " +
                         (seenMdat ? "mdat" : "idat");
        return new HeifStructureResult(true, extension, state, score, summary);
    }

    public static string DetectExtensionFromFtyp(ReadOnlySpan<byte> ftyp) => ClassifyBrands(ftyp);

    public static bool LooksLikeHeader(ReadOnlySpan<byte> data, string expectedExtension)
    {
        if (data.Length < 16 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
            return false;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (size < 16 || size > data.Length)
            size = (uint)data.Length;
        string extension = ClassifyBrands(data[..(int)Math.Min(size, (uint)data.Length)]);
        string expected = expectedExtension.Trim().TrimStart('.').ToUpperInvariant();
        return expected switch
        {
            "HEIC" => extension == "HEIC",
            "HEIF" => extension is "HEIF" or "HEIC",
            "AVIF" => extension == "AVIF",
            _ => extension is "HEIC" or "HEIF" or "AVIF"
        };
    }

    internal static HeifStructureResult AnalyzeBufferForRegression(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24 || !data.Slice(4, 4).SequenceEqual("ftyp"u8))
            return Invalid("ftyp bulunamadi");
        uint ftypSize = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (ftypSize < 16 || ftypSize > data.Length)
            return Invalid("ftyp boyutu gecersiz");
        string extension = ClassifyBrands(data[..(int)ftypSize]);
        bool meta = false, mdat = false, idat = false;
        int evidence = 0;
        int pos = (int)ftypSize;
        while (pos + 8 <= data.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(pos, 4));
            if (size < 8 || pos + size > data.Length) break;
            ReadOnlySpan<byte> type = data.Slice(pos + 4, 4);
            if (type.SequenceEqual("mdat"u8)) mdat = true;
            if (type.SequenceEqual("meta"u8))
            {
                meta = true;
                int child = pos + 12; // FullBox flags
                int end = pos + (int)size;
                while (child + 8 <= end)
                {
                    uint childSize = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(child, 4));
                    if (childSize < 8 || child + childSize > end) break;
                    ReadOnlySpan<byte> childType = data.Slice(child + 4, 4);
                    if (childType.SequenceEqual("hdlr"u8) || childType.SequenceEqual("pitm"u8) ||
                        childType.SequenceEqual("iloc"u8) || childType.SequenceEqual("iinf"u8) ||
                        childType.SequenceEqual("iprp"u8)) evidence++;
                    if (childType.SequenceEqual("idat"u8)) idat = true;
                    child += (int)childSize;
                }
            }
            pos += (int)size;
        }
        bool valid = extension is "HEIC" or "HEIF" or "AVIF" && meta && (mdat || idat) && evidence >= 2;
        return new HeifStructureResult(valid, extension, valid ? "İyi" : "Zayıf", valid ? 80 : 20,
            $"Regression HEIF • meta {meta} • payload {mdat || idat} • evidence {evidence}");
    }

    private static MetaEvidence AnalyzeMeta(
        RawDeviceReader reader,
        long boxStart,
        long boxSize,
        int headerSize,
        long fileStart,
        long fileLength,
        CancellationToken cancellationToken)
    {
        var result = new MetaEvidence { StructurallyValid = true };
        long metaEnd = boxStart + boxSize;
        long position = boxStart + headerSize + 4; // meta is a FullBox
        if (position > metaEnd)
            return result with { StructurallyValid = false };

        int children = 0;
        while (position + 8 <= metaEnd && children < 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadBoxHeader(reader, position, metaEnd, out long childSize, out int childHeader, out string type) ||
                childSize <= 0 || position + childSize > metaEnd)
            {
                result = result with { StructurallyValid = false };
                break;
            }

            children++;
            result = type switch
            {
                "hdlr" => result with { SeenHandler = true },
                "pitm" => result with { SeenPrimaryItem = true },
                "iinf" => result with { SeenItemInfo = true },
                "iprp" => result with { SeenItemProperties = true },
                "idat" => result with { SeenIdat = true },
                _ => result
            };

            if (type == "iloc")
            {
                result = result with { SeenItemLocation = true };
                int inspect = (int)Math.Min(childSize, MaxMetaInspectBytes);
                byte[] data = new byte[inspect];
                int read = reader.ReadBestEffort(position, data, out long unreadable);
                if (read >= Math.Min(inspect, childHeader + 8) && unreadable < inspect / 2)
                {
                    (int checkedExtents, int invalidExtents, bool parsed) = ParseIloc(
                        data.AsSpan(0, read), childHeader, fileLength);
                    bool completeIloc = childSize <= MaxMetaInspectBytes;
                    result = result with
                    {
                        IlocExtentsChecked = result.IlocExtentsChecked + checkedExtents,
                        IlocExtentsInvalid = result.IlocExtentsInvalid + invalidExtents,
                        // Multi-megabyte iloc tables are uncommon but valid. A deliberately
                        // bounded inspection must not turn a truncated inspection window
                        // into a false negative. Full boxes, however, must parse cleanly.
                        StructurallyValid = result.StructurallyValid && (!completeIloc || parsed)
                    };
                }
            }

            position += childSize;
        }

        return result;
    }

    private static (int Checked, int Invalid, bool Parsed) ParseIloc(ReadOnlySpan<byte> box, int headerSize, long fileLength)
    {
        int p = headerSize;
        if (p + 8 > box.Length) return (0, 0, false);
        byte version = box[p];
        p += 4; // version + flags
        byte sizes1 = box[p++];
        byte sizes2 = box[p++];
        int offsetSize = sizes1 >> 4;
        int lengthSize = sizes1 & 0x0F;
        int baseOffsetSize = sizes2 >> 4;
        int indexSize = version is 1 or 2 ? sizes2 & 0x0F : 0;
        if (offsetSize > 8 || lengthSize > 8 || baseOffsetSize > 8 || indexSize > 8)
            return (0, 0, false);

        ulong itemCount;
        if (version < 2)
        {
            if (p + 2 > box.Length) return (0, 0, false);
            itemCount = BinaryPrimitives.ReadUInt16BigEndian(box.Slice(p, 2)); p += 2;
        }
        else
        {
            if (p + 4 > box.Length) return (0, 0, false);
            itemCount = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(p, 4)); p += 4;
        }
        if (itemCount > 100_000) return (0, 0, false);

        int checkedExtents = 0;
        int invalidExtents = 0;
        for (ulong item = 0; item < itemCount; item++)
        {
            int idBytes = version < 2 ? 2 : 4;
            if (p + idBytes > box.Length) return (checkedExtents, invalidExtents, false);
            p += idBytes;

            ushort constructionMethod = 0;
            if (version is 1 or 2)
            {
                if (p + 2 > box.Length) return (checkedExtents, invalidExtents, false);
                constructionMethod = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(box.Slice(p, 2)) & 0x000F);
                p += 2;
            }
            if (p + 2 > box.Length) return (checkedExtents, invalidExtents, false);
            p += 2; // data_reference_index
            if (!TryReadUIntN(box, ref p, baseOffsetSize, out ulong baseOffset))
                return (checkedExtents, invalidExtents, false);
            if (p + 2 > box.Length) return (checkedExtents, invalidExtents, false);
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(box.Slice(p, 2)); p += 2;
            if (extentCount > 16_384) return (checkedExtents, invalidExtents, false);

            for (int e = 0; e < extentCount; e++)
            {
                if (indexSize > 0 && !TryReadUIntN(box, ref p, indexSize, out _))
                    return (checkedExtents, invalidExtents, false);
                if (!TryReadUIntN(box, ref p, offsetSize, out ulong extentOffset) ||
                    !TryReadUIntN(box, ref p, lengthSize, out ulong extentLength))
                    return (checkedExtents, invalidExtents, false);

                checkedExtents++;
                // construction_method 0 uses file offsets. idat/item-offset methods are
                // internally relative and are structurally checked but not falsely rejected.
                if (constructionMethod == 0 && extentLength > 0)
                {
                    try
                    {
                        ulong end = checked(baseOffset + extentOffset + extentLength);
                        if (end > (ulong)fileLength) invalidExtents++;
                    }
                    catch (OverflowException)
                    {
                        invalidExtents++;
                    }
                }
            }
        }
        return (checkedExtents, invalidExtents, true);
    }

    private static bool TryReadUIntN(ReadOnlySpan<byte> data, ref int offset, int width, out ulong value)
    {
        value = 0;
        if (width == 0) return true;
        if (width < 0 || width > 8 || offset + width > data.Length) return false;
        for (int i = 0; i < width; i++) value = (value << 8) | data[offset + i];
        offset += width;
        return true;
    }

    private static bool TryReadBoxHeader(
        RawDeviceReader reader,
        long position,
        long end,
        out long boxSize,
        out int headerSize,
        out string boxType)
    {
        boxSize = 0; headerSize = 0; boxType = string.Empty;
        if (position < 0 || position + 8 > end) return false;
        Span<byte> header = stackalloc byte[16];
        if (!reader.ReadExact(position, header[..8])) return false;
        ulong size = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        boxType = Encoding.ASCII.GetString(header.Slice(4, 4));
        headerSize = 8;
        if (size == 1)
        {
            if (position + 16 > end || !reader.ReadExact(position, header)) return false;
            size = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = (ulong)(end - position);
        }
        if (size < (ulong)headerSize || size > long.MaxValue) return false;
        boxSize = (long)size;
        return true;
    }

    private static string ClassifyBrands(ReadOnlySpan<byte> ftyp)
    {
        if (ftyp.Length < 16 || !ftyp.Slice(4, 4).SequenceEqual("ftyp"u8)) return "MP4";
        var brands = new List<string>();
        brands.Add(Encoding.ASCII.GetString(ftyp.Slice(8, 4)).TrimEnd('\0', ' '));
        for (int p = 16; p + 4 <= ftyp.Length; p += 4)
            brands.Add(Encoding.ASCII.GetString(ftyp.Slice(p, 4)).TrimEnd('\0', ' '));

        if (brands.Any(b => b is "avif" or "avis")) return "AVIF";
        if (brands.Any(b => b is "heic" or "heix" or "hevc" or "hevx")) return "HEIC";
        if (brands.Any(b => b is "mif1" or "msf1")) return "HEIF";
        return "MP4";
    }

    private static HeifStructureResult Invalid(string reason, string extension = "HEIF") =>
        new(false, extension, "Zayıf", 0, reason);

    private readonly record struct MetaEvidence(
        bool SeenHandler = false,
        bool SeenPrimaryItem = false,
        bool SeenItemLocation = false,
        bool SeenItemInfo = false,
        bool SeenItemProperties = false,
        bool SeenIdat = false,
        bool StructurallyValid = false,
        int IlocExtentsChecked = 0,
        int IlocExtentsInvalid = 0);
}
