using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LibVLCSharp.Shared;
using NSXVeriKurtarmaPro.Models;
using VlcLibVLC = LibVLCSharp.Shared.LibVLC;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace NSXVeriKurtarmaPro.Services;

internal sealed record VideoThumbnailResult(ImageSource Image, uint Width, uint Height);

internal sealed class VideoThumbnailService : IDisposable
{
    private const uint ThumbnailWidth = 320;
    private const uint ThumbnailHeight = 180;
    private const uint BytesPerPixel = 4;
    private const int CaptureTimeoutMilliseconds = 4200;
    private const int MinimumWarmupFrames = 2;
    private const int ForcedCaptureFrame = 12;
    private const int CacheLimit = 192;
    private const long MaximumPreviewReadBytes = 64L * 1024 * 1024;

    private static readonly uint Pitch = Align32(ThumbnailWidth * BytesPerPixel);
    private static readonly uint BufferLines = Align32(ThumbnailHeight);

    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly object _frameSync = new();
    private readonly object _cacheSync = new();
    private readonly Dictionary<string, VideoThumbnailResult> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly RecoveryService _recoveryService = new();

    private VlcLibVLC? _libVlc;
    private VlcMediaPlayer? _player;
    private IntPtr _frameBuffer;
    private TaskCompletionSource<byte[]>? _frameReady;
    private int _displayedFrames;
    private bool _disposed;

