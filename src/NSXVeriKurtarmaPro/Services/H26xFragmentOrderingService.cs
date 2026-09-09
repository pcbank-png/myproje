namespace NSXVeriKurtarmaPro.Services;

internal enum H26xCodecKind
{
    H264,
    H265
}

internal readonly record struct H26xFramePoint(
    int FrameNum,
    int Poc,
    int PocLsb,
    int FrameNumModulus,
    int PocModulus,
    int PpsId,
    int SpsId,
    int VpsId,
    int NalType,
    int IdrPicId,
    int TemporalId,
    int SliceType,
    bool IsRandomAccess,
    bool IsReference,
    bool IsFirstSlice,
    long SampleOffset);

internal sealed record H26xFragmentAnalysis(
    H26xCodecKind Codec,
    string StreamKey,
    ulong ParameterSignature,
    H26xFramePoint? FirstFrame,
    H26xFramePoint? LastFrame,
    int FrameCount,
    int FrameStep,
    int PocStep,
    bool HasParameterSets,
    bool BeginsWithParameterSets,
    bool BeginsWithRandomAccess,
    bool HasRandomAccess,
    bool HasEndMarker,
    int FirstRandomAccessId,
    int LastRandomAccessId,
    int RandomAccessCount,
    int Confidence)
{
    public bool HasFrames => FirstFrame.HasValue && LastFrame.HasValue && FrameCount > 0;
}

internal static class H26xFragmentOrderingService
{
    private const int MaxParameterNalBytes = 64 * 1024;
    private const int MaxSliceHeaderBytes = 16 * 1024;
    private const int MaxNalUnitsPerSample = 250_000;
    private const int MaxReasonableFrameGap = 128;
    private const long CodecScoreScale = 10_000L;

    internal sealed class ParameterSetCatalog
    {
        private readonly Dictionary<int, List<H264Sps>> _h264Sps = new();
        private readonly Dictionary<int, List<H264Pps>> _h264Pps = new();
        private readonly Dictionary<int, List<H265Vps>> _h265Vps = new();
        private readonly Dictionary<int, List<H265Sps>> _h265Sps = new();
        private readonly Dictionary<int, List<H265Pps>> _h265Pps = new();

        internal bool HasAny(H26xCodecKind codec) => codec == H26xCodecKind.H264
            ? _h264Sps.Count > 0 || _h264Pps.Count > 0
            : _h265Vps.Count > 0 || _h265Sps.Count > 0 || _h265Pps.Count > 0;

        internal void AddSample(ReadOnlySpan<byte> data, H26xCodecKind codec)
        {
            foreach (NalRange nal in EnumerateNalRanges(data, codec))
            {
                ReadOnlySpan<byte> payload = data.Slice(nal.HeaderOffset, nal.EndOffset - nal.HeaderOffset);
                if (codec == H26xCodecKind.H264)
                {
                    if (nal.NalType == 7 &&
                        TryParseH264Sps(payload, out H264Sps? sps) &&
                        sps is not null)
                        AddUnique(_h264Sps, sps.Id, sps, static value => value.RawHash);
                    else if (nal.NalType == 8 &&
                             TryParseH264Pps(payload, out H264Pps? pps) &&
                             pps is not null)
                        AddUnique(_h264Pps, pps.Id, pps, static value => value.RawHash);
                }
                else
                {
                    if (nal.NalType == 32 &&
                        TryParseH265Vps(payload, out H265Vps? vps) &&
                        vps is not null)
                        AddUnique(_h265Vps, vps.Id, vps, static value => value.RawHash);
                    else if (nal.NalType == 33 &&
                             TryParseH265Sps(payload, out H265Sps? sps) &&
                             sps is not null)
                        AddUnique(_h265Sps, sps.Id, sps, static value => value.RawHash);
                    else if (nal.NalType == 34 &&
                             TryParseH265Pps(payload, out H265Pps? pps) &&
                             pps is not null)
                        AddUnique(_h265Pps, pps.Id, pps, static value => value.RawHash);
                }
            }
        }

        internal bool TryResolveH264(int ppsId, out H264Pps? pps, out H264Sps? sps)
        {
            pps = null;
            sps = null;
            if (!TryChooseUnique(_h264Pps, ppsId, static value => value.RawHash, out H264Pps? chosenPps) ||
                chosenPps is null ||
                !TryChooseUnique(_h264Sps, chosenPps.SpsId, static value => value.RawHash, out H264Sps? chosenSps) ||
                chosenSps is null)
                return false;

            pps = chosenPps;
            sps = chosenSps;
            return true;
        }

        internal bool TryResolveH265(int ppsId, out H265Pps? pps, out H265Sps? sps, out H265Vps? vps)
        {
            pps = null;
            sps = null;
            vps = null;
            if (!TryChooseUnique(_h265Pps, ppsId, static value => value.RawHash, out H265Pps? chosenPps) ||
                chosenPps is null ||
                !TryChooseUnique(_h265Sps, chosenPps.SpsId, static value => value.RawHash, out H265Sps? chosenSps) ||
                chosenSps is null)
                return false;

            _ = TryChooseUnique(_h265Vps, chosenSps.VpsId, static value => value.RawHash, out H265Vps? chosenVps);
            pps = chosenPps;
            sps = chosenSps;
            vps = chosenVps;
            return true;
        }

        internal bool TryResolveUniqueH264(out H264Pps? pps, out H264Sps? sps)
        {
            pps = null;
            sps = null;
            var candidates = new List<(H264Pps Pps, H264Sps Sps)>();
            foreach (int ppsId in _h264Pps.Keys)
            {
                if (!TryResolveH264(ppsId, out H264Pps? candidatePps, out H264Sps? candidateSps) ||
                    candidatePps is null || candidateSps is null)
                    continue;
                if (candidates.Any(value =>
                        value.Pps.RawHash == candidatePps.RawHash &&
                        value.Sps.RawHash == candidateSps.RawHash))
                    continue;
                candidates.Add((candidatePps, candidateSps));
            }

            if (candidates.Count != 1)
                return false;
            pps = candidates[0].Pps;
            sps = candidates[0].Sps;
            return true;
        }

        internal bool TryResolveUniqueH265(out H265Pps? pps, out H265Sps? sps, out H265Vps? vps)
        {
            pps = null;
            sps = null;
            vps = null;
            var candidates = new List<(H265Pps Pps, H265Sps Sps, H265Vps? Vps)>();
            foreach (int ppsId in _h265Pps.Keys)
            {
                if (!TryResolveH265(ppsId, out H265Pps? candidatePps, out H265Sps? candidateSps, out H265Vps? candidateVps) ||
                    candidatePps is null || candidateSps is null)
                    continue;
                ulong candidateVpsSignature = candidateVps?.RawHash ?? 0;
                if (candidates.Any(value =>
                        value.Pps.RawHash == candidatePps.RawHash &&
                        value.Sps.RawHash == candidateSps.RawHash &&
                        (value.Vps?.RawHash ?? 0) == candidateVpsSignature))
                    continue;
                candidates.Add((candidatePps, candidateSps, candidateVps));
            }

            if (candidates.Count != 1)
                return false;
            pps = candidates[0].Pps;
            sps = candidates[0].Sps;
            vps = candidates[0].Vps;
            return true;
        }

