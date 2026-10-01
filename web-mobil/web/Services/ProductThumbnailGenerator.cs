using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace NSYazilim.Web.Services;

public static class ProductThumbnailGenerator
{
    public static readonly int[] StandardWidths = [480, 720, 960];

    public static string GetRelativePath(string sourceFileName, int width)
    {
        sourceFileName = Path.GetFileName(sourceFileName);
        width = NormalizeWidth(width);
        return $"/generated/product-thumbs/{Uri.EscapeDataString(sourceFileName)}/{width}.webp";
    }

    public static string GetPhysicalPath(string webRootPath, string sourceFileName, int width)
    {
        sourceFileName = Path.GetFileName(sourceFileName);
        width = NormalizeWidth(width);
        return Path.Combine(webRootPath, "generated", "product-thumbs", sourceFileName, $"{width}.webp");
    }

    public static async Task GenerateAllAsync(string sourcePath, string webRootPath, CancellationToken cancellationToken = default)
    {
        foreach (var width in StandardWidths)
            await GenerateAsync(sourcePath, webRootPath, width, cancellationToken);
    }

    public static async Task<string> GenerateAsync(string sourcePath, string webRootPath, int width, CancellationToken cancellationToken = default)
    {
        width = NormalizeWidth(width);
        var sourceFileName = Path.GetFileName(sourcePath);
        var destinationPath = GetPhysicalPath(webRootPath, sourceFileName, width);
        if (File.Exists(destinationPath))
            return destinationPath;

        var directory = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(directory);

        using var image = await Image.LoadAsync(sourcePath, cancellationToken);
        if (image.Width > width)
        {
            var targetHeight = Math.Max(1, (int)Math.Round(image.Height * (width / (double)image.Width)));
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(width, targetHeight),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3
            }));
        }

        var tempPath = destinationPath + ".tmp";
        try
        {
            await image.SaveAsWebpAsync(tempPath, new WebpEncoder { Quality = 80 }, cancellationToken);
            File.Move(tempPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        return destinationPath;
    }

    public static void DeleteAll(string webRootPath, string sourceFileName)
    {
        sourceFileName = Path.GetFileName(sourceFileName);
        var directory = Path.Combine(webRootPath, "generated", "product-thumbs", sourceFileName);
        if (!Directory.Exists(directory))
            return;

        try { Directory.Delete(directory, recursive: true); } catch { }
    }

    private static int NormalizeWidth(int width) => width switch
    {
        <= 480 => 480,
        <= 720 => 720,
        _ => 960
    };
}
