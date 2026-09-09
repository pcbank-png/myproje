using System.Buffers.Binary;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// JPEG-aware raw reconstruction. It validates marker tables and scan geometry, tracks
/// restart-marker continuity, validates EXIF/TIFF headers and can safely close a JPEG whose
/// EOI marker was lost when the next sector-aligned JPEG SOI provides a trustworthy boundary.
/// </summary>
public static class JpegAdvancedReconstructionService
{
    private const int HeaderWindowBytes = 2 * 1024 * 1024;
    private const int CursorBufferBytes = 256 * 1024;
    private const long MaxJpegBytes = 512L * 1024 * 1024;
    private const long MinReconstructableBytes = 8 * 1024;

    public static RawFileAnalysis? Analyze(
        RawDeviceReader reader,
        long start,
        long volumeLength,
        CancellationToken cancellationToken)
    {
        if (start < 0 || start >= volumeLength)
            return null;

        int headerRequest = (int)Math.Min(HeaderWindowBytes, volumeLength - start);
        if (headerRequest < 16)
            return null;

        byte[] header = new byte[headerRequest];
        int headerRead = reader.ReadBestEffort(start, header, out long headerUnreadable);
        if (headerRead < 16 || header[0] != 0xFF || header[1] != 0xD8 || header[2] != 0xFF)
            return null;

        HeaderEvidence? evidence = ParseHeader(header.AsSpan(0, headerRead));
        if (evidence is null || evidence.SosOffset <= 0 || evidence.Width <= 0 || evidence.Height <= 0)
            return null;

        long maxEnd = Math.Min(volumeLength, start + MaxJpegBytes);
        long entropyStart = start + evidence.EntropyOffset;
        if (entropyStart <= start || entropyStart >= maxEnd)
            return null;

        var cursor = new RawCursor(reader, entropyStart, maxEnd);
        int restartCount = 0;
        int restartSequenceErrors = 0;
        int unexpectedMarkers = 0;
        int? expectedRestart = null;

        while (!cancellationToken.IsCancellationRequested && cursor.TryReadByte(out byte value))
        {
            if (value != 0xFF)
                continue;

            long markerStart = cursor.Position - 1;
            byte marker;
            do
            {
                if (!cursor.TryReadByte(out marker))
                    return BuildTruncatedResult(reader, start, markerStart, evidence, headerUnreadable + cursor.UnreadableBytes);
            }
            while (marker == 0xFF);

            if (marker == 0x00)
                continue;

            if (marker is >= 0xD0 and <= 0xD7)
            {
                restartCount++;
                int current = marker - 0xD0;
                if (expectedRestart.HasValue && current != expectedRestart.Value)
                    restartSequenceErrors++;
                expectedRestart = (current + 1) & 7;
                continue;
            }

            if (marker == 0xD9)
            {
                long length = cursor.Position - start;
                if (length <= 4)
                    return null;

                string state = DetermineState(evidence, headerUnreadable + cursor.UnreadableBytes, restartSequenceErrors, unexpectedMarkers);
                return new RawFileAnalysis("JPG", length, state);
            }

            if (marker == 0xD8)
            {
                // A second SOI inside entropy is only accepted as a file boundary when it is
                // sector aligned. This is common on SD cards/HDDs after the original EOI was
                // overwritten but the next photo remained intact.
                if (markerStart > start + MinReconstructableBytes && markerStart % Math.Max(512, reader.SectorSize) == 0)
                {
                    long trimmedEnd = TrimUniformSectorPadding(reader, start, markerStart, reader.SectorSize);
                    if (trimmedEnd - start >= MinReconstructableBytes)
                    {
                        return new RawFileAnalysis(
                            "JPG",
                            trimmedEnd - start,
                            "Yeniden İnşa",
                            AppendData: [0xFF, 0xD9]);
                    }
                }

                unexpectedMarkers++;
                if (unexpectedMarkers > 4)
                    return null;
                continue;
            }

            if (marker == 0x01)
                continue;

            if (!MarkerHasLength(marker))
            {
                unexpectedMarkers++;
                if (unexpectedMarkers > 4)
                    return null;
                continue;
            }

            if (!cursor.TryReadUInt16BigEndian(out ushort segmentLength) || segmentLength < 2)
                return null;

            int payload = segmentLength - 2;
            if (!cursor.TrySkip(payload))
                return BuildTruncatedResult(reader, start, cursor.Position, evidence, headerUnreadable + cursor.UnreadableBytes);

            // SOS starts another entropy-coded scan in progressive/multi-scan JPEGs.
            if (marker == 0xDA)
                expectedRestart = null;
        }

        return BuildTruncatedResult(reader, start, cursor.Position, evidence, headerUnreadable + cursor.UnreadableBytes);
    }

