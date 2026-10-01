using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("api/update")]
    public class UpdateController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly InstallationVersionTelemetryService _versionTelemetry;

        public UpdateController(ApplicationDbContext context, InstallationVersionTelemetryService versionTelemetry)
        {
            _context = context;
            _versionTelemetry = versionTelemetry;
        }

        [HttpGet("check")]
        public async Task<IActionResult> Check(
            string productCode,
            string version,
            [FromQuery] string? licenseKey = null,
            [FromQuery] string? machineId = null)
        {
            productCode = (productCode ?? string.Empty).Trim().ToUpperInvariant();
            version = (version ?? "0.0.0").Trim();

            if (string.IsNullOrWhiteSpace(productCode))
            {
                return BadRequest(new
                {
                    hasUpdate = false,
                    message = "productCode zorunlu"
                });
            }

            var currentVersion = ParseVersion(version);

            var acceptedProductCodes = GetAcceptedUpdateProductCodes(productCode);

            // Evrensel NSX surum telemetrisi: V2/legacy lisans isteginde surum gelmese bile
            // programin zaten yaptigi guncelleme kontrolundeki current version bilgisini,
            // yalnizca guvenli ve tekil bir kurulum eslesmesi varsa admin istatistigine baglar.
            await _versionTelemetry.CaptureFromUpdateCheckAsync(
                HttpContext, acceptedProductCodes, version, licenseKey, machineId);

            var updates = await _context.ProductUpdates
                .AsNoTracking()
                .Where(x => x.IsActive && acceptedProductCodes.Contains(x.ProductCode.ToUpper()))
                .ToListAsync();

            var latest = updates
                .OrderByDescending(x => ParseVersion(x.Version))
                .ThenByDescending(x => x.CreatedAt)
                .FirstOrDefault();

            if (latest == null)
            {
                return Ok(new
                {
                    hasUpdate = false,
                    currentVersion = version,
                    latestVersion = version,
                    message = "Bu ürün için aktif güncelleme yok."
                });
            }

            var latestVersion = ParseVersion(latest.Version);
            if (latestVersion <= currentVersion)
            {
                return Ok(new
                {
                    hasUpdate = false,
                    currentVersion = version,
                    latestVersion = latest.Version,
                    message = "Programın Son Sürümünü Kullanıyorsun. Yeni Güncelleme Yok."
                });
            }

            var absoluteUrl = latest.DownloadUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? latest.DownloadUrl
                : $"{Request.Scheme}://{Request.Host}{latest.DownloadUrl}";

            return Ok(new
            {
                hasUpdate = true,
                currentVersion = version,
                latestVersion = latest.Version,
                version = latest.Version,
                required = latest.IsRequired,
                url = absoluteUrl,
                notes = latest.ReleaseNotes ?? string.Empty,
                message = "Yeni güncelleme mevcut."
            });
        }


        private static string[] GetAcceptedUpdateProductCodes(string productCode)
        {
            // Yalnizca masaustu guncelleme servisi icin geriye donuklu kod eslemesi.
            // Lisans/aktivasyon kodlarina dokunmaz.
            if (string.Equals(productCode, "NSXCARITAKIPPRO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(productCode, "NSXCARITAKIPPRO2", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { "NSXCARITAKIPPRO", "NSXCARITAKIPPRO2" };
            }

            return new[] { productCode };
        }

        private static Version ParseVersion(string value)
        {
            return Version.TryParse(value, out var v) ? v : new Version(0, 0, 0);
        }
    }
}
