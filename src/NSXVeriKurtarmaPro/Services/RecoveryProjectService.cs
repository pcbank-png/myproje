using System.IO.Compression;
using System.Text.Json;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed class RecoveryProjectService
{
    public const string ProjectExtension = ".nsx";
    public const int CurrentVersion = 3;
    private const long MaxCompressedProjectBytes = 512L * 1024 * 1024;
    private const int MaxProjectFiles = 500000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public RecoveryProjectState? Load(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            return null;

        if (!string.Equals(Path.GetExtension(projectPath), ProjectExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var info = new FileInfo(projectPath);
            if (info.Length <= 0 || info.Length > MaxCompressedProjectBytes)
                return null;

            using FileStream file = new(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using GZipStream gzip = new(file, CompressionMode.Decompress, leaveOpen: false);
            RecoveryProjectState? state = JsonSerializer.Deserialize<RecoveryProjectState>(gzip, JsonOptions);
            if (!ValidateAndUpgrade(state))
                return null;
            return state;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string projectPath, RecoveryProjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));

        projectPath = EnsureProjectExtension(projectPath);
        string? directory = Path.GetDirectoryName(projectPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        state.Version = CurrentVersion;
        if (string.IsNullOrWhiteSpace(state.ProjectId))
            state.ProjectId = Guid.NewGuid().ToString("N");
        state.SavedAtUtc = DateTime.UtcNow;

        string tempPath = projectPath + ".tmp";
        try
        {
            using (FileStream file = new(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       128 * 1024,
                       FileOptions.SequentialScan))
            {
                using (var gzip = new GZipStream(file, CompressionLevel.Fastest, leaveOpen: true))
                    JsonSerializer.Serialize(gzip, state, JsonOptions);

                file.Flush(flushToDisk: true);
            }

            File.Move(tempPath, projectPath, true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static string EnsureProjectExtension(string path)
    {
        if (string.Equals(Path.GetExtension(path), ProjectExtension, StringComparison.OrdinalIgnoreCase))
            return path;

        return Path.ChangeExtension(path, ProjectExtension);
    }

    private static bool ValidateAndUpgrade(RecoveryProjectState? state)
    {
        if (state is null || state.Version < 1 || state.Version > CurrentVersion)
            return false;

        state.Files ??= [];
        if (state.Files.Count > MaxProjectFiles)
            return false;
        if (state.DeviceTotalBytes < 0 || state.ResumePosition < 0 || state.ResumeTotal < 0)
            return false;
        if (state.DevicePartitionOffsetBytes < 0 || state.DevicePartitionLengthBytes < 0)
            return false;
        if (state.DeviceIsPartitionSource && state.DevicePartitionLengthBytes <= 0)
            return false;

        state.ProjectId = string.IsNullOrWhiteSpace(state.ProjectId)
            ? Guid.NewGuid().ToString("N")
            : state.ProjectId;
        state.ExpandedNavigationGroups ??= [];

        if (state.Version == 1)
        {
            state.ScanCheckpoint ??= new RecoveryScanCheckpoint
            {
                PassNumber = state.ScanMode == ScanMode.Deep && state.ResumePosition > 0 ? 2 : 1,
                Stage = state.ScanMode == ScanMode.Deep && state.ResumePosition > 0 ? "deep-raw" : "legacy-v1",
                ResumePosition = Math.Max(0, state.ResumePosition),
                ResumeTotal = Math.Max(0, state.ResumeTotal),
                MetadataStageCompleted = state.ScanMode == ScanMode.Deep && state.ResumePosition > 0,
                RawScanCompleted = state.ScanMode == ScanMode.Deep && state.ResumeTotal > 0 && state.ResumePosition >= state.ResumeTotal,
                ScannedRanges = state.ScanMode == ScanMode.Deep && state.ResumePosition > 0
                    ? [new RecoveryScannedRange { Offset = 0, Length = state.ResumePosition, Kind = "RAW" }]
                    : []
            };
            state.Version = 2;
        }

        if (state.Version == 2)
        {
            UpgradeQuickResumeCheckpoint(state);
            state.Version = CurrentVersion;
        }

        NormalizeResumeFlags(state);

        if (state.ScanCheckpoint is not null)
        {
            state.ScanCheckpoint.BadSectors ??= [];
            state.ScanCheckpoint.ScannedRanges ??= [];
            state.ScanCheckpoint.PendingFragmentNodes ??= [];
            state.ScanCheckpoint.PendingFragmentNodes = state.ScanCheckpoint.PendingFragmentNodes
                .Where(item => item is not null && item.SourceOffset >= 0 && item.SizeBytes >= 4096)
                .GroupBy(item => (item.SourceOffset, FileTypeHelper.Normalize(item.Extension), item.TransformKind))
                .Select(group => group.First())
                .OrderBy(item => item.SourceOffset)
                .ToList();
            // A persisted .nsx project always contains a complete anchor snapshot. Delta
            // checkpoints exist only transiently between the scan worker and the UI.
            state.ScanCheckpoint.PendingFragmentNodesAreDelta = false;
            state.ScanCheckpoint.ResumePosition = Math.Max(0, state.ScanCheckpoint.ResumePosition);
            state.ScanCheckpoint.ResumeTotal = Math.Max(0, state.ScanCheckpoint.ResumeTotal);
        }

        return true;
    }


    private static void UpgradeQuickResumeCheckpoint(RecoveryProjectState state)
    {
        if (state.ScanMode != ScanMode.Quick || state.IsFolderScan)
            return;

        bool incompleteByPosition = state.ResumeTotal > 0 && state.ResumePosition < state.ResumeTotal;
        // V2 could falsely mark an interrupted Quick Surface session as %100/completed
        // after reload because its byte cursor was fed back as an MFT record index and the
        // surface phase then exited on the restored-result threshold. Position < total is
        // authoritative for this legacy format, even when its UI flags were overwritten.
        bool interrupted = state.ResumeScan || incompleteByPosition;
        if (!interrupted)
            return;

        state.ResumeScan = true;
        state.ScanCompleted = false;

        bool hasUsefulCheckpoint = state.ScanCheckpoint is not null &&
                                   !string.IsNullOrWhiteSpace(state.ScanCheckpoint.Stage) &&
                                   !state.ScanCheckpoint.Stage.StartsWith("legacy", StringComparison.OrdinalIgnoreCase);
        if (hasUsefulCheckpoint)
            return;

        bool byteBasedSurface = LooksLikeLegacyQuickSurfaceProgress(state);
        state.ScanCheckpoint = new RecoveryScanCheckpoint
        {
            PassNumber = 1,
            Stage = byteBasedSurface ? "quick-surface-auto" : "quick-metadata",
            ResumePosition = Math.Max(0, state.ResumePosition),
            ResumeTotal = Math.Max(0, state.ResumeTotal),
            MetadataStageCompleted = byteBasedSurface,
            RawScanCompleted = false,
            ScannedRanges = []
        };
    }

    private static bool LooksLikeLegacyQuickSurfaceProgress(RecoveryProjectState state)
    {
        if (!string.Equals(state.DeviceFileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) ||
            state.ResumeTotal <= 0 ||
            state.DeviceTotalBytes <= 0)
        {
            return false;
        }

        // V2 did not persist the Quick Scan phase. MFT progress is a record count, while
        // Quick Surface/Free-Space progress is a byte count. A byte total large enough to
        // represent a meaningful fraction of the source is therefore a safe legacy hint.
        // This is used only to upgrade old projects; V3 stores the phase explicitly.
        long byteResumeThreshold = Math.Max(32L * 1024 * 1024, state.DeviceTotalBytes / 4096);
        return state.ResumeTotal >= byteResumeThreshold;
    }

    private static void NormalizeResumeFlags(RecoveryProjectState state)
    {
        state.ProgressPercent = Math.Clamp(state.ProgressPercent, 0d, 100d);

        bool checkpointClearlyIncomplete = state.ScanMode == ScanMode.Quick &&
                                           !state.IsFolderScan &&
                                           state.ScanCheckpoint is not null &&
                                           state.ScanCheckpoint.ResumeTotal > 0 &&
                                           state.ScanCheckpoint.ResumePosition < state.ScanCheckpoint.ResumeTotal &&
                                           !state.ScanCheckpoint.RawScanCompleted;
        if (state.ScanCompleted && checkpointClearlyIncomplete)
        {
            state.ScanCompleted = false;
            state.ResumeScan = true;
            return;
        }

        if (state.ScanCompleted)
        {
            state.ResumeScan = false;
            return;
        }

        if (state.ResumeScan)
            return;

        // Interrupted projects written by older builds can contain a stale ResumeScan=false
        // even though a valid checkpoint clearly points inside the source. Recover that intent
        // rather than presenting an unfinished session as a completed 100% scan.
        if (state.ResumeTotal > 0 && state.ResumePosition >= 0 && state.ResumePosition < state.ResumeTotal)
            state.ResumeScan = true;
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
}
