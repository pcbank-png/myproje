using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("License")]
    public class LicenseController : ControllerBase
    {
        private const string FreeVeresiyeProgramCode = "NSXVERESIYETAKIPPROFREE";
        

        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly ClientIpService _clientIpService;
        private readonly IpGeolocationService _ipGeolocationService;

        public LicenseController(ApplicationDbContext context, IConfiguration configuration, ClientIpService clientIpService, IpGeolocationService ipGeolocationService)
        {
            _context = context;
            _configuration = configuration;
            _clientIpService = clientIpService;
            _ipGeolocationService = ipGeolocationService;
        }

        [HttpGet("Activate")]
        public async Task<IActionResult> Activate(
            [FromQuery] string? key,
            [FromQuery] string? machineId,
            [FromQuery] string? apiKey = null,
            [FromQuery] string? email = null,
            [FromQuery] string? appVersion = null,
            [FromQuery] string? version = null)
        {
            if (!IsApiAuthorized(apiKey))
            {
                return Unauthorized(new
                {
                    success = false,
                    active = false,
                    message = "API anahtarı geçersiz."
                });
            }

            key = NormalizeKey(key);
            machineId = (machineId ?? string.Empty).Trim();
            var requestEmail = NormalizeEmail(email);

            if (string.IsNullOrWhiteSpace(key))
            {
                return BadRequest(new
                {
                    success = false,
                    active = false,
                    message = "Lisans anahtarı boş olamaz."
                });
            }

            if (string.IsNullOrWhiteSpace(machineId))
            {
                return BadRequest(new
                {
                    success = false,
                    active = false,
                    message = "MachineId boş olamaz."
                });
            }

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Devices)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.LicenseKey == key);

            if (license == null)
            {
                return NotFound(new
                {
                    success = false,
                    active = false,
                    message = "Lisans bulunamadı."
                });
            }

            var now = DateTime.Now;
            var resolvedAppVersion = AppVersionResolver.Resolve(HttpContext, appVersion, version);
            if (LicenseTermService.NormalizeTerm(license, now))
                await _context.SaveChangesAsync();

            var licenseStatus = NormalizeStatus(license.LicenseStatus);
            if (!license.IsActive || licenseStatus is "Suspended" or "Revoked" or "Blocked")
            {
                var statusMessage = licenseStatus switch
                {
                    "Suspended" => "Lisans askıya alınmış.",
                    "Revoked" => "Lisans iptal edilmiş.",
                    "Blocked" => "Lisans engellenmiş.",
                    _ => "Lisans pasif durumda."
                };

                return Ok(new
                {
                    success = false,
                    active = false,
                    status = licenseStatus,
                    message = statusMessage
                });
            }

            var ipAddress = GetIpAddress();
            if (license.EndDate.HasValue && license.EndDate.Value.Date < now.Date)
            {
                return Ok(new
                {
                    success = false,
                    active = false,
                    licenseKey = license.LicenseKey,
                    licenseType = license.LicenseType,
                    productName = license.Product?.Name ?? "NSX Veresiye Programı Pro V1.0",
                    expireDate = license.EndDate,
                    daysLeft = 0,
                    message = "Lisans süresi dolmuş."
                });
            }

            var productCode = string.IsNullOrWhiteSpace(license.ProductCode)
                ? NormalizeProductCode(license.Product?.Slug ?? license.Product?.Name ?? string.Empty)
                : NormalizeProductCode(license.ProductCode);
            var isFreeVeresiyeLicense = string.Equals(productCode, FreeVeresiyeProgramCode, StringComparison.OrdinalIgnoreCase);
            var licenseEmail = NormalizeEmail(license.User?.Email);
            var hasVerifiedReactivationCredentials =
                !string.IsNullOrWhiteSpace(requestEmail) &&
                !string.IsNullOrWhiteSpace(licenseEmail) &&
                string.Equals(requestEmail, licenseEmail, StringComparison.OrdinalIgnoreCase);
            var maxDeviceCount = license.MaxDeviceCount <= 0 ? 1 : license.MaxDeviceCount;
            var isSingleDeviceLicense = maxDeviceCount <= 1;
            var activeDevices = license.Devices
                .Where(x => !x.IsBlocked && !x.IsRejected)
                .OrderBy(x => x.FirstActivatedAt)
                .ThenBy(x => x.Id)
                .ToList();

            var primaryMachineId = (license.MachineId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(primaryMachineId) && isSingleDeviceLicense && activeDevices.Count > 0)
            {
                primaryMachineId = activeDevices[0].MachineId.Trim();
                license.MachineId = primaryMachineId;
            }

            if (isSingleDeviceLicense &&
                !string.IsNullOrWhiteSpace(primaryMachineId) &&
                !string.Equals(primaryMachineId, machineId, StringComparison.Ordinal))
            {
                if (hasVerifiedReactivationCredentials)
                {
                    LicenseDeviceReactivationService.TransferToVerifiedInstallation(license, machineId, maxDeviceCount, now);
                    primaryMachineId = machineId;
                    activeDevices = license.Devices
                        .Where(x => !x.IsBlocked && !x.IsRejected)
                        .OrderBy(x => x.FirstActivatedAt)
                        .ThenBy(x => x.Id)
                        .ToList();

                    _context.LicenseSecurityLogs.Add(new NSYazilim.Web.Models.LicenseSecurityLog
                    {
                        LicenseId = license.Id,
                        LicenseKeyMasked = MaskLicenseKey(license.LicenseKey),
                        ProductCode = productCode,
                        RequestEmail = requestEmail,
                        MachineId = machineId,
                        Severity = "Information",
                        EventType = "VerifiedDeviceReactivationV1",
                        Message = "Eski endpoint üzerinden doğrulanmış e-posta ve lisans anahtarıyla yeniden aktivasyon yapıldı.",
                        IpAddress = ipAddress,
                        CreatedAt = now
                    });
                }
                else
                {
                _context.LicenseSecurityLogs.Add(new NSYazilim.Web.Models.LicenseSecurityLog
                {
                    LicenseId = license.Id,
                    LicenseKeyMasked = MaskLicenseKey(license.LicenseKey),
                    ProductCode = productCode,
                    MachineId = machineId,
                    Severity = "Warning",
                    EventType = "DeviceRejectedPrimaryLocked",
                    RequestEmail = requestEmail,
                    Message = "V1 aktivasyonunda tek cihaz lisansı farklı cihazda ve doğrulanmış e-posta olmadan denenmiş.",
                    IpAddress = ipAddress,
                    CreatedAt = now
                });

                await _context.SaveChangesAsync();
                return Ok(new
                {
                    success = false,
                    active = false,
                    status = "DifferentDevice",
                    requiresAdminApproval = true,
                    primaryMachineProtected = true,
                    licenseKey = license.LicenseKey,
                    licenseType = license.LicenseType,
                    productName = license.Product?.Name ?? "NSX Veresiye Programı Pro V1.0",
                    message = "Bu lisans başka bir cihazda aktif. Format veya cihaz değişikliği yaptıysanız eski e-posta adresinizi de göndererek yeniden aktifleştirin."
                });
                }
            }

            var device = license.Devices.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
            if (device != null && device.IsBlocked)
            {
                return Ok(new
                {
                    success = false,
                    active = false,
                    status = "DeviceBlocked",
                    licenseKey = license.LicenseKey,
                    licenseType = license.LicenseType,
                    productName = license.Product?.Name ?? "NSX Veresiye Programı Pro V1.0",
                    message = "Bu lisansa bağlı cihaz engellenmiş."
                });
            }

            if (device == null)
            {
                if (activeDevices.Count >= maxDeviceCount)
                {
                    if (hasVerifiedReactivationCredentials)
                    {
                        LicenseDeviceReactivationService.TransferToVerifiedInstallation(license, machineId, maxDeviceCount, now);
                        activeDevices = license.Devices
                            .Where(x => !x.IsBlocked && !x.IsRejected)
                            .OrderBy(x => x.FirstActivatedAt)
                            .ThenBy(x => x.Id)
                            .ToList();
                    }
                    else
                    {
                    _context.LicenseSecurityLogs.Add(new NSYazilim.Web.Models.LicenseSecurityLog
                    {
                        LicenseId = license.Id,
                        LicenseKeyMasked = MaskLicenseKey(license.LicenseKey),
                        ProductCode = productCode,
                        MachineId = machineId,
                        Severity = "Warning",
                        EventType = "DeviceLimit",
                        RequestEmail = requestEmail,
                        Message = "V1 aktivasyonunda cihaz sınırı doğrulanmış e-posta olmadan aşılmak istendi.",
                        IpAddress = ipAddress,
                        CreatedAt = now
                    });

                    await _context.SaveChangesAsync();
                    return Ok(new
                    {
                        success = false,
                        active = false,
                        status = "DeviceLimit",
                        requiresAdminApproval = true,
                        primaryMachineProtected = true,
                        licenseKey = license.LicenseKey,
                        licenseType = license.LicenseType,
                        productName = license.Product?.Name ?? "NSX Veresiye Programı Pro V1.0",
                        message = "Lisans cihaz sınırı dolu. Format veya cihaz değişikliği yaptıysanız eski e-posta adresinizi de göndererek yeniden aktifleştirin."
                    });
                    }
                }

                device = new NSYazilim.Web.Models.LicenseDevice
                {
                    LicenseId = license.Id,
                    MachineId = machineId,
                    ProductCode = productCode,
                    AttemptEmail = string.IsNullOrWhiteSpace(requestEmail) ? license.User?.Email : requestEmail,
                    DeviceStatus = "Active",
                    IsRejected = false,
                    FirstIpAddress = ipAddress,
                    LastIpAddress = ipAddress,
                    AppVersion = string.IsNullOrWhiteSpace(resolvedAppVersion) ? null : resolvedAppVersion,
                    FirstActivatedAt = now,
                    LastSeenAt = now
                };
                _context.LicenseDevices.Add(device);
                license.Devices.Add(device);
            }
            else
            {
                device.ProductCode = productCode;
                device.AttemptEmail = string.IsNullOrWhiteSpace(requestEmail) ? device.AttemptEmail : requestEmail;
                device.DeviceStatus = "Active";
                device.IsRejected = false;
                device.BlockReason = null;
                if (isFreeVeresiyeLicense)
                {
                    device.AttemptEmail = license.User?.Email;
                    device.DeviceStatus = "Active";
                    device.IsRejected = false;
                    device.BlockReason = null;
                }
                device.LastIpAddress = ipAddress;
                device.LastSeenAt = now;
                if (!string.IsNullOrWhiteSpace(resolvedAppVersion))
                    device.AppVersion = resolvedAppVersion;
            }

            if (string.IsNullOrWhiteSpace(license.MachineId))
                license.MachineId = machineId;

            license.LastCheckedAt = now;
            if (!string.IsNullOrWhiteSpace(resolvedAppVersion))
                license.LastAppVersion = resolvedAppVersion;
            if (isFreeVeresiyeLicense)
            {
                license.IsActive = true;
                license.LicenseStatus = "Active";
                _context.LicenseCheckLogs.Add(new LicenseCheckLog
                {
                    LicenseId = license.Id,
                    LicenseKeyMasked = MaskLicenseKey(license.LicenseKey),
                    ProductCode = productCode,
                    RequestEmail = license.User?.Email,
                    MachineId = machineId,
                    AppVersion = string.IsNullOrWhiteSpace(resolvedAppVersion) ? null : resolvedAppVersion,
                    IpAddress = ipAddress,
                    Success = true,
                    Status = "LegacyFreeActivated",
                    Message = "Ucretsiz Veresiye eski aktivasyon endpoint'i ile cihaz eslesti.",
                    CreatedAt = now
                });
            }
            await UpdateLicenseLocationAsync(license, device, ipAddress);
            await _context.SaveChangesAsync();

            int? daysLeft = license.EndDate.HasValue
                ? Math.Max(0, (int)Math.Ceiling((license.EndDate.Value.Date - now.Date).TotalDays))
                : null;

            return Ok(new
            {
                success = true,
                active = true,
                valid = true,
                message = "Lisans aktif.",
                licenseKey = license.LicenseKey,
                licenseType = license.LicenseType,
                productName = license.Product?.Name ?? "NSX Veresiye Programı Pro V1.0",
                productCode,
                primaryMachineId = license.MachineId,
                maxDeviceCount,
                customerName = license.User?.FullName,
                customerEmail = license.User?.Email,
                machineId = license.MachineId,
                startDate = license.StartDate,
                expireDate = license.EndDate,
                endDate = license.EndDate,
                daysLeft,
                remainingDays = daysLeft,
                isLifetime = !license.EndDate.HasValue,
                isActive = true
            });
        }

        [HttpGet("Status")]
        public Task<IActionResult> Status(
            [FromQuery] string? key,
            [FromQuery] string? machineId,
            [FromQuery] string? apiKey = null,
            [FromQuery] string? email = null,
            [FromQuery] string? appVersion = null,
            [FromQuery] string? version = null)
        {
            return Task.FromResult<IActionResult>(RedirectToAction(nameof(Activate), new { key, machineId, apiKey, email, appVersion, version }));
        }

        private bool IsApiAuthorized(string? queryApiKey)
        {
            var expected = _configuration["LicenseApi:ApiKey"];


            var headerApiKey = Request.Headers["x-api-key"].FirstOrDefault();
            var incoming = !string.IsNullOrWhiteSpace(headerApiKey) ? headerApiKey : queryApiKey;
            var legacyKey = _configuration["LicenseApi:LegacyFreeVeresiyeApiKey"];
            if (!string.IsNullOrWhiteSpace(legacyKey) && string.Equals(incoming, legacyKey, StringComparison.Ordinal) && IsLegacyFreeVeresiyeLicenseRequest())
                return true;

            return !string.IsNullOrWhiteSpace(expected) && string.Equals(incoming, expected, StringComparison.Ordinal);
        }

        private bool IsLegacyFreeVeresiyeLicenseRequest()
        {
            var key = NormalizeKey(Request.Query["key"].FirstOrDefault());
            if (string.IsNullOrWhiteSpace(key))
                return false;

            return _context.Licenses.Any(x =>
                x.LicenseKey == key &&
                x.ProductCode != null &&
                x.ProductCode.ToUpper() == FreeVeresiyeProgramCode);
        }

        private string GetIpAddress()
        {
            return _clientIpService.GetClientIp(HttpContext);
        }

        private async Task UpdateLicenseLocationAsync(License license, LicenseDevice? device, string ipAddress)
        {
            var now = DateTime.Now;
            var previousLicenseIp = license.LastIpAddress;
            var previousDeviceIp = device?.LastIpAddress;

            license.LastIpAddress = ipAddress;
            if (device != null)
                device.LastIpAddress = ipAddress;

            if (string.IsNullOrWhiteSpace(ipAddress) || !_clientIpService.IsPublicIp(ipAddress))
                return;

            var shouldLookup =
                !string.Equals(previousLicenseIp, ipAddress, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previousDeviceIp, ipAddress, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(license.LastCity) ||
                license.LastGeoLookupAt == null ||
                license.LastGeoLookupAt.Value < now.AddDays(-7);

            if (!shouldLookup)
                return;

            var location = await _ipGeolocationService.ResolveAsync(ipAddress);
            if (location == null)
                return;

            license.LastCity = location.City;
            license.LastRegion = location.Region;
            license.LastCountry = location.Country;
            license.LastGeoLookupAt = now;

            if (device != null)
            {
                device.LastCity = location.City;
                device.LastRegion = location.Region;
                device.LastCountry = location.Country;
                device.LastGeoLookupAt = now;
            }

            if (license.User != null)
            {
                license.User.LastIpAddress = ipAddress;
                license.User.LastCity = location.City;
                license.User.LastRegion = location.Region;
                license.User.LastCountry = location.Country;
                license.User.LastGeoLookupAt = now;
                license.User.LastLoginAt = now;
            }
        }

        private static string NormalizeStatus(string? status)
        {
            status = (status ?? "Active").Trim();
            if (string.Equals(status, "Askida", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase)) return "Suspended";
            if (string.Equals(status, "Iptal", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Revoked", StringComparison.OrdinalIgnoreCase)) return "Revoked";
            if (string.Equals(status, "Engelli", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase)) return "Blocked";
            return "Active";
        }

        private static string NormalizeProductCode(string? value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private static string MaskLicenseKey(string? key)
        {
            key = (key ?? string.Empty).Trim().ToUpperInvariant();
            if (key.Length <= 8) return key;
            return key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
        }

        private static string NormalizeKey(string? key)
        {
            return (key ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeEmail(string? email)
        {
            return (email ?? string.Empty).Trim().ToLowerInvariant();
        }
    }
}
