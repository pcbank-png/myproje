using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// SQLite ana veritabanı, WAL ve rollback journal yapılarını sayfa/frame seviyesinde
/// doğrular. Bu motor yalnız okur; kaynak DB üzerinde checkpoint/repair/write çalıştırmaz.
/// </summary>
public static class SqliteRecoveryService
{
    private static readonly byte[] DatabaseMagic = "SQLite format 3\0"u8.ToArray();
    private static readonly byte[] JournalMagic = [0xD9, 0xD5, 0x05, 0xF9, 0x20, 0xA1, 0x63, 0xD7];
    private const long MaxDatabaseBytes = 128L * 1024 * 1024 * 1024;
    private const int MaxFramesToInspect = 1_000_000;

    public static RawFileAnalysis? AnalyzeDatabase(RawDeviceReader reader, long start, long volumeLength)
    {
        Span<byte> header = stackalloc byte[100];
        if (!reader.ReadExact(start, header) || !TryParseDatabaseHeader(header, volumeLength - start, out DatabaseHeader info))
            return null;

        long length;
        string state;
        if (info.PageCount > 0)
        {
            try { length = checked((long)info.PageCount * info.PageSize); }
            catch (OverflowException) { return null; }
            if (length <= 0 || length > volumeLength - start || length > MaxDatabaseBytes)
                return null;
            state = "İyi";
        }
        else
        {
            length = info.PageSize;
            state = "Kısmi";
        }

        int firstPageRead = (int)Math.Min(info.PageSize, 64 * 1024);
        byte[] firstPage = new byte[firstPageRead];
        int read = reader.ReadBestEffort(start, firstPage, out long unreadable);
        bool page1 = read >= Math.Min(108, firstPageRead) && unreadable < read / 2 &&
                     LooksLikePageOne(firstPage.AsSpan(0, read), info.PageSize);
        if (!page1 && state == "Kısmi")
            return null;
        if (page1 && info.PageCount > 0)
            state = "Çok İyi";

        return new RawFileAnalysis("SQLITE", length, state);
    }

