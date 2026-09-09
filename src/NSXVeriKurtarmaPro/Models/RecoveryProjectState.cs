namespace NSXVeriKurtarmaPro.Models;

public sealed class RecoveryProjectState
{
    public int Version { get; set; } = 3;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
    public string ApplicationVersion { get; set; } = string.Empty;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    public string DeviceRootPath { get; set; } = string.Empty;
    public string DeviceDisplayName { get; set; } = string.Empty;
    public string DeviceFileSystem { get; set; } = string.Empty;
    public long DeviceTotalBytes { get; set; }
    public int? DevicePhysicalDriveNumber { get; set; }
    public bool DeviceIsWholePhysicalDisk { get; set; }
    public bool DeviceIsPartitionSource { get; set; }
    public long DevicePartitionOffsetBytes { get; set; }
    public long DevicePartitionLengthBytes { get; set; }
    public string DevicePartitionIdentity { get; set; } = string.Empty;
    public string DevicePartitionScheme { get; set; } = string.Empty;
    public bool DeviceWasRecoveredPartition { get; set; }
    public int DevicePartitionConfidence { get; set; }
    public string DeviceSerialNumber { get; set; } = string.Empty;
    public string DeviceVisualKind { get; set; } = string.Empty;
    public string DeviceBusTypeText { get; set; } = string.Empty;
    public bool DeviceCameraContentDetected { get; set; }
    public string MediaProfileKey { get; set; } = string.Empty;

    public ScanMode ScanMode { get; set; }
    public DeepScanTarget QuickScanScope { get; set; } = DeepScanTarget.All;
    public DeepScanTarget DeepScanScope { get; set; } = DeepScanTarget.All;
    public bool IsFolderScan { get; set; }
    public string FolderScanPath { get; set; } = string.Empty;
    public DeepScanTarget FolderScanScope { get; set; } = DeepScanTarget.None;
    public bool ResumeScan { get; set; }
    public bool ScanCompleted { get; set; }
    public bool WasPaused { get; set; }
    public long ResumePosition { get; set; }
    public long ResumeTotal { get; set; }
    public double ProgressPercent { get; set; }
    public RecoveryScanCheckpoint? ScanCheckpoint { get; set; }

    public string ResultCategory { get; set; } = "T\u00FCm\u00FC";
    public string ResultSearchText { get; set; } = string.Empty;
    public string ResultTypeGroup { get; set; } = "all";
    public string ResultExtension { get; set; } = string.Empty;
    public string ResultPathFilter { get; set; } = string.Empty;
    public bool IsResultTypeNavigationActive { get; set; }
    public List<string> ExpandedNavigationGroups { get; set; } = [];
    public List<string> HistoricalFolders { get; set; } = [];
    public string SelectedResultKey { get; set; } = string.Empty;

    public List<RecoveryProjectFileState> Files { get; set; } = [];
}