        private static void AddUnique<T>(
            Dictionary<int, List<T>> map,
            int id,
            T value,
            Func<T, ulong> identity)
        {
            if (!map.TryGetValue(id, out List<T>? values))
            {
                values = [];
                map[id] = values;
            }

            ulong key = identity(value);
            if (values.Any(existing => identity(existing) == key))
                return;
            values.Add(value);
        }

        private static bool TryChooseUnique<T>(
            Dictionary<int, List<T>> map,
            int id,
            Func<T, ulong> signature,
            out T? value)
            where T : class
        {
            value = null;
            if (!map.TryGetValue(id, out List<T>? values) || values.Count == 0)
                return false;

            ulong firstSignature = signature(values[0]);
            if (values.Skip(1).Any(candidate => signature(candidate) != firstSignature))
                return false;

            value = values[0];
            return true;
        }
    }

    public static ParameterSetCatalog CreateCatalog() => new();

    public static H26xFragmentAnalysis? Analyze(
        ReadOnlySpan<byte> head,
        ReadOnlySpan<byte> tail,
        long fragmentLength,
        H26xCodecKind codec,
        ParameterSetCatalog globalCatalog)
    {
        if (head.Length == 0 && tail.Length == 0)
            return null;

        var localCatalog = new ParameterSetCatalog();
        localCatalog.AddSample(head, codec);
        if (tail.Length > 0)
            localCatalog.AddSample(tail, codec);

        var resolver = new ParameterResolver(localCatalog, globalCatalog);
        var frames = new List<H26xFramePoint>();
        SampleAnalysis headAnalysis = AnalyzeSample(head, 0, codec, resolver, frames);
        long tailBase = tail.Length > 0 ? Math.Max(0, fragmentLength - tail.Length) : 0;
        SampleAnalysis tailAnalysis = tail.Length > 0
            ? AnalyzeSample(tail, tailBase, codec, resolver, frames)
            : default;

        frames.Sort(static (left, right) => left.SampleOffset.CompareTo(right.SampleOffset));
        frames = DeduplicateFrames(frames);

        bool hasParameters = headAnalysis.HasParameterSets || tailAnalysis.HasParameterSets || localCatalog.HasAny(codec);
        bool beginsWithParameters = headAnalysis.BeginsWithParameterSets;
        bool hasEnd = headAnalysis.HasEndMarker || tailAnalysis.HasEndMarker;
        List<H26xFramePoint> randomAccessFrames = frames.Where(frame => frame.IsRandomAccess).ToList();
        bool hasRandomAccess = randomAccessFrames.Count > 0;
        bool beginsWithRandomAccess = frames.Count > 0 && frames[0].IsRandomAccess;
        int firstRandomAccessId = randomAccessFrames.Count > 0 ? randomAccessFrames[0].IdrPicId : -1;
        int lastRandomAccessId = randomAccessFrames.Count > 0 ? randomAccessFrames[^1].IdrPicId : -1;

        H26xFramePoint? first = frames.Count > 0 ? frames[0] : null;
        H26xFramePoint? last = frames.Count > 0 ? frames[^1] : null;
        ulong parameterSignature = SelectParameterSignature(frames, headAnalysis.ParameterSignature, tailAnalysis.ParameterSignature);
        string? streamKey = BuildStreamKey(codec, frames, parameterSignature, resolver);
        if (streamKey is null)
            return null;

        int frameStep = EstimateFrameStep(frames);
        int pocStep = EstimatePocStep(frames);
        int confidence = CalculateConfidence(
            frames.Count,
            parameterSignature != 0,
            hasParameters,
            beginsWithRandomAccess,
            frames.Count(frame => frame.IsFirstSlice),
            hasEnd);

        return new H26xFragmentAnalysis(
            codec,
            streamKey,
            parameterSignature,
            first,
            last,
            frames.Count,
            frameStep,
            pocStep,
            hasParameters,
            beginsWithParameters,
            beginsWithRandomAccess,
            hasRandomAccess,
            hasEnd,
            firstRandomAccessId,
            lastRandomAccessId,
            randomAccessFrames.Count,
            confidence);
    }

    public static bool TryScoreSuccessor(
        H26xFragmentAnalysis current,
        H26xFragmentAnalysis next,
        out long score)
    {
        score = long.MaxValue;
        if (current.Codec != next.Codec ||
            !string.Equals(current.StreamKey, next.StreamKey, StringComparison.OrdinalIgnoreCase) ||
            current.HasEndMarker)
            return false;

        if (!current.HasFrames)
        {
            if (!current.HasParameterSets || !next.HasFrames)
                return false;

            score = next.BeginsWithRandomAccess ? 0 : 2 * CodecScoreScale;
            if (!current.BeginsWithParameterSets)
                score += CodecScoreScale;
            return true;
        }

        if (!next.HasFrames)
            return false;

        H26xFramePoint from = current.LastFrame!.Value;
        H26xFramePoint to = next.FirstFrame!.Value;
        if (!ParameterRelationshipMatches(from, to))
            return false;

        long parameterPenalty = current.ParameterSignature != 0 && next.ParameterSignature != 0 &&
                                current.ParameterSignature == next.ParameterSignature
            ? 0
            : CodecScoreScale;

        if (to.IsRandomAccess)
        {
            // A random-access picture starts a new GOP. It may legally follow a completed
            // non-IDR GOP. Single-picture all-intra fragments are only linked when H.264
            // idr_pic_id supplies an actual logical relationship.
            bool hasH264IdrRelationship = current.Codec == H26xCodecKind.H264 &&
                                           current.LastRandomAccessId >= 0 &&
                                           next.FirstRandomAccessId >= 0;
            if (from.IsRandomAccess && current.FrameCount <= 1 && !hasH264IdrRelationship)
                return false;

            int idrPenalty = 0;
            if (hasH264IdrRelationship)
            {
                int idrDelta = ForwardModulo(current.LastRandomAccessId, next.FirstRandomAccessId, 65536);
                idrPenalty = idrDelta switch
                {
                    1 => 0,
                    0 => 2_000,
                    _ => Math.Min(CircularDistance(idrDelta, 1, 65536), 128) * 250
                };
            }

            score = 8 * CodecScoreScale + parameterPenalty + idrPenalty;
            // Repeated SPS/PPS can precede any GOP. Do not let that weak preference
            // outweigh the IDR relationship and link a later GOP back to the first one.
            if (next.BeginsWithParameterSets && !hasH264IdrRelationship)
                score -= CodecScoreScale;
            return true;
        }

        long framePenalty = ScoreFrameContinuity(current, next, from, to, out bool framePlausible);
        long pocPenalty = ScorePocContinuity(current, next, from, to, out bool pocPlausible);
        if (!framePlausible && !pocPlausible)
            return false;

        long randomAccessPenalty = current.BeginsWithRandomAccess && current.FrameCount == 1
            ? 0
            : current.HasRandomAccess && !current.BeginsWithRandomAccess ? CodecScoreScale / 2 : 0;
        long firstSlicePenalty = to.IsFirstSlice ? 0 : 3 * CodecScoreScale;

        score = SaturatingAdd(parameterPenalty,
            SaturatingAdd(framePenalty,
                SaturatingAdd(pocPenalty, randomAccessPenalty + firstSlicePenalty)));
        return true;
    }

