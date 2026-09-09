using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Correlates a RAW-carved file header with filesystem extents that already have a proven
/// original path. No path is guessed from a filename or file contents: a physical extent
/// overlap plus compatible type evidence is required. This is a zero-I/O post-processing
/// index and therefore does not change any recovery engine.
/// </summary>
internal sealed class RecoveredPathCorrelationService
{
    private readonly record struct PathExtent(
        long Start,
        long End,
        long LogicalFileSize,
        string Extension,
        string OriginalPath);

    private readonly PathExtent[] _extents;
    private readonly long[] _prefixMaxEnd;

    private RecoveredPathCorrelationService(PathExtent[] extents)
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

    public static RecoveredPathCorrelationService Build(IEnumerable<RecoveryFileItem>? items)
    {
        if (items is null)
            return new RecoveredPathCorrelationService([]);

        var extents = new List<PathExtent>();
        foreach (RecoveryFileItem item in items)
        {
            if (string.IsNullOrWhiteSpace(item.RecoveredOriginalPath) ||
                item.SourceKind == RecoverySourceKind.RawContiguous)
            {
                continue;
            }

            string extension = FileTypeHelper.Normalize(item.Extension);
            if (string.IsNullOrWhiteSpace(extension))
                continue;

            if (item.SourceExtents is { Count: > 0 })
            {
                // Only the first physical extent can contain the file's real header. Indexing
                // later fragments would let an unrelated embedded signature inherit the path.
                SourceExtent first = item.SourceExtents[0];
                TryAddExtent(extents, first.Offset, first.Length, item.SizeBytes, extension, item.RecoveredOriginalPath!);
                continue;
            }

            if (item.SourceKind is RecoverySourceKind.FatContiguous or RecoverySourceKind.ExFatContiguous)
            {
                TryAddExtent(extents, item.SourceOffset, item.SizeBytes, item.SizeBytes, extension, item.RecoveredOriginalPath!);
            }
        }

        PathExtent[] ordered = extents
            .OrderBy(item => item.Start)
            .ThenBy(item => item.End)
            .ToArray();
        return new RecoveredPathCorrelationService(ordered);
    }

    public bool TryApply(RecoveryFileItem item)
    {
        // Multi-fragment reconstructed files intentionally remain in "Yeniden İnşa" unless
        // their own filesystem metadata already carried a real path. Assigning one source
        // fragment's folder to a synthetic multi-file chain would be forensic guessing.
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
        for (int cursor = index; cursor >= 0; cursor--)
        {
            if (_prefixMaxEnd[cursor] <= item.SourceOffset)
                break;
            if (inspected++ >= 128)
                break;

            PathExtent extent = _extents[cursor];
            if (item.SourceOffset < extent.Start || item.SourceOffset >= extent.End)
                continue;
            if (!ExtensionsCompatible(candidateExtension, extent.Extension))
                continue;

            long delta = item.SourceOffset - extent.Start;
            int score = delta switch
            {
                0 => 18,
                <= 16 => 16,
                <= 4096 => 12,
                <= 64 * 1024 => 4,
                _ => 1
            };

            score += string.Equals(candidateExtension, extent.Extension, StringComparison.OrdinalIgnoreCase)
                ? 10
                : 6;

            if (item.SizeBytes > 0 && extent.LogicalFileSize > 0)
            {
                long difference = AbsoluteDifference(item.SizeBytes, extent.LogicalFileSize);
                if (difference <= 4096)
                    score += 8;
                else if (difference <= Math.Max(64 * 1024, extent.LogicalFileSize / 20))
                    score += 5;
                else if (item.SizeBytes <= extent.LogicalFileSize + 1024 * 1024)
                    score += 2;
            }

            if (score > bestScore)
            {
                best = extent;
                bestScore = score;
            }
        }

        // Exact/near file-start evidence is enough by itself; a deeper inside-file overlap
        // additionally needs size/type agreement. This avoids assigning arbitrary payload
        // signatures to a folder merely because they occur somewhere inside a large file.
        if (best is null || bestScore < 16)
            return false;

        item.RecoveredOriginalPath = best.Value.OriginalPath;
        if (!item.SourceText.Contains("özgün yol eşleşmesi", StringComparison.OrdinalIgnoreCase))
            item.SourceText = $"{item.SourceText} • özgün yol eşleşmesi";
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

    private static void TryAddExtent(
        ICollection<PathExtent> target,
        long start,
        long length,
        long logicalFileSize,
        string extension,
        string originalPath)
    {
        if (start < 0 || length <= 0 || string.IsNullOrWhiteSpace(originalPath))
            return;

        long end = start > long.MaxValue - length ? long.MaxValue : start + length;
        if (end <= start)
            return;

        target.Add(new PathExtent(start, end, Math.Max(0, logicalFileSize), extension, originalPath));
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

    private static long AbsoluteDifference(long left, long right)
    {
        if (left >= right)
            return left - right;
        return right - left;
    }

    private static bool ExtensionsCompatible(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsJpeg(left) && IsJpeg(right))
            return true;
        if (IsTiff(left) && IsTiff(right))
            return true;
        if (IsAvchdTransport(left) && IsAvchdTransport(right))
            return true;

        return false;
    }

    private static bool IsJpeg(string extension) => extension is "JPG" or "JPEG" or "JPE" or "JFIF";
    private static bool IsTiff(string extension) => extension is "TIF" or "TIFF";
    private static bool IsAvchdTransport(string extension) => extension is "MTS" or "M2TS" or "TS";
}
