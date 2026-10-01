using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;

namespace NSYazilim.Web.Controllers.Api
{
    [ApiController]
    [Route("api/ads")]
    public class AdsController : ControllerBase
    {
        private const int LegacyKnownActiveAdId = 1;
        private const string FreeVeresiyeProgramCode = "NSXVERESIYETAKIPPROFREE";
        private static readonly string[] VeresiyeProgramCodeAliases =
        {
            FreeVeresiyeProgramCode,
            "NSXVERESIYEDEFTERI",
            "NSXVERESIYE",
            "VERESIYEDEFTERI",
            "VERESIYE"
        };

        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _environment;

        public AdsController(ApplicationDbContext context, IWebHostEnvironment environment)
        {
            _context = context;
            _environment = environment;
        }

        [HttpGet("active")]
        public async Task<IActionResult> Active([FromQuery] string? productCode, [FromQuery] string? slot, [FromQuery] string? slotCode = null, [FromQuery] string? product = null, [FromQuery] string? programCode = null)
        {
            SetNoCacheHeaders();

            var now = DateTime.Now;
            var normalizedProductCode = NormalizeCode(productCode);
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
                normalizedProductCode = NormalizeCode(product);
            if (string.IsNullOrWhiteSpace(normalizedProductCode))
                normalizedProductCode = NormalizeCode(programCode);

            var normalizedSlotCode = NormalizeCode(slot);
            if (string.IsNullOrWhiteSpace(normalizedSlotCode))
                normalizedSlotCode = NormalizeCode(slotCode);

            var hasProductFilter = !string.IsNullOrWhiteSpace(normalizedProductCode);

            if (string.IsNullOrWhiteSpace(normalizedSlotCode))
                normalizedSlotCode = "FREE_BOTTOM_728X90";

            List<Advertisement> ads;
            if (hasProductFilter)
            {
                ads = await GetActiveAdvertisementsAsync(normalizedProductCode, normalizedSlotCode, now);
                if (ads.Count == 0 && IsVeresiyeProgramCode(normalizedProductCode))
                    ads = await GetActiveAdvertisementsForSlotAsync(normalizedSlotCode, now);
            }
            else
            {
                // Program ürün kodu göndermediğinde aynı reklam alanındaki bütün
                // aktif reklamlar sıraya alınır. Böylece farklı ürün reklamları
                // (ör. NSX Turbo) ücretsiz programda dönüşümlü gösterilebilir.
                ads = await GetActiveAdvertisementsForSlotAsync(normalizedSlotCode, now);
            }

            if (ads.Count == 0)
            {
                return Ok(new
                {
                    success = false,
                    isActive = false,
                    message = "Aktif reklam bulunamadı."
                });
            }

            var selected = SelectAdvertisementForNextRotation(ads);
            selected.ImpressionCount += 1;
            selected.LastShownAt = now;
            await _context.SaveChangesAsync();

            var rawImageUrl = ToAbsoluteUrl(selected.ImagePath);
            var imageUrl = BuildProgramSafeImageUrl(selected);
            var rawTargetUrl = ToAbsoluteUrl(string.IsNullOrWhiteSpace(selected.TargetUrl) ? "/" : selected.TargetUrl);
            var trackingClickUrl = ToAbsoluteUrl($"/api/ads/click/{selected.Id}");

            return Ok(new
            {
                success = true,
                isActive = true,
                id = selected.Id,
                title = selected.Title,
                description = selected.Description ?? string.Empty,
                altText = string.IsNullOrWhiteSpace(selected.AltText) ? selected.Title : selected.AltText,
                imageUrl,
                bannerUrl = imageUrl,
                rawImageUrl,
                targetUrl = selected.TrackClicks ? trackingClickUrl : rawTargetUrl,
                clickUrl = selected.TrackClicks ? trackingClickUrl : rawTargetUrl,
                rawTargetUrl,
                productCode = selected.ProductCode,
                slot = selected.SlotCode,
                slotCode = selected.SlotCode,
                width = selected.Width,
                height = selected.Height,
                displaySeconds = selected.DisplaySeconds,
                refreshSeconds = selected.DisplaySeconds
            });
        }

        [HttpGet("image/{id:int}")]
        public async Task<IActionResult> Image(int id)
        {
            var ad = await ResolveAdvertisementForImageAsync(id);

            if (ad == null || string.IsNullOrWhiteSpace(ad.ImagePath))
                return NotFound();

            if (Uri.TryCreate(ad.ImagePath, UriKind.Absolute, out var absoluteUri))
                return Redirect(absoluteUri.ToString());

            var filePath = GetSafeWebRootFilePath(ad.ImagePath);
            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return NotFound();

            if (id == LegacyKnownActiveAdId)
                SetNoCacheHeaders();
            else
                Response.Headers.CacheControl = "public,max-age=300";

            if (Path.GetExtension(filePath).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                await using var input = System.IO.File.OpenRead(filePath);
                using var image = await SixLabors.ImageSharp.Image.LoadAsync(input);
                await using var output = new MemoryStream();
                await image.SaveAsync(output, new PngEncoder());
                return File(output.ToArray(), "image/png");
            }

            return PhysicalFile(filePath, GetContentType(filePath));
        }

        [HttpGet("click/{id:int}")]
        public async Task<IActionResult> Click(int id)
        {
            var ad = await ResolveAdvertisementForClickAsync(id);

            if (ad == null)
                return Redirect("/");

            ad.ClickCount += 1;
            ad.LastClickedAt = DateTime.Now;
            await _context.SaveChangesAsync();

            return Redirect(ToAbsoluteUrl(string.IsNullOrWhiteSpace(ad.TargetUrl) ? "/" : ad.TargetUrl));
        }

