using System.Windows.Media;
using NSXVeriKurtarmaPro.Models;

namespace NSXVeriKurtarmaPro.Services;

public sealed record PreviewResult(RecoveryFileItem Item, ImageSource Image, uint Width = 0, uint Height = 0);

public sealed class PreviewService : IDisposable
{
    private readonly VideoThumbnailService _videoThumbnailService = new();
    private const int MaxPreviewItems = 300;
    private const long MaxPhotoBytes = 512L * 1024 * 1024;
    private const int VideoProbeBytes = 12 * 1024 * 1024;
    private const int MaxEmbeddedJpegBytes = 8 * 1024 * 1024;

    public IReadOnlyList<PreviewResult> BuildPreviews(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken)
    {
        var previews = new List<PreviewResult>();
        BuildPreviews(device, items, cancellationToken, previews.Add);
        return previews;
    }

    public void BuildPreviews(
        StorageDeviceInfo device,
        IReadOnlyList<RecoveryFileItem> items,
        CancellationToken cancellationToken,
        Action<PreviewResult> previewReady)
    {
        if (!device.IsReady || items.Count == 0 || cancellationToken.IsCancellationRequested)
            return;

        using RawDeviceReader reader = RawDeviceReader.OpenDevice(device, mediaProfile: RecoveryMediaProfileService.Create(device));

        int inspected = 0;
        foreach (RecoveryFileItem item in items)
        {
            if (cancellationToken.IsCancellationRequested || inspected++ >= MaxPreviewItems)
                break;

            try
            {
                uint width = 0;
                uint height = 0;
                ImageSource? image = item.Category switch
                {
                    "Fotoğraf" => BuildPhotoPreview(reader, item, cancellationToken, out width, out height),
                    "Video" => BuildVideoPreview(device, reader, item, cancellationToken, out width, out height),
                    _ => null
                };

                if (image is not null && !cancellationToken.IsCancellationRequested)
                    previewReady(new PreviewResult(item, image, width, height));
            }
            catch
            {
            }
        }
    }

    private static ImageSource? BuildPhotoPreview(
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken,
        out uint width,
        out uint height)
    {
        width = 0;
        height = 0;

        if (cancellationToken.IsCancellationRequested || item.SizeBytes <= 0 || item.SizeBytes > MaxPhotoBytes)
            return null;

        string ext = FileTypeHelper.Normalize(item.Extension);
        if (!GlobalImageCodec.CanDecode(ext))
            return null;

        byte[] bytes = ReadItemBytes(reader, item, item.SizeBytes, cancellationToken);
        if (cancellationToken.IsCancellationRequested || bytes.Length == 0)
            return null;

        try
        {
            (ImageSource? direct, width, height) = GlobalImageCodec.CreateThumbnailWithDimensions(bytes, 140);
            if (direct is not null)
                return direct;
        }
        catch
        {
            width = 0;
            height = 0;
        }

        if (cancellationToken.IsCancellationRequested)
            return null;

        return BuildPhotoPreviewFromTemporaryFile(bytes, ext, cancellationToken);
    }

