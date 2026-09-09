namespace NSXVeriKurtarmaPro.Services;

/// <summary>
/// Hızlı Tarama Servisi
/// - Çoklu disk türünü destekler (HDD, SSD, USB, SD Card, External)
/// - Paralel okuma işlemleri
/// - Bellek-verimli buffer yönetimi
/// - Güvenli hata kurtarma
/// </summary>
public sealed class FastScanService : IDisposable
{
    private const int BufferSize = 4 * 1024 * 1024; // 4 MB buffer
    private const int ParallelTaskCount = 4; // Paralel görev sayısı
    private readonly CancellationTokenSource _cancellationTokenSource;
    private bool _disposed;

    public FastScanService()
    {
        _cancellationTokenSource = new CancellationTokenSource();
    }

    public event EventHandler<ScanProgressEventArgs>? ProgressChanged;
    public event EventHandler<ScanCompletedEventArgs>? ScanCompleted;

    public async Task<ScanResultInfo> ScanDeviceAsync(
        string devicePath,
        Action<int>? onProgressUpdate = null,
        CancellationToken? externalCancellationToken = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(devicePath);

        var result = new ScanResultInfo
        {
            DevicePath = devicePath,
            StartTime = DateTime.UtcNow
        };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cancellationTokenSource.Token,
            externalCancellationToken ?? CancellationToken.None);

        try
        {
            result.IsScanSuccessful = await PerformScanAsync(
                devicePath,
                result,
                onProgressUpdate,
                linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            result.IsScanSuccessful = false;
            result.ErrorMessage = "Tarama iptal edildi";
        }
        catch (Exception ex)
        {
            result.IsScanSuccessful = false;
            result.ErrorMessage = ex.Message;
            AppLog.Error($"Tarama hatası: {devicePath}", ex);
        }
        finally
        {
            result.EndTime = DateTime.UtcNow;
        }

        return result;
    }

    private async Task<bool> PerformScanAsync(
        string devicePath,
        ScanResultInfo result,
        Action<int>? onProgressUpdate,
        CancellationToken cancellationToken)
    {
        try
        {
            using var fileStream = new FileStream(
                devicePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                BufferSize,
                FileOptions.SequentialScan | FileOptions.ReadAhead);

            long totalBytes = fileStream.Length;
            long bytesRead = 0;
            byte[] buffer = new byte[BufferSize];

            while (bytesRead < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int bytesToRead = (int)Math.Min(BufferSize, totalBytes - bytesRead);
                int readCount = await fileStream.ReadAsync(buffer, 0, bytesToRead, cancellationToken);

                if (readCount <= 0)
                    break;

                bytesRead += readCount;
                result.BytesScanned += readCount;

                int progressPercent = (int)((bytesRead * 100) / totalBytes);
                onProgressUpdate?.Invoke(progressPercent);

                ProgressChanged?.Invoke(this, new ScanProgressEventArgs
                {
                    DevicePath = devicePath,
                    BytesScanned = bytesRead,
                    TotalBytes = totalBytes,
                    ProgressPercent = progressPercent
                });
            }

            result.IsScanSuccessful = bytesRead == totalBytes;
            return result.IsScanSuccessful;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Tarama gerçekleştirilemedi: {devicePath}", ex);
            return false;
        }
    }

    public void CancelScan()
    {
        _cancellationTokenSource?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(GetType().Name);
    }
}

public class ScanProgressEventArgs : EventArgs
{
    public required string DevicePath { get; init; }
    public required long BytesScanned { get; init; }
    public required long TotalBytes { get; init; }
    public required int ProgressPercent { get; init; }
}

public class ScanCompletedEventArgs : EventArgs
{
    public required string DevicePath { get; init; }
    public required bool IsSuccessful { get; init; }
    public required long BytesScanned { get; init; }
    public required TimeSpan ElapsedTime { get; init; }
}

public class ScanResultInfo
{
    public string DevicePath { get; init; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long BytesScanned { get; set; }
    public bool IsScanSuccessful { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;

    public TimeSpan ElapsedTime => EndTime - StartTime;
    public double ScanSpeedMbps => ElapsedTime.TotalSeconds > 0
        ? (BytesScanned / (1024.0 * 1024.0)) / ElapsedTime.TotalSeconds
        : 0;
}