        private async Task<List<Advertisement>> GetActiveAdvertisementsAsync(string productCode, string slotCode, DateTime now)
        {
            var productCodes = GetProductCodeAliases(productCode);

            return await _context.Advertisements
                .Where(x => !x.IsDeleted && x.IsActive)
                .Where(x => productCodes.Contains(x.ProductCode) || x.ProductCode == "ALL")
                .Where(x => x.SlotCode == slotCode)
                .Where(x => x.StartDate == null || x.StartDate <= now)
                .Where(x => x.EndDate == null || x.EndDate >= now)
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .ToListAsync();
        }

        private async Task<List<Advertisement>> GetActiveAdvertisementsForSlotAsync(string slotCode, DateTime now)
        {
            return await _context.Advertisements
                .Where(x => !x.IsDeleted && x.IsActive)
                .Where(x => x.SlotCode == slotCode)
                .Where(x => x.StartDate == null || x.StartDate <= now)
                .Where(x => x.EndDate == null || x.EndDate >= now)
                .OrderByDescending(x => x.Priority)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .ToListAsync();
        }

        private async Task<Advertisement?> ResolveAdvertisementForImageAsync(int id)
        {
            if (id == LegacyKnownActiveAdId)
            {
                var now = DateTime.Now;
                var selected = await SelectLegacyAdvertisementAsync(now, preferLastShown: false);
                if (selected != null)
                {
                    selected.ImpressionCount += 1;
                    selected.LastShownAt = now;
                    await _context.SaveChangesAsync();
                    return selected;
                }
            }

            return await _context.Advertisements
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        }

        private async Task<Advertisement?> ResolveAdvertisementForClickAsync(int id)
        {
            if (id == LegacyKnownActiveAdId)
            {
                var selected = await SelectLegacyAdvertisementAsync(DateTime.Now, preferLastShown: true);
                if (selected != null)
                    return selected;
            }

            return await _context.Advertisements
                .FirstOrDefaultAsync(x => x.Id == id && !x.IsDeleted);
        }

        private async Task<Advertisement?> SelectLegacyAdvertisementAsync(DateTime now, bool preferLastShown)
        {
            var ads = await GetActiveAdvertisementsAsync(FreeVeresiyeProgramCode, "FREE_BOTTOM_728X90", now);
            if (ads.Count == 0)
                ads = await GetActiveAdvertisementsForSlotAsync("FREE_BOTTOM_728X90", now);

            if (ads.Count == 0)
                return null;

            if (preferLastShown)
            {
                return ads
                    .Where(x => x.LastShownAt.HasValue)
                    .OrderByDescending(x => x.LastShownAt)
                    .ThenByDescending(x => x.Priority)
                    .ThenBy(x => x.SortOrder)
                    .ThenBy(x => x.Id)
                    .FirstOrDefault()
                    ?? SelectAdvertisementForNextRotation(ads);
            }

            return SelectAdvertisementForNextRotation(ads);
        }

        private string BuildProgramSafeImageUrl(Advertisement ad)
        {
            if (IsLocalWebp(ad.ImagePath))
                return ToAbsoluteUrl($"/api/ads/image/{ad.Id}");

            return ToAbsoluteUrl(ad.ImagePath);
        }

        private string? GetSafeWebRootFilePath(string imagePath)
        {
            var normalized = (imagePath ?? string.Empty).Trim().Replace("\\", "/");
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("//", StringComparison.Ordinal))
            {
                return null;
            }

            normalized = normalized.TrimStart('/');
            var combined = Path.GetFullPath(Path.Combine(_environment.WebRootPath, normalized.Replace("/", Path.DirectorySeparatorChar.ToString())));
            var webRoot = Path.GetFullPath(_environment.WebRootPath);

            if (!combined.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            return combined;
        }

        private static bool IsLocalWebp(string? imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
                return false;

            var value = imagePath.Trim();
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            return Path.GetExtension(value.Split('?', '#')[0]).Equals(".webp", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetContentType(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".svg" => "image/svg+xml",
                _ => "application/octet-stream"
            };
        }

        private static Advertisement SelectAdvertisementForNextRotation(IReadOnlyList<Advertisement> ads)
        {
            if (ads.Count == 1)
                return ads[0];

            return ads
                .OrderBy(x => x.LastShownAt ?? DateTime.MinValue)
                .ThenBy(x => x.ImpressionCount)
                .ThenByDescending(x => x.Priority)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Id)
                .First();
        }

        private void SetNoCacheHeaders()
        {
            Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Expires = "0";
        }

        private string ToAbsoluteUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                return trimmed;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                return $"{Request.Scheme}:{trimmed}";

            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            return trimmed.StartsWith("/", StringComparison.Ordinal)
                ? baseUrl + trimmed
                : baseUrl + "/" + trimmed.TrimStart('/');
        }

        private static string NormalizeCode(string? value)
        {
            return (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string[] GetProductCodeAliases(string productCode)
        {
            var normalized = NormalizeCode(productCode);
            if (IsVeresiyeProgramCode(normalized))
                return VeresiyeProgramCodeAliases;

            return new[] { normalized };
        }

        private static bool IsVeresiyeProgramCode(string productCode)
        {
            return productCode.Contains("VERESIYE", StringComparison.OrdinalIgnoreCase);
        }
    }
}
