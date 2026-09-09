namespace NSXVeriKurtarmaPro.Models;

public enum ScanMode
{
    Quick,
    Deep
}

public enum DeepScanTarget
{
    // Values are kept stable for backward compatibility with older .nsx projects.
    All = 0,
    Photo = 1,
    Video = 2,
    Document = 3,
    PhotoVideo = 4,
    PhotoDocument = 5,
    VideoDocument = 6,
    None = 7
}

public static class DeepScanTargetExtensions
{
    public static bool Includes(this DeepScanTarget scope, DeepScanTarget target)
    {
        if (scope == DeepScanTarget.All)
            return target is DeepScanTarget.Photo or DeepScanTarget.Video or DeepScanTarget.Document;

        return target switch
        {
            DeepScanTarget.Photo => scope is DeepScanTarget.Photo or DeepScanTarget.PhotoVideo or DeepScanTarget.PhotoDocument,
            DeepScanTarget.Video => scope is DeepScanTarget.Video or DeepScanTarget.PhotoVideo or DeepScanTarget.VideoDocument,
            DeepScanTarget.Document => scope is DeepScanTarget.Document or DeepScanTarget.PhotoDocument or DeepScanTarget.VideoDocument,
            _ => false
        };
    }

    public static DeepScanTarget Toggle(this DeepScanTarget scope, DeepScanTarget target)
    {
        if (target is not (DeepScanTarget.Photo or DeepScanTarget.Video or DeepScanTarget.Document))
            return scope;

        int mask = ToMask(scope);
        int bit = target switch
        {
            DeepScanTarget.Photo => 1,
            DeepScanTarget.Video => 2,
            DeepScanTarget.Document => 4,
            _ => 0
        };

        int next = (mask & bit) != 0 ? mask & ~bit : mask | bit;
        return FromMask(next);
    }

    private static int ToMask(DeepScanTarget scope) => scope switch
    {
        DeepScanTarget.None => 0,
        DeepScanTarget.All => 7,
        DeepScanTarget.Photo => 1,
        DeepScanTarget.Video => 2,
        DeepScanTarget.Document => 4,
        DeepScanTarget.PhotoVideo => 3,
        DeepScanTarget.PhotoDocument => 5,
        DeepScanTarget.VideoDocument => 6,
        _ => 0
    };

    private static DeepScanTarget FromMask(int mask) => mask switch
    {
        0 => DeepScanTarget.None,
        1 => DeepScanTarget.Photo,
        2 => DeepScanTarget.Video,
        3 => DeepScanTarget.PhotoVideo,
        4 => DeepScanTarget.Document,
        5 => DeepScanTarget.PhotoDocument,
        6 => DeepScanTarget.VideoDocument,
        7 => DeepScanTarget.All,
        _ => DeepScanTarget.None
    };
}

public enum RecoverySourceKind
{
    RawContiguous,
    NtfsResident,
    NtfsRunList,
    FatContiguous,
    ExFatContiguous,
    Extents
}

public enum RecoveryTransformKind
{
    None,
    LengthPrefixedH264ToAnnexB,
    LengthPrefixedH265ToAnnexB
}

public sealed record DataRun(long LogicalClusterNumber, long ClusterCount, bool IsSparse);

public sealed record SourceExtent(long Offset, long Length);

public sealed class RecoveryBadSectorSnapshot
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public int FailureEvents { get; set; }
}

public sealed class RecoveryScannedRange
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public string Kind { get; set; } = "RAW";
}