    internal static IReadOnlyList<int> OrderForTest(
        H26xCodecKind codec,
        IReadOnlyList<byte[]> fragments)
    {
        var catalog = new ParameterSetCatalog();
        foreach (byte[] fragment in fragments)
            catalog.AddSample(fragment, codec);

        var analyses = new List<(int Index, H26xFragmentAnalysis Analysis)>();
        for (int index = 0; index < fragments.Count; index++)
        {
            H26xFragmentAnalysis? analysis = Analyze(
                fragments[index],
                ReadOnlySpan<byte>.Empty,
                fragments[index].LongLength,
                codec,
                catalog);
            if (analysis is null)
                throw new InvalidDataException($"Codec fragmenti çözümlenemedi: {codec} #{index}");
            analyses.Add((index, analysis));
        }

        var edges = new List<(int From, int To, long Score)>();
        for (int from = 0; from < analyses.Count; from++)
        {
            for (int to = 0; to < analyses.Count; to++)
            {
                if (from == to)
                    continue;
                if (TryScoreSuccessor(analyses[from].Analysis, analyses[to].Analysis, out long edgeScore))
                    edges.Add((from, to, edgeScore));
            }
        }

        var nextByNode = new Dictionary<int, int>();
        var previousByNode = new Dictionary<int, int>();
        foreach ((int from, int to, long edgeScore) in edges.OrderBy(edge => edge.Score).ThenBy(edge => edge.From).ThenBy(edge => edge.To))
        {
            _ = edgeScore;
            if (nextByNode.ContainsKey(from) || previousByNode.ContainsKey(to) || WouldCreateCycle(from, to, nextByNode))
                continue;
            nextByNode[from] = to;
            previousByNode[to] = from;
        }

        int seed = Enumerable.Range(0, analyses.Count)
            .Where(index => !previousByNode.ContainsKey(index))
            .OrderBy(index => analyses[index].Analysis.BeginsWithRandomAccess ? 0 : 1)
            .ThenBy(index => analyses[index].Analysis.FirstFrame?.Poc ?? int.MaxValue)
            .FirstOrDefault();

        var ordered = new List<int>();
        var visited = new HashSet<int>();
        int cursor = seed;
        while (visited.Add(cursor))
        {
            ordered.Add(analyses[cursor].Index);
            if (!nextByNode.TryGetValue(cursor, out int next))
                break;
            cursor = next;
        }

        foreach (int index in Enumerable.Range(0, analyses.Count).Where(index => visited.Add(index)))
            ordered.Add(analyses[index].Index);
        return ordered;
    }

    private static bool WouldCreateCycle(int from, int to, IReadOnlyDictionary<int, int> nextByNode)
    {
        int cursor = to;
        var visited = new HashSet<int>();
        while (visited.Add(cursor) && nextByNode.TryGetValue(cursor, out int next))
        {
            if (next == from)
                return true;
            cursor = next;
        }
        return false;
    }

    private readonly struct ParameterResolver
    {
        private readonly ParameterSetCatalog _local;
        private readonly ParameterSetCatalog _global;

        public ParameterResolver(ParameterSetCatalog local, ParameterSetCatalog global)
        {
            _local = local;
            _global = global;
        }

        internal bool TryResolveH264(int ppsId, out H264Pps? pps, out H264Sps? sps) =>
            _local.TryResolveH264(ppsId, out pps, out sps) || _global.TryResolveH264(ppsId, out pps, out sps);

        internal bool TryResolveH265(int ppsId, out H265Pps? pps, out H265Sps? sps, out H265Vps? vps) =>
            _local.TryResolveH265(ppsId, out pps, out sps, out vps) || _global.TryResolveH265(ppsId, out pps, out sps, out vps);

        internal bool TryResolveUniqueH264(out H264Pps? pps, out H264Sps? sps) =>
            _local.TryResolveUniqueH264(out pps, out sps) || _global.TryResolveUniqueH264(out pps, out sps);

        internal bool TryResolveUniqueH265(out H265Pps? pps, out H265Sps? sps, out H265Vps? vps) =>
            _local.TryResolveUniqueH265(out pps, out sps, out vps) ||
            _global.TryResolveUniqueH265(out pps, out sps, out vps);
    }

    private readonly record struct SampleAnalysis(
        bool HasParameterSets,
        bool BeginsWithParameterSets,
        bool HasEndMarker,
        ulong ParameterSignature);

    private static SampleAnalysis AnalyzeSample(
        ReadOnlySpan<byte> data,
        long sampleBase,
        H26xCodecKind codec,
        ParameterResolver resolver,
        List<H26xFramePoint> frames)
    {
        bool hasParameters = false;
        bool beginsWithParameters = false;
        bool hasEnd = false;
        ulong parameterSignature = 0;
        bool encounteredVcl = false;
        int units = 0;

        foreach (NalRange nal in EnumerateNalRanges(data, codec))
        {
            if (++units > MaxNalUnitsPerSample)
                break;

            ReadOnlySpan<byte> payload = data.Slice(nal.HeaderOffset, nal.EndOffset - nal.HeaderOffset);
            bool parameter = codec == H26xCodecKind.H264
                ? nal.NalType is 7 or 8
                : nal.NalType is 32 or 33 or 34;
            bool endMarker = codec == H26xCodecKind.H264
                ? nal.NalType is 10 or 11
                : nal.NalType is 36 or 37;
            bool vcl = codec == H26xCodecKind.H264
                ? nal.NalType is >= 1 and <= 5
                : nal.NalType is >= 0 and <= 31;

            if (parameter)
            {
                hasParameters = true;
                if (!encounteredVcl)
                    beginsWithParameters = true;
                parameterSignature = CombineHash(parameterSignature, HashBytes(payload[..Math.Min(payload.Length, MaxParameterNalBytes)]));
            }
            if (endMarker)
                hasEnd = true;

            if (!vcl)
                continue;
            encounteredVcl = true;

            H26xFramePoint? frame = codec == H26xCodecKind.H264
                ? TryParseH264Slice(payload, nal.NalType, sampleBase + nal.StartOffset, resolver)
                : TryParseH265Slice(payload, nal.NalType, sampleBase + nal.StartOffset, resolver);
            if (frame.HasValue)
                frames.Add(frame.Value);
        }

        return new SampleAnalysis(hasParameters, beginsWithParameters, hasEnd, parameterSignature);
    }

