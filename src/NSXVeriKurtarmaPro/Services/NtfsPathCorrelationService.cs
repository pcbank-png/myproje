using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Correlates RAW/quick-carved candidates with surviving NTFS $MFT data runs.
/// This is intentionally metadata-only: building the index performs no source-disk I/O.
/// If a carved header lands inside a surviving file run, the recovered original path can
/// be attached to the carved result so the YOL tree mirrors the original folder layout.
/// </summary>
internal sealed class NtfsPathCorrelationService
{
    private readonly record struct PathExtent(
        long Start,
        long End,
        long LogicalFileSize,
        string Extension,
        string OriginalPath,
        long RecordIndex);

    private readonly PathExtent[] _extents;
    private readonly long[] _prefixMaxEnd;

    private NtfsPathCorrelationService(PathExtent[] extents)
    {
        _extents = extents;
        _prefixMaxEnd = new long[extents.Length];

        long maxEnd = 0;
        for (int index = 0; index < extents.Length; index++)
        {
            maxEnd = Math.Max(maxEnd, extents[index].End);
            _prefixMaxEnd[index] = maxEnd;
        }
    }

    public bool HasEntries => _extents.Length > 0;

    public static NtfsPathCorrelationService Build(IEnumerable<RecoveryFileItem>? items)
    {
        if (items is null)
            return new NtfsPathCorrelationService([]);

        var extents = new List<PathExtent>();
        foreach (RecoveryFileItem item in items)
        {
            if (item.SourceKind != RecoverySourceKind.NtfsRunList ||
                item.ClusterSize <= 0 ||
                item.DataRuns is not { Count: > 0 } ||
                string.IsNullOrWhiteSpace(item.RecoveredOriginalPath))
            {
                continue;
            }

            string extension = FileTypeHelper.Normalize(item.Extension);
            IReadOnlyList<DataRun> runs = item.DataRuns;
            foreach (DataRun run in runs)
            {
                if (run.IsSparse || run.ClusterCount <= 0 || run.LogicalClusterNumber <= 0)
                    continue;

                try
                {
                    long start = checked(run.LogicalClusterNumber * (long)item.ClusterSize);
                    long length = checked(run.ClusterCount * (long)item.ClusterSize);
                    if (start < 0 || length <= 0)
                        continue;

                    long end = start > long.MaxValue - length ? long.MaxValue : start + length;
                    extents.Add(new PathExtent(
                        start,
                        end,
                        item.SizeBytes,
                        extension,
                        item.RecoveredOriginalPath!,
                        item.NtfsRecordIndex));
                }
                catch (OverflowException)
                {
                    // Corrupt/stale run metadata must never break the scan.
                }
            }
        }

        PathExtent[] ordered = extents
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();
        return new NtfsPathCorrelationService(ordered);
    }

    public bool TryApply(RecoveryFileItem item)
    {
        if (!HasEntries ||
            item.SourceKind != RecoverySourceKind.RawContiguous ||
            item.SourceOffset < 0 ||
            !string.IsNullOrWhiteSpace(item.RecoveredOriginalPath))
        {
            return false;
        }

        string candidateExtension = FileTypeHelper.Normalize(item.Extension);
        if (string.IsNullOrWhiteSpace(candidateExtension))
            return false;

        int index = FindLastStartAtOrBefore(item.SourceOffset);
        if (index < 0)
            return false;

        PathExtent? best = null;
        int bestScore = int.MinValue;
        int inspected = 0;

        // Prefix max-end lets us stop once no earlier interval can still contain the offset.
        for (int cursor = index; cursor >= 0; cursor--)
        {
            if (_prefixMaxEnd[cursor] <= item.SourceOffset)
                break;
            if (inspected++ >= 96)
                break;

            PathExtent extent = _extents[cursor];
            if (item.SourceOffset < extent.Start || item.SourceOffset >= extent.End)
                continue;
            if (!ExtensionsCompatible(candidateExtension, extent.Extension))
                continue;

            int score = 0;
            long delta = item.SourceOffset - extent.Start;
            if (delta == 0)
                score += 12;
            else if (delta <= 4096)
                score += 8;
            else if (delta <= 64 * 1024)
                score += 5;
            else
                score += 2;

            if (item.SizeBytes > 0 && extent.LogicalFileSize > 0)
            {
                long difference = Math.Abs(extent.LogicalFileSize - item.SizeBytes);
                if (difference <= 4096)
                    score += 8;
                else if (difference <= Math.Max(64 * 1024, extent.LogicalFileSize / 20))
                    score += 5;
                else if (item.SizeBytes <= extent.LogicalFileSize + 1024 * 1024)
                    score += 2;
            }

            // Exact JPG/JPEG family matches are intentionally treated equally.
            if (string.Equals(candidateExtension, extent.Extension, StringComparison.OrdinalIgnoreCase))
                score += 3;

            if (score > bestScore)
            {
                best = extent;
                bestScore = score;
            }
        }

        // Require more than a weak "somewhere inside the same run" coincidence.
        if (best is null || bestScore < 5)
            return false;

        item.RecoveredOriginalPath = best.Value.OriginalPath;
        if (!item.SourceText.Contains("MFT yol eşleşmesi", StringComparison.OrdinalIgnoreCase))
            item.SourceText = $"{item.SourceText} • MFT yol eşleşmesi";
        return true;
    }

    public int ApplyTo(IEnumerable<RecoveryFileItem>? items)
    {
        if (items is null || !HasEntries)
            return 0;

        int applied = 0;
        foreach (RecoveryFileItem item in items)
        {
            if (TryApply(item))
                applied++;
        }
        return applied;
    }

    private int FindLastStartAtOrBefore(long offset)
    {
        int low = 0;
        int high = _extents.Length - 1;
        int result = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (_extents[mid].Start <= offset)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }
        return result;
    }

    private static bool ExtensionsCompatible(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        bool leftJpeg = left is "JPG" or "JPEG" or "JPE" or "JFIF";
        bool rightJpeg = right is "JPG" or "JPEG" or "JPE" or "JFIF";
        if (leftJpeg && rightJpeg)
            return true;

        bool leftTiff = left is "TIF" or "TIFF";
        bool rightTiff = right is "TIF" or "TIFF";
        return leftTiff && rightTiff;
    }
}
