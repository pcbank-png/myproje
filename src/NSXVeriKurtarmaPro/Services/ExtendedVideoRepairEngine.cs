using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

internal static class ExtendedVideoRepairEngine
{
    private const int CopyBufferSize = 1024 * 1024;
    private const int ScanBlockSize = 4 * 1024 * 1024;
    private const long ResyncWindow = 64L * 1024 * 1024;

    private static readonly HashSet<string> MatroskaFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MKV", "WEBM"
    };

    private static readonly HashSet<string> AsfFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WMV", "ASF", "DVR-MS"
    };

    private static readonly HashSet<string> FlvFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "FLV"
    };

    private static readonly HashSet<string> MxfFamily = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MXF"
    };

    private static readonly byte[] EbmlId = new byte[] { 0x1A, 0x45, 0xDF, 0xA3 };
    private static readonly byte[] SegmentId = new byte[] { 0x18, 0x53, 0x80, 0x67 };
    private static readonly byte[] InfoId = new byte[] { 0x15, 0x49, 0xA9, 0x66 };
    private static readonly byte[] TracksId = new byte[] { 0x16, 0x54, 0xAE, 0x6B };
    private static readonly byte[] ClusterId = new byte[] { 0x1F, 0x43, 0xB6, 0x75 };
    private static readonly byte[] TimecodeId = new byte[] { 0xE7 };
    private static readonly byte[] SimpleBlockId = new byte[] { 0xA3 };
    private static readonly byte[] BlockId = new byte[] { 0xA1 };

    private static readonly byte[] AsfHeaderGuid = new byte[]
    {
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] AsfDataGuid = new byte[]
    {
        0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] AsfFilePropertiesGuid = new byte[]
    {
        0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11,
        0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };

    private static readonly byte[] FlvSignature = new byte[] { (byte)'F', (byte)'L', (byte)'V' };

    private static readonly byte[] MxfHeaderPrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x02
    };

    private static readonly byte[] MxfPartitionPrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01
    };

    private static readonly byte[] MxfEssencePrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0D, 0x01, 0x03, 0x01
    };

    private static readonly byte[] MxfAvidEssencePrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0E, 0x04, 0x03, 0x01
    };

    private static readonly byte[] MxfCanopusEssencePrefix = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x0A,
        0x0E, 0x0F, 0x03, 0x01
    };

    private static readonly byte[] MxfRandomIndexPackKey = new byte[]
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x11, 0x01, 0x00
    };

    public static bool Supports(string extension)
    {
        return MatroskaFamily.Contains(extension) ||
               AsfFamily.Contains(extension) ||
               FlvFamily.Contains(extension) ||
               MxfFamily.Contains(extension);
    }

    public static MediaRepairResult Repair(
        string extension,
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        try
        {
            if (MatroskaFamily.Contains(extension))
            {
                try
                {
                    return RepairMatroska(sourcePath, destinationPath, progress);
                }
                catch (InvalidDataException ex)
                {
                    progress?.Invoke("MKV/WebM • Metadata-loss fallback • " + ex.Message);
                    TryDelete(destinationPath);
                    return MetadataLossContainerReconstructionService.ReconstructMatroska(sourcePath, destinationPath, extension, progress);
                }
            }
            if (AsfFamily.Contains(extension))
            {
                try
                {
                    return RepairAsf(sourcePath, destinationPath, progress);
                }
                catch (InvalidDataException ex)
                {
                    progress?.Invoke("ASF/WMV • Metadata-loss fallback • " + ex.Message);
                    TryDelete(destinationPath);
                    return MetadataLossContainerReconstructionService.ReconstructAsf(sourcePath, destinationPath, progress);
                }
            }
            if (FlvFamily.Contains(extension))
                return RepairFlv(sourcePath, destinationPath, progress);
            if (MxfFamily.Contains(extension))
            {
                try
                {
                    return RepairMxf(sourcePath, destinationPath, progress);
                }
                catch (InvalidDataException ex)
                {
                    progress?.Invoke("MXF • Metadata-loss fallback • " + ex.Message);
                    TryDelete(destinationPath);
                    return MetadataLossContainerReconstructionService.ReconstructMxf(sourcePath, destinationPath, progress);
                }
            }

            return new MediaRepairResult(false, "Bu video kapsayıcısı için genişletilmiş onarım motoru bulunmuyor.");
        }
        catch (Exception ex)
        {
            TryDelete(destinationPath);
            return new MediaRepairResult(false, "Video kapsayıcısı onarılamadı: " + ex.Message);
        }
    }

    private static MediaRepairResult RepairMatroska(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("MKV/WebM EBML, Tracks ve Cluster yapısı analiz ediliyor...");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);

        long ebmlStart = FindSignature(input, EbmlId, 0, input.Length);
        if (ebmlStart < 0)
            throw new InvalidDataException("EBML başlığı bulunamadı.");

        if (!TryReadEbmlElement(input, ebmlStart, EbmlId, out EbmlElement ebmlHeader) || ebmlHeader.TotalLength <= 0)
            throw new InvalidDataException("EBML başlığı doğrulanamadı.");

        long segmentStart = FindSignature(input, SegmentId, ebmlHeader.EndOffset, Math.Min(input.Length, ebmlHeader.EndOffset + 8L * 1024 * 1024));
        if (segmentStart < 0)
            throw new InvalidDataException("Matroska Segment başlığı bulunamadı.");

        int segmentSizeLength;
        ulong segmentSize;
        bool segmentUnknown;
        if (!TryReadEbmlSize(input, segmentStart + SegmentId.Length, out segmentSize, out segmentSizeLength, out segmentUnknown))
            throw new InvalidDataException("Segment boyut alanı okunamadı.");

        long segmentContentStart = segmentStart + SegmentId.Length + segmentSizeLength;
        long firstClusterForMetadata = FindSignature(input, ClusterId, segmentContentStart, input.Length);
        long metadataSearchEnd = firstClusterForMetadata > segmentContentStart ? firstClusterForMetadata : input.Length;

        EbmlElement info = FindValidEbmlElement(input, InfoId, segmentContentStart, metadataSearchEnd);
        EbmlElement tracks = FindValidEbmlElement(input, TracksId, segmentContentStart, metadataSearchEnd);
        if (info.TotalLength <= 0 || tracks.TotalLength <= 0)
            throw new InvalidDataException("Info/Tracks metadata yapısı bulunamadı; codec tanımı olmadan güvenli MKV/WebM reconstruction yapılmadı.");

        progress?.Invoke("Geçerli Cluster blokları taranıyor ve yeni Segment altında birleştiriliyor...");
        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        CopyRange(input, output, ebmlHeader.Offset, ebmlHeader.TotalLength);
        output.Write(SegmentId, 0, SegmentId.Length);
        byte[] unknownSegmentSize = new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        output.Write(unknownSegmentSize, 0, unknownSegmentSize.Length);
        CopyRange(input, output, info.Offset, info.TotalLength);
        CopyRange(input, output, tracks.Offset, tracks.TotalLength);

        long searchPosition = Math.Max(segmentContentStart, Math.Max(info.EndOffset, tracks.EndOffset));
        int clusterCount = 0;
        long copiedClusterBytes = 0;
        while (searchPosition < input.Length)
        {
            long clusterStart = FindSignature(input, ClusterId, searchPosition, input.Length);
            if (clusterStart < 0)
                break;

            long nextCluster = FindSignature(input, ClusterId, clusterStart + ClusterId.Length, input.Length);
            long clusterEnd = DetermineClusterEnd(input, clusterStart, nextCluster);
            if (clusterEnd <= clusterStart)
            {
                searchPosition = clusterStart + 1;
                continue;
            }

            if (!ValidateCluster(input, clusterStart, clusterEnd))
            {
                searchPosition = clusterStart + 1;
                continue;
            }

            long clusterLength = clusterEnd - clusterStart;
            CopyRange(input, output, clusterStart, clusterLength);
            clusterCount++;
            copiedClusterBytes += clusterLength;
            searchPosition = clusterEnd;

            if (clusterCount % 64 == 0)
                progress?.Invoke("MKV/WebM reconstruction • " + clusterCount.ToString("N0") + " Cluster doğrulandı...");
        }

        output.Flush(true);
        if (clusterCount == 0 || copiedClusterBytes <= 0)
            throw new InvalidDataException("Doğrulanabilir video Cluster verisi bulunamadı.");

        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateMatroskaOutput(destinationPath);
        return new MediaRepairResult(
            true,
            "MKV/WebM EBML Segment yeniden kuruldu; Info/Tracks korundu, " + clusterCount.ToString("N0") + " geçerli Cluster yeniden zincirlendi. SeekHead/Cues zorunlu tutulmadan akış oynatılabilir yapıda oluşturuldu.",
            writtenLength);
    }

    private static long DetermineClusterEnd(FileStream input, long clusterStart, long nextCluster)
    {
        ulong size;
        int sizeLength;
        bool unknown;
        if (TryReadEbmlSize(input, clusterStart + ClusterId.Length, out size, out sizeLength, out unknown))
        {
            long contentStart = clusterStart + ClusterId.Length + sizeLength;
            if (!unknown && size <= (ulong)long.MaxValue)
            {
                long declaredEnd;
                try
                {
                    declaredEnd = checked(contentStart + (long)size);
                }
                catch (OverflowException)
                {
                    declaredEnd = -1;
                }

                if (declaredEnd > contentStart && declaredEnd <= input.Length)
                    return declaredEnd;
            }
        }

        if (nextCluster > clusterStart)
            return nextCluster;
        return input.Length;
    }

    private static bool ValidateCluster(FileStream input, long start, long end)
    {
        long inspectEnd = Math.Min(end, start + 4L * 1024 * 1024);
        long contentStart = start + ClusterId.Length;
        ulong ignoredSize;
        int sizeLength;
        bool ignoredUnknown;
        if (!TryReadEbmlSize(input, contentStart, out ignoredSize, out sizeLength, out ignoredUnknown))
            return false;
        contentStart += sizeLength;
        if (contentStart >= inspectEnd)
            return false;

        bool hasTimecode = FindSignature(input, TimecodeId, contentStart, inspectEnd) >= 0;
        bool hasBlock = FindSignature(input, SimpleBlockId, contentStart, inspectEnd) >= 0 ||
                        FindSignature(input, BlockId, contentStart, inspectEnd) >= 0;
        return hasTimecode && hasBlock;
    }

    private static void ValidateMatroskaOutput(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FindSignature(input, EbmlId, 0, Math.Min(input.Length, 1024 * 1024)) != 0)
            throw new InvalidDataException("Onarılan dosyada EBML başlığı doğrulanamadı.");
        if (FindSignature(input, SegmentId, 0, Math.Min(input.Length, 8L * 1024 * 1024)) < 0 ||
            FindSignature(input, InfoId, 0, Math.Min(input.Length, 64L * 1024 * 1024)) < 0 ||
            FindSignature(input, TracksId, 0, Math.Min(input.Length, 64L * 1024 * 1024)) < 0 ||
            FindSignature(input, ClusterId, 0, input.Length) < 0)
            throw new InvalidDataException("Onarılan MKV/WebM temel Segment/Tracks/Cluster yapısını geçemedi.");
    }

    private static EbmlElement FindValidEbmlElement(FileStream input, byte[] id, long start, long end)
    {
        long position = start;
        while (position < end)
        {
            long found = FindSignature(input, id, position, end);
            if (found < 0)
                return default;
            EbmlElement element;
            if (TryReadEbmlElement(input, found, id, out element) && element.EndOffset <= input.Length)
                return element;
            position = found + 1;
        }
        return default;
    }

    private static bool TryReadEbmlElement(FileStream input, long offset, byte[] expectedId, out EbmlElement element)
    {
        element = default;
        byte[] id = new byte[expectedId.Length];
        input.Position = offset;
        if (input.Read(id, 0, id.Length) != id.Length || !BytesEqual(id, expectedId))
            return false;

        ulong size;
        int sizeLength;
        bool unknown;
        if (!TryReadEbmlSize(input, offset + expectedId.Length, out size, out sizeLength, out unknown) || unknown || size > (ulong)long.MaxValue)
            return false;

        long total;
        try
        {
            total = checked(expectedId.Length + sizeLength + (long)size);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (total <= expectedId.Length + sizeLength || offset + total > input.Length)
            return false;

        element = new EbmlElement(offset, total);
        return true;
    }

    private static bool TryReadEbmlSize(FileStream input, long offset, out ulong value, out int length, out bool unknown)
    {
        value = 0;
        length = 0;
        unknown = false;
        if (offset < 0 || offset >= input.Length)
            return false;

        input.Position = offset;
        int first = input.ReadByte();
        if (first <= 0)
            return false;

        byte marker = 0x80;
        int len = 1;
        while (len <= 8 && ((byte)first & marker) == 0)
        {
            marker >>= 1;
            len++;
        }
        if (len > 8)
            return false;

        ulong result = (ulong)((byte)first & (marker - 1));
        bool allOnes = result == (ulong)(marker - 1);
        for (int i = 1; i < len; i++)
        {
            int next = input.ReadByte();
            if (next < 0)
                return false;
            result = (result << 8) | (byte)next;
            allOnes &= next == 0xFF;
        }

        value = result;
        length = len;
        unknown = allOnes;
        return true;
    }

    private static MediaRepairResult RepairAsf(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("WMV/ASF Header, File Properties ve Data Object yapısı analiz ediliyor...");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        long headerStart = FindSignature(input, AsfHeaderGuid, 0, input.Length);
        if (headerStart < 0)
            throw new InvalidDataException("ASF Header Object bulunamadı.");

        long dataStart = FindSignature(input, AsfDataGuid, headerStart + 24, input.Length);
        if (dataStart < 0 || dataStart <= headerStart + 30)
            throw new InvalidDataException("ASF Data Object bulunamadı.");

        long headerLength = dataStart - headerStart;
        if (headerLength > int.MaxValue)
            throw new InvalidDataException("ASF header alanı beklenmeyen büyüklükte.");

        byte[] header = new byte[(int)headerLength];
        input.Position = headerStart;
        ReadExactly(input, header);

        int filePropertiesOffset = IndexOfFrom(header, AsfFilePropertiesGuid, 0);
        uint packetSize = 0;
        if (filePropertiesOffset >= 0 && filePropertiesOffset + 104 <= header.Length)
        {
            uint minPacket = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(filePropertiesOffset + 92, 4));
            uint maxPacket = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(filePropertiesOffset + 96, 4));
            packetSize = maxPacket != 0 ? maxPacket : minPacket;
        }
        if (packetSize == 0 || packetSize > 16 * 1024 * 1024)
            throw new InvalidDataException("ASF paket boyutu File Properties içinden doğrulanamadı.");

        byte[] dataHeader = new byte[50];
        input.Position = dataStart;
        if (input.Read(dataHeader, 0, dataHeader.Length) != dataHeader.Length || !StartsWith(dataHeader, AsfDataGuid))
            throw new InvalidDataException("ASF Data Object başlığı eksik.");

        long availablePayload = input.Length - (dataStart + dataHeader.Length);
        long packetCount = availablePayload / packetSize;
        if (packetCount <= 0)
            throw new InvalidDataException("ASF içinde tam video paketi bulunamadı.");

        long cleanPayloadBytes = checked(packetCount * packetSize);
        long outputLength = checked(headerLength + dataHeader.Length + cleanPayloadBytes);

        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16, 8), (ulong)headerLength);
        if (filePropertiesOffset >= 0 && filePropertiesOffset + 104 <= header.Length)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(filePropertiesOffset + 40, 8), (ulong)outputLength);
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(filePropertiesOffset + 56, 8), (ulong)packetCount);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(filePropertiesOffset + 92, 4), packetSize);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(filePropertiesOffset + 96, 4), packetSize);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(dataHeader.AsSpan(16, 8), (ulong)(dataHeader.Length + cleanPayloadBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(dataHeader.AsSpan(40, 8), (ulong)packetCount);

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        output.Write(header, 0, header.Length);
        output.Write(dataHeader, 0, dataHeader.Length);
        CopyRange(input, output, dataStart + dataHeader.Length, cleanPayloadBytes);
        output.Flush(true);

        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateAsfOutput(destinationPath, packetSize, packetCount);
        return new MediaRepairResult(
            true,
            "WMV/ASF Header ve Data Object boyutları yeniden yazıldı; File Properties dosya/paket sayıları düzeltildi ve yalnız tam medya paketleri korundu.",
            writtenLength);
    }

    private static void ValidateAsfOutput(string path, uint packetSize, long expectedPackets)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] headerGuid = new byte[16];
        if (input.Read(headerGuid, 0, 16) != 16 || !BytesEqual(headerGuid, AsfHeaderGuid))
            throw new InvalidDataException("Onarılan ASF Header Object doğrulanamadı.");
        long dataStart = FindSignature(input, AsfDataGuid, 24, input.Length);
        if (dataStart < 0)
            throw new InvalidDataException("Onarılan ASF Data Object doğrulanamadı.");
        if (input.Length < dataStart + 50 + packetSize || (input.Length - dataStart - 50) / packetSize < expectedPackets)
            throw new InvalidDataException("Onarılan ASF paket geometrisi doğrulanamadı.");
    }

    private static MediaRepairResult RepairFlv(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("FLV tag zinciri, timestamp ve PreviousTagSize alanları analiz ediliyor...");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        long flvStart = FindSignature(input, FlvSignature, 0, input.Length);
        if (flvStart < 0)
            throw new InvalidDataException("FLV başlığı bulunamadı.");

        byte[] header = new byte[9];
        input.Position = flvStart;
        if (input.Read(header, 0, header.Length) != header.Length || header[3] == 0)
            throw new InvalidDataException("FLV başlığı doğrulanamadı.");
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        if (dataOffset < 9 || dataOffset > 1024 * 1024 || flvStart + dataOffset > input.Length)
            throw new InvalidDataException("FLV DataOffset geçersiz.");

        byte[] extendedHeader = new byte[checked((int)dataOffset)];
        input.Position = flvStart;
        ReadExactly(input, extendedHeader);
        extendedHeader[4] = 0;

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        output.Write(extendedHeader, 0, extendedHeader.Length);
        WriteUInt32BigEndian(output, 0);

        long position = flvStart + dataOffset;
        if (position + 4 <= input.Length)
            position += 4;

        int videoTags = 0;
        int audioTags = 0;
        int metadataTags = 0;
        long rejectedBytes = 0;
        while (position + 11 <= input.Length)
        {
            FlvTag tag;
            if (!TryReadFlvTag(input, position, out tag))
            {
                long next = FindNextFlvTag(input, position + 1, Math.Min(input.Length, position + ResyncWindow));
                if (next < 0)
                    break;
                rejectedBytes += next - position;
                position = next;
                continue;
            }

            if (!ValidateFlvTagPayload(input, tag))
            {
                position++;
                rejectedBytes++;
                continue;
            }

            CopyRange(input, output, tag.Offset, 11L + tag.DataSize);
            WriteUInt32BigEndian(output, (uint)(11 + tag.DataSize));
            if (tag.Type == 9) videoTags++;
            else if (tag.Type == 8) audioTags++;
            else if (tag.Type == 18) metadataTags++;

            position = tag.DataEnd + 4;
            if (position > input.Length)
                position = tag.DataEnd;
        }

        if (videoTags == 0)
            throw new InvalidDataException("Doğrulanabilir FLV video tag'i bulunamadı.");

        byte flags = 0x01;
        if (audioTags > 0)
            flags |= 0x04;
        output.Position = 4;
        output.WriteByte(flags);
        output.Flush(true);

        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateFlvOutput(destinationPath);
        string skipped = rejectedBytes > 0 ? " " + rejectedBytes.ToString("N0") + " bozuk bayt resync sırasında atlandı." : string.Empty;
        return new MediaRepairResult(
            true,
            "FLV tag zinciri yeniden kuruldu; " + videoTags.ToString("N0") + " video, " + audioTags.ToString("N0") + " audio ve " + metadataTags.ToString("N0") + " metadata tag'i korundu; PreviousTagSize alanları yeniden yazıldı." + skipped,
            writtenLength);
    }

    private static bool TryReadFlvTag(FileStream input, long offset, out FlvTag tag)
    {
        tag = default;
        if (offset < 0 || offset + 11 > input.Length)
            return false;
        byte[] h = new byte[11];
        input.Position = offset;
        if (input.Read(h, 0, h.Length) != h.Length)
            return false;

        byte type = (byte)(h[0] & 0x1F);
        if (type != 8 && type != 9 && type != 18)
            return false;
        if (h[8] != 0 || h[9] != 0 || h[10] != 0)
            return false;

        int dataSize = (h[1] << 16) | (h[2] << 8) | h[3];
        if (dataSize < 0 || dataSize > 64 * 1024 * 1024)
            return false;
        long end = offset + 11L + dataSize;
        if (end > input.Length)
            return false;

        tag = new FlvTag(offset, type, dataSize, end);
        return true;
    }

    private static bool ValidateFlvTagPayload(FileStream input, FlvTag tag)
    {
        if (tag.DataSize == 0)
            return tag.Type == 18;
        input.Position = tag.Offset + 11;
        int first = input.ReadByte();
        if (first < 0)
            return false;

        if (tag.Type == 9)
        {
            if ((first & 0x80) != 0)
                return tag.DataSize >= 5;
            int frameType = (first >> 4) & 0x0F;
            int codec = first & 0x0F;
            return frameType >= 1 && frameType <= 5 && codec >= 1 && codec <= 15;
        }
        if (tag.Type == 8)
        {
            int soundFormat = (first >> 4) & 0x0F;
            return soundFormat <= 15;
        }
        return true;
    }

    private static long FindNextFlvTag(FileStream input, long start, long end)
    {
        byte[] one = new byte[1];
        long position = start;
        while (position < end)
        {
            input.Position = position;
            if (input.Read(one, 0, 1) != 1)
                break;
            byte type = (byte)(one[0] & 0x1F);
            if (type == 8 || type == 9 || type == 18)
            {
                FlvTag tag;
                if (TryReadFlvTag(input, position, out tag) && ValidateFlvTagPayload(input, tag))
                    return position;
            }
            position++;
        }
        return -1;
    }

    private static void ValidateFlvOutput(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] header = new byte[9];
        if (input.Read(header, 0, header.Length) != header.Length || !StartsWith(header, FlvSignature))
            throw new InvalidDataException("Onarılan FLV başlığı doğrulanamadı.");
        uint dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(5, 4));
        long position = dataOffset + 4L;
        int video = 0;
        while (position + 11 <= input.Length)
        {
            FlvTag tag;
            if (!TryReadFlvTag(input, position, out tag))
                throw new InvalidDataException("Onarılan FLV tag zinciri doğrulanamadı.");
            if (tag.Type == 9)
                video++;
            position = tag.DataEnd + 4;
        }
        if (video == 0)
            throw new InvalidDataException("Onarılan FLV içinde video tag'i bulunamadı.");
    }

    private static MediaRepairResult RepairMxf(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("MXF KLV partition/essence zinciri analiz ediliyor...");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        long start = FindMxfHeaderPartition(input, 0, input.Length);
        if (start < 0)
            throw new InvalidDataException("MXF Header Partition Pack bulunamadı.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        long position = start;
        long rejectedBytes = start;
        int klvCount = 0;
        int essenceCount = 0;
        var partitions = new List<MxfPartitionInfo>();
        long previousPartitionOffset = 0;

        while (position + 17 <= input.Length)
        {
            byte[] key = new byte[16];
            input.Position = position;
            if (input.Read(key, 0, key.Length) != key.Length || !IsSmpteUl(key))
            {
                long next = FindNextSmpteUl(input, position + 1, Math.Min(input.Length, position + ResyncWindow));
                if (next < 0)
                    break;
                rejectedBytes += next - position;
                position = next;
                continue;
            }

            ulong valueLength;
            int berLength;
            if (!TryReadBerLength(input, position + 16, out valueLength, out berLength) || valueLength > (ulong)long.MaxValue)
            {
                position++;
                rejectedBytes++;
                continue;
            }

            long valueStart;
            long end;
            try
            {
                valueStart = checked(position + 16L + berLength);
                end = checked(valueStart + (long)valueLength);
            }
            catch (OverflowException)
            {
                position++;
                rejectedBytes++;
                continue;
            }
            if (end > input.Length || end <= valueStart)
            {
                position++;
                rejectedBytes++;
                continue;
            }

            if (BytesEqual(key, MxfRandomIndexPackKey))
            {
                position = end;
                continue;
            }

            long outputOffset = output.Position;
            if (IsMxfPartitionPack(key) && valueLength <= 1024 * 1024)
            {
                int totalLength = checked(16 + berLength + (int)valueLength);
                byte[] klv = new byte[totalLength];
                input.Position = position;
                ReadExactly(input, klv);
                int valueOffset = 16 + berLength;
                uint bodySid = 0;
                if (valueLength >= 64)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(klv.AsSpan(valueOffset + 8, 8), (ulong)outputOffset);
                    BinaryPrimitives.WriteUInt64BigEndian(klv.AsSpan(valueOffset + 16, 8), (ulong)(partitions.Count == 0 ? 0 : previousPartitionOffset));
                    BinaryPrimitives.WriteUInt64BigEndian(klv.AsSpan(valueOffset + 24, 8), 0UL);
                    bodySid = BinaryPrimitives.ReadUInt32BigEndian(klv.AsSpan(valueOffset + 60, 4));
                }
                output.Write(klv, 0, klv.Length);
                partitions.Add(new MxfPartitionInfo(outputOffset, bodySid));
                previousPartitionOffset = outputOffset;
            }
            else
            {
                CopyRange(input, output, position, end - position);
            }

            if (IsMxfEssenceKey(key))
                essenceCount++;
            klvCount++;
            position = end;

            if (klvCount % 512 == 0)
                progress?.Invoke("MXF reconstruction • " + klvCount.ToString("N0") + " KLV, " + essenceCount.ToString("N0") + " essence öğesi doğrulandı...");
        }

        if (klvCount < 3 || essenceCount == 0 || partitions.Count == 0)
            throw new InvalidDataException("Yeterli MXF partition/KLV/essence zinciri doğrulanamadı.");

        AppendMxfRandomIndexPack(output, partitions);
        output.Flush(true);
        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateMxfOutput(destinationPath);

        string skipped = rejectedBytes > 0 ? " " + rejectedBytes.ToString("N0") + " bozuk/uyumsuz bayt KLV resync sırasında atlandı." : string.Empty;
        return new MediaRepairResult(
            true,
            "MXF KLV zinciri yeniden yazıldı; partition ofsetleri düzeltildi, eski Random Index Pack kaldırılıp " + partitions.Count.ToString("N0") + " partition için yeni RIP üretildi ve essence akışı doğrulandı." + skipped,
            writtenLength);
    }

    private static long FindMxfHeaderPartition(FileStream input, long start, long end)
    {
        long position = start;
        while (position < end)
        {
            long found = FindSignature(input, MxfHeaderPrefix, position, end);
            if (found < 0 || found + 16 > input.Length)
                return -1;
            byte[] key = new byte[16];
            input.Position = found;
            if (input.Read(key, 0, key.Length) == key.Length && key[14] >= 0x01 && key[14] <= 0x04 && key[15] == 0x00)
                return found;
            position = found + 1;
        }
        return -1;
    }

    private static bool IsMxfPartitionPack(byte[] key)
    {
        if (key.Length != 16)
            return false;
        for (int i = 0; i < MxfPartitionPrefix.Length; i++)
        {
            if (key[i] != MxfPartitionPrefix[i])
                return false;
        }
        // Byte 13 identifies Header/Body/Footer Partition Pack; byte 14 is open/closed + complete/incomplete status.
        return key[13] >= 0x02 && key[13] <= 0x04 && key[14] >= 0x01 && key[14] <= 0x04 && key[15] == 0x00;
    }

    private static bool IsMxfEssenceKey(byte[] key)
    {
        return StartsWith(key, MxfEssencePrefix) || StartsWith(key, MxfAvidEssencePrefix) || StartsWith(key, MxfCanopusEssencePrefix);
    }

    private static bool IsSmpteUl(byte[] key)
    {
        return key.Length >= 4 && key[0] == 0x06 && key[1] == 0x0E && key[2] == 0x2B && key[3] == 0x34;
    }

    private static long FindNextSmpteUl(FileStream input, long start, long end)
    {
        byte[] prefix = new byte[] { 0x06, 0x0E, 0x2B, 0x34 };
        return FindSignature(input, prefix, start, end);
    }

    private static bool TryReadBerLength(FileStream input, long offset, out ulong value, out int lengthBytes)
    {
        value = 0;
        lengthBytes = 0;
        if (offset < 0 || offset >= input.Length)
            return false;
        input.Position = offset;
        int first = input.ReadByte();
        if (first < 0)
            return false;
        if ((first & 0x80) == 0)
        {
            value = (byte)first;
            lengthBytes = 1;
            return true;
        }
        int count = first & 0x7F;
        if (count <= 0 || count > 8 || offset + 1 + count > input.Length)
            return false;
        ulong result = 0;
        for (int i = 0; i < count; i++)
        {
            int next = input.ReadByte();
            if (next < 0)
                return false;
            result = (result << 8) | (byte)next;
        }
        value = result;
        lengthBytes = 1 + count;
        return true;
    }

    private static void AppendMxfRandomIndexPack(FileStream output, List<MxfPartitionInfo> partitions)
    {
        int valueLength = checked(partitions.Count * 12 + 4);
        byte[] ber = EncodeBerLength((ulong)valueLength);
        output.Write(MxfRandomIndexPackKey, 0, MxfRandomIndexPackKey.Length);
        output.Write(ber, 0, ber.Length);
        byte[] entry = new byte[12];
        foreach (MxfPartitionInfo partition in partitions)
        {
            BinaryPrimitives.WriteUInt32BigEndian(entry.AsSpan(0, 4), partition.BodySid);
            BinaryPrimitives.WriteUInt64BigEndian(entry.AsSpan(4, 8), (ulong)partition.Offset);
            output.Write(entry, 0, entry.Length);
        }
        uint totalLength = checked((uint)(16 + ber.Length + valueLength));
        byte[] total = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(total, totalLength);
        output.Write(total, 0, total.Length);

    }

    private static byte[] EncodeBerLength(ulong value)
    {
        if (value < 0x80)
            return new byte[] { (byte)value };
        int count = 0;
        ulong tmp = value;
        while (tmp > 0)
        {
            count++;
            tmp >>= 8;
        }
        byte[] result = new byte[count + 1];
        result[0] = (byte)(0x80 | count);
        for (int i = 0; i < count; i++)
            result[count - i] = (byte)(value >> (8 * i));
        return result;
    }

    private static void ValidateMxfOutput(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FindMxfHeaderPartition(input, 0, Math.Min(input.Length, 16L * 1024 * 1024)) != 0)
            throw new InvalidDataException("Onarılan MXF Header Partition doğrulanamadı.");
        if (FindSignature(input, MxfRandomIndexPackKey, 0, input.Length) < 0)
            throw new InvalidDataException("Onarılan MXF Random Index Pack doğrulanamadı.");
        if (FindMxfEssence(input, 0, input.Length) < 0)
            throw new InvalidDataException("Onarılan MXF essence akışı doğrulanamadı.");
    }

    private static long FindMxfEssence(FileStream input, long start, long end)
    {
        long a = FindSignature(input, MxfEssencePrefix, start, end);
        long b = FindSignature(input, MxfAvidEssencePrefix, start, end);
        long c = FindSignature(input, MxfCanopusEssencePrefix, start, end);
        long result = -1;
        if (a >= 0) result = a;
        if (b >= 0 && (result < 0 || b < result)) result = b;
        if (c >= 0 && (result < 0 || c < result)) result = c;
        return result;
    }

    private static long FindSignature(FileStream input, byte[] signature, long start, long end)
    {
        if (signature.Length == 0 || start < 0 || start >= end || start >= input.Length)
            return -1;
        end = Math.Min(end, input.Length);
        int overlap = signature.Length - 1;
        byte[] buffer = new byte[ScanBlockSize + overlap];
        int carry = 0;
        long position = start;

        while (position < end)
        {
            int request = (int)Math.Min(ScanBlockSize, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            int found = IndexOfWithinCount(buffer, signature, count);
            if (found >= 0)
                return position - carry + found;
            carry = Math.Min(overlap, count);
            if (carry > 0)
                Buffer.BlockCopy(buffer, count - carry, buffer, 0, carry);
            position += read;
        }
        return -1;
    }

    private static int IndexOfWithinCount(byte[] buffer, byte[] signature, int count)
    {
        if (signature.Length == 0 || count < signature.Length)
            return -1;
        int limit = count - signature.Length;
        for (int i = 0; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < signature.Length; j++)
            {
                if (buffer[i + j] != signature[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static int IndexOfFrom(byte[] buffer, byte[] signature, int start)
    {
        if (start < 0)
            start = 0;
        if (signature.Length == 0 || buffer.Length - start < signature.Length)
            return -1;
        int limit = buffer.Length - signature.Length;
        for (int i = start; i <= limit; i++)
        {
            bool match = true;
            for (int j = 0; j < signature.Length; j++)
            {
                if (buffer[i + j] != signature[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static bool StartsWith(byte[] buffer, byte[] prefix)
    {
        if (buffer.Length < prefix.Length)
            return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (buffer[i] != prefix[i])
                return false;
        }
        return true;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
                return false;
        }
        return true;
    }

    private static void CopyRange(FileStream input, FileStream output, long offset, long length)
    {
        if (length <= 0)
            return;
        input.Position = offset;
        byte[] buffer = new byte[CopyBufferSize];
        long remaining = length;
        while (remaining > 0)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = input.Read(buffer, 0, request);
            if (read <= 0)
                throw new EndOfStreamException("Video verisi beklenenden önce sona erdi.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static void WriteUInt32BigEndian(Stream output, uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        output.Write(bytes, 0, bytes.Length);
    }

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

    private readonly record struct EbmlElement(long Offset, long TotalLength)
    {
        public long EndOffset { get { return Offset + TotalLength; } }
    }

    private readonly record struct FlvTag(long Offset, byte Type, int DataSize, long DataEnd);
    private readonly record struct MxfPartitionInfo(long Offset, uint BodySid);
}