    private static H26xFramePoint? TryParseH264Slice(
        ReadOnlySpan<byte> nal,
        int nalType,
        long sampleOffset,
        ParameterResolver resolver)
    {
        if (nal.Length < 2)
            return null;

        int nalRefIdc = (nal[0] >> 5) & 0x03;
        var reader = new BitReader(ToRbsp(nal[1..], MaxSliceHeaderBytes));
        if (!reader.TryReadUnsignedExpGolomb(out uint firstMb) ||
            !reader.TryReadUnsignedExpGolomb(out uint rawSliceType) ||
            !reader.TryReadUnsignedExpGolomb(out uint ppsIdRaw) ||
            ppsIdRaw > int.MaxValue ||
            !resolver.TryResolveH264((int)ppsIdRaw, out H264Pps? pps, out H264Sps? sps) ||
            pps is null || sps is null)
            return null;

        if (sps.SeparateColourPlaneFlag && !reader.TrySkipBits(2))
            return null;
        if (!reader.TryReadBits(sps.FrameNumBits, out uint frameNumRaw))
            return null;

        bool fieldPic = false;
        if (!sps.FrameMbsOnlyFlag)
        {
            if (!reader.TryReadBit(out int fieldFlag))
                return null;
            fieldPic = fieldFlag != 0;
            if (fieldPic && !reader.TrySkipBits(1))
                return null;
        }

        int idrPicId = -1;
        bool idr = nalType == 5;
        if (idr)
        {
            if (!reader.TryReadUnsignedExpGolomb(out uint idrRaw) || idrRaw > 65535)
                return null;
            idrPicId = (int)idrRaw;
        }

        int pocLsb = -1;
        int poc;
        if (sps.PicOrderCntType == 0)
        {
            if (!reader.TryReadBits(sps.PocBits, out uint pocRaw))
                return null;
            pocLsb = (int)pocRaw;
            int deltaBottom = 0;
            if (pps.BottomFieldPicOrderInFramePresentFlag && !fieldPic &&
                !reader.TryReadSignedExpGolomb(out deltaBottom))
                return null;
            poc = pocLsb + deltaBottom;
        }
        else if (sps.PicOrderCntType == 1)
        {
            int delta0 = 0;
            int delta1 = 0;
            if (!sps.DeltaPicOrderAlwaysZeroFlag)
            {
                if (!reader.TryReadSignedExpGolomb(out delta0))
                    return null;
                if (pps.BottomFieldPicOrderInFramePresentFlag && !fieldPic &&
                    !reader.TryReadSignedExpGolomb(out delta1))
                    return null;
            }
            poc = ClampInt((long)frameNumRaw * 2L + delta0 + delta1);
            pocLsb = poc;
        }
        else
        {
            poc = ClampInt((long)frameNumRaw * 2L);
            pocLsb = poc;
        }

        return new H26xFramePoint(
            (int)frameNumRaw,
            poc,
            pocLsb,
            sps.FrameNumModulus,
            sps.PocModulus,
            pps.Id,
            sps.Id,
            -1,
            nalType,
            idrPicId,
            0,
            (int)(rawSliceType % 5),
            idr,
            nalRefIdc != 0,
            firstMb == 0,
            sampleOffset);
    }

    private static H26xFramePoint? TryParseH265Slice(
        ReadOnlySpan<byte> nal,
        int nalType,
        long sampleOffset,
        ParameterResolver resolver)
    {
        if (nal.Length < 3)
            return null;

        int temporalIdPlus1 = nal[1] & 0x07;
        if (temporalIdPlus1 == 0)
            return null;
        int temporalId = temporalIdPlus1 - 1;
        bool irap = nalType is >= 16 and <= 23;
        bool idr = nalType is 19 or 20;

        var reader = new BitReader(ToRbsp(nal[2..], MaxSliceHeaderBytes));
        if (!reader.TryReadBit(out int firstSliceFlag))
            return null;
        bool firstSlice = firstSliceFlag != 0;
        if (irap && !reader.TrySkipBits(1))
            return null;
        if (!reader.TryReadUnsignedExpGolomb(out uint ppsIdRaw) ||
            ppsIdRaw > int.MaxValue ||
            !resolver.TryResolveH265((int)ppsIdRaw, out H265Pps? pps, out H265Sps? sps, out H265Vps? vps) ||
            pps is null || sps is null)
            return null;

        // Parsing a non-first HEVC slice requires the CTB address width and additional
        // dependent-slice state. It is intentionally not guessed; another first slice in
        // the same access unit supplies the trustworthy ordering point.
        if (!firstSlice)
            return null;

        if (!reader.TrySkipBits(pps.NumExtraSliceHeaderBits) ||
            !reader.TryReadUnsignedExpGolomb(out uint rawSliceType))
            return null;
        if (pps.OutputFlagPresentFlag && !reader.TrySkipBits(1))
            return null;
        if (sps.SeparateColourPlaneFlag && !reader.TrySkipBits(2))
            return null;

        int pocLsb = 0;
        uint pocRaw = 0;
        if (!idr)
        {
            if (!reader.TryReadBits(sps.PocBits, out pocRaw))
                return null;
            pocLsb = (int)pocRaw;
        }

        return new H26xFramePoint(
            -1,
            pocLsb,
            pocLsb,
            0,
            sps.PocModulus,
            pps.Id,
            sps.Id,
            vps?.Id ?? sps.VpsId,
            nalType,
            -1,
            temporalId,
            (int)Math.Min(rawSliceType, 2),
            irap,
            nalType is not (0 or 2 or 4 or 6 or 8),
            true,
            sampleOffset);
    }

