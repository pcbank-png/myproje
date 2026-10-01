using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;
using System.Security.Cryptography;
using System.Text;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("License")]
    public class FreeLicenseActivationController : ControllerBase
    {
        private const string ProductCode = "NSXVERESIYETAKIPPROFREE";
        private readonly ApplicationDbContext _context;
        private readonly ClientIpService _clientIpService;

        public FreeLicenseActivationController(ApplicationDbContext context, ClientIpService clientIpService)
        {
            _context = context;
            _clientIpService = clientIpService;
        }

        public sealed class AutoActivateRequest
        {
            public string? Token { get; set; }
            public string? MachineId { get; set; }
            public string? ProductCode { get; set; }
            public string? DeviceName { get; set; }
            public string? AppVersion { get; set; }
            public string? OsVersion { get; set; }
        }

        [HttpGet("AutoActivate")]
        public Task<IActionResult> AutoActivateGet(
            [FromQuery] string? token,
            [FromQuery] string? machineId,
            [FromQuery] string? productCode,
            [FromQuery] string? deviceName = null,
            [FromQuery] string? appVersion = null,
            [FromQuery] string? osVersion = null)
            => ActivateAsync(token, machineId, productCode, deviceName, appVersion, osVersion);

        [HttpPost("AutoActivate")]
        public Task<IActionResult> AutoActivatePost([FromBody] AutoActivateRequest? request)
            => ActivateAsync(request?.Token, request?.MachineId, request?.ProductCode, request?.DeviceName, request?.AppVersion, request?.OsVersion);

        private async Task<IActionResult> ActivateAsync(
            string? token,
            string? machineId,
            string? productCode,
            string? deviceName,
            string? appVersion,
            string? osVersion)
        {
            token = (token ?? string.Empty).Trim();
            machineId = (machineId ?? string.Empty).Trim();
            productCode = NormalizeCode(productCode);
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(machineId))
                return BadRequest(new { success = false, message = "Otomatik eşleştirme bilgisi eksik." });
            if (!string.Equals(productCode, ProductCode, StringComparison.Ordinal))
                return NotFound(new { success = false, message = "Bu işlem yalnızca NSX Ücretsiz Veresiye Programı içindir." });

            var hash = HashToken(token);
            var now = DateTime.UtcNow;
            var activation = await _context.FreeLicenseActivationTokens
                .Include(x => x.User).Include(x => x.Product).Include(x => x.License).ThenInclude(x => x!.Devices)
                .FirstOrDefaultAsync(x => x.TokenHash == hash && x.ProductCode == ProductCode);
            if (activation == null || activation.UsedAt != null || activation.ExpiresAt < now)
                return Ok(new { success = false, message = "Eşleştirme bağlantısının süresi dolmuş veya daha önce kullanılmış." });
            if (activation.License == null || activation.User == null || activation.Product == null || !activation.License.IsActive)
                return Ok(new { success = false, message = "Ücretsiz lisans kaydı bulunamadı." });
            if (!string.Equals(NormalizeCode(activation.License.ProductCode), ProductCode, StringComparison.Ordinal))
                return NotFound(new { success = false, message = "Ürün eşleşmesi geçersiz." });

            var license = activation.License;
            var nowLocal = DateTime.Now;
            var resolvedAppVersion = AppVersionResolver.Resolve(HttpContext, appVersion);
            foreach (var old in license.Devices.Where(x => !string.Equals(x.MachineId, machineId, StringComparison.Ordinal)))
            {
                old.IsRejected = true;
                old.DeviceStatus = "Transferred";
                old.BlockReason = "Free otomatik eşleştirme ile yeni cihaza aktarıldı.";
            }
            var device = license.Devices.FirstOrDefault(x => x.MachineId == machineId);
            if (device == null)
            {
                device = new LicenseDevice { LicenseId = license.Id, MachineId = machineId, ProductCode = ProductCode, FirstActivatedAt = nowLocal };
                _context.LicenseDevices.Add(device);
            }
            device.AttemptEmail = activation.User.Email;
            if (!string.IsNullOrWhiteSpace(deviceName))
                device.DeviceName = TrimTo(deviceName, 120);
            if (!string.IsNullOrWhiteSpace(osVersion))
                device.OsVersion = TrimTo(osVersion, 120);
            if (!string.IsNullOrWhiteSpace(resolvedAppVersion))
                device.AppVersion = resolvedAppVersion;
            device.DeviceStatus = "Active";
            device.IsBlocked = false;
            device.IsRejected = false;
            device.BlockReason = null;
            device.LastSeenAt = nowLocal;
            device.LastIpAddress = _clientIpService.GetClientIp(HttpContext);
            license.MachineId = machineId;
            license.LicenseStatus = "Active";
            license.IsActive = true;
            license.LastCheckedAt = nowLocal;
            if (!string.IsNullOrWhiteSpace(resolvedAppVersion))
                license.LastAppVersion = resolvedAppVersion;
            activation.UsedAt = now;
            activation.UsedMachineId = machineId;
            activation.UsedIpAddress = device.LastIpAddress;
            await _context.SaveChangesAsync();

            return Ok(new { success = true, message = "Ömür boyu ücretsiz lisansınız aktif edildi.", customerEmail = activation.User.Email, licenseKey = license.LicenseKey, productName = activation.Product.Name, productCode = ProductCode, machineId });
        }

        private static string? TrimTo(string? value, int maxLength)
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
                return null;
            return text.Length <= maxLength ? text : text[..maxLength];
        }

        internal static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        private static string NormalizeCode(string? value) => new((value ?? string.Empty).Trim().ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
    }
}
