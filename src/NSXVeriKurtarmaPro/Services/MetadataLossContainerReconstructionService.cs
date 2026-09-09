using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Metadata-loss reconstruction layer for containers whose elementary payload survives while
/// top-level track/header metadata is missing. The engine is deliberately conservative: it only
/// synthesizes metadata from codec/container evidence that can be verified from the surviving data.
/// </summary>
internal static class MetadataLossContainerReconstructionService
{
    private const int CopyBufferSize = 1024 * 1024;
    private const long MaxClusterProbe = 8L * 1024 * 1024;
    private const long MaxCodecProbe = 32L * 1024 * 1024;

    private static readonly byte[] EbmlId = { 0x1A, 0x45, 0xDF, 0xA3 };
    private static readonly byte[] SegmentId = { 0x18, 0x53, 0x80, 0x67 };
    private static readonly byte[] ClusterId = { 0x1F, 0x43, 0xB6, 0x75 };
    private static readonly byte[] InfoId = { 0x15, 0x49, 0xA9, 0x66 };
    private static readonly byte[] TracksId = { 0x16, 0x54, 0xAE, 0x6B };
    private static readonly byte[] TimecodeId = { 0xE7 };

    private static readonly byte[] AsfHeaderGuid =
    {
        0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] AsfDataGuid =
    {
        0x36, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
        0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C
    };

    private static readonly byte[] AsfFilePropertiesGuid =
    {
        0xA1, 0xDC, 0xAB, 0x8C, 0x47, 0xA9, 0xCF, 0x11,
        0x8E, 0xE4, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };

    private static readonly byte[] AsfStreamPropertiesGuid =
    {
        0xB7, 0xDC, 0x07, 0x91, 0xA9, 0xB7, 0xCF, 0x11,
        0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };

    private static readonly byte[] AsfHeaderExtensionGuid =
    {
        0xB5, 0x03, 0xBF, 0x5F, 0x2E, 0xA9, 0xCF, 0x11,
        0x8E, 0xE3, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };

    private static readonly byte[] AsfReserved1Guid =
    {
        0x11, 0xD2, 0xD3, 0xAB, 0xBA, 0xA9, 0xCF, 0x11,
        0x8E, 0xE6, 0x00, 0xC0, 0x0C, 0x20, 0x53, 0x65
    };

    private static readonly byte[] AsfVideoMediaGuid =
    {
        0xC0, 0xEF, 0x19, 0xBC, 0x4D, 0x5B, 0xCF, 0x11,
        0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
    };

    private static readonly byte[] AsfNoErrorCorrectionGuid =
    {
        0x00, 0x57, 0xFB, 0x20, 0x55, 0x5B, 0xCF, 0x11,
        0xA8, 0xFD, 0x00, 0x80, 0x5F, 0x5C, 0x44, 0x2B
    };