    private static bool TryParseH264Sps(ReadOnlySpan<byte> nal, out H264Sps? result)
    {
        result = null;
        if (nal.Length < 4 || (nal[0] & 0x1F) != 7)
            return false;

        byte profile = nal[1];
        byte constraints = nal[2];
        byte level = nal[3];
        var reader = new BitReader(ToRbsp(nal[4..], MaxParameterNalBytes));
        if (!reader.TryReadUnsignedExpGolomb(out uint spsIdRaw) || spsIdRaw > 31)
            return false;

        uint chromaFormatIdc = 1;
        bool separateColourPlane = false;
        if (IsH264HighProfile(profile))
        {
            if (!reader.TryReadUnsignedExpGolomb(out chromaFormatIdc) || chromaFormatIdc > 3)
                return false;
            if (chromaFormatIdc == 3)
            {
                if (!reader.TryReadBit(out int separateFlag))
                    return false;
                separateColourPlane = separateFlag != 0;
            }
            if (!reader.TryReadUnsignedExpGolomb(out _) ||
                !reader.TryReadUnsignedExpGolomb(out _) ||
                !reader.TrySkipBits(1) ||
                !reader.TryReadBit(out int scalingMatrixPresent))
                return false;
            if (scalingMatrixPresent != 0)
            {
                int count = chromaFormatIdc != 3 ? 8 : 12;
                for (int index = 0; index < count; index++)
                {
                    if (!reader.TryReadBit(out int listPresent))
                        return false;
                    if (listPresent != 0 && !SkipH264ScalingList(ref reader, index < 6 ? 16 : 64))
                        return false;
                }
            }
        }

        if (!reader.TryReadUnsignedExpGolomb(out uint log2FrameNumMinus4) || log2FrameNumMinus4 > 12 ||
            !reader.TryReadUnsignedExpGolomb(out uint pocTypeRaw) || pocTypeRaw > 2)
            return false;

        int pocBits = 0;
        bool deltaAlwaysZero = false;
        if (pocTypeRaw == 0)
        {
            if (!reader.TryReadUnsignedExpGolomb(out uint log2PocMinus4) || log2PocMinus4 > 12)
                return false;
            pocBits = checked((int)log2PocMinus4 + 4);
        }
        else if (pocTypeRaw == 1)
        {
            if (!reader.TryReadBit(out int deltaZero) ||
                !reader.TryReadSignedExpGolomb(out _) ||
                !reader.TryReadSignedExpGolomb(out _) ||
                !reader.TryReadUnsignedExpGolomb(out uint cycleCount) ||
                cycleCount > 255)
                return false;
            deltaAlwaysZero = deltaZero != 0;
            for (uint index = 0; index < cycleCount; index++)
            {
                if (!reader.TryReadSignedExpGolomb(out _))
                    return false;
            }
        }

        if (!reader.TryReadUnsignedExpGolomb(out _) ||
            !reader.TrySkipBits(1) ||
            !reader.TryReadUnsignedExpGolomb(out uint widthInMbsMinus1) || widthInMbsMinus1 > 65535 ||
            !reader.TryReadUnsignedExpGolomb(out uint heightInMapUnitsMinus1) || heightInMapUnitsMinus1 > 65535 ||
            !reader.TryReadBit(out int frameMbsOnly))
            return false;
        bool frameMbsOnlyFlag = frameMbsOnly != 0;
        if (!frameMbsOnlyFlag && !reader.TrySkipBits(1))
            return false;
        if (!reader.TrySkipBits(1) || !reader.TryReadBit(out int croppingFlag))
            return false;

        uint cropLeft = 0;
        uint cropRight = 0;
        uint cropTop = 0;
        uint cropBottom = 0;
        if (croppingFlag != 0 &&
            (!reader.TryReadUnsignedExpGolomb(out cropLeft) ||
             !reader.TryReadUnsignedExpGolomb(out cropRight) ||
             !reader.TryReadUnsignedExpGolomb(out cropTop) ||
             !reader.TryReadUnsignedExpGolomb(out cropBottom)))
            return false;

        int frameNumBits = checked((int)log2FrameNumMinus4 + 4);
        int frameNumModulus = 1 << frameNumBits;
        int pocModulus = pocBits > 0 ? 1 << pocBits : frameNumModulus * 2;
        int chromaArrayType = separateColourPlane ? 0 : (int)chromaFormatIdc;
        int subWidth = chromaArrayType switch { 1 or 2 => 2, _ => 1 };
        int subHeight = chromaArrayType == 1 ? 2 : 1;
        int cropUnitX = chromaArrayType == 0 ? 1 : subWidth;
        int cropUnitY = chromaArrayType == 0
            ? 2 - (frameMbsOnlyFlag ? 1 : 0)
            : subHeight * (2 - (frameMbsOnlyFlag ? 1 : 0));
        long codedWidth = ((long)widthInMbsMinus1 + 1) * 16;
        long codedHeight = (2 - (frameMbsOnlyFlag ? 1 : 0)) * ((long)heightInMapUnitsMinus1 + 1) * 16;
        int width = ClampDimension(codedWidth - ((long)cropLeft + cropRight) * cropUnitX);
        int height = ClampDimension(codedHeight - ((long)cropTop + cropBottom) * cropUnitY);
        ulong rawHash = HashBytes(nal[..Math.Min(nal.Length, MaxParameterNalBytes)]);
        ulong stable = HashFields(profile, constraints, level, spsIdRaw, chromaFormatIdc,
            (uint)width, (uint)height, (uint)frameNumBits, pocTypeRaw, (uint)pocBits,
            frameMbsOnlyFlag ? 1U : 0U, separateColourPlane ? 1U : 0U);

        result = new H264Sps(
            (int)spsIdRaw,
            profile,
            constraints,
            level,
            width,
            height,
            frameNumBits,
            frameNumModulus,
            (int)pocTypeRaw,
            pocBits,
            pocModulus,
            frameMbsOnlyFlag,
            separateColourPlane,
            deltaAlwaysZero,
            rawHash,
            stable);
        return true;
    }

    private static bool TryParseH264Pps(ReadOnlySpan<byte> nal, out H264Pps? result)
    {
        result = null;
        if (nal.Length < 2 || (nal[0] & 0x1F) != 8)
            return false;

        var reader = new BitReader(ToRbsp(nal[1..], MaxParameterNalBytes));
        if (!reader.TryReadUnsignedExpGolomb(out uint ppsIdRaw) || ppsIdRaw > 255 ||
            !reader.TryReadUnsignedExpGolomb(out uint spsIdRaw) || spsIdRaw > 31 ||
            !reader.TryReadBit(out int entropyCodingFlag) ||
            !reader.TryReadBit(out int bottomFieldFlag))
            return false;

        ulong rawHash = HashBytes(nal[..Math.Min(nal.Length, MaxParameterNalBytes)]);
        ulong stable = HashFields(ppsIdRaw, spsIdRaw, (uint)entropyCodingFlag, (uint)bottomFieldFlag);
        result = new H264Pps(
            (int)ppsIdRaw,
            (int)spsIdRaw,
            entropyCodingFlag != 0,
            bottomFieldFlag != 0,
            rawHash,
            stable);
        return true;
    }

    private static bool TryParseH265Vps(ReadOnlySpan<byte> nal, out H265Vps? result)
    {
        result = null;
        if (nal.Length < 3 || ((nal[0] >> 1) & 0x3F) != 32)
            return false;

        var reader = new BitReader(ToRbsp(nal[2..], MaxParameterNalBytes));
        if (!reader.TryReadBits(4, out uint vpsIdRaw))
            return false;
        ulong rawHash = HashBytes(nal[..Math.Min(nal.Length, MaxParameterNalBytes)]);
        result = new H265Vps((int)vpsIdRaw, rawHash, HashFields(vpsIdRaw));
        return true;
    }

