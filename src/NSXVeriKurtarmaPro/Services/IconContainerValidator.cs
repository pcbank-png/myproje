using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Strict ICO/CUR directory and embedded image validation. A four-byte ICO magic is very
/// common in arbitrary binary data, so raw recovery must never trust it without validating
/// the directory geometry and the PNG/DIB payloads referenced by every entry.
/// </summary>
internal static class IconContainerValidator
{
    internal const int MaxEntries = 256;
    internal const long MaxContainerLength = 256L * 1024 * 1024;
    private const uint MaxResourceLength = 64U * 1024 * 1024;

    private readonly record struct Entry(
        int Width,
        int Height,
        uint ResourceLength,
        uint ResourceOffset);

    internal static bool LooksLikePrefix(ReadOnlySpan<byte> data, bool cursor)
    {
        if (!TryParseDirectory(data, data.Length, cursor, out List<Entry> entries, out _))
            return false;

        // When the prefix already contains a referenced payload, require it to be a real
        // PNG or DIB image. Otherwise the full analyzer performs the payload validation.
        // Do not capture ReadOnlySpan<byte> in a LINQ lambda: ref-like values cannot escape
        // into anonymous methods and doing so breaks the Windows build (CS9108).
        foreach (Entry entry in entries)
        {
            if (entry.ResourceOffset >= data.Length)
                continue;

            int available = (int)Math.Min(entry.ResourceLength, (uint)(data.Length - (int)entry.ResourceOffset));
            if (available < 12)
                continue;

            if (!LooksLikeEmbeddedImage(data.Slice((int)entry.ResourceOffset, available), entry.Width, entry.Height))
                return false;
        }

        return entries.Count > 0;
    }

    internal static bool TryAnalyzeBuffer(
        ReadOnlySpan<byte> data,
        out string extension,
        out long containerLength)
    {
        extension = string.Empty;
        containerLength = 0;
        if (data.Length < 6)
            return false;

        bool cursor = data[2] == 2;
        if (!TryParseDirectory(data, data.Length, cursor, out List<Entry> entries, out long length))
            return false;

        foreach (Entry entry in entries)
        {
            if (entry.ResourceOffset > int.MaxValue || entry.ResourceLength > int.MaxValue)
                return false;

            int offset = (int)entry.ResourceOffset;
            int resourceLength = (int)entry.ResourceLength;
            if (offset < 0 || resourceLength <= 0 || offset > data.Length - resourceLength)
                return false;

            if (!LooksLikeEmbeddedImage(data.Slice(offset, resourceLength), entry.Width, entry.Height))
                return false;
        }

        extension = cursor ? "CUR" : "ICO";
        containerLength = length;
        return true;
    }

    internal static bool TryAnalyze(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        out string extension,
        out long containerLength)
    {
        extension = string.Empty;
        containerLength = 0;
        if (start < 0 || volumeLength <= start || volumeLength - start < 6)
            return false;

        Span<byte> header = stackalloc byte[6];
        if (!reader.ReadExact(start, header) ||
            header[0] != 0 || header[1] != 0 || header[3] != 0 ||
            header[2] is not (1 or 2))
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        if (count is <= 0 or > MaxEntries)
            return false;

        int directoryLength = checked(6 + count * 16);
        if (directoryLength > volumeLength - start)
            return false;

        byte[] directory = new byte[directoryLength];
        if (!reader.ReadExact(start, directory))
            return false;

        bool cursor = header[2] == 2;
        if (!TryParseDirectory(
                directory,
                volumeLength - start,
                cursor,
                out List<Entry> entries,
                out long length))
        {
            return false;
        }

        foreach (Entry entry in entries)
        {
            int inspectLength = checked((int)Math.Min(entry.ResourceLength, 128U));
            byte[] payload = reader.ReadBytes(start + entry.ResourceOffset, inspectLength);
            if (payload.Length < Math.Min(12, inspectLength) ||
                !LooksLikeEmbeddedImage(payload, entry.Width, entry.Height))
            {
                return false;
            }
        }

        extension = cursor ? "CUR" : "ICO";
        containerLength = length;
        return true;
    }

