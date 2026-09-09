using System.Buffers.Binary;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal static class ClassicMp4MdatFragmentStitchingService
{
    private const int ProbeBytes = 1024 * 1024;
    private const int MaxProbeSearchBytes = 256 * 1024;
    private const int MinimumNalRun = 2;
    private const uint MaxNalBytes = 64U * 1024U * 1024U;

    private sealed record MdatFragmentDescriptor(
        RecoveryFileItem Item,
        H26xCodecKind Codec,
        H26xFragmentAnalysis Analysis,
        int Confidence);

    private sealed record FragmentEdge(MdatFragmentDescriptor From, MdatFragmentDescriptor To, long Score);

    public static IReadOnlyList<RecoveryFileItem> BuildCandidates(
        RawDeviceReader reader,
        IReadOnlyList<RecoveryFileItem> sourceItems,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(sourceItems);

        var rawCandidates = new List<(RecoveryFileItem Item, H26xCodecKind Codec)>();
        foreach (RecoveryFileItem item in sourceItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEligible(item, out H26xCodecKind codec))
                continue;

            byte[] probe = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
            if (!TryConvertLengthPrefixedProbe(probe, codec, searchForStart: false, out _, out int nalCount) ||
                nalCount < MinimumNalRun)
                continue;

            rawCandidates.Add((item, codec));
        }

        if (rawCandidates.Count < 2)
            return [];

        H26xFragmentOrderingService.ParameterSetCatalog h264Catalog = H26xFragmentOrderingService.CreateCatalog();
        H26xFragmentOrderingService.ParameterSetCatalog h265Catalog = H26xFragmentOrderingService.CreateCatalog();

        foreach ((RecoveryFileItem item, H26xCodecKind codec) in rawCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddFragmentParameters(reader, item, codec, codec == H26xCodecKind.H264 ? h264Catalog : h265Catalog);
        }

        var descriptors = new List<MdatFragmentDescriptor>(rawCandidates.Count);
        foreach ((RecoveryFileItem item, H26xCodecKind codec) in rawCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            H26xFragmentOrderingService.ParameterSetCatalog catalog = codec == H26xCodecKind.H264 ? h264Catalog : h265Catalog;
            MdatFragmentDescriptor? descriptor = TryDescribe(reader, item, codec, catalog);
            if (descriptor is not null)
                descriptors.Add(descriptor);
        }

        var reconstructed = new List<RecoveryFileItem>();
        int sequence = 1;

        foreach (IGrouping<string, MdatFragmentDescriptor> group in descriptors
                     .GroupBy(d => $"{d.Codec}|{d.Analysis.StreamKey}", StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<MdatFragmentDescriptor> nodes = group.ToList();
            if (nodes.Count < 2)
                continue;

            foreach (List<MdatFragmentDescriptor> chain in BuildChains(nodes))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsRecoverableChain(chain))
                    continue;

                var extents = new List<SourceExtent>(chain.Count);
                long payloadBytes = 0;
                foreach (MdatFragmentDescriptor fragment in chain)
                {
                    if (fragment.Item.SourceOffset < 0 || fragment.Item.SizeBytes <= 0)
                        continue;
                    extents.Add(new SourceExtent(fragment.Item.SourceOffset, fragment.Item.SizeBytes));
                    payloadBytes = checked(payloadBytes + fragment.Item.SizeBytes);
                }

                if (extents.Count < 2 || payloadBytes <= 0 || !HasPhysicalFragmentation(extents) ||
                    IsCoveredByHealthyContainer(extents, sourceItems))
                    continue;

                int confidence = chain.Min(fragment => fragment.Confidence);
                byte[] prefix = BuildContainerPrefix(chain[0].Codec, payloadBytes);
                reconstructed.Add(new RecoveryFileItem
                {
                    FileName = $"Parcali_MDAT_{sequence:000000}_{extents[0].Offset:X}.mp4",
                    Extension = "MP4",
                    SizeBytes = payloadBytes,
                    RecoveryState = confidence >= 90 ? "Parçalı Video" : "Yeniden İnşa",
                    TypeGlyph = FileTypeHelper.GetGlyph("MP4"),
                    SourceText = $"Klasik MP4/MOV mdat stitching • {extents.Count:N0} codec-zaman çizgisi parçası",
                    SourceKind = RecoverySourceKind.Extents,
                    SourceOffset = extents[0].Offset,
                    SourceExtents = extents,
                    PrefixData = prefix,
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

    internal static IReadOnlyList<int> OrderLengthPrefixedForTest(
        H26xCodecKind codec,
        IReadOnlyList<byte[]> fragments)
    {
        var annex = new List<byte[]>(fragments.Count);
        foreach (byte[] fragment in fragments)
        {
            if (!TryConvertLengthPrefixedProbe(fragment, codec, searchForStart: false, out byte[] converted, out int nalCount) ||
                nalCount < MinimumNalRun)
                throw new InvalidDataException("Length-prefixed mdat test fragmenti çözümlenemedi.");
            annex.Add(converted);
        }

        return H26xFragmentOrderingService.OrderForTest(codec, annex);
    }

    internal static byte[] BuildContainerPrefixForTest(H26xCodecKind codec, long payloadBytes) =>
        BuildContainerPrefix(codec, payloadBytes);

    private static bool IsEligible(RecoveryFileItem item, out H26xCodecKind codec)
    {
        codec = default;
        if (item.SourceKind != RecoverySourceKind.RawContiguous ||
            item.SourceOffset < 0 || item.SizeBytes < 4096 || item.Category != "Video")
            return false;

        if (item.TransformKind == RecoveryTransformKind.LengthPrefixedH264ToAnnexB)
        {
            codec = H26xCodecKind.H264;
            return true;
        }
        if (item.TransformKind == RecoveryTransformKind.LengthPrefixedH265ToAnnexB)
        {
            codec = H26xCodecKind.H265;
            return true;
        }

        string extension = FileTypeHelper.Normalize(item.Extension);
        if (extension is "H264" or "AVC")
        {
            codec = H26xCodecKind.H264;
            return true;
        }
        if (extension is "H265" or "HEVC")
        {
            codec = H26xCodecKind.H265;
            return true;
        }
        return false;
    }

    private static void AddFragmentParameters(
        RawDeviceReader reader,
        RecoveryFileItem item,
        H26xCodecKind codec,
        H26xFragmentOrderingService.ParameterSetCatalog catalog)
    {
        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        if (TryConvertLengthPrefixedProbe(head, codec, searchForStart: false, out byte[] headAnnex, out _))
            catalog.AddSample(headAnnex, codec);

        if (item.SizeBytes <= head.LongLength)
            return;

        byte[] tail = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true);
        if (TryConvertLengthPrefixedProbe(tail, codec, searchForStart: true, out byte[] tailAnnex, out _))
            catalog.AddSample(tailAnnex, codec);
    }

    private static MdatFragmentDescriptor? TryDescribe(
        RawDeviceReader reader,
        RecoveryFileItem item,
        H26xCodecKind codec,
        H26xFragmentOrderingService.ParameterSetCatalog catalog)
    {
        byte[] head = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: false);
        if (!TryConvertLengthPrefixedProbe(head, codec, searchForStart: false, out byte[] headAnnex, out int headNals) ||
            headNals < MinimumNalRun)
            return null;

        byte[] tailAnnex = [];
        int tailNals = 0;
        if (item.SizeBytes > head.LongLength)
        {
            byte[] tail = ReadSample(reader, item.SourceOffset, item.SizeBytes, fromEnd: true);
            _ = TryConvertLengthPrefixedProbe(tail, codec, searchForStart: true, out tailAnnex, out tailNals);
        }

        H26xFragmentAnalysis? analysis = H26xFragmentOrderingService.Analyze(
            headAnnex,
            tailAnnex,
            item.SizeBytes,
            codec,
            catalog);
        if (analysis is null || (!analysis.HasFrames && !analysis.HasParameterSets))
            return null;

        int confidence = Math.Min(99, analysis.Confidence + (headNals >= 8 ? 2 : 0) + (tailNals >= 3 ? 1 : 0));
        return new MdatFragmentDescriptor(item, codec, analysis, confidence);
    }

    private static IEnumerable<List<MdatFragmentDescriptor>> BuildChains(List<MdatFragmentDescriptor> nodes)
    {
        var edges = new List<FragmentEdge>();
        foreach (MdatFragmentDescriptor from in nodes)
        {
            foreach (MdatFragmentDescriptor to in nodes)
            {
                if (ReferenceEquals(from, to) || RangesOverlap(from.Item, to.Item))
                    continue;
                if (H26xFragmentOrderingService.TryScoreSuccessor(from.Analysis, to.Analysis, out long score))
                    edges.Add(new FragmentEdge(from, to, score));
            }
        }

        var nextByNode = new Dictionary<MdatFragmentDescriptor, MdatFragmentDescriptor>();
        var previousByNode = new Dictionary<MdatFragmentDescriptor, MdatFragmentDescriptor>();
        foreach (FragmentEdge edge in edges.OrderBy(edge => edge.Score)
                     .ThenBy(edge => edge.From.Item.SourceOffset)
                     .ThenBy(edge => edge.To.Item.SourceOffset))
        {
            if (nextByNode.ContainsKey(edge.From) || previousByNode.ContainsKey(edge.To))
                continue;
            if (WouldCreateCycle(edge.From, edge.To, nextByNode))
                continue;
            nextByNode[edge.From] = edge.To;
            previousByNode[edge.To] = edge.From;
        }

        var emitted = new HashSet<MdatFragmentDescriptor>();
        foreach (MdatFragmentDescriptor seed in nodes
                     .Where(node => !previousByNode.ContainsKey(node))
                     .OrderBy(node => node.Analysis.BeginsWithParameterSets ? 0 : 1)
                     .ThenBy(node => node.Analysis.BeginsWithRandomAccess ? 0 : 1)
                     .ThenBy(node => node.Item.SourceOffset))
        {
            var chain = new List<MdatFragmentDescriptor>();
            MdatFragmentDescriptor current = seed;
            while (emitted.Add(current))
            {
                chain.Add(current);
                if (!nextByNode.TryGetValue(current, out MdatFragmentDescriptor? next))
                    break;
                current = next;
            }
            yield return chain;
        }

        foreach (MdatFragmentDescriptor node in nodes.Where(node => emitted.Add(node)))
            yield return [node];
    }

    private static bool IsRecoverableChain(IReadOnlyList<MdatFragmentDescriptor> chain)
    {
        if (chain.Count < 2)
            return false;
        H26xCodecKind codec = chain[0].Codec;
        if (chain.Any(fragment => fragment.Codec != codec))
            return false;
        if (!chain.Any(fragment => fragment.Analysis.HasFrames) ||
            !chain.Any(fragment => fragment.Analysis.HasParameterSets))
            return false;

        bool hasRandomAccessEvidence = chain.Any(fragment =>
            fragment.Analysis.HasRandomAccess ||
            ((fragment.Item.TransformKind == RecoveryTransformKind.LengthPrefixedH264ToAnnexB ||
              fragment.Item.TransformKind == RecoveryTransformKind.LengthPrefixedH265ToAnnexB) &&
             (fragment.Item.RecoveryState == "İyi" || fragment.Item.RecoveryState == "Video Akışı")));
        return hasRandomAccessEvidence;
    }

    private static bool HasPhysicalFragmentation(IReadOnlyList<SourceExtent> extents)
    {
        for (int index = 1; index < extents.Count; index++)
        {
            SourceExtent previous = extents[index - 1];
            SourceExtent current = extents[index];
            long expected;
            try
            {
                expected = checked(previous.Offset + previous.Length);
            }
            catch (OverflowException)
            {
                return true;
            }
            if (current.Offset != expected)
                return true;
        }
        return false;
    }


    private static bool IsCoveredByHealthyContainer(
        IReadOnlyList<SourceExtent> extents,
        IReadOnlyList<RecoveryFileItem> sourceItems)
    {
        foreach (RecoveryFileItem container in sourceItems)
        {
            if (container.SourceKind != RecoverySourceKind.RawContiguous ||
                container.SourceOffset < 0 || container.SizeBytes <= 0 ||
                container.RecoveryState is not ("Çok İyi" or "İyi"))
                continue;

            string extension = FileTypeHelper.Normalize(container.Extension);
            if (extension is not ("MP4" or "MOV" or "QT" or "M4V" or "3GP" or "3G2" or "F4V"))
                continue;

            long containerEnd = SaturatingAdd(container.SourceOffset, container.SizeBytes);
            bool coversAll = extents.All(extent =>
                extent.Offset >= container.SourceOffset &&
                SaturatingAdd(extent.Offset, extent.Length) <= containerEnd);
            if (coversAll)
                return true;
        }
        return false;
    }

    private static bool RangesOverlap(RecoveryFileItem left, RecoveryFileItem right)
    {
        long leftEnd = SaturatingAdd(left.SourceOffset, left.SizeBytes);
        long rightEnd = SaturatingAdd(right.SourceOffset, right.SizeBytes);
        return left.SourceOffset < rightEnd && right.SourceOffset < leftEnd;
    }

    private static bool WouldCreateCycle(
        MdatFragmentDescriptor from,
        MdatFragmentDescriptor to,
        IReadOnlyDictionary<MdatFragmentDescriptor, MdatFragmentDescriptor> nextByNode)
    {
        MdatFragmentDescriptor cursor = to;
        var visited = new HashSet<MdatFragmentDescriptor>();
        while (visited.Add(cursor) && nextByNode.TryGetValue(cursor, out MdatFragmentDescriptor? next))
        {
            if (ReferenceEquals(next, from))
                return true;
            cursor = next;
        }
        return false;
    }

    private static byte[] ReadSample(RawDeviceReader reader, long offset, long length, bool fromEnd)
    {
        if (offset < 0 || length <= 0)
            return [];
        int count = (int)Math.Min(ProbeBytes, length);
        long sampleOffset = fromEnd ? SaturatingAdd(offset, Math.Max(0, length - count)) : offset;
        byte[] data = new byte[count];
        int read = reader.ReadBestEffort(sampleOffset, data, out _);
        if (read <= 0)
            return [];
        if (read != data.Length)
            Array.Resize(ref data, read);
        return data;
    }

    private static bool TryConvertLengthPrefixedProbe(
        ReadOnlySpan<byte> data,
        H26xCodecKind codec,
        bool searchForStart,
        out byte[] annexB,
        out int nalCount)
    {
        annexB = [];
        nalCount = 0;
        if (data.Length < 6)
            return false;

        int start = 0;
        if (searchForStart)
        {
            start = FindLengthPrefixedRun(data, codec);
            if (start < 0)
                return false;
        }
        else if (!ValidateLengthPrefixedRun(data, 0, codec, MinimumNalRun))
        {
            return false;
        }

        using var output = new MemoryStream(Math.Min(data.Length + 1024, ProbeBytes + 4096));
        int position = start;
        ReadOnlySpan<byte> startCode = [0x00, 0x00, 0x00, 0x01];
        while (TryReadLengthPrefixedNal(data, position, codec, out int payloadOffset, out int end, out _))
        {
            output.Write(startCode);
            output.Write(data.Slice(payloadOffset, end - payloadOffset));
            nalCount++;
            position = end;
        }

        if (nalCount < MinimumNalRun)
            return false;
        annexB = output.ToArray();
        return annexB.Length > 0;
    }

    private static int FindLengthPrefixedRun(ReadOnlySpan<byte> data, H26xCodecKind codec)
    {
        int limit = Math.Min(Math.Max(0, data.Length - 6), MaxProbeSearchBytes);
        for (int start = 0; start <= limit; start++)
        {
            if (ValidateLengthPrefixedRun(data, start, codec, MinimumNalRun))
                return start;
        }
        return -1;
    }

    private static bool ValidateLengthPrefixedRun(ReadOnlySpan<byte> data, int start, H26xCodecKind codec, int required)
    {
        int position = start;
        for (int index = 0; index < required; index++)
        {
            if (!TryReadLengthPrefixedNal(data, position, codec, out _, out int end, out _))
                return false;
            position = end;
        }
        return true;
    }

    private static bool TryReadLengthPrefixedNal(
        ReadOnlySpan<byte> data,
        int position,
        H26xCodecKind codec,
        out int payloadOffset,
        out int end,
        out int nalType)
    {
        payloadOffset = 0;
        end = 0;
        nalType = -1;
        if (position < 0 || position + 5 > data.Length)
            return false;

        uint length = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(position, 4));
        int minimum = codec == H26xCodecKind.H264 ? 2 : 3;
        if (length < minimum || length > MaxNalBytes || length > int.MaxValue)
            return false;

        long candidateEnd = position + 4L + length;
        if (candidateEnd > data.Length || candidateEnd > int.MaxValue)
            return false;

        payloadOffset = position + 4;
        byte first = data[payloadOffset];
        if ((first & 0x80) != 0)
            return false;

        if (codec == H26xCodecKind.H264)
        {
            nalType = first & 0x1F;
            if (nalType is <= 0 or > 12)
                return false;
        }
        else
        {
            if (payloadOffset + 1 >= data.Length || (data[payloadOffset + 1] & 0x07) == 0)
                return false;
            nalType = (first >> 1) & 0x3F;
            if (nalType > 40)
                return false;
        }

        end = (int)candidateEnd;
        return true;
    }

    private static byte[] BuildContainerPrefix(H26xCodecKind codec, long payloadBytes)
    {
        if (payloadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(payloadBytes));

        ulong mdatSize = checked((ulong)payloadBytes + 16UL);
        byte[] prefix = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(0, 4), 32);
        "ftyp"u8.CopyTo(prefix.AsSpan(4, 4));
        "isom"u8.CopyTo(prefix.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(12, 4), 0x00000200);
        "isom"u8.CopyTo(prefix.AsSpan(16, 4));
        "iso2"u8.CopyTo(prefix.AsSpan(20, 4));
        if (codec == H26xCodecKind.H264)
            "avc1"u8.CopyTo(prefix.AsSpan(24, 4));
        else
            "hvc1"u8.CopyTo(prefix.AsSpan(24, 4));
        "mp41"u8.CopyTo(prefix.AsSpan(28, 4));

        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(32, 4), 1);
        "mdat"u8.CopyTo(prefix.AsSpan(36, 4));
        BinaryPrimitives.WriteUInt64BigEndian(prefix.AsSpan(40, 8), mdatSize);
        return prefix;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
            return long.MaxValue;
        if (right < 0 && left < long.MinValue - right)
            return long.MinValue;
        return left + right;
    }
}