    private static RawFileAnalysis? BuildTruncatedResult(
        RawDeviceReader reader,
        long start,
        long currentEnd,
        HeaderEvidence evidence,
        long unreadableBytes)
    {
        if (currentEnd - start < MinReconstructableBytes)
            return null;

        // Do not fabricate an EOI merely because the scan reached the configured ceiling.
        // Reconstruction is only safe when there is a verified next-image boundary.
        return null;
    }

    internal static bool ValidateHeaderForRegression(ReadOnlySpan<byte> data) => ParseHeader(data) is not null;

    private static HeaderEvidence? ParseHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16 || data[0] != 0xFF || data[1] != 0xD8)
            return null;

        int position = 2;
        int width = 0;
        int height = 0;
        int components = 0;
        int dqtTables = 0;
        int dhtTables = 0;
        int restartInterval = 0;
        bool progressive = false;
        bool exifPresent = false;
        bool exifValid = true;
        int sosOffset = -1;
        int entropyOffset = -1;

        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            int markerStart = position;
            while (position < data.Length && data[position] == 0xFF)
                position++;
            if (position >= data.Length)
                break;

            byte marker = data[position++];
            if (marker == 0xD9)
                return null;
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;

            if (position + 2 > data.Length)
                return null;

            ushort segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(position, 2));
            if (segmentLength < 2)
                return null;

            int segmentEnd = position + segmentLength;
            if (segmentEnd > data.Length)
                return null;

            ReadOnlySpan<byte> payload = data.Slice(position + 2, segmentLength - 2);
            switch (marker)
            {
                case 0xDB:
                    if (!ValidateDqt(payload, out int qTables))
                        return null;
                    dqtTables += qTables;
                    break;

                case 0xC4:
                    if (!ValidateDht(payload, out int hTables))
                        return null;
                    dhtTables += hTables;
                    break;

                case 0xDD:
                    if (payload.Length != 2)
                        return null;
                    restartInterval = BinaryPrimitives.ReadUInt16BigEndian(payload);
                    break;

                case 0xE1:
                    if (payload.Length >= 6 && payload[..6].SequenceEqual("Exif\0\0"u8))
                    {
                        exifPresent = true;
                        exifValid &= ValidateExif(payload[6..]);
                    }
                    break;
            }

            if (IsStartOfFrame(marker))
            {
                if (payload.Length < 6)
                    return null;

                byte precision = payload[0];
                height = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2));
                components = payload[5];
                if (precision is not (8 or 12) || width <= 0 || height <= 0 || components is < 1 or > 4)
                    return null;

                int expected = 6 + components * 3;
                if (payload.Length < expected)
                    return null;

                for (int i = 0; i < components; i++)
                {
                    int componentOffset = 6 + i * 3;
                    byte sampling = payload[componentOffset + 1];
                    int horizontal = sampling >> 4;
                    int vertical = sampling & 0x0F;
                    if (horizontal is < 1 or > 4 || vertical is < 1 or > 4)
                        return null;
                }

                progressive = marker == 0xC2;
            }

            if (marker == 0xDA)
            {
                if (width <= 0 || height <= 0 || components <= 0 || payload.Length < 4)
                    return null;

                int scanComponents = payload[0];
                if (scanComponents is < 1 or > 4 || payload.Length < 1 + scanComponents * 2 + 3)
                    return null;

                sosOffset = markerStart;
                entropyOffset = segmentEnd;
                break;
            }

            position = segmentEnd;
        }

        if (sosOffset < 0 || entropyOffset < 0)
            return null;

        return new HeaderEvidence(
            width,
            height,
            components,
            dqtTables,
            dhtTables,
            restartInterval,
            progressive,
            exifPresent,
            exifValid,
            sosOffset,
            entropyOffset);
    }

    private static bool ValidateDqt(ReadOnlySpan<byte> payload, out int tableCount)
    {
        tableCount = 0;
        int position = 0;
        while (position < payload.Length)
        {
            byte info = payload[position++];
            int precision = info >> 4;
            int tableId = info & 0x0F;
            if (precision is not (0 or 1) || tableId > 3)
                return false;

            int bytes = precision == 0 ? 64 : 128;
            if (position + bytes > payload.Length)
                return false;

            bool allZero = true;
            for (int i = 0; i < bytes; i++)
            {
                if (payload[position + i] != 0)
                {
                    allZero = false;
                    break;
                }
            }
            if (allZero)
                return false;

            position += bytes;
            tableCount++;
        }

        return position == payload.Length && tableCount > 0;
    }

    private static bool ValidateDht(ReadOnlySpan<byte> payload, out int tableCount)
    {
        tableCount = 0;
        int position = 0;
        while (position < payload.Length)
        {
            if (position + 17 > payload.Length)
                return false;

            byte info = payload[position++];
            int tableClass = info >> 4;
            int tableId = info & 0x0F;
            if (tableClass > 1 || tableId > 3)
                return false;

            int symbols = 0;
            for (int i = 0; i < 16; i++)
                symbols += payload[position + i];
            position += 16;

            if (symbols <= 0 || symbols > 256 || position + symbols > payload.Length)
                return false;

            position += symbols;
            tableCount++;
        }

        return position == payload.Length && tableCount > 0;
    }

    private static bool ValidateExif(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
            return false;

        bool little = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
        bool big = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
        if (!little && !big)
            return false;

        ushort magic = little
            ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(2, 2));
        if (magic != 42)
            return false;

        uint ifdOffset = little
            ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(4, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(4, 4));
        return ifdOffset >= 8 && ifdOffset < tiff.Length;
    }

    private static string DetermineState(
        HeaderEvidence evidence,
        long unreadableBytes,
        int restartSequenceErrors,
        int unexpectedMarkers)
    {
        if (unreadableBytes == 0 &&
            evidence.DqtTables > 0 &&
            evidence.DhtTables > 0 &&
            (!evidence.ExifPresent || evidence.ExifValid) &&
            restartSequenceErrors == 0 &&
            unexpectedMarkers == 0)
            return "Çok İyi";

        if (unreadableBytes == 0 && evidence.DqtTables > 0 && restartSequenceErrors <= 1 && unexpectedMarkers <= 1)
            return "İyi";

        return "Kısmi";
    }

    private static bool MarkerHasLength(byte marker) =>
        marker is not (0xD8 or 0xD9 or 0x01) && marker is not (>= 0xD0 and <= 0xD7);

    private static bool IsStartOfFrame(byte marker) => marker is
        0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static long TrimUniformSectorPadding(RawDeviceReader reader, long start, long boundary, int sectorSize)
    {
        int sector = Math.Max(512, sectorSize);
        long end = boundary;
        byte[] block = new byte[sector];

        // Bound the backward inspection. We only remove whole zero/FF sectors immediately
        // before the next JPEG; entropy bytes are never trimmed byte-by-byte.
        long minimum = Math.Max(start + MinReconstructableBytes, boundary - 1024L * 1024);
        while (end - sector >= minimum)
        {
            long sectorStart = end - sector;
            int read = reader.ReadBestEffort(sectorStart, block, out long unreadable);
            if (read != sector || unreadable > 0)
                break;

            byte first = block[0];
            if (first is not (0x00 or 0xFF))
                break;

            bool uniform = true;
            for (int i = 1; i < block.Length; i++)
            {
                if (block[i] != first)
                {
                    uniform = false;
                    break;
                }
            }
            if (!uniform)
                break;

            end -= sector;
        }

        return end;
    }

    private sealed record HeaderEvidence(
        int Width,
        int Height,
        int Components,
        int DqtTables,
        int DhtTables,
        int RestartInterval,
        bool Progressive,
        bool ExifPresent,
        bool ExifValid,
        int SosOffset,
        int EntropyOffset);

    private sealed class RawCursor
    {
        private readonly RawDeviceReader _reader;
        private readonly long _end;
        private readonly byte[] _buffer = new byte[CursorBufferBytes];
        private long _bufferStart;
        private int _bufferLength;
        private int _index;

        public RawCursor(RawDeviceReader reader, long start, long end)
        {
            _reader = reader;
            Position = start;
            _end = end;
        }

        public long Position { get; private set; }
        public long UnreadableBytes { get; private set; }

        public bool TryReadByte(out byte value)
        {
            value = 0;
            if (Position >= _end)
                return false;

            if (_index >= _bufferLength || Position < _bufferStart || Position >= _bufferStart + _bufferLength)
            {
                int request = (int)Math.Min(_buffer.Length, _end - Position);
                if (request <= 0)
                    return false;

                _bufferStart = Position;
                _bufferLength = _reader.ReadBestEffort(Position, _buffer.AsSpan(0, request), out long unreadable);
                UnreadableBytes += unreadable;
                _index = 0;
                if (_bufferLength <= 0)
                    return false;
            }

            value = _buffer[_index++];
            Position++;
            return true;
        }

        public bool TryReadUInt16BigEndian(out ushort value)
        {
            value = 0;
            if (!TryReadByte(out byte hi) || !TryReadByte(out byte lo))
                return false;
            value = (ushort)((hi << 8) | lo);
            return true;
        }

        public bool TrySkip(int bytes)
        {
            if (bytes < 0 || Position + bytes > _end)
                return false;

            for (int i = 0; i < bytes; i++)
            {
                if (!TryReadByte(out _))
                    return false;
            }
            return true;
        }
    }
}
