using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    public sealed class MediaController : ControllerBase
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ResizeLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".jfif"
        };

        private readonly IWebHostEnvironment _environment;
        private readonly FileExtensionContentTypeProvider _contentTypes = new();

        public MediaController(IWebHostEnvironment environment)
        {
            _environment = environment;
        }

        // Generated thumbnails are served directly by StaticFiles when present.
        // This endpoint only runs as a safe first-request fallback for a missing thumbnail.
        [HttpGet("/generated/product-thumbs/{fileName}/{w:int}.webp")]
        public async Task<IActionResult> ProductThumbnail(string fileName, int w, CancellationToken cancellationToken = default)
        {
            fileName = (fileName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(fileName) || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
                return BadRequest();

            var extension = Path.GetExtension(fileName);
            if (!AllowedExtensions.Contains(extension))
                return NotFound();

            if (w is not (480 or 720 or 960))
                return BadRequest();

            var sourceDirectory = Path.Combine(_environment.WebRootPath, "uploads", "products", "images");
            var sourcePath = Path.GetFullPath(Path.Combine(sourceDirectory, fileName));
            var safeRoot = Path.GetFullPath(sourceDirectory) + Path.DirectorySeparatorChar;
            if (!sourcePath.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(sourcePath))
                return NotFound();

            var thumbnailPath = ProductThumbnailGenerator.GetPhysicalPath(_environment.WebRootPath, fileName, w);
            Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";

            if (System.IO.File.Exists(thumbnailPath))
                return PhysicalFile(thumbnailPath, "image/webp");

            var resizeLock = ResizeLocks.GetOrAdd(thumbnailPath, static _ => new SemaphoreSlim(1, 1));
            await resizeLock.WaitAsync(cancellationToken);
            try
            {
                if (!System.IO.File.Exists(thumbnailPath))
                    await ProductThumbnailGenerator.GenerateAsync(sourcePath, _environment.WebRootPath, w, cancellationToken);

                return PhysicalFile(thumbnailPath, "image/webp");
            }
            catch
            {
                if (!_contentTypes.TryGetContentType(sourcePath, out var contentType))
                    contentType = "application/octet-stream";

                Response.Headers["Cache-Control"] = "public,max-age=604800,stale-while-revalidate=86400";
                return PhysicalFile(sourcePath, contentType);
            }
            finally
            {
                resizeLock.Release();
            }
        }
    }
}
