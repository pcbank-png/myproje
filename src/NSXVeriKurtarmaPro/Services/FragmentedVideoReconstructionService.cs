using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class FragmentedVideoReconstructionService
{
    private const int SampleBytes = 2 * 1024 * 1024;
    private const int ParameterCatalogProbeBytes = 512 * 1024;
    private const long MpegPtsClock = 90000L;

    private enum FragmentFamily
    {
        TransportStream,
        IsoBmff,
        Mpeg,
        AnnexB
    }

    private sealed record FragmentDescriptor(
        RecoveryFileItem Item,
        FragmentFamily Family,
        string StreamKey,
        long TimelineStart,
        long TimelineEnd,
        long SequenceStart,
        long SequenceEnd,
        int ContinuityHead,
        int ContinuityTail,
        int Confidence,
        H26xFragmentAnalysis? CodecOrder = null);

    private sealed record FragmentEdge(FragmentDescriptor From, FragmentDescriptor To, long Score);

    public static IReadOnlyList<RecoveryFileItem> BuildCandidates(
        RawDeviceReader reader,
        IReadOnlyList<RecoveryFileItem> sourceItems,
        CancellationToken cancellationToken)
    {
        var descriptors = new List<FragmentDescriptor>();
        H26xFragmentOrderingService.ParameterSetCatalog parameterCatalog =
            H26xFragmentOrderingService.CreateCatalog();

        // Parameter sets are often stored in a different physical fragment than the
        // slice that references them. Build a scan-wide SPS/PPS/VPS catalog before
        // describing any Annex-B node so frame_num/POC parsing does not depend on disk order.
        foreach (RecoveryFileItem item in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEligibleVideoFragment(item) ||
                item.TransformKind != RecoveryTransformKind.None ||
                !TryGetAnnexCodec(item.Extension, out H26xCodecKind codec))
                continue;

            byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false, maximumBytes: ParameterCatalogProbeBytes);
            byte[] tail = item.SizeBytes > head.LongLength
                ? ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true, maximumBytes: ParameterCatalogProbeBytes)
                : [];
            parameterCatalog.AddSample(head, codec);
            if (tail.Length > 0)
                parameterCatalog.AddSample(tail, codec);
        }

        foreach (RecoveryFileItem item in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEligibleVideoFragment(item))
                continue;

            FragmentDescriptor? descriptor = TryDescribe(reader, item, parameterCatalog, cancellationToken);
            if (descriptor is not null)
                descriptors.Add(descriptor);
        }

        var reconstructed = new List<RecoveryFileItem>();
        int sequence = 1;

        foreach (IGrouping<string, FragmentDescriptor> group in descriptors
                     .GroupBy(d => $"{d.Family}|{d.StreamKey}", StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<FragmentDescriptor> ordered = group.Key.StartsWith($"{FragmentFamily.IsoBmff}|", StringComparison.Ordinal)
                ? group.OrderBy(d => d.SequenceStart).ThenBy(d => d.TimelineStart).ThenBy(d => d.Item.SourceOffset).ToList()
                : group.OrderBy(d => d.TimelineStart).ThenBy(d => d.Item.SourceOffset).ToList();

            foreach (List<FragmentDescriptor> chain in BuildChains(ordered))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chain.Count < 2)
                    continue;

                long totalBytes = 0;
                var extents = new List<SourceExtent>(chain.Count);
                foreach (FragmentDescriptor fragment in chain)
                {
                    if (fragment.Item.SizeBytes <= 0 || fragment.Item.SourceOffset < 0)
                        continue;

                    extents.Add(new SourceExtent(fragment.Item.SourceOffset, fragment.Item.SizeBytes));
                    totalBytes = checked(totalBytes + fragment.Item.SizeBytes);
                }

                if (extents.Count < 2 || totalBytes <= 0)
                    continue;

                string extension = ChooseExtension(chain);
                int confidence = chain.Min(d => d.Confidence);
                string state = confidence >= 90 ? "Parçalı Video" : "Yeniden İnşa";
                string sourceText = chain[0].Family switch
                {
                    FragmentFamily.TransportStream => $"Ultra video fragment zinciri • {extents.Count:N0} TS/MTS parçası",
                    FragmentFamily.IsoBmff => $"Ultra video fragment zinciri • {extents.Count:N0} MP4/MOV parçası",
                    FragmentFamily.Mpeg => $"Ultra video fragment zinciri • {extents.Count:N0} MPEG parçası",
                    FragmentFamily.AnnexB => $"Ultra video codec-zaman çizgisi • {extents.Count:N0} H.264/H.265 fragment",
                    _ => $"Ultra video fragment zinciri • {extents.Count:N0} parça"
                };

                reconstructed.Add(new RecoveryFileItem
                {
                    FileName = $"Parcali_Video_{sequence:000000}_{extents[0].Offset:X}.{extension.ToLowerInvariant()}",
                    Extension = extension,
                    SizeBytes = totalBytes,
                    RecoveryState = state,
                    TypeGlyph = FileTypeHelper.GetGlyph(extension),
                    SourceText = sourceText,
                    SourceKind = RecoverySourceKind.Extents,
                    SourceOffset = extents[0].Offset,
                    SourceExtents = extents,
                    FileSystemCreatedAt = chain.Select(d => d.Item.FileSystemCreatedAt).FirstOrDefault(t => t.HasValue),
                    FileSystemModifiedAt = chain.Select(d => d.Item.FileSystemModifiedAt).OrderByDescending(t => t).FirstOrDefault(t => t.HasValue),
                    DeletedAt = chain.Select(d => d.Item.DeletedAt).OrderByDescending(t => t).FirstOrDefault(t => t.HasValue),
                    DeletionDateSource = chain.Select(d => d.Item.DeletionDateSource).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                });

                sequence++;
            }
        }

        return reconstructed;
    }

    private static IEnumerable<List<FragmentDescriptor>> BuildChains(List<FragmentDescriptor> ordered)
    {
        if (ordered.Count == 0)
            yield break;

        // Build a directed evidence graph first. Selecting globally sorted edges prevents
        // an early fragment from greedily consuming a much stronger successor belonging
        // to another chain. Each node has at most one predecessor and one successor.
        var edges = new List<FragmentEdge>();
        foreach (FragmentDescriptor from in ordered)
        {
            foreach (FragmentDescriptor to in ordered)
            {
                if (ReferenceEquals(from, to))
                    continue;
                if (IsCompatible(from, to, out long score))
                    edges.Add(new FragmentEdge(from, to, score));
            }
        }

        var nextByNode = new Dictionary<FragmentDescriptor, FragmentDescriptor>();
        var previousByNode = new Dictionary<FragmentDescriptor, FragmentDescriptor>();
        foreach (FragmentEdge edge in edges.OrderBy(e => e.Score))
        {
            if (nextByNode.ContainsKey(edge.From) || previousByNode.ContainsKey(edge.To))
                continue;
            if (WouldCreateCycle(edge.From, edge.To, nextByNode))
                continue;

            nextByNode[edge.From] = edge.To;
            previousByNode[edge.To] = edge.From;
        }

        var emitted = new HashSet<FragmentDescriptor>();
        foreach (FragmentDescriptor seed in ordered.Where(node => !previousByNode.ContainsKey(node)))
        {
            var chain = new List<FragmentDescriptor>();
            FragmentDescriptor current = seed;

            while (emitted.Add(current))
            {
                chain.Add(current);
                if (!nextByNode.TryGetValue(current, out FragmentDescriptor? next))
                    break;
                current = next;
            }

            yield return chain;
        }

        foreach (FragmentDescriptor node in ordered.Where(node => emitted.Add(node)))
            yield return [node];
    }

    private static bool WouldCreateCycle(
        FragmentDescriptor from,
        FragmentDescriptor to,
        IReadOnlyDictionary<FragmentDescriptor, FragmentDescriptor> nextByNode)
    {
        FragmentDescriptor cursor = to;
        var visited = new HashSet<FragmentDescriptor>();
        while (visited.Add(cursor) && nextByNode.TryGetValue(cursor, out FragmentDescriptor? next))
        {
            if (ReferenceEquals(next, from))
                return true;
            cursor = next;
        }
        return false;
    }

    private static bool IsCompatible(FragmentDescriptor current, FragmentDescriptor next, out long score)
    {
        score = long.MaxValue;
        if (current.Family != next.Family ||
            !string.Equals(current.StreamKey, next.StreamKey, StringComparison.OrdinalIgnoreCase))
            return false;

        if (RangesOverlap(current.Item, next.Item))
            return false;

        if (current.Family == FragmentFamily.AnnexB)
        {
            if (current.CodecOrder is null || next.CodecOrder is null ||
                !H26xFragmentOrderingService.TryScoreSuccessor(current.CodecOrder, next.CodecOrder, out long codecScore))
                return false;

            // Physical distance remains only a weak tie-breaker. Direction is determined
            // by slice headers, frame_num/POC, IDR and parameter-set relationships, so a
            // later logical fragment may live at a lower disk offset.
            long currentEnd = SaturatingAdd(current.Item.SourceOffset, current.Item.SizeBytes);
            long annexPhysicalDistance = AbsoluteDistance(currentEnd, next.Item.SourceOffset);
            score = SaturatingAdd(codecScore, LogDistancePenalty(annexPhysicalDistance) / 8);
            return true;
        }

        if (current.Family == FragmentFamily.IsoBmff)
        {
            if (current.SequenceEnd < 0 || next.SequenceStart < 0 ||
                next.SequenceStart != current.SequenceEnd + 1)
                return false;

            if (current.TimelineEnd >= 0 && next.TimelineStart >= 0 &&
                next.TimelineStart < current.TimelineEnd)
                return false;

            long timelineGap = current.TimelineEnd >= 0 && next.TimelineStart >= 0
                ? next.TimelineStart - current.TimelineEnd
                : 0;
            long physicalGap = AbsoluteDistance(current.Item.SourceOffset + current.Item.SizeBytes, next.Item.SourceOffset);
            score = SaturatingAdd(Math.Max(0, timelineGap), LogDistancePenalty(physicalGap));
            return true;
        }

        if (current.TimelineEnd < 0 || next.TimelineStart < 0)
            return false;

        long ptsGap = ForwardPtsDistance(current.TimelineEnd, next.TimelineStart);
        if (ptsGap <= 0)
            return false;

        bool continuityMatches = current.ContinuityTail >= 0 && next.ContinuityHead >= 0 &&
                                 next.ContinuityHead == ((current.ContinuityTail + 1) & 0x0F);

        long physicalDistance = AbsoluteDistance(current.Item.SourceOffset + current.Item.SizeBytes, next.Item.SourceOffset);
        long continuityPenalty = continuityMatches ? 0 : 10L * MpegPtsClock;
        score = SaturatingAdd(ptsGap, SaturatingAdd(continuityPenalty, LogDistancePenalty(physicalDistance)));
        return true;
    }

    private static long LogDistancePenalty(long distance)
    {
        if (distance <= 0)
            return 0;

        ulong value = (ulong)(distance / 4096L) + 1;
        int log = 0;
        while (value > 1)
        {
            value >>= 1;
            log++;
        }
        return log * 4096L;
    }

    private static bool IsEligibleVideoFragment(RecoveryFileItem item) =>
        item.SourceKind == RecoverySourceKind.RawContiguous &&
        item.SourceOffset >= 0 &&
        item.SizeBytes > 0 &&
        item.Category == "Video";

    private static bool TryGetAnnexCodec(string extension, out H26xCodecKind codec)
    {
        string normalized = FileTypeHelper.Normalize(extension);
        if (normalized is "H264" or "AVC")
        {
            codec = H26xCodecKind.H264;
            return true;
        }
        if (normalized is "H265" or "HEVC")
        {
            codec = H26xCodecKind.H265;
            return true;
        }
        codec = default;
        return false;
    }

    private static FragmentDescriptor? TryDescribe(
        RawDeviceReader reader,
        RecoveryFileItem item,
        H26xFragmentOrderingService.ParameterSetCatalog parameterCatalog,
        CancellationToken cancellationToken)
    {
        string extension = FileTypeHelper.Normalize(item.Extension);

        if (extension is "MTS" or "M2TS" or "TS" or "M2T" or "TP" or "TRP" or "TOD")
            return TryDescribeTransport(reader, item, cancellationToken);

        if (extension is "MP4" or "M4V" or "MOV" or "QT" or "3GP" or "3G2" or "F4V")
            return TryDescribeIsoFragment(reader, item, cancellationToken);

        if (extension is "MPG" or "MPEG" or "MPE" or "MPV" or "M1V" or "M2V" or "VOB" or "EVO" or "MOD")
            return TryDescribeMpeg(reader, item, cancellationToken);

        if (extension is "H264" or "AVC")
            return item.TransformKind == RecoveryTransformKind.None
                ? TryDescribeAnnexB(reader, item, H26xCodecKind.H264, parameterCatalog, cancellationToken)
                : null;

        if (extension is "H265" or "HEVC")
            return item.TransformKind == RecoveryTransformKind.None
                ? TryDescribeAnnexB(reader, item, H26xCodecKind.H265, parameterCatalog, cancellationToken)
                : null;

        return null;
    }

    private static FragmentDescriptor? TryDescribeTransport(
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        if (head.Length < 188 * 8)
            return null;

        if (!TryDetectTransportGeometry(head, out int packetSize, out int syncOffset))
            return null;

        byte[] tail = ReadTransportTailSample(reader, item.SourceOffset, item.SizeBytes, packetSize);
        if (tail.Length < packetSize * 4)
            return null;

        TransportTimeline first = ReadTransportTimeline(head, packetSize, syncOffset, takeFirst: true);
        TransportTimeline last = ReadTransportTimeline(tail, packetSize, syncOffset, takeFirst: false);
        if (first.VideoPid < 0 || (first.Pts < 0 && last.Pts < 0 && first.Pcr < 0 && last.Pcr < 0))
            return null;

        string codec = DetectElementaryCodec(head) ?? DetectElementaryCodec(tail) ?? "VIDEO";
        string streamKey = $"{packetSize}:{first.VideoPid}:{codec}";
        long timelineStart = first.Pts >= 0 ? first.Pts : first.Pcr >= 0 ? first.Pcr / 300 : last.Pts;
        long timelineEnd = last.Pts >= 0 ? last.Pts : last.Pcr >= 0 ? last.Pcr / 300 : timelineStart;

        return new FragmentDescriptor(
            item,
            FragmentFamily.TransportStream,
            streamKey,
            timelineStart,
            timelineEnd,
            -1,
            -1,
            first.Continuity,
            last.Continuity,
            first.Pts >= 0 && last.Pts >= 0 ? 96 : first.Pcr >= 0 || last.Pcr >= 0 ? 91 : 88);
    }

    private static FragmentDescriptor? TryDescribeMpeg(
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        byte[] tail = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true);
        if (head.Length < 64 || tail.Length < 64)
            return null;

        long firstPts = FindMpegPesPts(head, takeFirst: true);
        long lastPts = FindMpegPesPts(tail, takeFirst: false);
        long firstGop = FindMpegGopClock(head, takeFirst: true);
        long lastGop = FindMpegGopClock(tail, takeFirst: false);
        if ((firstPts < 0 || lastPts < 0) && (firstGop < 0 || lastGop < 0))
            return null;

        string codec = ContainsSequence(head, [0x00, 0x00, 0x01, 0xB3]) ? "MPEG2" : "MPEG";
        ulong sequenceFingerprint = FindMpegSequenceFingerprint(head);
        return new FragmentDescriptor(
            item,
            FragmentFamily.Mpeg,
            $"{codec}:{sequenceFingerprint:X16}",
            firstPts >= 0 ? firstPts : firstGop,
            lastPts >= 0 ? lastPts : lastGop,
            firstGop,
            lastGop,
            -1,
            -1,
            firstPts >= 0 && lastPts >= 0 && firstGop >= 0 ? 96 : 91);
    }

    private static FragmentDescriptor? TryDescribeIsoFragment(
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (item.RecoveryState is not ("Video Parçası" or "Yeniden İnşa"))
            return null;

        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        byte[] tail = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true);
        if (head.Length < 32)
            return null;

        IsoTimeline first = ParseIsoTimeline(head, takeFirst: true);
        IsoTimeline last = ParseIsoTimeline(tail.Length > 0 ? tail : head, takeFirst: false);
        if (first.Sequence < 0 || first.TrackId < 0)
            return null;

        long endSequence = last.Sequence >= first.Sequence ? last.Sequence : first.Sequence;
        long startTime = first.DecodeTime;
        long endTime = last.DecodeTime >= 0 ? SaturatingAdd(last.DecodeTime, Math.Max(0, last.Duration)) : startTime;
        string brand = DetectIsoBrand(head);
        string streamKey = $"{brand}:TRACK:{first.TrackId}";

        return new FragmentDescriptor(
            item,
            FragmentFamily.IsoBmff,
            streamKey,
            startTime,
            endTime,
            first.Sequence,
            endSequence,
            -1,
            -1,
            startTime >= 0 && (first.HasTrun || last.HasTrun) ? 98 : startTime >= 0 ? 94 : 89);
    }

    private static FragmentDescriptor? TryDescribeAnnexB(
        RawDeviceReader reader,
        RecoveryFileItem item,
        H26xCodecKind codec,
        H26xFragmentOrderingService.ParameterSetCatalog parameterCatalog,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        byte[] tail = item.SizeBytes > head.LongLength
            ? ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true)
            : [];
        if (head.Length < 16)
            return null;

        H26xFragmentAnalysis? analysis = H26xFragmentOrderingService.Analyze(
            head,
            tail,
            item.SizeBytes,
            codec,
            parameterCatalog);
        if (analysis is null || (!analysis.HasFrames && !analysis.HasParameterSets))
            return null;

        H26xFramePoint? first = analysis.FirstFrame;
        H26xFramePoint? last = analysis.LastFrame;
        long timelineStart = first?.Poc ?? -1;
        long timelineEnd = last?.Poc ?? timelineStart;
        long sequenceStart = first.HasValue
            ? first.Value.FrameNum >= 0 ? first.Value.FrameNum : first.Value.PocLsb
            : -1;
        long sequenceEnd = last.HasValue
            ? last.Value.FrameNum >= 0 ? last.Value.FrameNum : last.Value.PocLsb
            : -1;

        return new FragmentDescriptor(
            item,
            FragmentFamily.AnnexB,
            analysis.StreamKey,
            timelineStart,
            timelineEnd,
            sequenceStart,
            sequenceEnd,
            first?.NalType ?? -1,
            last?.NalType ?? -1,
            analysis.Confidence,
            analysis);
    }

    private static ulong HashNalParameter(ReadOnlySpan<byte> data)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static byte[] ReadSample(
        RawDeviceReader reader,
        long offset,
        long length,
        bool fromEnd,
        int maximumBytes = SampleBytes)
    {
        if (offset < 0 || length <= 0 || maximumBytes <= 0)
            return [];

        int count = (int)Math.Min(maximumBytes, length);
        long sampleOffset = fromEnd ? offset + Math.Max(0, length - count) : offset;
        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(sampleOffset, data, out _);
        if (read <= 0)
            return [];
        if (read == data.Length)
            return data;

        Array.Resize(ref data, read);
        return data;
    }

    private static byte[] ReadTransportTailSample(
        RawDeviceReader reader,
        long offset,
        long length,
        int packetSize)
    {
        if (offset < 0 || length <= 0 || packetSize <= 0)
            return [];

        long requested = Math.Min(SampleBytes, length);
        long relativeStart = Math.Max(0, length - requested);
        relativeStart -= relativeStart % packetSize;
        long available = length - relativeStart;
        int count = (int)Math.Min(SampleBytes, available);
        count -= count % packetSize;
        if (count <= 0)
            return [];

        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(offset + relativeStart, data, out _);
        if (read <= 0)
            return [];

        read -= read % packetSize;
        if (read <= 0)
            return [];
        if (read != data.Length)
            Array.Resize(ref data, read);
        return data;
    }

    private static bool TryDetectTransportGeometry(ReadOnlySpan<byte> data, out int packetSize, out int syncOffset)
    {
        if (HasTransportSync(data, 188, 0, 6))
        {
            packetSize = 188;
            syncOffset = 0;
            return true;
        }

        if (HasTransportSync(data, 192, 4, 6))
        {
            packetSize = 192;
            syncOffset = 4;
            return true;
        }

        packetSize = 0;
        syncOffset = 0;
        return false;
    }

    private static bool HasTransportSync(ReadOnlySpan<byte> data, int packetSize, int syncOffset, int minimumPackets)
    {
        if (data.Length < syncOffset + packetSize * minimumPackets)
            return false;

        for (int i = 0; i < minimumPackets; i++)
        {
            int index = syncOffset + i * packetSize;
            if (index >= data.Length || data[index] != 0x47)
                return false;
        }

        return true;
    }

    private readonly record struct TransportTimeline(int VideoPid, long Pts, long Pcr, int Continuity);

    private static TransportTimeline ReadTransportTimeline(
        ReadOnlySpan<byte> data,
        int packetSize,
        int syncOffset,
        bool takeFirst)
    {
        int selectedPid = -1;
        long selectedPts = -1;
        long selectedPcr = -1;
        int firstContinuity = -1;
        int lastContinuity = -1;

        int packets = data.Length / packetSize;
        for (int p = 0; p < packets; p++)
        {
            int packetStart = p * packetSize;
            int ts = packetStart + syncOffset;
            if (ts + 4 > data.Length || data[ts] != 0x47)
                continue;

            byte b1 = data[ts + 1];
            byte b2 = data[ts + 2];
            byte b3 = data[ts + 3];
            int pid = ((b1 & 0x1F) << 8) | b2;
            int adaptation = (b3 >> 4) & 0x03;
            bool hasPayload = adaptation is 1 or 3;

            if (adaptation is 2 or 3 && TryReadPacketPcr(data, ts, packetStart + packetSize, out long pcr))
                selectedPcr = pcr;

            if (selectedPid >= 0 && pid == selectedPid && hasPayload)
                lastContinuity = b3 & 0x0F;

            if (!hasPayload || (b1 & 0x40) == 0)
                continue;

            int payload = ts + 4;
            if (adaptation == 3)
            {
                if (payload >= data.Length)
                    continue;
                payload += 1 + data[payload];
            }

            int packetEnd = Math.Min(packetStart + packetSize, data.Length);
            if (payload + 14 > packetEnd)
                continue;

            ReadOnlySpan<byte> pes = data[payload..packetEnd];
            if (!TryReadVideoPesPts(pes, out long pts))
                continue;

            if (selectedPid < 0)
            {
                selectedPid = pid;
                firstContinuity = b3 & 0x0F;
                lastContinuity = firstContinuity;
            }

            if (pid != selectedPid)
                continue;

            selectedPts = pts;
            if (takeFirst)
                return new TransportTimeline(selectedPid, selectedPts, selectedPcr, firstContinuity);
        }

        return new TransportTimeline(selectedPid, selectedPts, selectedPcr, lastContinuity);
    }

    private static bool TryReadPacketPcr(ReadOnlySpan<byte> data, int ts, int packetEnd, out long pcr)
    {
        pcr = -1;
        if (ts + 12 > packetEnd || ts + 12 > data.Length)
            return false;
        int adaptationLength = data[ts + 4];
        if (adaptationLength < 7 || ts + 5 + adaptationLength > packetEnd || (data[ts + 5] & 0x10) == 0)
            return false;

        long baseValue = ((long)data[ts + 6] << 25) |
                         ((long)data[ts + 7] << 17) |
                         ((long)data[ts + 8] << 9) |
                         ((long)data[ts + 9] << 1) |
                         (long)(data[ts + 10] >> 7);
        int extension = ((data[ts + 10] & 1) << 8) | data[ts + 11];
        pcr = baseValue * 300 + extension;
        return true;
    }

    private static bool TryReadVideoPesPts(ReadOnlySpan<byte> pes, out long pts)
    {
        pts = -1;
        if (pes.Length < 14 || pes[0] != 0x00 || pes[1] != 0x00 || pes[2] != 0x01 || pes[3] is < 0xE0 or > 0xEF)
            return false;

        if ((pes[7] & 0x80) == 0 || pes[8] < 5)
            return false;

        pts = DecodePts(pes.Slice(9, 5));
        return pts >= 0;
    }

    private static long FindMpegPesPts(ReadOnlySpan<byte> data, bool takeFirst)
    {
        long found = -1;
        for (int i = 0; i + 14 <= data.Length; i++)
        {
            if (data[i] != 0x00 || data[i + 1] != 0x00 || data[i + 2] != 0x01 || data[i + 3] is < 0xE0 or > 0xEF)
                continue;

            if (TryReadVideoPesPts(data[i..], out long pts))
            {
                found = pts;
                if (takeFirst)
                    return found;
            }
        }

        return found;
    }

    private static long FindMpegGopClock(ReadOnlySpan<byte> data, bool takeFirst)
    {
        long found = -1;
        for (int i = 0; i + 8 <= data.Length; i++)
        {
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 1 || data[i + 3] != 0xB8)
                continue;

            byte a = data[i + 4];
            byte b = data[i + 5];
            byte c = data[i + 6];
            byte d = data[i + 7];
            int hours = (a >> 2) & 0x1F;
            int minutes = ((a & 0x03) << 4) | (b >> 4);
            int seconds = ((b & 0x07) << 3) | (c >> 5);
            int pictures = ((c & 0x1F) << 1) | (d >> 7);
            if ((b & 0x08) == 0 || hours > 23 || minutes > 59 || seconds > 59 || pictures > 59)
                continue;

            found = ((hours * 3600L + minutes * 60L + seconds) * 30L + pictures) * 3000L;
            if (takeFirst)
                return found;
        }
        return found;
    }

    private static ulong FindMpegSequenceFingerprint(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 12 <= data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 && data[i + 3] == 0xB3)
                return HashNalParameter(data.Slice(i + 3, Math.Min(64, data.Length - i - 3)));
        }
        return 0;
    }

    private static long DecodePts(ReadOnlySpan<byte> pts)
    {
        if (pts.Length < 5 || (pts[0] & 0x01) == 0 || (pts[2] & 0x01) == 0 || (pts[4] & 0x01) == 0)
            return -1;

        return ((long)((pts[0] >> 1) & 0x07) << 30) |
               ((long)pts[1] << 22) |
               ((long)((pts[2] >> 1) & 0x7F) << 15) |
               ((long)pts[3] << 7) |
               ((long)((pts[4] >> 1) & 0x7F));
    }

    private readonly record struct IsoTimeline(long Sequence, int TrackId, long DecodeTime, long Duration, bool HasTrun);

    private static IsoTimeline ParseIsoTimeline(ReadOnlySpan<byte> data, bool takeFirst)
    {
        long sequence = -1;
        int trackId = -1;
        long decodeTime = -1;
        long defaultSampleDuration = 0;
        long duration = 0;
        bool hasTrun = false;

        for (int i = 4; i + 12 <= data.Length; i++)
        {
            if (MatchesAscii(data, i, "mfhd") && IsPlausibleBox(data, i, 16))
            {
                long value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 8, 4));
                if (!takeFirst || sequence < 0)
                    sequence = value;
            }
            else if (MatchesAscii(data, i, "tfhd") && IsPlausibleBox(data, i, 16))
            {
                int value = (int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 8, 4));
                if (!takeFirst || trackId < 0)
                    trackId = value;
                defaultSampleDuration = TryReadTfhdDefaultDuration(data, i);
            }
            else if (MatchesAscii(data, i, "tfdt") && IsPlausibleBox(data, i, 16))
            {
                byte version = data[i + 4];
                long value = -1;
                if (version == 1 && i + 16 <= data.Length)
                {
                    ulong raw = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(i + 8, 8));
                    value = raw <= long.MaxValue ? (long)raw : -1;
                }
                else if (version == 0 && i + 12 <= data.Length)
                {
                    value = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(i + 8, 4));
                }

                if (value >= 0 && (!takeFirst || decodeTime < 0))
                    decodeTime = value;
            }
            else if (MatchesAscii(data, i, "trun") && IsPlausibleBox(data, i, 16))
            {
                long parsedDuration = TryReadTrunDuration(data, i, defaultSampleDuration);
                if (parsedDuration >= 0)
                {
                    duration = SaturatingAdd(duration, parsedDuration);
                    hasTrun = true;
                }
            }

            if (takeFirst && sequence >= 0 && trackId >= 0 && decodeTime >= 0)
                break;
        }

        return new IsoTimeline(sequence, trackId, decodeTime, duration, hasTrun);
    }

    private static long TryReadTfhdDefaultDuration(ReadOnlySpan<byte> data, int typeOffset)
    {
        if (typeOffset + 12 > data.Length)
            return 0;
        int flags = (data[typeOffset + 5] << 16) | (data[typeOffset + 6] << 8) | data[typeOffset + 7];
        int cursor = typeOffset + 12;
        if ((flags & 0x000001) != 0) cursor += 8;
        if ((flags & 0x000002) != 0) cursor += 4;
        if ((flags & 0x000008) == 0 || cursor + 4 > data.Length)
            return 0;
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(cursor, 4));
    }

    private static long TryReadTrunDuration(ReadOnlySpan<byte> data, int typeOffset, long defaultDuration)
    {
        if (typeOffset + 12 > data.Length)
            return -1;
        int flags = (data[typeOffset + 5] << 16) | (data[typeOffset + 6] << 8) | data[typeOffset + 7];
        uint count = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(typeOffset + 8, 4));
        if (count > 10_000_000)
            return -1;

        int cursor = typeOffset + 12;
        if ((flags & 0x000001) != 0) cursor += 4;
        if ((flags & 0x000004) != 0) cursor += 4;
        bool hasDuration = (flags & 0x000100) != 0;
        int perSample = (hasDuration ? 4 : 0) +
                        ((flags & 0x000200) != 0 ? 4 : 0) +
                        ((flags & 0x000400) != 0 ? 4 : 0) +
                        ((flags & 0x000800) != 0 ? 4 : 0);
        if ((long)cursor + (long)perSample * count > data.Length)
            return -1;

        if (!hasDuration)
            return defaultDuration > 0 ? SaturatingMultiply(defaultDuration, count) : 0;

        long total = 0;
        for (uint sample = 0; sample < count; sample++)
        {
            total = SaturatingAdd(total, BinaryPrimitives.ReadUInt32BigEndian(data.Slice(cursor, 4)));
            cursor += perSample;
        }
        return total;
    }

    private static string DetectIsoBrand(ReadOnlySpan<byte> data)
    {
        int limit = Math.Min(data.Length - 8, 256);
        Span<char> chars = stackalloc char[4];
        for (int i = 4; i <= limit; i++)
        {
            if ((MatchesAscii(data, i, "ftyp") || MatchesAscii(data, i, "styp")) && i + 8 <= data.Length)
            {
                bool printable = true;
                for (int c = 0; c < 4; c++)
                {
                    byte value = data[i + 4 + c];
                    if (value is < 0x20 or > 0x7E)
                    {
                        printable = false;
                        break;
                    }
                    chars[c] = (char)value;
                }

                if (printable)
                    return new string(chars);
            }
        }

        return "ISO";
    }

    private static bool IsPlausibleBox(ReadOnlySpan<byte> data, int typeOffset, int minimumSize)
    {
        if (typeOffset < 4 || typeOffset + 4 > data.Length)
            return false;

        uint size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(typeOffset - 4, 4));
        return size >= minimumSize;
    }

    private static bool MatchesAscii(ReadOnlySpan<byte> data, int offset, string value)
    {
        if (offset < 0 || offset + value.Length > data.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (data[offset + i] != (byte)value[i])
                return false;
        }

        return true;
    }

    private static string? DetectElementaryCodec(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i + 6 < data.Length; i++)
        {
            int header;
            if (data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x01)
                header = i + 3;
            else if (i + 7 < data.Length && data[i] == 0x00 && data[i + 1] == 0x00 && data[i + 2] == 0x00 && data[i + 3] == 0x01)
                header = i + 4;
            else
                continue;

            byte b0 = data[header];
            int h264 = b0 & 0x1F;
            int h265 = (b0 >> 1) & 0x3F;
            if (h265 is 32 or 33 or 34 or 19 or 20)
                return "H265";
            if (h264 is 7 or 8 or 5)
                return "H264";
        }

        return null;
    }

    private static bool ContainsSequence(ReadOnlySpan<byte> data, ReadOnlySpan<byte> sequence)
    {
        if (sequence.Length == 0 || data.Length < sequence.Length)
            return false;

        for (int i = 0; i <= data.Length - sequence.Length; i++)
        {
            if (data.Slice(i, sequence.Length).SequenceEqual(sequence))
                return true;
        }

        return false;
    }

    private static bool RangesOverlap(RecoveryFileItem a, RecoveryFileItem b)
    {
        long aEnd;
        long bEnd;
        try
        {
            aEnd = checked(a.SourceOffset + a.SizeBytes);
            bEnd = checked(b.SourceOffset + b.SizeBytes);
        }
        catch (OverflowException)
        {
            return true;
        }

        return a.SourceOffset < bEnd && b.SourceOffset < aEnd;
    }

    private static long ForwardPtsDistance(long from, long to)
    {
        const long wrap = 1L << 33;
        long distance = to - from;
        if (distance < 0)
            distance += wrap;
        return distance;
    }

    private static long AbsoluteDistance(long a, long b)
    {
        if (a >= b)
            return a - b;
        return b - a;
    }

    private static long SaturatingAdd(long a, long b)
    {
        if (a > long.MaxValue - b)
            return long.MaxValue;
        return a + b;
    }

    private static long SaturatingMultiply(long value, uint count)
    {
        if (value <= 0 || count == 0)
            return 0;
        if (value > long.MaxValue / count)
            return long.MaxValue;
        return value * count;
    }

    private static string ChooseExtension(IReadOnlyList<FragmentDescriptor> chain)
    {
        return chain
            .Select(d => FileTypeHelper.Normalize(d.Item.Extension))
            .OrderBy(FileTypeHelper.GetVideoPriority)
            .FirstOrDefault() ?? "MP4";
    }
}