    public VideoThumbnailResult? BuildThumbnail(
        StorageDeviceInfo device,
        RecoveryFileItem item,
        CancellationToken cancellationToken)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || item.SizeBytes <= 0)
            return null;

        string cacheKey = BuildCacheKey(device, item);
        if (TryGetCached(cacheKey, out VideoThumbnailResult? cached))
            return cached;

        return BuildThumbnailAsync(device, item, cacheKey, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    private async Task<VideoThumbnailResult?> BuildThumbnailAsync(
        StorageDeviceInfo device,
        RecoveryFileItem item,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        bool entered = false;
        try
        {
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;

            if (_disposed || cancellationToken.IsCancellationRequested)
                return null;

            if (TryGetCached(cacheKey, out VideoThumbnailResult? cached))
                return cached;

            EnsureEngine();
            if (_player is null || _libVlc is null)
                return null;

            string? temporaryPath = null;
            Stream? sourceStream = null;
            StreamMediaInput? mediaInput = null;
            VlcMedia? media = null;

            try
            {
                ResetCaptureState();
                _player.Stop();

                if (item.IsRepaired && !string.IsNullOrWhiteSpace(item.RepairedFilePath))
                {
                    media = new VlcMedia(_libVlc, item.RepairedFilePath, FromType.FromPath);
                }
                else if (item.TransformKind == RecoveryTransformKind.None)
                {
                    sourceStream = new RecoveryItemReadStream(device, item, MaximumPreviewReadBytes);
                    mediaInput = new StreamMediaInput(sourceStream);
                    media = new VlcMedia(_libVlc, mediaInput);
                }
                else
                {
                    string previewDirectory = Path.Combine(
                        Path.GetTempPath(),
                        "NSXVeriKurtarmaPro",
                        "VideoThumbnailSource",
                        Environment.ProcessId.ToString());
                    temporaryPath = _recoveryService.CreateTemporaryVideoPreviewFile(
                        device,
                        item,
                        previewDirectory,
                        MaximumPreviewReadBytes,
                        cancellationToken);
                    if (string.IsNullOrWhiteSpace(temporaryPath))
                        return null;

                    media = new VlcMedia(_libVlc, temporaryPath, FromType.FromPath);
                }

                media.AddOption(":no-audio");
                media.AddOption(":no-sub-autodetect-file");
                media.AddOption(":file-caching=180");
                media.AddOption(":stop-time=10");

                Task<byte[]> frameTask;
                lock (_frameSync)
                {
                    _frameReady = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                    frameTask = _frameReady.Task;
                }

                if (!_player.Play(media))
                    return null;

                Task timeoutTask = Task.Delay(CaptureTimeoutMilliseconds, cancellationToken);
                Task completed = await Task.WhenAny(frameTask, timeoutTask).ConfigureAwait(false);
                if (!ReferenceEquals(completed, frameTask) || cancellationToken.IsCancellationRequested)
                    return null;

                byte[] pixels = await frameTask.ConfigureAwait(false);
                ImageSource image = CreateFrozenBitmap(pixels);
                _ = TryGetOriginalVideoSize(out uint width, out uint height);
                var result = new VideoThumbnailResult(image, width, height);
                AddCache(cacheKey, result);
                return result;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                lock (_frameSync)
                    _frameReady = null;

                try
                {
                    _player?.Stop();
                }
                catch
                {
                }

                media?.Dispose();
                mediaInput?.Dispose();
                sourceStream?.Dispose();
                TryDelete(temporaryPath);
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (entered)
                _captureGate.Release();
        }
    }

    private void EnsureEngine()
    {
        if (_player is not null && _libVlc is not null && _frameBuffer != IntPtr.Zero)
            return;

        Core.Initialize();
        _libVlc = new VlcLibVLC(
            "--no-video-title-show",
            "--no-osd",
            "--quiet",
            "--no-audio",
            "--avcodec-hw=none",
            "--file-caching=180");
        _player = new VlcMediaPlayer(_libVlc);

        int bufferSize = checked((int)(Pitch * BufferLines));
        _frameBuffer = Marshal.AllocHGlobal(bufferSize);

        _player.SetVideoFormat("RV32", ThumbnailWidth, ThumbnailHeight, Pitch);
        _player.SetVideoCallbacks(LockVideo, null, DisplayVideo);
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        if (_frameBuffer == IntPtr.Zero)
            return IntPtr.Zero;

        Marshal.WriteIntPtr(planes, _frameBuffer);
        return IntPtr.Zero;
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        TaskCompletionSource<byte[]>? ready;
        int frameNumber;

        lock (_frameSync)
        {
            ready = _frameReady;
            frameNumber = ++_displayedFrames;
        }

        if (ready is null || ready.Task.IsCompleted || frameNumber < MinimumWarmupFrames || _frameBuffer == IntPtr.Zero)
            return;

        int visibleBytes = checked((int)(Pitch * ThumbnailHeight));
        byte[] pixels = new byte[visibleBytes];
        Marshal.Copy(_frameBuffer, pixels, 0, visibleBytes);

        if (frameNumber < ForcedCaptureFrame && !LooksLikeUsefulFrame(pixels, checked((int)Pitch)))
            return;

        ready.TrySetResult(pixels);
    }

    private bool TryGetOriginalVideoSize(out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (_player is null)
            return false;

        try
        {
            // LibVLC decoder zaten thumbnail için açık. Burada yeni parse/decode başlatmadan
            // yalnız decoder'ın mevcut video geometry bilgisini okuyoruz.
            foreach (System.Reflection.MethodInfo method in _player.GetType().GetMethods())
            {
                if (!string.Equals(method.Name, "Size", StringComparison.Ordinal) || method.GetParameters().Length != 3)
                    continue;

                System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                Type firstType = parameters[0].ParameterType;
                object index = firstType == typeof(uint) ? 0u : 0;
                object[] args = [index, 0u, 0u];
                method.Invoke(_player, args);

                width = Convert.ToUInt32(args[1], System.Globalization.CultureInfo.InvariantCulture);
                height = Convert.ToUInt32(args[2], System.Globalization.CultureInfo.InvariantCulture);
                if (width > 0 && height > 0)
                    return true;
            }
        }
        catch
        {
        }

        width = 0;
        height = 0;
        return false;
    }

    private static bool LooksLikeUsefulFrame(byte[] pixels, int stride)
    {
        if (pixels.Length < stride || stride <= 0)
            return false;

        int minimum = 255;
        int maximum = 0;
        long total = 0;
        int samples = 0;

        for (int y = 8; y < (int)ThumbnailHeight - 8; y += 12)
        {
            int row = y * stride;
            for (int x = 8; x < (int)ThumbnailWidth - 8; x += 12)
            {
                int index = row + x * 4;
                if (index + 2 >= pixels.Length)
                    continue;

                int b = pixels[index];
                int g = pixels[index + 1];
                int r = pixels[index + 2];
                int luminance = (r * 54 + g * 183 + b * 19) >> 8;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                total += luminance;
                samples++;
            }
        }

        if (samples == 0)
            return false;

        double average = total / (double)samples;
        return maximum - minimum >= 14 || average >= 12d;
    }

    private static ImageSource CreateFrozenBitmap(byte[] pixels)
    {
        var bitmap = BitmapSource.Create(
            checked((int)ThumbnailWidth),
            checked((int)ThumbnailHeight),
            96d,
            96d,
            PixelFormats.Bgr32,
            null,
            pixels,
            checked((int)Pitch));
        bitmap.Freeze();
        return bitmap;
    }

    private void ResetCaptureState()
    {
        lock (_frameSync)
        {
            _displayedFrames = 0;
            _frameReady = null;
        }
    }

    private bool TryGetCached(string key, out VideoThumbnailResult? result)
    {
        lock (_cacheSync)
            return _cache.TryGetValue(key, out result);
    }

    private void AddCache(string key, VideoThumbnailResult result)
    {
        lock (_cacheSync)
        {
            if (_cache.ContainsKey(key))
                return;

            _cache[key] = result;
            _cacheOrder.Enqueue(key);

            while (_cache.Count > CacheLimit && _cacheOrder.Count > 0)
            {
                string oldest = _cacheOrder.Dequeue();
                _cache.Remove(oldest);
            }
        }
    }

    private static string BuildCacheKey(StorageDeviceInfo device, RecoveryFileItem item)
    {
        string extentKey = item.SourceExtents is { Count: > 0 }
            ? string.Join(";", item.SourceExtents.Take(8).Select(e => $"{e.Offset:X}:{e.Length:X}"))
            : string.Empty;
        string runKey = item.DataRuns is { Count: > 0 }
            ? string.Join(";", item.DataRuns.Take(8).Select(r => $"{r.LogicalClusterNumber:X}:{r.ClusterCount:X}:{(r.IsSparse ? 1 : 0)}"))
            : string.Empty;
        string residentKey = BuildResidentKey(item.ResidentData);
        string repaired = item.IsRepaired ? item.RepairedFilePath ?? string.Empty : string.Empty;

        return string.Join(
            "|",
            device.RootPath,
            item.FileName,
            item.SourceKind,
            item.SourceOffset,
            item.SizeBytes,
            FileTypeHelper.Normalize(item.Extension),
            item.TransformKind,
            extentKey,
            runKey,
            residentKey,
            repaired);
    }

    private static string BuildResidentKey(byte[]? data)
    {
        if (data is not { Length: > 0 })
            return string.Empty;

        int count = Math.Min(24, data.Length);
        return Convert.ToHexString(data, 0, count);
    }

    private static uint Align32(uint value)
    {
        uint remainder = value % 32u;
        return remainder == 0 ? value : checked(value + (32u - remainder));
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        bool entered = false;
        try
        {
            _captureGate.Wait();
            entered = true;

            try
            {
                _player?.Stop();
            }
            catch
            {
            }

            _player?.Dispose();
            _player = null;
            _libVlc?.Dispose();
            _libVlc = null;

            if (_frameBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_frameBuffer);
                _frameBuffer = IntPtr.Zero;
            }
        }
        finally
        {
            if (entered)
                _captureGate.Release();
            _captureGate.Dispose();
        }
    }
}