    private static bool TryParseDirectory(
        ReadOnlySpan<byte> data,
        long availableLength,
        bool cursor,
        out List<Entry> entries,
        out long containerLength)
    {
        entries = [];
        containerLength = 0;
        if (data.Length < 6 || availableLength < 6 ||
            data[0] != 0 || data[1] != 0 || data[3] != 0 ||
            data[2] != (cursor ? (byte)2 : (byte)1))
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        if (count is <= 0 or > MaxEntries)
            return false;

        int directoryLength = checked(6 + count * 16);
        if (data.Length < directoryLength || availableLength < directoryLength)
            return false;

        long maxEnd = directoryLength;
        var ranges = new List<(long Start, long End)>(count);
        for (int index = 0; index < count; index++)
        {
            ReadOnlySpan<byte> entry = data.Slice(6 + index * 16, 16);
            int width = entry[0] == 0 ? 256 : entry[0];
            int height = entry[1] == 0 ? 256 : entry[1];
            if (width <= 0 || height <= 0 || entry[3] != 0)
                return false;

            ushort planesOrHotspotX = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(4, 2));
            ushort bitCountOrHotspotY = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(6, 2));
            if (cursor)
            {
                if (planesOrHotspotX >= width || bitCountOrHotspotY >= height)
                    return false;
            }
            else
            {
                if (planesOrHotspotX is not (0 or 1))
                    return false;
                if (bitCountOrHotspotY is not (0 or 1 or 2 or 4 or 8 or 16 or 24 or 32))
                    return false;
            }

            uint resourceLength = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            uint resourceOffset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            if (resourceLength < 12 || resourceLength > MaxResourceLength ||
                resourceOffset < directoryLength)
            {
                return false;
            }

            long end = (long)resourceOffset + resourceLength;
            if (end <= resourceOffset || end > availableLength || end > MaxContainerLength)
                return false;

            ranges.Add((resourceOffset, end));
            entries.Add(new Entry(width, height, resourceLength, resourceOffset));
            maxEnd = Math.Max(maxEnd, end);
        }

        // Shared duplicate resources are legal. Partial overlaps are not.
        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        for (int index = 1; index < ranges.Count; index++)
        {
            (long previousStart, long previousEnd) = ranges[index - 1];
            (long currentStart, long currentEnd) = ranges[index];
            if (currentStart < previousEnd &&
                (currentStart != previousStart || currentEnd != previousEnd))
            {
                return false;
            }
        }

        containerLength = maxEnd;
        return containerLength > directoryLength && containerLength <= MaxContainerLength;
    }

    private static bool LooksLikeEmbeddedImage(ReadOnlySpan<byte> data, int directoryWidth, int directoryHeight)
    {
        if (data.Length >= 24 &&
            data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            uint ihdrLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
            if (ihdrLength != 13 || !data.Slice(12, 4).SequenceEqual("IHDR"u8))
                return false;

            uint width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            uint height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            return width is > 0 and <= 16384 && height is > 0 and <= 16384 &&
                   DimensionsAreCompatible(directoryWidth, directoryHeight, width, height);
        }

        if (data.Length < 12)
            return false;

        uint dibSize = BinaryPrimitives.ReadUInt32LittleEndian(data[..4]);
        if (dibSize == 12)
        {
            if (data.Length < 12)
                return false;
            uint width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
            uint doubledHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
            ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
            ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(10, 2));
            uint height = Math.Max(1U, doubledHeight / 2U);
            return planes == 1 && IsValidBitDepth(bitCount) &&
                   width is > 0 and <= 16384 && doubledHeight is > 0 and <= 32768 &&
                   DimensionsAreCompatible(directoryWidth, directoryHeight, width, height);
        }

        if (dibSize is not (40 or 52 or 56 or 64 or 108 or 124) || data.Length < 16)
            return false;

        int widthSigned = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4, 4));
        int doubledHeightSigned = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(8, 4));
        ushort dibPlanes = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
        ushort dibBitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
        long widthAbs = Math.Abs((long)widthSigned);
        long doubledHeightAbs = Math.Abs((long)doubledHeightSigned);
        uint actualHeight = (uint)Math.Max(1L, doubledHeightAbs / 2L);

        return dibPlanes == 1 && IsValidBitDepth(dibBitCount) &&
               widthAbs is > 0 and <= 16384 && doubledHeightAbs is > 0 and <= 32768 &&
               DimensionsAreCompatible(directoryWidth, directoryHeight, (uint)widthAbs, actualHeight);
    }

    private static bool DimensionsAreCompatible(int directoryWidth, int directoryHeight, uint width, uint height)
    {
        // Some encoders leave the directory dimension at zero (256) while storing a higher
        // resolution PNG. Reject grossly unrelated dimensions but tolerate legitimate variants.
        return width <= Math.Max(4096, directoryWidth * 16) &&
               height <= Math.Max(4096, directoryHeight * 16);
    }

    private static bool IsValidBitDepth(ushort bitCount) =>
        bitCount is 1 or 2 or 4 or 8 or 16 or 24 or 32;
}