    private static readonly byte[] MxfPartitionPrefix =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01
    };

    private static readonly byte[] MxfClosedCompleteHeaderKey =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x02, 0x04, 0x00
    };

    private static readonly byte[] MxfEssencePrefix =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0D, 0x01, 0x03, 0x01
    };

    private static readonly byte[] MxfAvidEssencePrefix =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x01,
        0x0E, 0x04, 0x03, 0x01
    };

    private static readonly byte[] MxfCanopusEssencePrefix =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x01, 0x02, 0x01, 0x0A,
        0x0E, 0x0F, 0x03, 0x01
    };

    private static readonly byte[] MxfRandomIndexPackKey =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x05, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x11, 0x01, 0x00
    };

    private static readonly byte[] Op1aOperationalPattern =
    {
        0x06, 0x0E, 0x2B, 0x34, 0x04, 0x01, 0x01, 0x01,
        0x0D, 0x01, 0x02, 0x01, 0x01, 0x01, 0x01, 0x00
    };

    public static MediaRepairResult ReconstructMatroska(
        string sourcePath,
        string destinationPath,
        string extension,
        Action<string>? progress)
    {
        progress?.Invoke("MKV/WebM • Video Yapısı Yeniden Oluşturuluyor");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);

        List<ClusterSpan> clusters = CollectMatroskaClusters(input, progress, out Dictionary<ulong, TrackEvidence> evidence);
        if (clusters.Count == 0)
            throw new InvalidDataException("Metadata-loss reconstruction için doğrulanabilir Matroska Cluster bulunamadı.");

        List<InferredTrack> tracks = InferTracks(evidence);
        if (tracks.Count == 0)
            throw new InvalidDataException("Cluster bloklarından güvenilir codec/track kanıtı üretilemedi; sentetik Tracks yazılmadı.");

        string docType = extension.Equals("WEBM", StringComparison.OrdinalIgnoreCase) ? "webm" : "matroska";
        byte[] ebml = TryCopyEbmlHeader(input) ?? BuildEbmlHeader(docType);
        byte[] info = BuildMatroskaInfo();
        byte[] tracksElement = BuildMatroskaTracks(tracks);

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        output.Write(ebml, 0, ebml.Length);
        output.Write(SegmentId, 0, SegmentId.Length);
        output.Write(new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        output.Write(info, 0, info.Length);
        output.Write(tracksElement, 0, tracksElement.Length);

        long copied = 0;
        for (int i = 0; i < clusters.Count; i++)
        {
            ClusterSpan cluster = clusters[i];
            CopyRange(input, output, cluster.Offset, cluster.Length);
            copied += cluster.Length;
            if ((i + 1) % 64 == 0)
                progress?.Invoke($"MKV/WebM • Video blokları {i + 1:N0}/{clusters.Count:N0}");
        }
        output.Flush(true);

        if (copied <= 0)
            throw new InvalidDataException("Cluster payload yeniden yazılamadı.");

        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateMatroskaReconstruction(destinationPath);
        return new MediaRepairResult(
            true,
            $"MKV/WebM metadata-loss reconstruction tamamlandı; {tracks.Count:N0} track codec kanıtından yeniden üretildi, {clusters.Count:N0} Cluster korundu.",
            writtenLength);
    }

    public static MediaRepairResult ReconstructAsf(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("ASF/WMV • Akış Yapısı Yeniden Oluşturuluyor");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);
        long dataStart = FindSignature(input, AsfDataGuid, 0, input.Length);

        byte[] dataHeader;
        byte[] fileId = new byte[16];
        long payloadStart;
        long payloadBytes;
        ulong declaredPackets;
        uint packetSize;
        bool synthesizedDataObject = false;

        if (dataStart >= 0 && dataStart + 50 <= input.Length)
        {
            dataHeader = new byte[50];
            input.Position = dataStart;
            ReadExactly(input, dataHeader);
            declaredPackets = BinaryPrimitives.ReadUInt64LittleEndian(dataHeader.AsSpan(40, 8));
            payloadStart = dataStart + 50;
            payloadBytes = input.Length - payloadStart;
            Buffer.BlockCopy(dataHeader, 24, fileId, 0, fileId.Length);
            packetSize = TryRecoverAsfPacketSize(input, dataStart, payloadBytes, declaredPackets);
        }
        else
        {
            if (!TryLocateRawAsfPacketRun(input, out payloadStart, out packetSize, out long rawPacketCount))
                throw new InvalidDataException("ASF Header/Data Object tamamen kayıp ve doğrulanabilir sabit packet run bulunamadı.");
            payloadBytes = checked(rawPacketCount * packetSize);
            declaredPackets = (ulong)rawPacketCount;
            BuildDeterministicAsfFileId(input.Length, payloadStart, packetSize, fileId);
            dataHeader = new byte[50];
            Buffer.BlockCopy(AsfDataGuid, 0, dataHeader, 0, 16);
            Buffer.BlockCopy(fileId, 0, dataHeader, 24, 16);
            dataHeader[48] = 1;
            dataHeader[49] = 1;
            synthesizedDataObject = true;
            progress?.Invoke("ASF/WMV • Raw Packet Run doğrulandı; Data Object yeniden üretiliyor...");
        }

        if (payloadBytes <= 0 || packetSize == 0)
            throw new InvalidDataException("ASF packet geometrisi metadata kaybından sonra doğrulanamadı.");

        long packetCount = payloadBytes / packetSize;
        if (packetCount <= 0)
            throw new InvalidDataException("ASF içinde tam medya paketi bulunamadı.");
        long cleanPayloadBytes = checked(packetCount * packetSize);

        List<byte[]> streamProperties = dataStart > 0
            ? CollectOrphanedAsfStreamProperties(input, dataStart)
            : new List<byte[]>();
        if (streamProperties.Count == 0)
        {
            if (!TryInferAsfVideoConfiguration(input, payloadStart, Math.Min(cleanPayloadBytes, MaxCodecProbe), out AsfVideoConfig config))
                throw new InvalidDataException("ASF Stream Properties kayıp ve packet payload içinden doğrulanabilir video formatı/çözünürlüğü çıkarılamadı.");
            streamProperties.Add(BuildAsfVideoStreamProperties(config, streamNumber: 1));
        }

        byte[] fileProperties = BuildAsfFileProperties(fileId, packetSize, packetCount, 0, 0);
        byte[] header = BuildAsfHeader(fileProperties, streamProperties);
        long outputLength = checked(header.Length + 50L + cleanPayloadBytes);
        PatchAsfFileSizeAndPacketCount(fileProperties, outputLength, packetCount, packetSize);
        header = BuildAsfHeader(fileProperties, streamProperties);
        outputLength = checked(header.Length + 50L + cleanPayloadBytes);
        PatchAsfFileSizeAndPacketCount(fileProperties, outputLength, packetCount, packetSize);
        header = BuildAsfHeader(fileProperties, streamProperties);

        BinaryPrimitives.WriteUInt64LittleEndian(dataHeader.AsSpan(16, 8), (ulong)(50L + cleanPayloadBytes));
        BinaryPrimitives.WriteUInt64LittleEndian(dataHeader.AsSpan(40, 8), (ulong)packetCount);

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        output.Write(header, 0, header.Length);
        output.Write(dataHeader, 0, dataHeader.Length);
        CopyRange(input, output, payloadStart, cleanPayloadBytes);
        output.Flush(true);

        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateAsfReconstruction(destinationPath, packetSize, packetCount);
        string dataObjectText = synthesizedDataObject ? " Data Object packet run üzerinden yeniden üretildi." : string.Empty;
        return new MediaRepairResult(
            true,
            $"ASF/WMV metadata-loss reconstruction tamamlandı; Header/File Properties yeniden üretildi, {streamProperties.Count:N0} Stream Properties korundu/çıkarıldı, {packetCount:N0} packet geometrisi doğrulandı.{dataObjectText}",
            writtenLength);
    }

    public static MediaRepairResult ReconstructMxf(
        string sourcePath,
        string destinationPath,
        Action<string>? progress)
    {
        progress?.Invoke("MXF • Medya Yapısı Yeniden Oluşturuluyor");
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.RandomAccess);

        long firstPartition = FindAnyMxfPartition(input, 0, input.Length);
        long firstEssence = FindMxfEssence(input, 0, input.Length);
        if (firstEssence < 0)
            throw new InvalidDataException("MXF essence KLV bulunamadı.");

        EnsureParentDirectory(destinationPath);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);

        var partitions = new List<MxfPartitionInfo>();
        if (firstPartition >= 0 && TryReadKlv(input, firstPartition, out KlvSpan partitionKlv) && partitionKlv.ValueLength >= 64)
        {
            byte[] promoted = ReadKlv(input, partitionKlv);
            // Promote surviving body/footer partition to a closed/complete Header Partition.
            Buffer.BlockCopy(MxfClosedCompleteHeaderKey, 0, promoted, 0, 16);
            int valueOffset = 16 + partitionKlv.BerLength;
            BinaryPrimitives.WriteUInt64BigEndian(promoted.AsSpan(valueOffset + 8, 8), 0UL);
            BinaryPrimitives.WriteUInt64BigEndian(promoted.AsSpan(valueOffset + 16, 8), 0UL);
            BinaryPrimitives.WriteUInt64BigEndian(promoted.AsSpan(valueOffset + 24, 8), 0UL);
            uint bodySid = BinaryPrimitives.ReadUInt32BigEndian(promoted.AsSpan(valueOffset + 60, 4));
            if (bodySid == 0)
            {
                bodySid = 1;
                BinaryPrimitives.WriteUInt32BigEndian(promoted.AsSpan(valueOffset + 60, 4), bodySid);
            }
            output.Write(promoted, 0, promoted.Length);
            partitions.Add(new MxfPartitionInfo(0, bodySid));
        }
        else
        {
            byte[] synthetic = BuildSyntheticMxfHeaderPartition();
            output.Write(synthetic, 0, synthetic.Length);
            partitions.Add(new MxfPartitionInfo(0, 1));
        }

        long position = firstEssence;
        int essenceCount = 0;
        long rejected = Math.Max(0, firstEssence - Math.Max(0, firstPartition));
        while (position + 17 <= input.Length)
        {
            if (!TryReadKlv(input, position, out KlvSpan klv))
            {
                long next = FindNextSmpteUl(input, position + 1, Math.Min(input.Length, position + 64L * 1024 * 1024));
                if (next < 0)
                    break;
                rejected += next - position;
                position = next;
                continue;
            }

            byte[] key = ReadKey(input, position);
            if (BytesEqual(key, MxfRandomIndexPackKey) || IsAnyMxfPartitionKey(key))
            {
                position = klv.EndOffset;
                continue;
            }

            if (IsMxfEssenceKey(key))
            {
                CopyRange(input, output, position, klv.TotalLength);
                essenceCount++;
                if (essenceCount % 256 == 0)
                    progress?.Invoke($"MXF • Medya blokları {essenceCount:N0}");
            }
            position = klv.EndOffset;
        }

        if (essenceCount == 0)
            throw new InvalidDataException("MXF essence zinciri metadata kaybından sonra yeniden oluşturulamadı.");

        AppendMxfRandomIndexPack(output, partitions);
        output.Flush(true);
        long writtenLength = output.Length;
        output.Dispose(); // Release the exclusive writer before read-only validation.
        ValidateMxfReconstruction(destinationPath);

        string rejectedText = rejected > 0 ? $" {rejected:N0} metadata/uyumsuz bayt atlandı." : string.Empty;
        return new MediaRepairResult(
            true,
            $"MXF metadata-loss reconstruction tamamlandı; Header Partition yeniden üretildi ve {essenceCount:N0} essence KLV forensic zincire alındı.{rejectedText}",
            writtenLength);
    }

    private static List<ClusterSpan> CollectMatroskaClusters(
        FileStream input,
        Action<string>? progress,
        out Dictionary<ulong, TrackEvidence> evidence)
    {
        evidence = new Dictionary<ulong, TrackEvidence>();
        var result = new List<ClusterSpan>();
        long position = 0;
        while (position < input.Length)
        {
            long start = FindSignature(input, ClusterId, position, input.Length);
            if (start < 0)
                break;
            long next = FindSignature(input, ClusterId, start + ClusterId.Length, input.Length);
            long end = DetermineEbmlElementEnd(input, start, ClusterId.Length, next);
            if (end <= start || end > input.Length)
            {
                position = start + 1;
                continue;
            }

            if (AnalyzeCluster(input, start, end, evidence))
            {
                result.Add(new ClusterSpan(start, end - start));
                if (result.Count % 64 == 0)
                    progress?.Invoke($"MKV/WebM • Cluster Evidence {result.Count:N0}");
                position = end;
            }
            else
            {
                position = start + 1;
            }
        }
        return result;
    }

    private static bool AnalyzeCluster(FileStream input, long start, long end, Dictionary<ulong, TrackEvidence> evidence)
    {
        if (!TryReadEbmlSize(input, start + ClusterId.Length, out _, out int clusterSizeLength, out _))
            return false;
        long contentStart = start + ClusterId.Length + clusterSizeLength;
        long inspectEnd = Math.Min(end, start + MaxClusterProbe);
        if (contentStart >= inspectEnd)
            return false;

        bool hasTimecode = false;
        int validBlocks = 0;
        long position = contentStart;
        while (position < inspectEnd)
        {
            input.Position = position;
            int first = input.ReadByte();
            if (first < 0)
                break;

            if (first == TimecodeId[0])
                hasTimecode = true;

            if (first is 0xA3 or 0xA1)
            {
                if (TryReadEbmlSize(input, position + 1, out ulong blockSize, out int sizeLength, out bool unknown) &&
                    !unknown && blockSize >= 4 && blockSize <= 16UL * 1024 * 1024)
                {
                    long payloadStart = position + 1 + sizeLength;
                    long blockEnd;
                    try { blockEnd = checked(payloadStart + (long)blockSize); }
                    catch (OverflowException) { blockEnd = -1; }
                    if (blockEnd > payloadStart && blockEnd <= end && TryAnalyzeMatroskaBlock(input, payloadStart, (int)Math.Min(blockSize, 1024UL * 1024), evidence))
                    {
                        validBlocks++;
                        position = blockEnd;
                        continue;
                    }
                }
            }
            position++;
        }
        return hasTimecode && validBlocks > 0;
    }

    private static bool TryAnalyzeMatroskaBlock(
        FileStream input,
        long payloadStart,
        int inspectLength,
        Dictionary<ulong, TrackEvidence> evidence)
    {
        if (inspectLength < 4)
            return false;
        byte[] buffer = new byte[inspectLength];
        input.Position = payloadStart;
        int read = input.Read(buffer, 0, buffer.Length);
        if (read < 4)
            return false;
        if (!TryReadVInt(buffer.AsSpan(0, read), out ulong trackNumber, out int trackLength) || trackNumber == 0 || trackLength + 3 > read)
            return false;

        byte flags = buffer[trackLength + 2];
        if ((flags & 0x06) != 0)
            return true; // laced blocks are valid evidence for the Cluster but skipped for codec inference.

        int frameOffset = trackLength + 3;
        int frameLength = read - frameOffset;
        if (frameLength <= 0)
            return true;
        if (!evidence.TryGetValue(trackNumber, out TrackEvidence? track) || track is null)
        {
            track = new TrackEvidence(trackNumber);
            evidence[trackNumber] = track;
        }
        track.BlockCount++;
        AnalyzeCodecEvidence(buffer.AsSpan(frameOffset, frameLength), track);
        return true;
    }

    private static void AnalyzeCodecEvidence(ReadOnlySpan<byte> payload, TrackEvidence evidence)
    {
        if (payload.Length < 2)
            return;

        bool foundH264 = false;
        bool foundH265 = false;
        int cursor = 0;
        int nals = 0;
        while (cursor + 5 < payload.Length && nals < 32)
        {
            int startCode = FindAnnexBStartCode(payload, cursor, out int prefixLength);
            if (startCode < 0)
                break;
            int nalStart = startCode + prefixLength;
            if (nalStart >= payload.Length)
                break;
            byte h264Type = (byte)(payload[nalStart] & 0x1F);
            byte h265Type = (byte)((payload[nalStart] >> 1) & 0x3F);
            if (h264Type is 1 or 5 or 6 or 7 or 8 or 9)
            {
                evidence.H264Score += h264Type is 7 or 8 ? 4 : 1;
                foundH264 = true;
                int h264Next = FindAnnexBStartCode(payload, nalStart + 1, out _);
                int h264Length = (h264Next > nalStart ? h264Next : payload.Length) - nalStart;
                if (h264Length > 1 && h264Length < 65536)
                {
                    byte[] nal = payload.Slice(nalStart, h264Length).ToArray();
                    if (h264Type == 7)
                    {
                        evidence.H264Sps ??= nal;
                        if (TryParseH264Sps(nal, out int h264Width, out int h264Height))
                        {
                            evidence.Width = h264Width;
                            evidence.Height = h264Height;
                        }
                    }
                    else if (h264Type == 8)
                    {
                        evidence.H264Pps ??= nal;
                    }
                }
            }
            if (h265Type is 19 or 20 or 21 or 32 or 33 or 34 or 35)
            {
                evidence.H265Score += h265Type is 32 or 33 or 34 ? 4 : 1;
                foundH265 = true;
                int h265Next = FindAnnexBStartCode(payload, nalStart + 2, out _);
                int h265Length = (h265Next > nalStart ? h265Next : payload.Length) - nalStart;
                if (h265Length > 2 && h265Length < 65536)
                {
                    byte[] nal = payload.Slice(nalStart, h265Length).ToArray();
                    if (h265Type == 32)
                        evidence.H265Vps ??= nal;
                    else if (h265Type == 33)
                    {
                        evidence.H265Sps ??= nal;
                        if (TryParseH265Sps(nal, out int h265Width, out int h265Height))
                        {
                            evidence.Width = h265Width;
                            evidence.Height = h265Height;
                        }
                    }
                    else if (h265Type == 34)
                        evidence.H265Pps ??= nal;
                }
            }
            cursor = nalStart + 1;
            nals++;
        }

        if (!foundH264 && !foundH265 && payload.Length >= 8)
        {
            int lp = 0;
            int parsed = 0;
            while (lp + 5 <= payload.Length && parsed < 32)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(lp, 4));
                if (length == 0 || length > payload.Length - lp - 4 || length > 16 * 1024 * 1024)
                    break;
                int nalStart = lp + 4;
                int nalLength = (int)length;
                int h264Type = payload[nalStart] & 0x1F;
                int h265Type = (payload[nalStart] >> 1) & 0x3F;
                if (h264Type is 1 or 5 or 7 or 8)
                {
                    evidence.H264Score += h264Type is 7 or 8 ? 4 : 2;
                    evidence.H264LengthPrefixedScore += h264Type is 7 or 8 ? 4 : 2;
                    if (h264Type == 7)
                    {
                        byte[] nal = payload.Slice(nalStart, nalLength).ToArray();
                        evidence.H264Sps ??= nal;
                        if (TryParseH264Sps(nal, out int lpH264Width, out int lpH264Height))
                        {
                            evidence.Width = lpH264Width;
                            evidence.Height = lpH264Height;
                        }
                    }
                    else if (h264Type == 8)
                    {
                        evidence.H264Pps ??= payload.Slice(nalStart, nalLength).ToArray();
                    }
                }
                if (h265Type is 19 or 20 or 21 or 32 or 33 or 34)
                {
                    evidence.H265Score += h265Type is 32 or 33 or 34 ? 4 : 2;
                    evidence.H265LengthPrefixedScore += h265Type is 32 or 33 or 34 ? 4 : 2;
                    byte[] nal = payload.Slice(nalStart, nalLength).ToArray();
                    if (h265Type == 32) evidence.H265Vps ??= nal;
                    else if (h265Type == 33)
                    {
                        evidence.H265Sps ??= nal;
                        if (TryParseH265Sps(nal, out int lpH265Width, out int lpH265Height))
                        {
                            evidence.Width = lpH265Width;
                            evidence.Height = lpH265Height;
                        }
                    }
                    else if (h265Type == 34) evidence.H265Pps ??= nal;
                }
                lp = checked(nalStart + nalLength);
                parsed++;
            }
        }

        if (payload.Length >= 10 && (payload[0] & 1) == 0 && payload[3] == 0x9D && payload[4] == 0x01 && payload[5] == 0x2A)
        {
            evidence.Vp8Score += 6;
            evidence.Width = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3FFF;
            evidence.Height = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3FFF;
        }
        else if ((payload[0] & 0xC0) == 0x80)
        {
            evidence.Vp9Score++;
        }

        int obuType = (payload[0] >> 3) & 0x0F;
        if ((payload[0] & 0x80) == 0 && (payload[0] & 0x01) == 0 && obuType is >= 1 and <= 15)
        {
            if (obuType == 1)
                evidence.Av1Score += 5;
            else if (obuType is 3 or 6)
                evidence.Av1Score++;
        }

        if (TryParseAdts(payload, out int sampleRate, out int channels, out byte[] codecPrivate))
        {
            evidence.AacScore += 6;
            evidence.SampleRate = sampleRate;
            evidence.Channels = channels;
            evidence.CodecPrivate = codecPrivate;
        }
        if (payload.Length >= 7 && payload[0] == 0x01 && Encoding.ASCII.GetString(payload.Slice(1, 6)) == "vorbis")
            evidence.VorbisScore += 6;
    }

    private static List<InferredTrack> InferTracks(Dictionary<ulong, TrackEvidence> evidence)
    {
        var tracks = new List<InferredTrack>();
        foreach (TrackEvidence item in evidence.Values)
        {
            string? codecId = null;
            byte type = 0;
            int score = 0;

            // Cluster bytes are copied losslessly. For AVC/HEVC, therefore only accept
            // length-prefixed samples; Annex-B would require rewriting every Block payload.
            // Parameter sets are mandatory because CodecPrivate must be reconstructed.
            bool h264Eligible = item.H264LengthPrefixedScore >= 3 && item.H264Sps is not null && item.H264Pps is not null;
            bool h265Eligible = item.H265LengthPrefixedScore >= 3 && item.H265Vps is not null && item.H265Sps is not null && item.H265Pps is not null;
            if (h264Eligible && item.H264Score > score) { score = item.H264Score; codecId = "V_MPEG4/ISO/AVC"; type = 1; }
            if (h265Eligible && item.H265Score > score) { score = item.H265Score; codecId = "V_MPEGH/ISO/HEVC"; type = 1; }
            if (item.Vp8Score > score) { score = item.Vp8Score; codecId = "V_VP8"; type = 1; }

            // AAC ADTS, Vorbis, VP9 and AV1 evidence is retained for forensic classification,
            // but track synthesis is deferred: their Block framing / CodecPrivate cannot be
            // proven losslessly here without the dedicated codec-level reconstruction layer.
            int requiredScore = 3;
            if (codecId is null || score < requiredScore)
                continue;

            byte[]? codecPrivate = item.CodecPrivate;
            if (codecId == "V_MPEG4/ISO/AVC")
            {
                byte[]? sps = item.H264Sps;
                byte[]? pps = item.H264Pps;
                if (sps is null || pps is null)
                    continue;
                codecPrivate = BuildAvcDecoderConfigurationRecord(sps, pps);
            }
            else if (codecId == "V_MPEGH/ISO/HEVC")
            {
                byte[]? vps = item.H265Vps;
                byte[]? sps = item.H265Sps;
                byte[]? pps = item.H265Pps;
                if (vps is null || sps is null || pps is null)
                    continue;
                codecPrivate = BuildHevcDecoderConfigurationRecord(vps, sps, pps);
            }

            tracks.Add(new InferredTrack(
                item.TrackNumber,
                type,
                codecId,
                item.Width,
                item.Height,
                item.SampleRate,
                item.Channels,
                codecPrivate));
        }
        tracks.Sort((a, b) => a.TrackNumber.CompareTo(b.TrackNumber));
        return tracks;
    }

    private static byte[] BuildAvcDecoderConfigurationRecord(byte[] sps, byte[] pps)
    {
        if (sps.Length < 4 || sps.Length > ushort.MaxValue || pps.Length == 0 || pps.Length > ushort.MaxValue)
            throw new InvalidDataException("AVC parameter-set geometrisi geçersiz.");
        byte[] result = new byte[11 + sps.Length + pps.Length];
        int cursor = 0;
        result[cursor++] = 1;
        result[cursor++] = sps[1];
        result[cursor++] = sps[2];
        result[cursor++] = sps[3];
        result[cursor++] = 0xFF; // 4-byte NAL length.
        result[cursor++] = 0xE1; // one SPS.
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(cursor, 2), (ushort)sps.Length); cursor += 2;
        Buffer.BlockCopy(sps, 0, result, cursor, sps.Length); cursor += sps.Length;
        result[cursor++] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(cursor, 2), (ushort)pps.Length); cursor += 2;
        Buffer.BlockCopy(pps, 0, result, cursor, pps.Length);
        return result;
    }

    private static byte[] BuildHevcDecoderConfigurationRecord(byte[] vps, byte[] sps, byte[] pps)
    {
        if (vps.Length > ushort.MaxValue || sps.Length > ushort.MaxValue || pps.Length > ushort.MaxValue)
            throw new InvalidDataException("HEVC parameter-set geometrisi geçersiz.");
        int arraysLength = 3 * 5 + vps.Length + sps.Length + pps.Length;
        byte[] result = new byte[23 + arraysLength];
        HevcProfile profile = ParseHevcProfile(sps);
        result[0] = 1;
        result[1] = (byte)((profile.Space << 6) | (profile.Tier << 5) | profile.Idc);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(2, 4), profile.Compat);
        result[6] = (byte)(profile.Constraints >> 40);
        result[7] = (byte)(profile.Constraints >> 32);
        result[8] = (byte)(profile.Constraints >> 24);
        result[9] = (byte)(profile.Constraints >> 16);
        result[10] = (byte)(profile.Constraints >> 8);
        result[11] = (byte)profile.Constraints;
        result[12] = profile.Level;
        result[13] = 0xF0;
        result[14] = 0x00;
        result[15] = 0xFC;
        result[16] = 0xFD; // chromaFormat=1 fallback; SPS remains authoritative for decoder geometry.
        result[17] = 0xF8;
        result[18] = 0xF8;
        result[21] = 0x03; // lengthSizeMinusOne=3.
        result[22] = 3;
        int cursor = 23;
        cursor = WriteHevcArray(result, cursor, 32, vps);
        cursor = WriteHevcArray(result, cursor, 33, sps);
        _ = WriteHevcArray(result, cursor, 34, pps);
        return result;
    }

    private static int WriteHevcArray(byte[] target, int cursor, int nalType, byte[] nal)
    {
        target[cursor++] = (byte)(0x80 | (nalType & 0x3F));
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(cursor, 2), 1); cursor += 2;
        BinaryPrimitives.WriteUInt16BigEndian(target.AsSpan(cursor, 2), (ushort)nal.Length); cursor += 2;
        Buffer.BlockCopy(nal, 0, target, cursor, nal.Length);
        return cursor + nal.Length;
    }

    private static HevcProfile ParseHevcProfile(byte[] sps)
    {
        if (sps.Length < 6)
            return new HevcProfile(0, 0, 1, 0, 0, 120);

        var bits = new BitReader(RemoveEmulationPrevention(sps.AsSpan(2)));
        if (!bits.TrySkip(4) ||
            !bits.TryReadBits(3, out _) ||
            !bits.TrySkip(1) ||
            !bits.TryReadBits(2, out uint space) ||
            !bits.TryReadBits(1, out uint tier) ||
            !bits.TryReadBits(5, out uint idc) ||
            !bits.TryReadBits(32, out uint compat) ||
            !bits.TryReadBits64(48, out ulong constraints) ||
            !bits.TryReadBits(8, out uint level))
        {
            return new HevcProfile(0, 0, 1, 0, 0, 120);
        }

        return new HevcProfile((byte)space, (byte)tier, (byte)idc, compat, constraints, (byte)level);
    }

    private static byte[] BuildEbmlHeader(string docType)
    {
        using var payload = new MemoryStream();
        WriteEbmlUInt(payload, new byte[] { 0x42, 0x86 }, 1);
        WriteEbmlUInt(payload, new byte[] { 0x42, 0xF7 }, 1);
        WriteEbmlUInt(payload, new byte[] { 0x42, 0xF2 }, 4);
        WriteEbmlUInt(payload, new byte[] { 0x42, 0xF3 }, 8);
        WriteEbmlString(payload, new byte[] { 0x42, 0x82 }, docType);
        WriteEbmlUInt(payload, new byte[] { 0x42, 0x87 }, 4);
        WriteEbmlUInt(payload, new byte[] { 0x42, 0x85 }, 2);
        return WrapEbmlElement(EbmlId, payload.ToArray());
    }

    private static byte[] BuildMatroskaInfo()
    {
        using var payload = new MemoryStream();
        WriteEbmlUInt(payload, new byte[] { 0x2A, 0xD7, 0xB1 }, 1_000_000);
        WriteEbmlString(payload, new byte[] { 0x4D, 0x80 }, "NSX Veri Kurtarma Pro");
        WriteEbmlString(payload, new byte[] { 0x57, 0x41 }, "NSX Metadata Reconstruction V2");
        return WrapEbmlElement(InfoId, payload.ToArray());
    }

    private static byte[] BuildMatroskaTracks(List<InferredTrack> tracks)
    {
        using var payload = new MemoryStream();
        foreach (InferredTrack track in tracks)
        {
            using var entry = new MemoryStream();
            WriteEbmlUInt(entry, new byte[] { 0xD7 }, track.TrackNumber);
            WriteEbmlUInt(entry, new byte[] { 0x73, 0xC5 }, track.TrackNumber);
            WriteEbmlUInt(entry, new byte[] { 0x83 }, track.TrackType);
            WriteEbmlUInt(entry, new byte[] { 0x88 }, 1);
            WriteEbmlString(entry, new byte[] { 0x86 }, track.CodecId);
            if (track.CodecPrivate is { Length: > 0 })
                WriteEbmlBinary(entry, new byte[] { 0x63, 0xA2 }, track.CodecPrivate);

            if (track.TrackType == 1 && track.Width > 0 && track.Height > 0)
            {
                using var video = new MemoryStream();
                WriteEbmlUInt(video, new byte[] { 0xB0 }, (ulong)track.Width);
                WriteEbmlUInt(video, new byte[] { 0xBA }, (ulong)track.Height);
                entry.Write(WrapEbmlElement(new byte[] { 0xE0 }, video.ToArray()));
            }
            else if (track.TrackType == 2)
            {
                using var audio = new MemoryStream();
                if (track.SampleRate > 0)
                    WriteEbmlFloat(audio, new byte[] { 0xB5 }, track.SampleRate);
                if (track.Channels > 0)
                    WriteEbmlUInt(audio, new byte[] { 0x9F }, (ulong)track.Channels);
                entry.Write(WrapEbmlElement(new byte[] { 0xE1 }, audio.ToArray()));
            }
            payload.Write(WrapEbmlElement(new byte[] { 0xAE }, entry.ToArray()));
        }
        return WrapEbmlElement(TracksId, payload.ToArray());
    }

    private static byte[]? TryCopyEbmlHeader(FileStream input)
    {
        long start = FindSignature(input, EbmlId, 0, Math.Min(input.Length, 1024 * 1024));
        if (start < 0 || !TryReadEbmlSize(input, start + EbmlId.Length, out ulong size, out int sizeLength, out bool unknown) || unknown || size > 1024 * 1024)
            return null;
        long total = EbmlId.Length + sizeLength + (long)size;
        if (start + total > input.Length || total <= 0 || total > int.MaxValue)
            return null;
        byte[] result = new byte[(int)total];
        input.Position = start;
        ReadExactly(input, result);
        return result;
    }

    private static byte[] BuildAsfHeader(byte[] fileProperties, List<byte[]> streamProperties)
    {
        byte[] headerExtension = BuildAsfHeaderExtension();
        int childBytes = checked(fileProperties.Length + headerExtension.Length);
        foreach (byte[] stream in streamProperties)
            childBytes = checked(childBytes + stream.Length);
        int length = checked(30 + childBytes);
        byte[] header = new byte[length];
        Buffer.BlockCopy(AsfHeaderGuid, 0, header, 0, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(16, 8), (ulong)length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24, 4), (uint)(2 + streamProperties.Count));
        header[28] = 0x01;
        header[29] = 0x02;
        int cursor = 30;
        Buffer.BlockCopy(fileProperties, 0, header, cursor, fileProperties.Length);
        cursor += fileProperties.Length;
        foreach (byte[] stream in streamProperties)
        {
            Buffer.BlockCopy(stream, 0, header, cursor, stream.Length);
            cursor += stream.Length;
        }
        Buffer.BlockCopy(headerExtension, 0, header, cursor, headerExtension.Length);
        return header;
    }

    private static byte[] BuildAsfHeaderExtension()
    {
        byte[] result = new byte[46];
        Buffer.BlockCopy(AsfHeaderExtensionGuid, 0, result, 0, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16, 8), 46);
        Buffer.BlockCopy(AsfReserved1Guid, 0, result, 24, 16);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(40, 2), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(42, 4), 0);
        return result;
    }

    private static byte[] BuildAsfFileProperties(byte[] fileId, uint packetSize, long packetCount, ulong playDuration, ulong preroll)
    {
        byte[] result = new byte[104];
        Buffer.BlockCopy(AsfFilePropertiesGuid, 0, result, 0, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16, 8), 104);
        Buffer.BlockCopy(fileId, 0, result, 24, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(56, 8), (ulong)packetCount);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(64, 8), playDuration);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(72, 8), playDuration);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(80, 8), preroll);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(88, 4), 0x02); // seekable flag
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(92, 4), packetSize);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(96, 4), packetSize);
        return result;
    }

    private static void PatchAsfFileSizeAndPacketCount(byte[] fileProperties, long fileSize, long packetCount, uint packetSize)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(fileProperties.AsSpan(40, 8), (ulong)fileSize);
        BinaryPrimitives.WriteUInt64LittleEndian(fileProperties.AsSpan(56, 8), (ulong)packetCount);
        BinaryPrimitives.WriteUInt32LittleEndian(fileProperties.AsSpan(92, 4), packetSize);
        BinaryPrimitives.WriteUInt32LittleEndian(fileProperties.AsSpan(96, 4), packetSize);
    }

    private static List<byte[]> CollectOrphanedAsfStreamProperties(FileStream input, long dataStart)
    {
        var result = new List<byte[]>();
        long position = 0;
        long end = Math.Min(dataStart, 64L * 1024 * 1024);
        while (position < end && result.Count < 16)
        {
            long found = FindSignature(input, AsfStreamPropertiesGuid, position, end);
            if (found < 0 || found + 24 > input.Length)
                break;
            byte[] header = new byte[24];
            input.Position = found;
            if (input.Read(header, 0, header.Length) != header.Length)
                break;
            ulong size = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16, 8));
            if (size >= 78 && size <= 16UL * 1024 * 1024 && found + (long)size <= dataStart && size <= int.MaxValue)
            {
                byte[] objectBytes = new byte[(int)size];
                input.Position = found;
                ReadExactly(input, objectBytes);
                result.Add(objectBytes);
                position = found + (long)size;
            }
            else
            {
                position = found + 1;
            }
        }
        return result;
    }

    private static bool TryLocateRawAsfPacketRun(FileStream input, out long start, out uint packetSize, out long packetCount)
    {
        start = 0;
        packetSize = 0;
        packetCount = 0;
        byte[] commonPrefix = { 0x82, 0x00, 0x00 };
        long searchEnd = Math.Min(input.Length, 64L * 1024 * 1024);
        long first = FindSignature(input, commonPrefix, 0, searchEnd);
        while (first >= 0)
        {
            long second = FindSignature(input, commonPrefix, first + commonPrefix.Length, Math.Min(input.Length, first + 2L * 1024 * 1024));
            if (second < 0)
                return false;
            long distance = second - first;
            if (distance is >= 64 and <= ushort.MaxValue)
            {
                long third = second + distance;
                if (third + commonPrefix.Length <= input.Length && SignatureAt(input, commonPrefix, third))
                {
                    long count = (input.Length - first) / distance;
                    if (count >= 3 && distance <= uint.MaxValue)
                    {
                        start = first;
                        packetSize = (uint)distance;
                        packetCount = count;
                        return true;
                    }
                }
            }
            first = second;
        }
        return false;
    }

    private static bool SignatureAt(FileStream input, byte[] signature, long offset)
    {
        if (offset < 0 || offset + signature.Length > input.Length)
            return false;
        byte[] data = new byte[signature.Length];
        input.Position = offset;
        return input.Read(data, 0, data.Length) == data.Length && BytesEqual(data, signature);
    }

    private static void BuildDeterministicAsfFileId(long length, long payloadStart, uint packetSize, byte[] destination)
    {
        if (destination.Length < 16)
            throw new ArgumentException("ASF File ID buffer en az 16 bayt olmalıdır.", nameof(destination));
        BinaryPrimitives.WriteInt64LittleEndian(destination.AsSpan(0, 8), length);
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(8, 4), unchecked((int)packetSize));
        BinaryPrimitives.WriteInt32LittleEndian(destination.AsSpan(12, 4), unchecked((int)(payloadStart ^ (payloadStart >> 32))));
    }

    private static uint TryRecoverAsfPacketSize(FileStream input, long dataStart, long payloadBytes, ulong declaredPackets)
    {
        long fileProps = FindSignature(input, AsfFilePropertiesGuid, 0, Math.Min(dataStart, 64L * 1024 * 1024));
        if (fileProps >= 0 && fileProps + 104 <= input.Length)
        {
            byte[] fp = new byte[104];
            input.Position = fileProps;
            ReadExactly(input, fp);
            uint min = BinaryPrimitives.ReadUInt32LittleEndian(fp.AsSpan(92, 4));
            uint max = BinaryPrimitives.ReadUInt32LittleEndian(fp.AsSpan(96, 4));
            uint packet = max != 0 ? max : min;
            if (packet is >= 64 and <= 16 * 1024 * 1024)
                return packet;
        }

        if (declaredPackets > 0 && declaredPackets <= (ulong)long.MaxValue)
        {
            long count = (long)declaredPackets;
            if (payloadBytes >= count && payloadBytes % count == 0)
            {
                long candidate = payloadBytes / count;
                if (candidate is >= 64 and <= 16 * 1024 * 1024)
                    return (uint)candidate;
            }
        }
        return 0;
    }

    private static bool TryInferAsfVideoConfiguration(FileStream input, long start, long length, out AsfVideoConfig config)
    {
        config = default;
        long end = Math.Min(input.Length, start + length);
        byte[] buffer = new byte[1024 * 1024 + 64];
        long position = start;
        int carry = 0;
        while (position < end)
        {
            int request = (int)Math.Min(1024 * 1024, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            for (int i = 0; i + 40 <= count; i++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i, 4)) != 40)
                    continue;
                int width = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 4, 4));
                int heightRaw = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(i + 8, 4));
                ushort planes = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 12, 2));
                uint compression = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 16, 4));
                string fourCc = Encoding.ASCII.GetString(buffer, i + 16, 4);
                if (width is <= 0 or > 16384 || Math.Abs((long)heightRaw) is <= 0 or > 16384 || planes != 1)
                    continue;
                if (fourCc is not ("WVC1" or "WMV3" or "WMVA" or "MSS1" or "MSS2"))
                    continue;
                byte[] bmi = new byte[40];
                Buffer.BlockCopy(buffer, i, bmi, 0, bmi.Length);
                config = new AsfVideoConfig(width, Math.Abs(heightRaw), compression, bmi);
                return true;
            }
            carry = Math.Min(63, count);
            if (carry > 0)
                Buffer.BlockCopy(buffer, count - carry, buffer, 0, carry);
            position += read;
        }
        return false;
    }

    private static byte[] BuildAsfVideoStreamProperties(AsfVideoConfig config, ushort streamNumber)
    {
        // ASF video type-specific data: width(4), height(4), reserved flags(1), format data size(2), BITMAPINFOHEADER.
        byte[] typeSpecific = new byte[11 + config.BitmapInfoHeader.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(typeSpecific.AsSpan(0, 4), (uint)config.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(typeSpecific.AsSpan(4, 4), (uint)config.Height);
        typeSpecific[8] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(typeSpecific.AsSpan(9, 2), (ushort)config.BitmapInfoHeader.Length);
        Buffer.BlockCopy(config.BitmapInfoHeader, 0, typeSpecific, 11, config.BitmapInfoHeader.Length);

        int length = 78 + typeSpecific.Length;
        byte[] result = new byte[length];
        Buffer.BlockCopy(AsfStreamPropertiesGuid, 0, result, 0, 16);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16, 8), (ulong)length);
        Buffer.BlockCopy(AsfVideoMediaGuid, 0, result, 24, 16);
        Buffer.BlockCopy(AsfNoErrorCorrectionGuid, 0, result, 40, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(64, 4), (uint)typeSpecific.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(68, 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(72, 2), (ushort)(streamNumber & 0x7F));
        Buffer.BlockCopy(typeSpecific, 0, result, 78, typeSpecific.Length);
        return result;
    }

    private static byte[] BuildSyntheticMxfHeaderPartition()
    {
        byte[] value = new byte[88];
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(2, 2), 3);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(8, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(16, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(24, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(32, 8), 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(40, 8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(48, 4), 0);
        BinaryPrimitives.WriteUInt64BigEndian(value.AsSpan(52, 8), 0);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(60, 4), 1);
        Buffer.BlockCopy(Op1aOperationalPattern, 0, value, 64, 16);
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(80, 4), 0); // EssenceContainer batch count unknown.
        BinaryPrimitives.WriteUInt32BigEndian(value.AsSpan(84, 4), 16);

        byte[] ber = EncodeBerLength((ulong)value.Length);
        byte[] result = new byte[16 + ber.Length + value.Length];
        Buffer.BlockCopy(MxfClosedCompleteHeaderKey, 0, result, 0, 16);
        Buffer.BlockCopy(ber, 0, result, 16, ber.Length);
        Buffer.BlockCopy(value, 0, result, 16 + ber.Length, value.Length);
        return result;
    }

    private static long FindAnyMxfPartition(FileStream input, long start, long end)
    {
        long position = start;
        while (position < end)
        {
            long found = FindSignature(input, MxfPartitionPrefix, position, end);
            if (found < 0 || found + 16 > input.Length)
                return -1;
            byte[] key = ReadKey(input, found);
            if (IsAnyMxfPartitionKey(key))
                return found;
            position = found + 1;
        }
        return -1;
    }

    private static bool IsAnyMxfPartitionKey(byte[] key)
    {
        if (key.Length != 16 || !StartsWith(key, MxfPartitionPrefix))
            return false;
        return key[13] is >= 0x02 and <= 0x04 && key[14] is >= 0x01 and <= 0x04 && key[15] == 0x00;
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

    private static bool IsMxfEssenceKey(byte[] key) =>
        StartsWith(key, MxfEssencePrefix) || StartsWith(key, MxfAvidEssencePrefix) || StartsWith(key, MxfCanopusEssencePrefix);

    private static bool TryReadKlv(FileStream input, long offset, out KlvSpan klv)
    {
        klv = default;
        if (offset < 0 || offset + 17 > input.Length)
            return false;
        byte[] key = ReadKey(input, offset);
        if (!IsSmpteUl(key))
            return false;
        if (!TryReadBerLength(input, offset + 16, out ulong valueLength, out int berLength) || valueLength > (ulong)long.MaxValue)
            return false;
        long valueStart;
        long end;
        try
        {
            valueStart = checked(offset + 16L + berLength);
            end = checked(valueStart + (long)valueLength);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (end <= valueStart || end > input.Length)
            return false;
        klv = new KlvSpan(offset, berLength, (long)valueLength, end);
        return true;
    }

    private static byte[] ReadKlv(FileStream input, KlvSpan span)
    {
        if (span.TotalLength > int.MaxValue)
            throw new InvalidDataException("MXF KLV tek parça için çok büyük.");
        byte[] result = new byte[(int)span.TotalLength];
        input.Position = span.Offset;
        ReadExactly(input, result);
        return result;
    }

    private static byte[] ReadKey(FileStream input, long offset)
    {
        byte[] key = new byte[16];
        input.Position = offset;
        if (input.Read(key, 0, key.Length) != key.Length)
            return Array.Empty<byte>();
        return key;
    }

    private static long FindNextSmpteUl(FileStream input, long start, long end) =>
        FindSignature(input, new byte[] { 0x06, 0x0E, 0x2B, 0x34 }, start, end);

    private static bool IsSmpteUl(byte[] key) =>
        key.Length == 16 && key[0] == 0x06 && key[1] == 0x0E && key[2] == 0x2B && key[3] == 0x34;

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

    private static void ValidateMatroskaReconstruction(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FindSignature(input, EbmlId, 0, Math.Min(input.Length, 1024 * 1024)) != 0 ||
            FindSignature(input, SegmentId, 0, Math.Min(input.Length, 1024 * 1024)) < 0 ||
            FindSignature(input, InfoId, 0, Math.Min(input.Length, 8L * 1024 * 1024)) < 0 ||
            FindSignature(input, TracksId, 0, Math.Min(input.Length, 8L * 1024 * 1024)) < 0 ||
            FindSignature(input, ClusterId, 0, input.Length) < 0)
            throw new InvalidDataException("MKV/WebM reconstructed EBML/Segment/Info/Tracks/Cluster doğrulaması başarısız.");
    }

    private static void ValidateAsfReconstruction(string path, uint packetSize, long packetCount)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (FindSignature(input, AsfHeaderGuid, 0, Math.Min(input.Length, 1024 * 1024)) != 0)
            throw new InvalidDataException("ASF reconstructed Header Object doğrulanamadı.");
        long data = FindSignature(input, AsfDataGuid, 0, input.Length);
        if (data < 0 || input.Length < data + 50L + packetSize || (input.Length - data - 50) / packetSize < packetCount)
            throw new InvalidDataException("ASF reconstructed Data Object/packet geometrisi doğrulanamadı.");
        if (FindSignature(input, AsfFilePropertiesGuid, 0, data) < 0 ||
            FindSignature(input, AsfStreamPropertiesGuid, 0, data) < 0 ||
            FindSignature(input, AsfHeaderExtensionGuid, 0, data) < 0)
            throw new InvalidDataException("ASF reconstructed File/Stream/Header Extension Properties doğrulanamadı.");
    }

    private static void ValidateMxfReconstruction(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] key = ReadKey(input, 0);
        if (!IsAnyMxfPartitionKey(key) || key[13] != 0x02)
            throw new InvalidDataException("MXF reconstructed Header Partition doğrulanamadı.");
        if (FindMxfEssence(input, 0, input.Length) < 0 || FindSignature(input, MxfRandomIndexPackKey, 0, input.Length) < 0)
            throw new InvalidDataException("MXF reconstructed essence/RIP doğrulaması başarısız.");
    }

    private static long DetermineEbmlElementEnd(FileStream input, long start, int idLength, long nextSameId)
    {
        if (TryReadEbmlSize(input, start + idLength, out ulong size, out int sizeLength, out bool unknown) && !unknown && size <= (ulong)long.MaxValue)
        {
            long content = start + idLength + sizeLength;
            try
            {
                long end = checked(content + (long)size);
                if (end > content && end <= input.Length)
                    return end;
            }
            catch (OverflowException) { }
        }
        return nextSameId > start ? nextSameId : input.Length;
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
        int mask = 0x80;
        int len = 1;
        while (len <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            len++;
        }
        if (len > 8 || offset + len > input.Length)
            return false;
        ulong result = (ulong)(first & (mask - 1));
        bool allOnes = (first & (mask - 1)) == mask - 1;
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

    private static bool TryReadVInt(ReadOnlySpan<byte> data, out ulong value, out int length)
    {
        value = 0;
        length = 0;
        if (data.Length == 0 || data[0] == 0)
            return false;
        int mask = 0x80;
        int len = 1;
        while (len <= 8 && (data[0] & mask) == 0)
        {
            mask >>= 1;
            len++;
        }
        if (len > 8 || data.Length < len)
            return false;
        ulong result = (ulong)(data[0] & (mask - 1));
        for (int i = 1; i < len; i++)
            result = (result << 8) | data[i];
        value = result;
        length = len;
        return true;
    }

    private static int FindAnnexBStartCode(ReadOnlySpan<byte> data, int start, out int prefixLength)
    {
        prefixLength = 0;
        for (int i = Math.Max(0, start); i + 3 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1)
                {
                    prefixLength = 3;
                    return i;
                }
                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
                {
                    prefixLength = 4;
                    return i;
                }
            }
        }
        return -1;
    }

    private static bool TryParseAdts(ReadOnlySpan<byte> data, out int sampleRate, out int channels, out byte[] codecPrivate)
    {
        sampleRate = 0;
        channels = 0;
        codecPrivate = Array.Empty<byte>();
        if (data.Length < 7 || data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
            return false;
        int profile = ((data[2] >> 6) & 0x03) + 1;
        int sampleIndex = (data[2] >> 2) & 0x0F;
        int[] rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        if (sampleIndex >= rates.Length)
            return false;
        channels = ((data[2] & 1) << 2) | ((data[3] >> 6) & 0x03);
        if (channels <= 0 || channels > 8)
            return false;
        int frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | ((data[5] >> 5) & 0x07);
        if (frameLength < 7 || frameLength > data.Length)
            return false;
        sampleRate = rates[sampleIndex];
        int audioObjectType = Math.Clamp(profile, 1, 4);
        ushort asc = (ushort)((audioObjectType << 11) | (sampleIndex << 7) | (channels << 3));
        codecPrivate = new byte[] { (byte)(asc >> 8), (byte)asc };
        return true;
    }

    private static bool TryParseH264Sps(byte[] nal, out int width, out int height)
    {
        width = height = 0;
        if (nal.Length < 4 || (nal[0] & 0x1F) != 7)
            return false;
        byte[] rbsp = RemoveEmulationPrevention(nal.AsSpan(1));
        var bits = new BitReader(rbsp);
        if (!bits.TryReadBits(8, out uint profile) || !bits.TrySkip(16) || !bits.TryReadUE(out _))
            return false;
        uint chroma = 1;
        bool separate = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            if (!bits.TryReadUE(out chroma)) return false;
            if (chroma == 3 && !bits.TryReadBit(out separate)) return false;
            if (!bits.TryReadUE(out _) || !bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadBit(out bool scaling)) return false;
            if (scaling)
            {
                int count = chroma != 3 ? 8 : 12;
                for (int i = 0; i < count; i++)
                {
                    if (!bits.TryReadBit(out bool present)) return false;
                    if (present && !SkipScalingList(bits, i < 6 ? 16 : 64)) return false;
                }
            }
        }
        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint pocType)) return false;
        if (pocType == 0) { if (!bits.TryReadUE(out _)) return false; }
        else if (pocType == 1)
        {
            if (!bits.TrySkip(1) || !bits.TryReadSE(out _) || !bits.TryReadSE(out _) || !bits.TryReadUE(out uint cycle)) return false;
            for (uint i = 0; i < cycle; i++) if (!bits.TryReadSE(out _)) return false;
        }
        if (!bits.TryReadUE(out _) || !bits.TrySkip(1) || !bits.TryReadUE(out uint widthMbs) || !bits.TryReadUE(out uint heightMaps) || !bits.TryReadBit(out bool frameOnly)) return false;
        if (!frameOnly && !bits.TrySkip(1)) return false;
        if (!bits.TrySkip(1) || !bits.TryReadBit(out bool crop)) return false;
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (crop && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;
        int baseWidth = checked((int)((widthMbs + 1) * 16));
        int baseHeight = checked((int)((2 - (frameOnly ? 1 : 0)) * (heightMaps + 1) * 16));
        uint chromaArray = separate ? 0u : chroma;
        int subWidth = chromaArray is 1 or 2 ? 2 : 1;
        int subHeight = chromaArray == 1 ? 2 : 1;
        int cropX = chromaArray == 0 ? 1 : subWidth;
        int cropY = chromaArray == 0 ? 2 - (frameOnly ? 1 : 0) : subHeight * (2 - (frameOnly ? 1 : 0));
        width = baseWidth - checked((int)((left + right) * (uint)cropX));
        height = baseHeight - checked((int)((top + bottom) * (uint)cropY));
        return width > 0 && height > 0;
    }

    private static bool TryParseH265Sps(byte[] nal, out int width, out int height)
    {
        width = height = 0;
        if (nal.Length < 6 || ((nal[0] >> 1) & 0x3F) != 33)
            return false;
        byte[] rbsp = RemoveEmulationPrevention(nal.AsSpan(2));
        var bits = new BitReader(rbsp);
        if (!bits.TrySkip(4) || !bits.TryReadBits(3, out uint layers) || !bits.TrySkip(1)) return false;
        if (!bits.TrySkip(2 + 1 + 5 + 32 + 48 + 8)) return false;
        bool[] profilePresent = new bool[(int)layers];
        bool[] levelPresent = new bool[(int)layers];
        for (int i = 0; i < layers; i++)
            if (!bits.TryReadBit(out profilePresent[i]) || !bits.TryReadBit(out levelPresent[i])) return false;
        if (layers > 0) for (int i = (int)layers; i < 8; i++) if (!bits.TrySkip(2)) return false;
        for (int i = 0; i < layers; i++)
        {
            if (profilePresent[i] && !bits.TrySkip(88)) return false;
            if (levelPresent[i] && !bits.TrySkip(8)) return false;
        }
        if (!bits.TryReadUE(out _) || !bits.TryReadUE(out uint chroma)) return false;
        if (chroma == 3 && !bits.TrySkip(1)) return false;
        if (!bits.TryReadUE(out uint picWidth) || !bits.TryReadUE(out uint picHeight) || !bits.TryReadBit(out bool window)) return false;
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (window && (!bits.TryReadUE(out left) || !bits.TryReadUE(out right) || !bits.TryReadUE(out top) || !bits.TryReadUE(out bottom))) return false;
        int subWidth = chroma is 1 or 2 ? 2 : 1;
        int subHeight = chroma == 1 ? 2 : 1;
        width = checked((int)picWidth - (int)((left + right) * (uint)subWidth));
        height = checked((int)picHeight - (int)((top + bottom) * (uint)subHeight));
        return width > 0 && height > 0;
    }

    private static bool SkipScalingList(BitReader bits, int size)
    {
        int last = 8, next = 8;
        for (int i = 0; i < size; i++)
        {
            if (next != 0)
            {
                if (!bits.TryReadSE(out int delta)) return false;
                next = (last + delta + 256) % 256;
            }
            last = next == 0 ? last : next;
        }
        return true;
    }

    private static byte[] RemoveEmulationPrevention(ReadOnlySpan<byte> data)
    {
        byte[] output = new byte[data.Length];
        int count = 0;
        int zeros = 0;
        for (int i = 0; i < data.Length; i++)
        {
            byte value = data[i];
            if (zeros >= 2 && value == 0x03)
            {
                zeros = 0;
                continue;
            }
            output[count++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        if (count == output.Length)
            return output;
        Array.Resize(ref output, count);
        return output;
    }

    private static byte[] WrapEbmlElement(byte[] id, byte[] payload)
    {
        byte[] size = EncodeEbmlSize((ulong)payload.Length);
        byte[] result = new byte[id.Length + size.Length + payload.Length];
        Buffer.BlockCopy(id, 0, result, 0, id.Length);
        Buffer.BlockCopy(size, 0, result, id.Length, size.Length);
        Buffer.BlockCopy(payload, 0, result, id.Length + size.Length, payload.Length);
        return result;
    }

    private static void WriteEbmlUInt(Stream output, byte[] id, ulong value)
    {
        int bytes = 1;
        while (bytes < 8 && value >= (1UL << (bytes * 8)))
            bytes++;
        byte[] payload = new byte[bytes];
        for (int i = 0; i < bytes; i++)
            payload[bytes - 1 - i] = (byte)(value >> (i * 8));
        output.Write(WrapEbmlElement(id, payload));
    }

    private static void WriteEbmlString(Stream output, byte[] id, string value) =>
        output.Write(WrapEbmlElement(id, Encoding.UTF8.GetBytes(value)));

    private static void WriteEbmlBinary(Stream output, byte[] id, byte[] value) =>
        output.Write(WrapEbmlElement(id, value));

    private static void WriteEbmlFloat(Stream output, byte[] id, double value)
    {
        byte[] payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, unchecked((ulong)BitConverter.DoubleToInt64Bits(value)));
        output.Write(WrapEbmlElement(id, payload));
    }

    private static byte[] EncodeEbmlSize(ulong value)
    {
        for (int length = 1; length <= 8; length++)
        {
            int bits = 7 * length;
            ulong max = bits == 56 ? (1UL << 56) - 2 : (1UL << bits) - 2;
            if (value > max)
                continue;
            ulong encoded = value | (1UL << bits);
            byte[] result = new byte[length];
            for (int i = 0; i < length; i++)
                result[length - 1 - i] = (byte)(encoded >> (8 * i));
            return result;
        }
        throw new ArgumentOutOfRangeException(nameof(value), "EBML element boyutu 56-bit sınırını aşıyor.");
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

    private static long FindSignature(FileStream input, byte[] signature, long start, long end)
    {
        if (signature.Length == 0 || start < 0 || start >= end || start >= input.Length)
            return -1;
        end = Math.Min(end, input.Length);
        byte[] buffer = new byte[1024 * 1024 + signature.Length];
        long position = start;
        int carry = 0;
        while (position < end)
        {
            int request = (int)Math.Min(1024 * 1024, end - position);
            input.Position = position;
            int read = input.Read(buffer, carry, request);
            if (read <= 0)
                break;
            int count = carry + read;
            int index = IndexOfWithinCount(buffer, signature, count);
            if (index >= 0)
                return position - carry + index;
            carry = Math.Min(signature.Length - 1, count);
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
                if (buffer[i + j] != signature[j]) { match = false; break; }
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
            if (buffer[i] != prefix[i]) return false;
        return true;
    }

    private static bool BytesEqual(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            return false;
        for (int i = 0; i < left.Length; i++)
            if (left[i] != right[i]) return false;
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
                throw new EndOfStreamException("Container payload beklenenden önce sona erdi.");
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

    private static void EnsureParentDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private sealed class TrackEvidence
    {
        public TrackEvidence(ulong trackNumber) { TrackNumber = trackNumber; }
        public ulong TrackNumber { get; }
        public int BlockCount { get; set; }
        public int H264Score { get; set; }
        public int H265Score { get; set; }
        public int H264LengthPrefixedScore { get; set; }
        public int H265LengthPrefixedScore { get; set; }
        public int Vp8Score { get; set; }
        public int Vp9Score { get; set; }
        public int Av1Score { get; set; }
        public int AacScore { get; set; }
        public int VorbisScore { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public byte[]? CodecPrivate { get; set; }
        public byte[]? H264Sps { get; set; }
        public byte[]? H264Pps { get; set; }
        public byte[]? H265Vps { get; set; }
        public byte[]? H265Sps { get; set; }
        public byte[]? H265Pps { get; set; }
    }

    private sealed class BitReader
    {
        private readonly byte[] _data;
        private int _bit;
        public BitReader(byte[] data) { _data = data; }
        public bool TrySkip(int count)
        {
            if (count < 0 || _bit + count > _data.Length * 8) return false;
            _bit += count;
            return true;
        }
        public bool TryReadBit(out bool value)
        {
            value = false;
            if (_bit >= _data.Length * 8) return false;
            value = ((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1) != 0;
            _bit++;
            return true;
        }
        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (count < 0 || count > 32 || _bit + count > _data.Length * 8) return false;
            for (int i = 0; i < count; i++)
            {
                value <<= 1;
                if (((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1) != 0) value |= 1;
                _bit++;
            }
            return true;
        }
        public bool TryReadBits64(int count, out ulong value)
        {
            value = 0;
            if (count < 0 || count > 64 || _bit + count > _data.Length * 8) return false;
            for (int i = 0; i < count; i++)
            {
                value <<= 1;
                if (((_data[_bit >> 3] >> (7 - (_bit & 7))) & 1) != 0) value |= 1;
                _bit++;
            }
            return true;
        }

        public bool TryReadUE(out uint value)
        {
            value = 0;
            int zeros = 0;
            while (true)
            {
                if (!TryReadBit(out bool bit)) return false;
                if (bit) break;
                zeros++;
                if (zeros > 31) return false;
            }
            if (zeros == 0) return true;
            if (!TryReadBits(zeros, out uint suffix)) return false;
            value = ((1u << zeros) - 1u) + suffix;
            return true;
        }
        public bool TryReadSE(out int value)
        {
            value = 0;
            if (!TryReadUE(out uint code)) return false;
            value = (code & 1) != 0 ? (int)((code + 1) >> 1) : -(int)(code >> 1);
            return true;
        }
    }

    private readonly record struct HevcProfile(byte Space, byte Tier, byte Idc, uint Compat, ulong Constraints, byte Level);
    private readonly record struct ClusterSpan(long Offset, long Length);
    private readonly record struct InferredTrack(ulong TrackNumber, byte TrackType, string CodecId, int Width, int Height, int SampleRate, int Channels, byte[]? CodecPrivate);
    private readonly record struct AsfVideoConfig(int Width, int Height, uint Compression, byte[] BitmapInfoHeader);
    private readonly record struct KlvSpan(long Offset, int BerLength, long ValueLength, long EndOffset)
    {
        public long TotalLength => EndOffset - Offset;
    }
    private readonly record struct MxfPartitionInfo(long Offset, uint BodySid);
}
