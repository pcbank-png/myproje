using System.Buffers.Binary;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

public sealed record ZipReconstructionResult(
    long SourceLength,
    byte[] CentralDirectorySuffix,
    string Extension,
    int EntryCount,
    bool Partial);

public static class ZipOfficeReconstructionService
{
    private const int MaxEntries = 100_000;
    private const int MaxCentralDirectoryBytes = 64 * 1024 * 1024;
    private const long MaxDescriptorSearchBytes = 2L * 1024 * 1024 * 1024;

    private sealed record LocalEntry(
        ushort VersionNeeded,
        ushort Flags,
        ushort Method,
        ushort ModTime,
        ushort ModDate,
        uint Crc32,
        uint CompressedSize,
        uint UncompressedSize,
        byte[] Name,
        uint LocalOffset);

    public static ZipReconstructionResult? TryReconstruct(
        RawDeviceReader reader,
        long start,
        long maxEnd,
        CancellationToken cancellationToken)
    {
        if (start < 0 || maxEnd <= start + 30)
            return null;

        var entries = new List<LocalEntry>();
        long position = start;
        bool partial = false;
        Span<byte> header = stackalloc byte[30];
        Span<byte> next = stackalloc byte[4];

        while (position + 30 <= maxEnd && entries.Count < MaxEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.ReadExact(position, header) || !IsLocalHeader(header))
                break;

            ushort versionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
            ushort method = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
            ushort modTime = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(10, 2));
            ushort modDate = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(12, 2));
            uint crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(14, 4));
            uint compressed32 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(18, 4));
            uint uncompressed32 = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(22, 4));
            ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2));
            ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));

            if (versionNeeded > 63 || nameLength == 0 || nameLength > 4096)
                break;

            long variableEnd = position + 30L + nameLength + extraLength;
            if (variableEnd > maxEnd)
                break;

            byte[] name = reader.ReadBytes(position + 30, nameLength);
            if (name.Length != nameLength || !LooksLikeSafeZipName(name))
                break;

            byte[] extra = extraLength > 0
                ? reader.ReadBytes(position + 30L + nameLength, extraLength)
                : [];
            if (extra.Length != extraLength)
                break;

            ulong compressed = compressed32;
            ulong uncompressed = uncompressed32;
            if (compressed32 == uint.MaxValue || uncompressed32 == uint.MaxValue)
            {
                bool needCompressed = compressed32 == uint.MaxValue;
                bool needUncompressed = uncompressed32 == uint.MaxValue;
                if (!TryReadZip64Sizes(extra, needCompressed, needUncompressed,
                        out ulong zip64Compressed, out ulong zip64Uncompressed))
                {
                    break;
                }
                compressed = needCompressed ? zip64Compressed : compressed32;
                uncompressed = needUncompressed ? zip64Uncompressed : uncompressed32;
            }

            long dataStart = variableEnd;
            long entryEnd;
            uint finalCrc = crc32;
            ulong finalCompressed = compressed;
            ulong finalUncompressed = uncompressed;

            if ((flags & 0x0008) == 0)
            {
                if (finalCompressed > (ulong)(maxEnd - dataStart))
                    break;
                entryEnd = dataStart + (long)finalCompressed;
            }
            else
            {
                if (!TryResolveDataDescriptor(
                        reader,
                        dataStart,
                        Math.Min(maxEnd, dataStart + MaxDescriptorSearchBytes),
                        cancellationToken,
                        out long descriptorEnd,
                        out finalCrc,
                        out finalCompressed,
                        out finalUncompressed))
                {
                    partial = entries.Count > 0;
                    break;
                }
                entryEnd = descriptorEnd;
            }

            long localOffsetLong = position - start;
            if (localOffsetLong < 0 || localOffsetLong > uint.MaxValue ||
                finalCompressed > uint.MaxValue || finalUncompressed > uint.MaxValue)
            {
                partial = true;
                break;
            }

            entries.Add(new LocalEntry(
                versionNeeded,
                flags,
                method,
                modTime,
                modDate,
                finalCrc,
                (uint)finalCompressed,
                (uint)finalUncompressed,
                name,
                (uint)localOffsetLong));

            position = entryEnd;
            if (position + 4 > maxEnd)
                break;

            if (!reader.ReadExact(position, next) || !IsLocalHeader(next))
                break;
        }

        if (entries.Count == 0 || entries.Count > ushort.MaxValue)
            return null;

        long sourceLength = position - start;
        if (sourceLength <= 0 || sourceLength > uint.MaxValue)
            return null;

        byte[] suffix = BuildCentralDirectory(entries, checked((uint)sourceLength));
        if (suffix.Length == 0 || suffix.Length > MaxCentralDirectoryBytes)
            return null;

        string extension = DetectOfficeExtension(entries);
        return new ZipReconstructionResult(sourceLength, suffix, extension, entries.Count, partial);
    }

    private static bool TryResolveDataDescriptor(
        RawDeviceReader reader,
        long dataStart,
        long searchEnd,
        CancellationToken cancellationToken,
        out long descriptorEnd,
        out uint crc32,
        out ulong compressedSize,
        out ulong uncompressedSize)
    {
        descriptorEnd = 0;
        crc32 = 0;
        compressedSize = 0;
        uncompressedSize = 0;

        const int blockSize = 1024 * 1024;
        byte[] block = new byte[blockSize + 3];
        int carry = 0;
        long offset = dataStart;
        ReadOnlySpan<byte> localSignature = [(byte)'P', (byte)'K', 0x03, 0x04];

        while (offset < searchEnd)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int request = (int)Math.Min(blockSize, searchEnd - offset);
            int read = reader.ReadBestEffort(offset, block.AsSpan(carry, request), out _);
            if (read <= 0)
                break;

            int count = carry + read;
            int search = 0;
            while (search <= count - 4)
            {
                int relative = block.AsSpan(search, count - search).IndexOf(localSignature);
                if (relative < 0)
                    break;
                int match = search + relative;
                long nextLocal = offset - carry + match;
                if (nextLocal > dataStart && LooksLikeLocalHeaderAt(reader, nextLocal))
                {
                    if (TryReadDescriptorBefore(reader, dataStart, nextLocal, out long descriptorStart,
                            out crc32, out compressedSize, out uncompressedSize))
                    {
                        descriptorEnd = nextLocal;
                        return descriptorStart >= dataStart;
                    }
                }
                search = match + 1;
            }

            carry = Math.Min(3, count);
            if (carry > 0)
                block.AsSpan(count - carry, carry).CopyTo(block);
            offset += read;
        }

        return false;
    }

    private static bool TryReadDescriptorBefore(
        RawDeviceReader reader,
        long dataStart,
        long nextLocal,
        out long descriptorStart,
        out uint crc32,
        out ulong compressedSize,
        out ulong uncompressedSize)
    {
        descriptorStart = 0;
        crc32 = 0;
        compressedSize = 0;
        uncompressedSize = 0;

        if (nextLocal - dataStart >= 16)
        {
            Span<byte> signed = stackalloc byte[16];
            if (reader.ReadExact(nextLocal - 16, signed) &&
                signed[0] == (byte)'P' && signed[1] == (byte)'K' && signed[2] == 0x07 && signed[3] == 0x08)
            {
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(signed.Slice(8, 4));
                if (compressed == nextLocal - 16 - dataStart)
                {
                    descriptorStart = nextLocal - 16;
                    crc32 = BinaryPrimitives.ReadUInt32LittleEndian(signed.Slice(4, 4));
                    compressedSize = compressed;
                    uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(signed.Slice(12, 4));
                    return true;
                }
            }
        }

        if (nextLocal - dataStart >= 12)
        {
            Span<byte> plain = stackalloc byte[12];
            if (reader.ReadExact(nextLocal - 12, plain))
            {
                uint compressed = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(4, 4));
                if (compressed == nextLocal - 12 - dataStart)
                {
                    descriptorStart = nextLocal - 12;
                    crc32 = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(0, 4));
                    compressedSize = compressed;
                    uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(plain.Slice(8, 4));
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadZip64Sizes(
        ReadOnlySpan<byte> extra,
        bool needCompressed,
        bool needUncompressed,
        out ulong compressed,
        out ulong uncompressed)
    {
        compressed = 0;
        uncompressed = 0;
        int position = 0;
        while (position + 4 <= extra.Length)
        {
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(position, 2));
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(extra.Slice(position + 2, 2));
            position += 4;
            if (position + length > extra.Length)
                return false;
            if (id != 0x0001)
            {
                position += length;
                continue;
            }

            ReadOnlySpan<byte> payload = extra.Slice(position, length);
            int cursor = 0;
            if (needUncompressed)
            {
                if (cursor + 8 > payload.Length) return false;
                uncompressed = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor, 8));
                cursor += 8;
            }
            if (needCompressed)
            {
                if (cursor + 8 > payload.Length) return false;
                compressed = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(cursor, 8));
            }
            return true;
        }
        return false;
    }

    private static byte[] BuildCentralDirectory(IReadOnlyList<LocalEntry> entries, uint centralOffset)
    {
        using var memory = new MemoryStream();
        Span<byte> central = stackalloc byte[46];
        foreach (LocalEntry entry in entries)
        {
            central.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(0, 4), 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(4, 2), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(6, 2), entry.VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(8, 2), entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(10, 2), entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(12, 2), entry.ModTime);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(14, 2), entry.ModDate);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(16, 4), entry.Crc32);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(20, 4), entry.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(24, 4), entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(central.Slice(28, 2), checked((ushort)entry.Name.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(central.Slice(42, 4), entry.LocalOffset);
            memory.Write(central);
            memory.Write(entry.Name);
            if (memory.Length > MaxCentralDirectoryBytes)
                return [];
        }

        long centralSizeLong = memory.Length;
        if (centralSizeLong > uint.MaxValue)
            return [];

        Span<byte> eocd = stackalloc byte[22];
        eocd.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(0, 4), 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(8, 2), checked((ushort)entries.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.Slice(10, 2), checked((ushort)entries.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(12, 4), checked((uint)centralSizeLong));
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.Slice(16, 4), centralOffset);
        memory.Write(eocd);
        return memory.ToArray();
    }

    private static string DetectOfficeExtension(IReadOnlyList<LocalEntry> entries)
    {
        bool contentTypes = entries.Any(entry => GetName(entry).Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
        if (entries.Any(entry => GetName(entry).StartsWith("word/", StringComparison.OrdinalIgnoreCase)))
            return contentTypes ? "DOCX" : "ZIP";
        if (entries.Any(entry => GetName(entry).StartsWith("xl/", StringComparison.OrdinalIgnoreCase)))
            return contentTypes ? "XLSX" : "ZIP";
        if (entries.Any(entry => GetName(entry).StartsWith("ppt/", StringComparison.OrdinalIgnoreCase)))
            return contentTypes ? "PPTX" : "ZIP";
        return "ZIP";
    }

    internal static (byte[] Suffix, string Extension) BuildCentralDirectoryForRegression(params string[] names)
    {
        var entries = new List<LocalEntry>();
        uint offset = 0;
        foreach (string name in names)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            entries.Add(new LocalEntry(20, 0x0800, 8, 0, 0, 0, 0, 0, bytes, offset));
            offset += checked((uint)(30 + bytes.Length));
        }
        return (BuildCentralDirectory(entries, offset), DetectOfficeExtension(entries));
    }

    private static string GetName(LocalEntry entry)
    {
        try
        {
            return (entry.Flags & 0x0800) != 0
                ? Encoding.UTF8.GetString(entry.Name)
                : Encoding.ASCII.GetString(entry.Name);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool LooksLikeSafeZipName(ReadOnlySpan<byte> name)
    {
        if (name.Length == 0)
            return false;
        int printable = 0;
        foreach (byte value in name)
        {
            if (value == 0)
                return false;
            if (value is >= 0x20 and < 0x7F || value >= 0x80)
                printable++;
        }
        return printable >= Math.Max(1, name.Length * 3 / 4);
    }

    private static bool LooksLikeLocalHeaderAt(RawDeviceReader reader, long offset)
    {
        Span<byte> header = stackalloc byte[30];
        if (!reader.ReadExact(offset, header) || !IsLocalHeader(header))
            return false;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(4, 2));
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26, 2));
        ushort extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28, 2));
        return version <= 63 && nameLength is > 0 and <= 4096;
    }

    private static bool IsLocalHeader(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && header[0] == (byte)'P' && header[1] == (byte)'K' &&
        header[2] == 0x03 && header[3] == 0x04;
}
