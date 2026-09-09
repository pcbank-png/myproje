using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

internal sealed class RecoveryItemReadStream : Stream
{
    private readonly RecoveryFileItem _item;
    private readonly RawDeviceReader? _reader;
    private readonly byte[] _prefix;
    private readonly byte[] _suffix;
    private readonly long _contentLength;
    private readonly long _length;
    private readonly long _maximumBytesRead;
    private readonly object _sync = new();
    private long _position;
    private long _bytesRead;
    private bool _disposed;

    public RecoveryItemReadStream(
        StorageDeviceInfo device,
        RecoveryFileItem item,
        long maximumBytesRead = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(item);
        if (maximumBytesRead <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytesRead));

        if (item.TransformKind != RecoveryTransformKind.None)
            throw new NotSupportedException("Dönüştürülmüş video akışı doğrudan seek edilebilir stream olarak açılamaz.");

        _item = item;
        _prefix = item.PrefixData is { Length: > 0 }
            ? item.PrefixData
            : Array.Empty<byte>();
        _suffix = item.SuffixData is { Length: > 0 }
            ? item.SuffixData
            : Array.Empty<byte>();
        _contentLength = Math.Max(0, item.SizeBytes);
        _length = checked(_prefix.LongLength + _contentLength + _suffix.LongLength);
        _maximumBytesRead = maximumBytesRead;

        if (item.SourceKind != RecoverySourceKind.NtfsResident)
            _reader = RawDeviceReader.OpenDevice(device, mediaProfile: RecoveryMediaProfileService.Create(device));
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            lock (_sync)
                return _position;
        }
        set
        {
            ThrowIfDisposed();
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            lock (_sync)
                _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException();
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (buffer.Length == 0)
            return 0;

        lock (_sync)
        {
            if (_position >= _length)
                return 0;

            long remainingBudget = _maximumBytesRead - _bytesRead;
            if (remainingBudget <= 0)
                return 0;
            if (buffer.Length > remainingBudget)
                buffer = buffer[..checked((int)remainingBudget)];

            int total = 0;

            if (_position < _prefix.LongLength)
            {
                int prefixOffset = checked((int)_position);
                int prefixCount = Math.Min(buffer.Length, _prefix.Length - prefixOffset);
                _prefix.AsSpan(prefixOffset, prefixCount).CopyTo(buffer);
                _position += prefixCount;
                total += prefixCount;

                if (total == buffer.Length || _position >= _length)
                {
                    _bytesRead += total;
                    return total;
                }
            }

            long logicalAfterPrefix = _position - _prefix.LongLength;
            if (logicalAfterPrefix < _contentLength && total < buffer.Length)
            {
                long contentPosition = Math.Max(0, logicalAfterPrefix);
                int requested = (int)Math.Min(buffer.Length - total, _contentLength - contentPosition);
                if (requested > 0)
                {
                    int read = ReadContent(contentPosition, buffer.Slice(total, requested));
                    if (read > 0)
                    {
                        _position += read;
                        total += read;
                    }
                }
            }

            if (total < buffer.Length && _position >= _prefix.LongLength + _contentLength && _suffix.Length > 0)
            {
                long suffixPosition = _position - _prefix.LongLength - _contentLength;
                if (suffixPosition >= 0 && suffixPosition < _suffix.LongLength)
                {
                    int suffixOffset = checked((int)suffixPosition);
                    int suffixCount = Math.Min(buffer.Length - total, _suffix.Length - suffixOffset);
                    _suffix.AsSpan(suffixOffset, suffixCount).CopyTo(buffer.Slice(total));
                    _position += suffixCount;
                    total += suffixCount;
                }
            }

            _bytesRead += total;
            return total;
        }
    }

    private int ReadContent(long contentPosition, Span<byte> destination)
    {
        if (contentPosition < 0 || contentPosition >= _contentLength || destination.Length == 0)
            return 0;

        int target = (int)Math.Min(destination.Length, _contentLength - contentPosition);
        Span<byte> output = destination.Slice(0, target);

        return _item.SourceKind switch
        {
            RecoverySourceKind.NtfsResident => ReadResident(contentPosition, output),
            RecoverySourceKind.RawContiguous or RecoverySourceKind.FatContiguous or RecoverySourceKind.ExFatContiguous
                => ReadPhysical(_item.SourceOffset + contentPosition, output),
            RecoverySourceKind.Extents => ReadExtents(contentPosition, output),
            RecoverySourceKind.NtfsRunList => ReadRuns(contentPosition, output),
            _ => 0
        };
    }

    private int ReadResident(long contentPosition, Span<byte> destination)
    {
        if (_item.ResidentData is not { Length: > 0 } data || contentPosition >= data.LongLength)
            return 0;

        int count = (int)Math.Min(destination.Length, data.LongLength - contentPosition);
        data.AsSpan(checked((int)contentPosition), count).CopyTo(destination);
        return count;
    }

    private int ReadPhysical(long physicalOffset, Span<byte> destination)
    {
        if (_reader is null || destination.Length == 0)
            return 0;

        destination.Clear();
        return _reader.ReadBestEffort(physicalOffset, destination, out _);
    }

    private int ReadExtents(long contentPosition, Span<byte> destination)
    {
        if (_reader is null || _item.SourceExtents is not { Count: > 0 } extents)
            return 0;

        int total = 0;
        long logicalStart = 0;

        foreach (SourceExtent extent in extents)
        {
            if (total >= destination.Length)
                break;

            long extentLength = Math.Max(0, extent.Length);
            long logicalEnd = checked(logicalStart + extentLength);
            if (contentPosition >= logicalEnd)
            {
                logicalStart = logicalEnd;
                continue;
            }

            long withinExtent = Math.Max(0, contentPosition - logicalStart);
            long available = extentLength - withinExtent;
            if (available <= 0)
            {
                logicalStart = logicalEnd;
                continue;
            }

            int count = (int)Math.Min(destination.Length - total, available);
            int read = ReadPhysical(extent.Offset + withinExtent, destination.Slice(total, count));
            if (read <= 0)
                break;

            total += read;
            contentPosition += read;
            logicalStart = logicalEnd;
        }

        return total;
    }

    private int ReadRuns(long contentPosition, Span<byte> destination)
    {
        if (_reader is null || _item.DataRuns is not { Count: > 0 } runs || _item.ClusterSize <= 0)
            return 0;

        int total = 0;
        long logicalStart = 0;

        foreach (DataRun run in runs)
        {
            if (total >= destination.Length)
                break;

            long runLength;
            try
            {
                runLength = checked(run.ClusterCount * (long)_item.ClusterSize);
            }
            catch (OverflowException)
            {
                break;
            }

            if (runLength <= 0)
                continue;

            long logicalEnd = checked(logicalStart + runLength);
            if (contentPosition >= logicalEnd)
            {
                logicalStart = logicalEnd;
                continue;
            }

            long withinRun = Math.Max(0, contentPosition - logicalStart);
            long available = runLength - withinRun;
            int count = (int)Math.Min(destination.Length - total, available);
            Span<byte> target = destination.Slice(total, count);

            if (run.IsSparse)
            {
                target.Clear();
            }
            else
            {
                long physicalOffset = checked(run.LogicalClusterNumber * (long)_item.ClusterSize + withinRun);
                ReadPhysical(physicalOffset, target);
            }

            total += count;
            contentPosition += count;
            logicalStart = logicalEnd;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();

        lock (_sync)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(_position + offset),
                SeekOrigin.End => checked(_length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            if (target < 0)
                throw new IOException("Video stream konumu sıfırın altına taşınamaz.");

            _position = target;
            return _position;
        }
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
                _reader?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
