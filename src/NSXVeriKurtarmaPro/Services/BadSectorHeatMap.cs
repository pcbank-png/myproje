using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record BadSectorRange(long Offset, long Length, int FailureEvents);
public sealed record BadSectorRetryResult(int AttemptedSectors, int RecoveredSectors, long RecoveredBytes);

/// <summary>
/// Salt-okunur I/O sırasında gerçek okunamayan sektörleri tekilleştirir. Aynı kötü
/// sektörü parser/preview tekrar tekrar istediğinde Safe Scan fiziksel diski gereksiz
/// yere dövmek yerine bilinen aralığı sıfır doldurarak devam eder.
/// </summary>
public sealed class BadSectorHeatMap
{
    private readonly object _sync = new();
    private readonly List<BadSectorRange> _ranges = new();
    private readonly int _sectorSize;

    public BadSectorHeatMap(int sectorSize) => _sectorSize = Math.Max(512, sectorSize);

    public long FailureEvents { get; private set; }
    public long RecoveredBytes { get; private set; }
    public long SkippedKnownBadReads { get; private set; }

    public long UnreadableBytes
    {
        get { lock (_sync) return _ranges.Sum(range => range.Length); }
    }

    public int RangeCount
    {
        get { lock (_sync) return _ranges.Count; }
    }

    public bool SafeModeRecommended => FailureEvents >= 4 || UnreadableBytes >= _sectorSize * 16L;

    public void RecordFailure(long offset, long length)
    {
        if (offset < 0 || length <= 0)
            return;

        long start = offset - offset % _sectorSize;
        long end = RoundUp(checked(offset + length), _sectorSize);
        lock (_sync)
        {
            FailureEvents++;
            InsertOrMerge(start, end - start);
        }
    }

    public bool IsKnownBad(long offset, long length)
    {
        if (offset < 0 || length <= 0)
            return false;
        long end = checked(offset + length);
        lock (_sync)
        {
            foreach (BadSectorRange range in _ranges)
            {
                if (range.Offset >= end)
                    break;
                if (range.Offset + range.Length > offset)
                    return true;
            }
        }
        return false;
    }

    public void RecordSkippedKnownBadRead() => SkippedKnownBadReads++;

    public void MarkRecovered(long offset, long length)
    {
        if (offset < 0 || length <= 0)
            return;

        long recoverStart = offset - offset % _sectorSize;
        long recoverEnd = RoundUp(checked(offset + length), _sectorSize);
        lock (_sync)
        {
            for (int i = _ranges.Count - 1; i >= 0; i--)
            {
                BadSectorRange range = _ranges[i];
                long rangeEnd = range.Offset + range.Length;
                if (rangeEnd <= recoverStart || range.Offset >= recoverEnd)
                    continue;

                long overlapStart = Math.Max(range.Offset, recoverStart);
                long overlapEnd = Math.Min(rangeEnd, recoverEnd);
                if (overlapEnd <= overlapStart)
                    continue;

                _ranges.RemoveAt(i);
                if (range.Offset < overlapStart)
                    _ranges.Insert(i, range with { Length = overlapStart - range.Offset });
                if (overlapEnd < rangeEnd)
                    _ranges.Insert(Math.Min(i + 1, _ranges.Count),
                        new BadSectorRange(overlapEnd, rangeEnd - overlapEnd, range.FailureEvents));
                RecoveredBytes += overlapEnd - overlapStart;
            }
            _ranges.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }
    }

    public IReadOnlyList<BadSectorRange> Snapshot(int maxRanges = 4096)
    {
        lock (_sync)
            return _ranges.Take(Math.Max(0, maxRanges)).ToArray();
    }

    public IReadOnlyList<RecoveryBadSectorSnapshot> SnapshotState(int maxRanges = 4096)
    {
        lock (_sync)
        {
            return _ranges
                .Take(Math.Max(0, maxRanges))
                .Select(range => new RecoveryBadSectorSnapshot
                {
                    Offset = range.Offset,
                    Length = range.Length,
                    FailureEvents = range.FailureEvents
                })
                .ToArray();
        }
    }

    public void RestoreState(RecoveryScanCheckpoint? checkpoint)
    {
        if (checkpoint is null)
            return;

        List<RecoveryBadSectorSnapshot> source = checkpoint.BadSectors ?? [];
        lock (_sync)
        {
            _ranges.Clear();
            foreach (RecoveryBadSectorSnapshot item in source
                         .Where(item => item.Offset >= 0 && item.Length > 0 && item.Offset <= long.MaxValue - item.Length)
                         .OrderBy(item => item.Offset))
            {
                long start = item.Offset - item.Offset % _sectorSize;
                long end = RoundUp(item.Offset + item.Length, _sectorSize);
                if (_ranges.Count > 0)
                {
                    BadSectorRange previous = _ranges[^1];
                    long previousEnd = previous.Offset + previous.Length;
                    if (start <= previousEnd)
                    {
                        _ranges[^1] = new BadSectorRange(
                            previous.Offset,
                            Math.Max(previousEnd, end) - previous.Offset,
                            previous.FailureEvents + Math.Max(1, item.FailureEvents));
                        continue;
                    }
                }

                _ranges.Add(new BadSectorRange(start, end - start, Math.Max(1, item.FailureEvents)));
            }

            long persistedFailures = Math.Max(0, checkpoint.BadSectorFailureEvents);
            long rangeFailures = _ranges.Sum(range => (long)Math.Max(1, range.FailureEvents));
            FailureEvents = Math.Max(persistedFailures, rangeFailures);
            RecoveredBytes = Math.Max(0, checkpoint.BadSectorRecoveredBytes);
            SkippedKnownBadReads = Math.Max(0, checkpoint.BadSectorSkippedKnownBadReads);
        }
    }

    public string DiagnosticText
    {
        get
        {
            long unreadable = UnreadableBytes;
            return unreadable <= 0
                ? "Bad-sector heat map temiz"
                : $"Bad-sector heat map • {RangeCount:N0} bolge • {RecoveryFileItem.FormatBytes(unreadable)}" +
                  (SafeModeRecommended ? " • Safe Scan aktif" : string.Empty) +
                  (SkippedKnownBadReads > 0 ? $" • tekrar atlanan {SkippedKnownBadReads:N0}" : string.Empty) +
                  (RecoveredBytes > 0 ? $" • geri okunan {RecoveryFileItem.FormatBytes(RecoveredBytes)}" : string.Empty);
        }
    }

    private void InsertOrMerge(long offset, long length)
    {
        long start = offset;
        long end = offset + length;
        int failures = 1;
        int insertAt = 0;

        while (insertAt < _ranges.Count && _ranges[insertAt].Offset + _ranges[insertAt].Length < start)
            insertAt++;

        while (insertAt < _ranges.Count && _ranges[insertAt].Offset <= end)
        {
            BadSectorRange existing = _ranges[insertAt];
            start = Math.Min(start, existing.Offset);
            end = Math.Max(end, existing.Offset + existing.Length);
            failures += existing.FailureEvents;
            _ranges.RemoveAt(insertAt);
        }

        _ranges.Insert(insertAt, new BadSectorRange(start, end - start, failures));
    }

    private long RoundUp(long value, int alignment)
    {
        long remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }
}