    public static RawFileAnalysis? AnalyzeWal(RawDeviceReader reader, long start, long volumeLength, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[32];
        if (!reader.ReadExact(start, header) || !TryParseWalHeader(header, out int pageSize, out uint salt1, out uint salt2))
            return null;

        long available = volumeLength - start;
        long frameSize = 24L + pageSize;
        if (available < 32 + frameSize)
            return null;

        int frames = 0;
        long position = start + 32;
        Span<byte> frameHeader = stackalloc byte[24];
        while (position + frameSize <= volumeLength && frames < MaxFramesToInspect)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.ReadExact(position, frameHeader)) break;
            uint pageNumber = BinaryPrimitives.ReadUInt32BigEndian(frameHeader[..4]);
            uint frameSalt1 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(8, 4));
            uint frameSalt2 = BinaryPrimitives.ReadUInt32BigEndian(frameHeader.Slice(12, 4));
            if (pageNumber == 0 || frameSalt1 != salt1 || frameSalt2 != salt2)
                break;
            frames++;
            position += frameSize;
        }

        if (frames == 0) return null;
        return new RawFileAnalysis("WAL", position - start, frames >= 2 ? "Çok İyi" : "İyi");
    }

    public static RawFileAnalysis? AnalyzeJournal(RawDeviceReader reader, long start, long volumeLength, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[28];
        if (!reader.ReadExact(start, header) || !TryParseJournalHeader(header, out uint recordCount, out uint dbPages, out int sectorSize, out int pageSize))
            return null;

        long available = volumeLength - start;
        if (sectorSize > available) return null;
        long recordSize = pageSize + 8L;
        long position = start + sectorSize;
        int records = 0;
        int maxRecords = recordCount == uint.MaxValue
            ? (int)Math.Min(MaxFramesToInspect, available / Math.Max(1, recordSize))
            : (int)Math.Min(recordCount, MaxFramesToInspect);
        Span<byte> pageNumberBytes = stackalloc byte[4];

        while (records < maxRecords && position + recordSize <= volumeLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.ReadExact(position, pageNumberBytes)) break;
            uint pageNumber = BinaryPrimitives.ReadUInt32BigEndian(pageNumberBytes);
            if (pageNumber == 0 || (dbPages > 0 && pageNumber > dbPages)) break;
            records++;
            position += recordSize;
        }

        if (records == 0) return null;
        if (recordCount != uint.MaxValue && records != recordCount)
            return new RawFileAnalysis("JOURNAL", position - start, "Kısmi");
        return new RawFileAnalysis("JOURNAL", position - start, records >= 2 ? "Çok İyi" : "İyi");
    }

    public static bool LooksLikeDatabaseHeader(ReadOnlySpan<byte> data, long availableLength = long.MaxValue) =>
        TryParseDatabaseHeader(data, availableLength, out _);

    public static bool LooksLikeWalHeader(ReadOnlySpan<byte> data) =>
        TryParseWalHeader(data, out _, out _, out _);

    public static bool LooksLikeJournalHeader(ReadOnlySpan<byte> data) =>
        TryParseJournalHeader(data, out _, out _, out _, out _);

    internal static bool LooksLikePageOneForRegression(ReadOnlySpan<byte> data, int pageSize) =>
        LooksLikePageOne(data, pageSize);

    private static bool TryParseDatabaseHeader(ReadOnlySpan<byte> data, long availableLength, out DatabaseHeader info)
    {
        info = default;
        if (data.Length < 100 || !data[..16].SequenceEqual(DatabaseMagic)) return false;
        int pageSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(16, 2));
        if (pageSize == 1) pageSize = 65536;
        if (!IsPowerOfTwo(pageSize) || pageSize < 512 || pageSize > 65536) return false;
        if (data[18] is not (1 or 2) || data[19] is not (1 or 2)) return false;
        if (data[20] > pageSize - 480) return false;
        if (data[21] != 64 || data[22] != 32 || data[23] != 32) return false;

        uint pageCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(28, 4));
        uint firstFreeList = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(32, 4));
        uint freeListPages = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(36, 4));
        uint schemaFormat = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(44, 4));
        uint encoding = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(56, 4));
        if (schemaFormat > 4) return false;
        if (encoding > 3) return false;
        if (pageCount > 0)
        {
            if (firstFreeList > pageCount || freeListPages > pageCount) return false;
            long declared;
            try { declared = checked((long)pageCount * pageSize); }
            catch (OverflowException) { return false; }
            if (declared > availableLength || declared > MaxDatabaseBytes) return false;
        }

        info = new DatabaseHeader(pageSize, pageCount, firstFreeList, freeListPages);
        return true;
    }

    private static bool LooksLikePageOne(ReadOnlySpan<byte> data, int pageSize)
    {
        if (data.Length < 108 || pageSize < 512) return false;
        int p = 100;
        byte type = data[p];
        if (type is not (2 or 5 or 10 or 13)) return false;
        ushort firstFree = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 1, 2));
        ushort cellCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 3, 2));
        int cellContent = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(p + 5, 2));
        if (cellContent == 0 && pageSize == 65536) cellContent = 65536;
        int headerSize = type is 2 or 5 ? 12 : 8;
        if (firstFree != 0 && (firstFree < 100 + headerSize || firstFree >= pageSize)) return false;
        if (cellContent < 100 + headerSize || cellContent > pageSize) return false;
        int pointerBytes = cellCount * 2;
        return 100 + headerSize + pointerBytes <= pageSize;
    }

    private static bool TryParseWalHeader(ReadOnlySpan<byte> data, out int pageSize, out uint salt1, out uint salt2)
    {
        pageSize = 0; salt1 = salt2 = 0;
        if (data.Length < 32) return false;
        uint magic = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (magic is not (0x377F0682u or 0x377F0683u)) return false;
        uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        if (size == 1) size = 65536;
        if (size > int.MaxValue || !IsPowerOfTwo((int)size) || size < 512 || size > 65536) return false;
        pageSize = (int)size;
        salt1 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        salt2 = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        return true;
    }

    private static bool TryParseJournalHeader(
        ReadOnlySpan<byte> data,
        out uint recordCount,
        out uint dbPages,
        out int sectorSize,
        out int pageSize)
    {
        recordCount = dbPages = 0; sectorSize = pageSize = 0;
        if (data.Length < 28 || !data[..8].SequenceEqual(JournalMagic)) return false;
        recordCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
        dbPages = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        uint sector = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        uint page = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(24, 4));
        if (sector > int.MaxValue || page > int.MaxValue) return false;
        sectorSize = (int)sector;
        pageSize = (int)page;
        if (!IsPowerOfTwo(sectorSize) || sectorSize < 512 || sectorSize > 65536) return false;
        if (!IsPowerOfTwo(pageSize) || pageSize < 512 || pageSize > 65536) return false;
        return true;
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private readonly record struct DatabaseHeader(int PageSize, uint PageCount, uint FirstFreelistTrunk, uint FreelistPages);
}