    private static ImageSource? BuildPhotoPreviewFromTemporaryFile(
        byte[] bytes,
        string extension,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "NSXVeriKurtarmaPro",
            "ThumbnailDecode",
            Environment.ProcessId.ToString());
        string normalizedExtension = FileTypeHelper.Normalize(extension).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedExtension))
            normalizedExtension = "img";

        string path = Path.Combine(directory, $"thumb_{Guid.NewGuid():N}.{normalizedExtension}");

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return null;

            Directory.CreateDirectory(directory);
            File.WriteAllBytes(path, bytes);

            if (cancellationToken.IsCancellationRequested)
                return null;

            return GlobalImageCodec.LoadPreview(path, 160);
        }
        catch
        {
            return null;
        }
        finally
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

    private ImageSource? BuildVideoPreview(
        StorageDeviceInfo device,
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken,
        out uint width,
        out uint height)
    {
        width = 0;
        height = 0;
        if (cancellationToken.IsCancellationRequested || item.SizeBytes <= 0)
            return null;

        VideoThumbnailResult? decoded = _videoThumbnailService.BuildThumbnail(device, item, cancellationToken);
        if (decoded is not null)
        {
            width = decoded.Width;
            height = decoded.Height;
            return decoded.Image;
        }

        if (cancellationToken.IsCancellationRequested)
            return null;

        return BuildEmbeddedVideoPreview(reader, item, cancellationToken);
    }

    private static ImageSource? BuildEmbeddedVideoPreview(
        RawDeviceReader reader,
        RecoveryFileItem item,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || item.SizeBytes <= 0)
            return null;

        long probeLength = Math.Min(item.SizeBytes, VideoProbeBytes);
        byte[] probe = ReadItemBytes(reader, item, probeLength, cancellationToken);
        if (cancellationToken.IsCancellationRequested || probe.Length < 4096)
            return null;

        if (!TryExtractJpeg(probe, out byte[] jpeg) || !LooksLikeCompleteImage(jpeg, "JPG"))
            return null;

        return GlobalImageCodec.CreateThumbnail(jpeg, 120);
    }

    private static byte[] ReadItemBytes(
        RawDeviceReader reader,
        RecoveryFileItem item,
        long requestedLength,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return [];

        requestedLength = Math.Min(requestedLength, item.SizeBytes);
        if (requestedLength <= 0 || requestedLength > int.MaxValue)
            return [];

        if (item.SourceKind == RecoverySourceKind.NtfsResident)
        {
            if (item.ResidentData is null || cancellationToken.IsCancellationRequested)
                return [];
            int count = (int)Math.Min(requestedLength, item.ResidentData.Length);
            return item.ResidentData.AsSpan(0, count).ToArray();
        }

        using var memory = new MemoryStream((int)requestedLength);
        long remaining = requestedLength;

        switch (item.SourceKind)
        {
            case RecoverySourceKind.RawContiguous:
            case RecoverySourceKind.FatContiguous:
            case RecoverySourceKind.ExFatContiguous:
                AppendContiguous(reader, item.SourceOffset, remaining, memory, cancellationToken);
                break;

            case RecoverySourceKind.Extents:
                if (item.SourceExtents is null) return [];
                foreach (SourceExtent extent in item.SourceExtents)
                {
                    if (cancellationToken.IsCancellationRequested || remaining <= 0)
                        break;
                    long count = Math.Min(extent.Length, remaining);
                    AppendContiguous(reader, extent.Offset, count, memory, cancellationToken);
                    remaining -= count;
                }
                break;

            case RecoverySourceKind.NtfsRunList:
                if (item.DataRuns is null || item.ClusterSize <= 0) return [];
                foreach (DataRun run in item.DataRuns)
                {
                    if (cancellationToken.IsCancellationRequested || remaining <= 0)
                        break;

                    long runBytes = Math.Min(run.ClusterCount * (long)item.ClusterSize, remaining);
                    if (run.IsSparse)
                    {
                        byte[] zero = new byte[Math.Min(1024 * 1024, (int)Math.Min(runBytes, int.MaxValue))];
                        long sparse = runBytes;
                        while (sparse > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            int count = (int)Math.Min(zero.Length, sparse);
                            memory.Write(zero, 0, count);
                            sparse -= count;
                        }
                    }
                    else
                    {
                        long sourceOffset = run.LogicalClusterNumber * (long)item.ClusterSize;
                        AppendContiguous(reader, sourceOffset, runBytes, memory, cancellationToken);
                    }
                    remaining -= runBytes;
                }
                break;
        }

        return cancellationToken.IsCancellationRequested ? [] : memory.ToArray();
    }

    private static void AppendContiguous(
        RawDeviceReader reader,
        long sourceOffset,
        long length,
        MemoryStream output,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long remaining = length;
        long offset = sourceOffset;

        while (remaining > 0 && !cancellationToken.IsCancellationRequested)
        {
            int request = (int)Math.Min(buffer.Length, remaining);
            int read = reader.Read(offset, buffer, request);
            if (read <= 0)
                break;
            output.Write(buffer, 0, read);
            offset += read;
            remaining -= read;
        }
    }

    private static bool LooksLikeCompleteImage(ReadOnlySpan<byte> data, string ext)
    {
        if (data.Length < 16)
            return false;

        return ext switch
        {
            "JPG" or "JPEG" =>
                data[0] == 0xFF && data[1] == 0xD8 &&
                FindLastMarker(data, 0xFF, 0xD9),

            "PNG" =>
                data.Length >= 20 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A &&
                ContainsAscii(data, "IEND"),

            "BMP" => data[0] == (byte)'B' && data[1] == (byte)'M' && data.Length >= 54,

            "GIF" =>
                data.Length >= 14 &&
                data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[^1] == 0x3B,

            "TIF" or "TIFF" =>
                (data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 42 && data[3] == 0) ||
                (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 42),

            _ => false
        };
    }

    private static bool FindLastMarker(ReadOnlySpan<byte> data, byte first, byte second)
    {
        int start = Math.Max(0, data.Length - 64 * 1024);
        for (int i = data.Length - 2; i >= start; i--)
        {
            if (data[i] == first && data[i + 1] == second)
                return true;
        }
        return false;
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> data, string text)
    {
        ReadOnlySpan<byte> needle = System.Text.Encoding.ASCII.GetBytes(text);
        return data.IndexOf(needle) >= 0;
    }

    private static bool TryExtractJpeg(ReadOnlySpan<byte> data, out byte[] jpeg)
    {
        jpeg = [];
        int start = -1;

        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD8 && data[i + 2] == 0xFF)
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return false;

        int maxEnd = Math.Min(data.Length - 1, start + MaxEmbeddedJpegBytes);
        for (int i = start + 3; i < maxEnd; i++)
        {
            if (data[i] == 0xFF && data[i + 1] == 0xD9)
            {
                int length = i + 2 - start;
                if (length < 4096)
                    return false;

                jpeg = data.Slice(start, length).ToArray();
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        _videoThumbnailService.Dispose();
    }

}