/// <summary>
/// Compact, serializable fragment anchor needed to continue Pass 3 reconstruction
/// after an interrupted deep scan. It stores source coordinates and reconstruction
/// metadata only; it never embeds the source file payload.
/// </summary>
public sealed class RecoveryFragmentCheckpointItem
{
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string RecoveryState { get; set; } = "Video Parçası";
    public string TypeGlyph { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public RecoverySourceKind SourceKind { get; set; } = RecoverySourceKind.RawContiguous;
    public long SourceOffset { get; set; }
    public RecoveryTransformKind TransformKind { get; set; } = RecoveryTransformKind.None;

    public static RecoveryFragmentCheckpointItem FromItem(RecoveryFileItem item) => new()
    {
        FileName = item.FileName,
        Extension = item.Extension,
        SizeBytes = item.SizeBytes,
        RecoveryState = item.RecoveryState,
        TypeGlyph = item.TypeGlyph,
        SourceText = item.SourceText,
        SourceKind = item.SourceKind,
        SourceOffset = item.SourceOffset,
        TransformKind = item.TransformKind
    };

    public RecoveryFileItem? ToItem()
    {
        string extension = FileTypeHelper.Normalize(Extension);
        if (SourceOffset < 0 || SizeBytes < 4096 || !FileTypeHelper.IsVideo(extension))
            return null;

        return new RecoveryFileItem
        {
            FileName = string.IsNullOrWhiteSpace(FileName)
                ? $"Graph_Dugumu_{SourceOffset:X}.{extension.ToLowerInvariant()}"
                : FileName,
            Extension = extension,
            SizeBytes = SizeBytes,
            RecoveryState = string.IsNullOrWhiteSpace(RecoveryState) ? "Video Parçası" : RecoveryState,
            TypeGlyph = string.IsNullOrWhiteSpace(TypeGlyph) ? FileTypeHelper.GetGlyph(extension) : TypeGlyph,
            SourceText = string.IsNullOrWhiteSpace(SourceText) ? "NSX V2 fragment checkpoint" : SourceText,
            SourceKind = SourceKind,
            SourceOffset = SourceOffset,
            TransformKind = TransformKind
        };
    }
}

/// <summary>
/// Serializable scan checkpoint used by .nsx projects. Deep Scan stores pass/fragment
/// state here; Quick Scan stores the active metadata/surface phase and exact resume offset.
/// Recovered file payloads are never copied into the project.
/// </summary>
public sealed class RecoveryScanCheckpoint
{
    public int PassNumber { get; set; } = 1;
    public string Stage { get; set; } = string.Empty;
    public long ResumePosition { get; set; }
    public long ResumeTotal { get; set; }
    public bool MetadataStageCompleted { get; set; }
    public bool RawScanCompleted { get; set; }
    public bool FragmentStageCompleted { get; set; }
    public bool FinalValidationStarted { get; set; }
    public int AdaptiveBlockSize { get; set; }
    public bool AdaptiveSafeScanActive { get; set; }
    public long AdaptiveBlocksObserved { get; set; }
    public long AdaptiveCleanBlocks { get; set; }
    public long AdaptiveDenseBlocks { get; set; }
    public long AdaptiveErrorBlocks { get; set; }
    public long AdaptiveUnreadableBytes { get; set; }
    public int AdaptiveBlockSizeChanges { get; set; }
    public long AdaptiveReadSamples { get; set; }
    public long AdaptiveReadBytes { get; set; }
    public double AdaptiveReadMilliseconds { get; set; }
    public double AdaptivePeakBytesPerSecond { get; set; }
    public long AdaptiveSlowReadSamples { get; set; }
    public long AdaptiveFastReadSamples { get; set; }
    public long BadSectorFailureEvents { get; set; }
    public long BadSectorRecoveredBytes { get; set; }
    public long BadSectorSkippedKnownBadReads { get; set; }
    public List<RecoveryBadSectorSnapshot> BadSectors { get; set; } = [];
    public List<RecoveryScannedRange> ScannedRanges { get; set; } = [];

    // In-memory progress checkpoints may carry only newly discovered anchors as a
    // delta to avoid copying the entire fragment graph every 64 MB. Before a .nsx
    // project is saved, MainViewModel merges deltas into a full snapshot and sets
    // PendingFragmentNodesAreDelta to false.
    public bool PendingFragmentNodesAreDelta { get; set; }
    public List<RecoveryFragmentCheckpointItem> PendingFragmentNodes { get; set; } = [];
}

public sealed record OperationProgress(
    double Percent,
    string Title,
    string Detail,
    long ProcessedBytes = 0,
    long TotalBytes = 0,
    int FoundCount = 0,
    IReadOnlyList<RecoveryFileItem>? NewFiles = null,
    RecoveryScanCheckpoint? Checkpoint = null,
    IReadOnlyList<string>? NewHistoricalFolders = null);

public sealed class RecoveryScanDiagnostics
{
    public string MediaProfileKey { get; init; } = string.Empty;
    public string MediaProfileName { get; init; } = string.Empty;
    public long ReadSamples { get; init; }
    public long ReadBytes { get; init; }
    public double ActiveReadMilliseconds { get; init; }
    public double AverageReadBytesPerSecond { get; init; }
    public double PeakReadBytesPerSecond { get; init; }
    public long SlowReadSamples { get; init; }
    public long FastReadSamples { get; init; }
    public int InitialBlockSize { get; init; }
    public int FinalBlockSize { get; init; }
    public int BlockSizeChanges { get; init; }
    public long UnreadableBytes { get; init; }
    public int FalsePositiveRejected { get; init; }
    public int HighConfidenceFiles { get; init; }
    public int ScoredFiles { get; init; }
    public double AverageConfidenceScore { get; init; }
}

public sealed record ScanReport(
    IReadOnlyList<RecoveryFileItem> Files,
    string Summary,
    ScanMode ModeUsed,
    bool UsedFallback = false)
{
    public RecoveryScanDiagnostics? Diagnostics { get; init; }
    public IReadOnlyList<string> HistoricalFolders { get; init; } = Array.Empty<string>();
}