public sealed class RecoveryProjectFileState
{
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string RecoveryState { get; set; } = string.Empty;
    public string TypeGlyph { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public RecoverySourceKind SourceKind { get; set; }
    public bool IsExistingFile { get; set; }
    public long SourceOffset { get; set; }
    public int ClusterSize { get; set; }
    public byte[]? ResidentData { get; set; }
    public List<DataRun>? DataRuns { get; set; }
    public byte[]? PrefixData { get; set; }
    public byte[]? SuffixData { get; set; }
    public List<SourceExtent>? SourceExtents { get; set; }
    public RecoveryTransformKind TransformKind { get; set; }
    public long SourceUnreadableBytes { get; set; } = -1;
    public int RecoveryConfidenceScore { get; set; } = -1;
    public string? RecoveryConfidenceGrade { get; set; }
    public string? RecoveryConfidenceSummary { get; set; }
    public DateTimeOffset? FileSystemCreatedAt { get; set; }
    public DateTimeOffset? FileSystemModifiedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public string? DeletionDateSource { get; set; }
    public long NtfsRecordIndex { get; set; } = -1;
    public ulong NtfsFileReference { get; set; }
    public ulong NtfsParentReference { get; set; }
    public string? RecoveredOriginalPath { get; set; }
    public string? NtfsForensicEvidence { get; set; }
    public DateTimeOffset? CapturedAt { get; set; }
    public string? CaptureDateSource { get; set; }
    public string? RepairedFilePath { get; set; }
    public string? RepairMessage { get; set; }
    public int? VideoHealthScore { get; set; }
    public string? VideoHealthGrade { get; set; }
    public string? VideoHealthSummary { get; set; }
    public bool IsChecked { get; set; }

    public static RecoveryProjectFileState FromItem(RecoveryFileItem item) => new()
    {
        FileName = item.FileName,
        Extension = item.Extension,
        SizeBytes = item.SizeBytes,
        RecoveryState = item.RecoveryState,
        TypeGlyph = item.TypeGlyph,
        SourceText = item.SourceText,
        SourceKind = item.SourceKind,
        IsExistingFile = item.IsExistingFile,
        SourceOffset = item.SourceOffset,
        ClusterSize = item.ClusterSize,
        ResidentData = item.ResidentData,
        DataRuns = item.DataRuns?.ToList(),
        PrefixData = item.PrefixData,
        SuffixData = item.SuffixData,
        SourceExtents = item.SourceExtents?.ToList(),
        TransformKind = item.TransformKind,
        SourceUnreadableBytes = item.SourceUnreadableBytes,
        RecoveryConfidenceScore = item.RecoveryConfidenceScore,
        RecoveryConfidenceGrade = item.RecoveryConfidenceGrade,
        RecoveryConfidenceSummary = item.RecoveryConfidenceSummary,
        FileSystemCreatedAt = item.FileSystemCreatedAt,
        FileSystemModifiedAt = item.FileSystemModifiedAt,
        DeletedAt = item.DeletedAt,
        DeletionDateSource = item.DeletionDateSource,
        NtfsRecordIndex = item.NtfsRecordIndex,
        NtfsFileReference = item.NtfsFileReference,
        NtfsParentReference = item.NtfsParentReference,
        RecoveredOriginalPath = item.RecoveredOriginalPath,
        NtfsForensicEvidence = item.NtfsForensicEvidence,
        CapturedAt = item.CapturedAt,
        CaptureDateSource = item.CaptureDateSource,
        RepairedFilePath = item.RepairedFilePath,
        RepairMessage = item.RepairMessage,
        VideoHealthScore = item.VideoHealthScore,
        VideoHealthGrade = item.VideoHealthGrade,
        VideoHealthSummary = item.VideoHealthSummary,
        IsChecked = item.IsChecked
    };

    public RecoveryFileItem ToItem()
    {
        var item = new RecoveryFileItem
        {
            FileName = FileName,
            Extension = Extension,
            SizeBytes = SizeBytes,
            RecoveryState = RecoveryState,
            TypeGlyph = string.IsNullOrWhiteSpace(TypeGlyph) ? FileTypeHelper.GetGlyph(Extension) : TypeGlyph,
            SourceText = SourceText,
            SourceKind = SourceKind,
            IsExistingFile = IsExistingFile,
            SourceOffset = SourceOffset,
            ClusterSize = ClusterSize,
            ResidentData = ResidentData,
            DataRuns = DataRuns,
            PrefixData = PrefixData,
            SuffixData = SuffixData,
            SourceExtents = SourceExtents,
            TransformKind = TransformKind,
            SourceUnreadableBytes = SourceUnreadableBytes,
            RecoveryConfidenceScore = RecoveryConfidenceScore,
            RecoveryConfidenceGrade = RecoveryConfidenceGrade,
            RecoveryConfidenceSummary = RecoveryConfidenceSummary,
            FileSystemCreatedAt = FileSystemCreatedAt,
            FileSystemModifiedAt = FileSystemModifiedAt,
            DeletedAt = DeletedAt,
            DeletionDateSource = DeletionDateSource,
            NtfsRecordIndex = NtfsRecordIndex,
            NtfsFileReference = NtfsFileReference,
            NtfsParentReference = NtfsParentReference,
            RecoveredOriginalPath = RecoveredOriginalPath,
            NtfsForensicEvidence = NtfsForensicEvidence,
            CapturedAt = CapturedAt,
            CaptureDateSource = CaptureDateSource,
            IsChecked = IsChecked
        };

        if (!string.IsNullOrWhiteSpace(RepairedFilePath) && File.Exists(RepairedFilePath))
            item.RepairedFilePath = RepairedFilePath;
        item.RepairMessage = RepairMessage;
        item.VideoHealthScore = VideoHealthScore;
        item.VideoHealthGrade = VideoHealthGrade;
        item.VideoHealthSummary = VideoHealthSummary;
        return item;
    }
}