    private static bool TryParseH265Sps(ReadOnlySpan<byte> nal, out H265Sps? result)
    {
        result = null;
        if (nal.Length < 4 || ((nal[0] >> 1) & 0x3F) != 33)
            return false;

        var reader = new BitReader(ToRbsp(nal[2..], MaxParameterNalBytes));
        if (!reader.TryReadBits(4, out uint vpsIdRaw) ||
            !reader.TryReadBits(3, out uint maxSubLayersMinus1) || maxSubLayersMinus1 > 6 ||
            !reader.TrySkipBits(1) ||
            !TrySkipH265ProfileTierLevel(ref reader, (int)maxSubLayersMinus1) ||
            !reader.TryReadUnsignedExpGolomb(out uint spsIdRaw) || spsIdRaw > 15 ||
            !reader.TryReadUnsignedExpGolomb(out uint chromaFormatIdc) || chromaFormatIdc > 3)
            return false;

        bool separateColourPlane = false;
        if (chromaFormatIdc == 3)
        {
            if (!reader.TryReadBit(out int separateFlag))
                return false;
            separateColourPlane = separateFlag != 0;
        }

        if (!reader.TryReadUnsignedExpGolomb(out uint widthRaw) || widthRaw is 0 or > 131072 ||
            !reader.TryReadUnsignedExpGolomb(out uint heightRaw) || heightRaw is 0 or > 131072 ||
            !reader.TryReadBit(out int conformanceWindowFlag))
            return false;

        uint left = 0;
        uint right = 0;
        uint top = 0;
        uint bottom = 0;
        if (conformanceWindowFlag != 0 &&
            (!reader.TryReadUnsignedExpGolomb(out left) ||
             !reader.TryReadUnsignedExpGolomb(out right) ||
             !reader.TryReadUnsignedExpGolomb(out top) ||
             !reader.TryReadUnsignedExpGolomb(out bottom)))
            return false;

        if (!reader.TryReadUnsignedExpGolomb(out _) ||
            !reader.TryReadUnsignedExpGolomb(out _) ||
            !reader.TryReadUnsignedExpGolomb(out uint log2PocMinus4) || log2PocMinus4 > 12)
            return false;

        int subWidth = chromaFormatIdc is 1 or 2 ? 2 : 1;
        int subHeight = chromaFormatIdc == 1 ? 2 : 1;
        int width = ClampDimension((long)widthRaw - ((long)left + right) * subWidth);
        int height = ClampDimension((long)heightRaw - ((long)top + bottom) * subHeight);
        int pocBits = checked((int)log2PocMinus4 + 4);
        int pocModulus = 1 << pocBits;
        ulong rawHash = HashBytes(nal[..Math.Min(nal.Length, MaxParameterNalBytes)]);
        ulong stable = HashFields(vpsIdRaw, spsIdRaw, chromaFormatIdc, (uint)width, (uint)height,
            (uint)pocBits, separateColourPlane ? 1U : 0U);
        result = new H265Sps(
            (int)spsIdRaw,
            (int)vpsIdRaw,
            width,
            height,
            pocBits,
            pocModulus,
            separateColourPlane,
            rawHash,
            stable);
        return true;
    }

    private static bool TryParseH265Pps(ReadOnlySpan<byte> nal, out H265Pps? result)
    {
        result = null;
        if (nal.Length < 3 || ((nal[0] >> 1) & 0x3F) != 34)
            return false;

        var reader = new BitReader(ToRbsp(nal[2..], MaxParameterNalBytes));
        if (!reader.TryReadUnsignedExpGolomb(out uint ppsIdRaw) || ppsIdRaw > 63 ||
            !reader.TryReadUnsignedExpGolomb(out uint spsIdRaw) || spsIdRaw > 15 ||
            !reader.TryReadBit(out int dependentSlices) ||
            !reader.TryReadBit(out int outputFlagPresent) ||
            !reader.TryReadBits(3, out uint extraBits))
            return false;

        ulong rawHash = HashBytes(nal[..Math.Min(nal.Length, MaxParameterNalBytes)]);
        ulong stable = HashFields(ppsIdRaw, spsIdRaw, (uint)dependentSlices, (uint)outputFlagPresent, extraBits);
        result = new H265Pps(
            (int)ppsIdRaw,
            (int)spsIdRaw,
            dependentSlices != 0,
            outputFlagPresent != 0,
            (int)extraBits,
            rawHash,
            stable);
        return true;
    }

    private static bool TrySkipH265ProfileTierLevel(ref BitReader reader, int maxSubLayersMinus1)
    {
        if (!reader.TrySkipBits(2 + 1 + 5 + 32 + 48 + 8))
            return false;

        Span<int> profilePresent = stackalloc int[7];
        Span<int> levelPresent = stackalloc int[7];
        for (int layer = 0; layer < maxSubLayersMinus1; layer++)
        {
            if (!reader.TryReadBit(out profilePresent[layer]) || !reader.TryReadBit(out levelPresent[layer]))
                return false;
        }
        if (maxSubLayersMinus1 > 0 && !reader.TrySkipBits((8 - maxSubLayersMinus1) * 2))
            return false;

        for (int layer = 0; layer < maxSubLayersMinus1; layer++)
        {
            if (profilePresent[layer] != 0 && !reader.TrySkipBits(2 + 1 + 5 + 32 + 48))
                return false;
            if (levelPresent[layer] != 0 && !reader.TrySkipBits(8))
                return false;
        }
        return true;
    }

    private static bool SkipH264ScalingList(ref BitReader reader, int size)
    {
        int lastScale = 8;
        int nextScale = 8;
        for (int index = 0; index < size; index++)
        {
            if (nextScale != 0)
            {
                if (!reader.TryReadSignedExpGolomb(out int deltaScale))
                    return false;
                nextScale = (lastScale + deltaScale + 256) & 0xFF;
            }
            lastScale = nextScale == 0 ? lastScale : nextScale;
        }
        return true;
    }

    private static bool IsH264HighProfile(int profile) => profile is
        44 or 83 or 86 or 100 or 110 or 118 or 122 or 128 or 134 or 135 or 138 or 139 or 144 or 244;

    private static string? BuildStreamKey(
        H26xCodecKind codec,
        IReadOnlyList<H26xFramePoint> frames,
        ulong parameterSignature,
        ParameterResolver resolver)
    {
        if (codec == H26xCodecKind.H264)
        {
            H264Pps? pps = null;
            H264Sps? sps = null;
            bool resolved = frames.Count > 0
                ? resolver.TryResolveH264(frames[0].PpsId, out pps, out sps)
                : resolver.TryResolveUniqueH264(out pps, out sps);
            if (resolved && pps is not null && sps is not null)
                return BuildH264StreamKey(pps, sps);
        }
        else
        {
            H265Pps? pps = null;
            H265Sps? sps = null;
            H265Vps? vps = null;
            bool resolved = frames.Count > 0
                ? resolver.TryResolveH265(frames[0].PpsId, out pps, out sps, out vps)
                : resolver.TryResolveUniqueH265(out pps, out sps, out vps);
            if (resolved && pps is not null && sps is not null)
                return BuildH265StreamKey(pps, sps, vps);
        }

        return parameterSignature != 0 ? $"{codec}:{parameterSignature:X16}" : null;
    }

    private static string BuildH264StreamKey(H264Pps pps, H264Sps sps) =>
        $"H264:{sps.ProfileIdc}:{sps.LevelIdc}:{sps.Width}x{sps.Height}:" +
        $"S{sps.Id}:P{pps.Id}:{sps.RawHash:X16}:{pps.RawHash:X16}";

    private static string BuildH265StreamKey(H265Pps pps, H265Sps sps, H265Vps? vps) =>
        $"H265:{sps.Width}x{sps.Height}:V{vps?.Id ?? sps.VpsId}:" +
        $"S{sps.Id}:P{pps.Id}:{(vps?.RawHash ?? 0):X16}:" +
        $"{sps.RawHash:X16}:{pps.RawHash:X16}";

    private static bool ParameterRelationshipMatches(H26xFramePoint current, H26xFramePoint next)
    {
        if (current.PpsId != next.PpsId || current.SpsId != next.SpsId)
            return false;
        if (current.VpsId >= 0 && next.VpsId >= 0 && current.VpsId != next.VpsId)
            return false;
        return true;
    }

    private static long ScoreFrameContinuity(
        H26xFragmentAnalysis current,
        H26xFragmentAnalysis next,
        H26xFramePoint from,
        H26xFramePoint to,
        out bool plausible)
    {
        plausible = false;
        if (current.Codec != H26xCodecKind.H264 || from.FrameNumModulus <= 0 || to.FrameNumModulus != from.FrameNumModulus)
            return 0;

        int delta = ForwardModulo(from.FrameNum, to.FrameNum, from.FrameNumModulus);
        int expected = current.FrameStep > 0 ? current.FrameStep : next.FrameStep > 0 ? next.FrameStep : 1;
        int maximum = Math.Min(MaxReasonableFrameGap, Math.Max(8, from.FrameNumModulus / 4));
        plausible = delta <= maximum || (delta == 0 && from.Poc != to.Poc);
        if (!plausible)
            return 20 * CodecScoreScale;

        int distance = Math.Abs(delta - expected);
        long penalty = distance * CodecScoreScale;
        if (delta == 0)
            penalty += CodecScoreScale / 2;
        if (!from.IsReference || !to.IsReference)
            penalty /= 2;
        return penalty;
    }

    private static long ScorePocContinuity(
        H26xFragmentAnalysis current,
        H26xFragmentAnalysis next,
        H26xFramePoint from,
        H26xFramePoint to,
        out bool plausible)
    {
        plausible = false;
        if (from.PocModulus <= 0 || to.PocModulus != from.PocModulus)
            return 0;

        int forward = ForwardModulo(from.PocLsb, to.PocLsb, from.PocModulus);
        int expected = current.PocStep != 0 ? current.PocStep : next.PocStep != 0 ? next.PocStep : 2;
        int expectedForward = expected >= 0 ? expected : from.PocModulus + expected;
        expectedForward %= from.PocModulus;

        int circularDistance = CircularDistance(forward, expectedForward, from.PocModulus);
        int maximum = Math.Min(Math.Max(32, from.PocModulus / 4), 512);
        plausible = forward <= maximum || circularDistance <= Math.Max(8, maximum / 4);
        if (!plausible)
            return 20 * CodecScoreScale;

        long penalty = (long)circularDistance * (CodecScoreScale / 2);
        // B pictures may move display POC backwards while decode order still advances.
        if (forward > from.PocModulus / 2)
            penalty += 2 * CodecScoreScale;
        if (current.Codec == H26xCodecKind.H265 && to.TemporalId > 0)
            penalty += to.TemporalId * 250L;
        return penalty;
    }

    private static int EstimateFrameStep(IReadOnlyList<H26xFramePoint> frames)
    {
        var deltas = new List<int>();
        for (int index = 1; index < frames.Count; index++)
        {
            H26xFramePoint previous = frames[index - 1];
            H26xFramePoint current = frames[index];
            if (previous.FrameNumModulus <= 0 || current.FrameNumModulus != previous.FrameNumModulus || current.IsRandomAccess)
                continue;
            int delta = ForwardModulo(previous.FrameNum, current.FrameNum, previous.FrameNumModulus);
            if (delta is > 0 and <= MaxReasonableFrameGap)
                deltas.Add(delta);
        }
        return MedianOrDefault(deltas, 1);
    }

    private static int EstimatePocStep(IReadOnlyList<H26xFramePoint> frames)
    {
        var deltas = new List<int>();
        for (int index = 1; index < frames.Count; index++)
        {
            H26xFramePoint previous = frames[index - 1];
            H26xFramePoint current = frames[index];
            if (previous.PocModulus <= 0 || current.PocModulus != previous.PocModulus || current.IsRandomAccess)
                continue;
            int delta = SignedModuloDistance(previous.PocLsb, current.PocLsb, previous.PocModulus);
            if (Math.Abs(delta) <= Math.Min(512, previous.PocModulus / 2))
                deltas.Add(delta);
        }
        return MedianOrDefault(deltas, 2);
    }

    private static int MedianOrDefault(List<int> values, int fallback)
    {
        if (values.Count == 0)
            return fallback;
        values.Sort();
        return values[values.Count / 2];
    }

    private static List<H26xFramePoint> DeduplicateFrames(List<H26xFramePoint> frames)
    {
        if (frames.Count < 2)
            return frames;

        var result = new List<H26xFramePoint>(frames.Count);
        foreach (H26xFramePoint frame in frames)
        {
            if (result.Count > 0)
            {
                H26xFramePoint previous = result[^1];
                bool samePicture = previous.PpsId == frame.PpsId &&
                                   previous.FrameNum == frame.FrameNum &&
                                   previous.PocLsb == frame.PocLsb &&
                                   previous.IdrPicId == frame.IdrPicId &&
                                   previous.NalType == frame.NalType &&
                                   frame.SampleOffset - previous.SampleOffset < 1024 * 1024;
                if (samePicture)
                {
                    if (!previous.IsFirstSlice && frame.IsFirstSlice)
                        result[^1] = frame;
                    continue;
                }
            }
            result.Add(frame);
        }
        return result;
    }

    private static int CalculateConfidence(
        int frameCount,
        bool hasResolvedSignature,
        bool hasParameterSets,
        bool beginsWithRandomAccess,
        int firstSliceCount,
        bool hasEnd)
    {
        int score = 62;
        if (frameCount >= 2) score += 12;
        if (frameCount >= 8) score += 6;
        if (hasResolvedSignature) score += 8;
        if (hasParameterSets) score += 5;
        if (beginsWithRandomAccess) score += 4;
        if (firstSliceCount == frameCount && frameCount > 0) score += 3;
        if (hasEnd) score += 2;
        return Math.Clamp(score, 0, 99);
    }

    private static ulong SelectParameterSignature(
        IReadOnlyList<H26xFramePoint> frames,
        ulong headSignature,
        ulong tailSignature)
    {
        ulong signature = CombineHash(headSignature, tailSignature);
        foreach (H26xFramePoint frame in frames.Take(8))
        {
            signature = CombineHash(signature, HashFields(
                (uint)Math.Max(0, frame.PpsId),
                (uint)Math.Max(0, frame.SpsId),
                (uint)Math.Max(0, frame.VpsId),
                (uint)Math.Max(0, frame.FrameNumModulus),
                (uint)Math.Max(0, frame.PocModulus)));
        }
        return signature;
    }

    private readonly record struct NalRange(int StartOffset, int HeaderOffset, int EndOffset, int NalType);

    private static IEnumerable<NalRange> EnumerateNalRanges(ReadOnlySpan<byte> data, H26xCodecKind codec)
    {
        // Span-backed iterators cannot use yield. Materialize only compact integer ranges;
        // payload bytes stay in the caller's sample buffer.
        var ranges = new List<NalRange>();
        int position = FindStartCode(data, 0, out int prefixLength);
        int units = 0;
        while (position >= 0 && units++ < MaxNalUnitsPerSample)
        {
            int header = position + prefixLength;
            int headerLength = codec == H26xCodecKind.H264 ? 1 : 2;
            if (header + headerLength > data.Length)
                break;

            int next = FindStartCode(data, header + headerLength, out int nextPrefix);
            int end = next >= 0 ? next : data.Length;
            while (end > header + headerLength && data[end - 1] == 0)
                end--;

            int nalType = codec == H26xCodecKind.H264
                ? data[header] & 0x1F
                : (data[header] >> 1) & 0x3F;
            bool validHeader = (data[header] & 0x80) == 0 &&
                               (codec == H26xCodecKind.H264 || ((data[header + 1] & 0x07) != 0));
            if (validHeader && end > header + headerLength)
                ranges.Add(new NalRange(position, header, end, nalType));

            if (next < 0)
                break;
            position = next;
            prefixLength = nextPrefix;
        }
        return ranges;
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int start, out int prefixLength)
    {
        prefixLength = 0;
        for (int index = Math.Max(0, start); index + 3 <= data.Length; index++)
        {
            if (data[index] != 0 || data[index + 1] != 0)
                continue;
            if (index + 3 <= data.Length && data[index + 2] == 1)
            {
                prefixLength = 3;
                return index;
            }
            if (index + 4 <= data.Length && data[index + 2] == 0 && data[index + 3] == 1)
            {
                prefixLength = 4;
                return index;
            }
        }
        return -1;
    }

    private static byte[] ToRbsp(ReadOnlySpan<byte> ebsp, int maximumBytes)
    {
        int length = Math.Min(ebsp.Length, maximumBytes);
        byte[] rbsp = new byte[length];
        int written = 0;
        int zeroCount = 0;
        for (int index = 0; index < length; index++)
        {
            byte value = ebsp[index];
            if (zeroCount >= 2 && value == 0x03)
            {
                zeroCount = 0;
                continue;
            }
            rbsp[written++] = value;
            zeroCount = value == 0 ? zeroCount + 1 : 0;
        }
        if (written != rbsp.Length)
            Array.Resize(ref rbsp, written);
        return rbsp;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bitOffset;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bitOffset = 0;
        }

        public bool TryReadBit(out int value)
        {
            value = 0;
            if (_bitOffset >= _data.Length * 8)
                return false;
            int byteIndex = _bitOffset >> 3;
            int shift = 7 - (_bitOffset & 7);
            value = (_data[byteIndex] >> shift) & 1;
            _bitOffset++;
            return true;
        }

        public bool TrySkipBits(int count)
        {
            if (count < 0 || _bitOffset > _data.Length * 8 - count)
                return false;
            _bitOffset += count;
            return true;
        }

        public bool TryReadBits(int count, out uint value)
        {
            value = 0;
            if (count is < 0 or > 32 || _bitOffset > _data.Length * 8 - count)
                return false;
            for (int index = 0; index < count; index++)
            {
                if (!TryReadBit(out int bit))
                    return false;
                value = (value << 1) | (uint)bit;
            }
            return true;
        }

        public bool TryReadUnsignedExpGolomb(out uint value)
        {
            value = 0;
            int leadingZeros = 0;
            while (leadingZeros < 32)
            {
                if (!TryReadBit(out int bit))
                    return false;
                if (bit != 0)
                    break;
                leadingZeros++;
            }
            if (leadingZeros >= 32)
                return false;
            if (leadingZeros == 0)
                return true;
            if (!TryReadBits(leadingZeros, out uint suffix))
                return false;
            value = ((1U << leadingZeros) - 1U) + suffix;
            return true;
        }

        public bool TryReadSignedExpGolomb(out int value)
        {
            value = 0;
            if (!TryReadUnsignedExpGolomb(out uint codeNum) || codeNum > int.MaxValue)
                return false;
            int magnitude = (int)((codeNum + 1) >> 1);
            value = (codeNum & 1) == 0 ? -magnitude : magnitude;
            return true;
        }
    }

    internal sealed record H264Sps(
        int Id,
        int ProfileIdc,
        int ConstraintFlags,
        int LevelIdc,
        int Width,
        int Height,
        int FrameNumBits,
        int FrameNumModulus,
        int PicOrderCntType,
        int PocBits,
        int PocModulus,
        bool FrameMbsOnlyFlag,
        bool SeparateColourPlaneFlag,
        bool DeltaPicOrderAlwaysZeroFlag,
        ulong RawHash,
        ulong StableSignature);

    internal sealed record H264Pps(
        int Id,
        int SpsId,
        bool EntropyCodingModeFlag,
        bool BottomFieldPicOrderInFramePresentFlag,
        ulong RawHash,
        ulong StableSignature);

    internal sealed record H265Vps(int Id, ulong RawHash, ulong StableSignature);

    internal sealed record H265Sps(
        int Id,
        int VpsId,
        int Width,
        int Height,
        int PocBits,
        int PocModulus,
        bool SeparateColourPlaneFlag,
        ulong RawHash,
        ulong StableSignature);

    internal sealed record H265Pps(
        int Id,
        int SpsId,
        bool DependentSliceSegmentsEnabledFlag,
        bool OutputFlagPresentFlag,
        int NumExtraSliceHeaderBits,
        ulong RawHash,
        ulong StableSignature);

    private static int ForwardModulo(int from, int to, int modulus)
    {
        if (modulus <= 0)
            return int.MaxValue;
        int normalizedFrom = ((from % modulus) + modulus) % modulus;
        int normalizedTo = ((to % modulus) + modulus) % modulus;
        int delta = normalizedTo - normalizedFrom;
        return delta < 0 ? delta + modulus : delta;
    }

    private static int SignedModuloDistance(int from, int to, int modulus)
    {
        int forward = ForwardModulo(from, to, modulus);
        return forward > modulus / 2 ? forward - modulus : forward;
    }

    private static int CircularDistance(int left, int right, int modulus)
    {
        int direct = Math.Abs(left - right);
        return Math.Min(direct, modulus - direct);
    }

    private static int ClampInt(long value) => value switch
    {
        > int.MaxValue => int.MaxValue,
        < int.MinValue => int.MinValue,
        _ => (int)value
    };

    private static int ClampDimension(long value) => value is > 0 and <= 131072 ? (int)value : 0;

    private static long SaturatingAdd(long left, long right)
    {
        if (right > 0 && left > long.MaxValue - right)
            return long.MaxValue;
        if (right < 0 && left < long.MinValue - right)
            return long.MinValue;
        return left + right;
    }

    private static ulong HashBytes(ReadOnlySpan<byte> data)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (byte value in data)
        {
            hash ^= value;
            hash *= prime;
        }
        return hash;
    }

    private static ulong HashFields(params uint[] fields)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (uint field in fields)
        {
            hash ^= field;
            hash *= prime;
            hash ^= field >> 16;
            hash *= prime;
        }
        return hash;
    }

    private static ulong CombineHash(ulong left, ulong right)
    {
        if (left == 0)
            return right;
        if (right == 0)
            return left;
        ulong hash = left;
        hash ^= right;
        hash *= 1099511628211UL;
        return hash;
    }
}
