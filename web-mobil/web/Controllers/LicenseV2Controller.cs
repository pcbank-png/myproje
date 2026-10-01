using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.Controllers
{
    [ApiController]
    [Route("api/license/v2")]
    public class LicenseV2Controller : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly OfflineLicenseService _offlineLicenseService;
        private readonly IWebHostEnvironment _environment;
        private readonly ClientIpService _clientIpService;
        private readonly IpGeolocationService _ipGeolocationService;

        public LicenseV2Controller(ApplicationDbContext context, IConfiguration configuration, OfflineLicenseService offlineLicenseService, IWebHostEnvironment environment, ClientIpService clientIpService, IpGeolocationService ipGeolocationService)
        {
            _context = context;
            _configuration = configuration;
            _offlineLicenseService = offlineLicenseService;
            _environment = environment;
            _clientIpService = clientIpService;
            _ipGeolocationService = ipGeolocationService;
        }

        [HttpPost("activate")]
        public Task<IActionResult> Activate([FromBody] LicenseV2Request request, [FromQuery] string? apiKey = null)
        {
            return ProcessLicenseRequest(request, apiKey, false, allowVerifiedDeviceReactivation: true);
        }

        [HttpPost("check")]
        public Task<IActionResult> Check([FromBody] LicenseV2Request request, [FromQuery] string? apiKey = null)
        {
            return ProcessLicenseRequest(request, apiKey, false, allowVerifiedDeviceReactivation: false);
        }

        [HttpPost("offline-certificate")]
        public Task<IActionResult> OfflineCertificate([FromBody] LicenseV2Request request, [FromQuery] string? apiKey = null)
        {
            return ProcessLicenseRequest(request, apiKey, true, allowVerifiedDeviceReactivation: false);
        }

        [HttpPost("report-security")]
        public async Task<IActionResult> ReportSecurity([FromBody] LicenseSecurityReportRequest request, [FromQuery] string? apiKey = null)
        {
            request.AppVersion = AppVersionResolver.Resolve(HttpContext, request.AppVersion);

            if (!IsApiAuthorized(apiKey))
                return Unauthorized(new { success = false, allowed = false, message = "API anahtarı geçersiz." });

            var key = NormalizeLicenseKey(request.LicenseKey);
            var productCode = NormalizeProductCode(request.ProductCode);
            var requestEmail = NormalizeEmail(request.Email ?? request.CustomerEmail);
            var machineId = (request.MachineId ?? string.Empty).Trim();
            var ipAddress = GetIpAddress();

            var license = string.IsNullOrWhiteSpace(key)
                ? null
                : await _context.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key);

            _context.LicenseSecurityLogs.Add(new LicenseSecurityLog
            {
                LicenseId = license?.Id,
                LicenseKeyMasked = MaskLicenseKey(key),
                ProductCode = productCode,
                RequestEmail = requestEmail,
                MachineId = machineId,
                Severity = string.IsNullOrWhiteSpace(request.Severity) ? "Warning" : request.Severity.Trim(),
                EventType = string.IsNullOrWhiteSpace(request.EventType) ? "Unknown" : request.EventType.Trim(),
                Message = string.IsNullOrWhiteSpace(request.Message) ? "Program güvenlik bildirimi gönderdi." : request.Message.Trim(),
                AppVersion = TrimTo(request.AppVersion, 50),
                IpAddress = ipAddress,
                CreatedAt = DateTime.Now
            });

            await _context.SaveChangesAsync();
            return Ok(new { success = true, message = "Güvenlik bildirimi kaydedildi." });
        }

        [HttpGet("public-key")]
        public IActionResult PublicKey()
        {
            return Ok(new
            {
                success = true,
                algorithm = "RS256",
                publicKeyPem = _offlineLicenseService.GetPublicKeyPem()
            });
        }

        private async Task<IActionResult> ProcessLicenseRequest(
            LicenseV2Request request,
            string? apiKey,
            bool includeOfflineCertificate,
            bool allowVerifiedDeviceReactivation)
        {
            if (!IsApiAuthorized(apiKey))
                return Unauthorized(new { success = false, allowed = false, message = "API anahtarı geçersiz." });

            request.AppVersion = AppVersionResolver.Resolve(HttpContext, request.AppVersion);

            var now = DateTime.Now;
            var key = NormalizeLicenseKey(request.LicenseKey);
            var requestEmail = NormalizeEmail(request.Email ?? request.CustomerEmail);
            var productCode = NormalizeProductCode(request.ProductCode);
            var machineId = (request.MachineId ?? string.Empty).Trim();
            var ipAddress = GetIpAddress();

            if (string.IsNullOrWhiteSpace(requestEmail))
                return await Deny(null, key, requestEmail, productCode, machineId, request, "EmailRequired", "E-posta boş olamaz.");

            if (string.IsNullOrWhiteSpace(key))
                return await Deny(null, key, requestEmail, productCode, machineId, request, "InvalidRequest", "Lisans anahtarı boş olamaz.");

            if (string.IsNullOrWhiteSpace(productCode))
                return await Deny(null, key, requestEmail, productCode, machineId, request, "InvalidRequest", "ProductCode boş olamaz.");

            if (string.IsNullOrWhiteSpace(machineId))
                return await Deny(null, key, requestEmail, productCode, machineId, request, "InvalidRequest", "MachineId boş olamaz.");

            var license = await _context.Licenses
                .Include(x => x.Product)
                .Include(x => x.User)
                .Include(x => x.Devices)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.LicenseKey == key);

            if (license == null)
                return await Deny(null, key, requestEmail, productCode, machineId, request, "NotFound", "Lisans bulunamadı.");

            if (LicenseTermService.NormalizeTerm(license, now))
                await _context.SaveChangesAsync();

            var licenseEmail = NormalizeEmail(license.User?.Email);
            if (string.IsNullOrWhiteSpace(licenseEmail) || !string.Equals(licenseEmail, requestEmail, StringComparison.OrdinalIgnoreCase))
            {
                await AddSecurityLog(license, key, requestEmail, productCode, machineId, request, "EmailMismatch", "Warning", "Lisans anahtarı farklı e-posta ile kullanılmak istendi.");
                return await Deny(license, key, requestEmail, productCode, machineId, request, "EmailMismatch", "E-posta ile lisans anahtarı eşleşmiyor.");
            }

            var status = NormalizeStatus(license.LicenseStatus);
            if (!license.IsActive || status is "Suspended" or "Revoked" or "Blocked")
            {
                var message = status switch
                {
                    "Suspended" => "Lisans askıya alınmış.",
                    "Revoked" => "Lisans iptal edilmiş.",
                    "Blocked" => "Lisans engellenmiş.",
                    _ => "Lisans pasif durumda."
                };

                return await Deny(license, key, requestEmail, productCode, machineId, request, status, message);
            }

            if (license.EndDate.HasValue && license.EndDate.Value.Date < now.Date)
                return await Deny(license, key, requestEmail, productCode, machineId, request, "Expired", "Lisans süresi dolmuş.");

            var globalDeviceBlock = await _context.BlockedDevices.AnyAsync(x =>
                x.IsActive &&
                x.MachineId == machineId &&
                (x.ProductCode == null || x.ProductCode == "" || x.ProductCode == productCode));

            if (globalDeviceBlock)
                return await Deny(license, key, requestEmail, productCode, machineId, request, "DeviceBlocked", "Bu cihaz engellenmiş.");

            var expectedProductCode = ResolveExpectedProductCode(license);
            if (string.IsNullOrWhiteSpace(expectedProductCode))
            {
                expectedProductCode = productCode;
                license.ProductCode = productCode;
            }
            else if (!string.Equals(expectedProductCode, productCode, StringComparison.OrdinalIgnoreCase))
            {
                await AddSecurityLog(license, key, requestEmail, productCode, machineId, request, "ProductMismatch", "Warning", $"Lisans farklı ürün koduyla kullanılmak istendi. Beklenen: {expectedProductCode}");
                return await Deny(license, key, requestEmail, productCode, machineId, request, "ProductMismatch", "Bu lisans bu ürün için geçerli değil.");
            }
            else if (string.IsNullOrWhiteSpace(license.ProductCode))
            {
                license.ProductCode = expectedProductCode;
            }

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
                if (!allowVerifiedDeviceReactivation)
                {
                    await TrackRejectedDevice(license, requestEmail, productCode, machineId, request, ipAddress, "DifferentDevice", "İlk/ana cihaz korunuyor. Yeniden aktivasyon için programın lisans ekranından e-posta ve lisans anahtarı girilmelidir.");
                    await AddSecurityLog(
                        license,
                        key,
                        requestEmail,
                        productCode,
                        machineId,
                        request,
                        "DeviceRejectedPrimaryLocked",
                        "Warning",
                        $"Tek cihaz lisansı farklı cihazda kontrol edildi. Ana cihaz: {MaskMachineId(primaryMachineId)}");

                    return await Deny(
                        license,
                        key,
                        requestEmail,
                        productCode,
                        machineId,
                        request,
                        "DifferentDevice",
                        "Bu lisans başka bir cihazda aktif. Format veya cihaz değişikliği yaptıysanız lisans ekranından eski e-posta ve lisans anahtarınızla yeniden aktifleştirin.");
                }

                LicenseDeviceReactivationService.TransferToVerifiedInstallation(license, machineId, maxDeviceCount, now);
                primaryMachineId = machineId;
                activeDevices = license.Devices
                    .Where(x => !x.IsBlocked && !x.IsRejected)
                    .OrderBy(x => x.FirstActivatedAt)
                    .ThenBy(x => x.Id)
                    .ToList();

                await AddSecurityLog(
                    license,
                    key,
                    requestEmail,
                    productCode,
                    machineId,
                    request,
                    "VerifiedDeviceReactivation",
                    "Information",
                    "Doğrulanmış e-posta ve lisans anahtarıyla format/cihaz sonrası yeniden aktivasyon yapıldı.");
            }

            var device = license.Devices.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));

            if (device != null && device.IsBlocked)
                return await Deny(license, key, requestEmail, productCode, machineId, request, "DeviceBlocked", "Bu lisansa bağlı cihaz engellenmiş.");

            if (device == null)
            {
                var activeDeviceCount = activeDevices.Count;

                if (activeDeviceCount >= maxDeviceCount)
                {
                    if (!allowVerifiedDeviceReactivation)
                    {
                        await TrackRejectedDevice(license, requestEmail, productCode, machineId, request, ipAddress, "DeviceLimit", "Lisans cihaz sınırı dolu. Yeniden aktivasyon için programın lisans ekranı kullanılmalıdır.");
                        await AddSecurityLog(license, key, requestEmail, productCode, machineId, request, "DeviceLimit", "Warning", "Lisans cihaz sınırı doluyken yeni cihaz kontrol edildi.");
                        return await Deny(license, key, requestEmail, productCode, machineId, request, "DeviceLimit", "Lisans cihaz sınırı dolu. Format veya cihaz değişikliği yaptıysanız eski e-posta ve lisans anahtarınızla yeniden aktifleştirin.");
                    }

                    LicenseDeviceReactivationService.TransferToVerifiedInstallation(license, machineId, maxDeviceCount, now);
                    activeDevices = license.Devices
                        .Where(x => !x.IsBlocked && !x.IsRejected)
                        .OrderBy(x => x.FirstActivatedAt)
                        .ThenBy(x => x.Id)
                        .ToList();

                    await AddSecurityLog(
                        license,
                        key,
                        requestEmail,
                        productCode,
                        machineId,
                        request,
                        "VerifiedDeviceReactivation",
                        "Information",
                        "Doğrulanmış e-posta ve lisans anahtarıyla dolu cihaz yuvası yeni kuruluma aktarıldı.");
                }

                device = new LicenseDevice
                {
                    LicenseId = license.Id,
                    MachineId = machineId,
                    DeviceName = TrimTo(request.DeviceName, 120),
                    OsVersion = TrimTo(request.OsVersion, 120),
                    AppVersion = TrimTo(request.AppVersion, 50),
                    ProductCode = productCode,
                    AttemptEmail = requestEmail,
                    DeviceStatus = "Active",
                    FirstIpAddress = ipAddress,
                    LastIpAddress = ipAddress,
                    IsBlocked = false,
                    IsRejected = false,
                    BlockReason = null,
                    FirstActivatedAt = now,
                    LastSeenAt = now
                };
                _context.LicenseDevices.Add(device);
                license.Devices.Add(device);

                if (string.IsNullOrWhiteSpace(license.MachineId))
                    license.MachineId = machineId;
            }
            else
            {
                device.DeviceName = TrimTo(request.DeviceName, 120) ?? device.DeviceName;
                device.OsVersion = TrimTo(request.OsVersion, 120) ?? device.OsVersion;
                device.AppVersion = TrimTo(request.AppVersion, 50) ?? device.AppVersion;
                device.ProductCode = productCode;
                device.AttemptEmail = requestEmail;
                device.DeviceStatus = "Active";
                device.IsRejected = false;
                device.BlockReason = null;
                device.LastIpAddress = ipAddress;
                device.LastSeenAt = now;

                if (string.IsNullOrWhiteSpace(license.MachineId))
                    license.MachineId = machineId;
            }

            license.LastCheckedAt = now;
            var resolvedAppVersion = TrimTo(request.AppVersion, 50);
            if (!string.IsNullOrWhiteSpace(resolvedAppVersion))
                license.LastAppVersion = resolvedAppVersion;
            license.LicenseStatus = "Active";
            await UpdateLicenseLocationAsync(license, device, ipAddress);

            await AddCheckLog(license, key, requestEmail, productCode, machineId, request, true, "OnlineVerified", "Lisans online doğrulandı.");

            object? offlineCertificate = null;
            if (includeOfflineCertificate && license.OfflineAllowed)
            {
                offlineCertificate = await GetOrCreateOfflineCertificate(license, productCode, machineId);
            }

            var daysLeft = license.EndDate.HasValue
                ? Math.Max(0, (int)Math.Ceiling((license.EndDate.Value.Date - now.Date).TotalDays))
                : (int?)null;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = true,
                allowed = true,
                active = true,
                valid = true,
                status = "OnlineVerified",
                licenseStatus = "Active",
                activationMode = "OnlineVerified",
                message = "Lisans online doğrulandı.",
                licenseKey = license.LicenseKey,
                licenseType = license.LicenseType,
                productCode,
                productName = license.Product?.Name ?? "NSX Yazılım Ürünü",
                customerName = license.User?.FullName,
                customerEmail = license.User?.Email,
                requestEmail,
                machineId,
                primaryMachineId = license.MachineId,
                maxDeviceCount,
                startDate = license.StartDate,
                endDate = license.EndDate,
                expireDate = license.EndDate,
                daysLeft,
                remainingDays = daysLeft,
                isLifetime = !license.EndDate.HasValue,
                offlineAllowed = license.OfflineAllowed,
                serverTime = now,
                publicKeyPem = _offlineLicenseService.GetPublicKeyPem(),
                offlineLicense = offlineCertificate
            });
        }

        private async Task<object> GetOrCreateOfflineCertificate(License license, string productCode, string machineId)
        {
            var certificate = license.OfflineCertificates
                .Where(x => !x.IsRevoked &&
                            string.Equals(x.ProductCode, productCode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.MachineId, machineId, StringComparison.Ordinal))
                .OrderByDescending(x => x.Id)
                .FirstOrDefault();

            if (certificate == null)
            {
                var created = _offlineLicenseService.CreateCertificate(license, productCode, machineId);
                certificate = new OfflineLicenseCertificate
                {
                    LicenseId = license.Id,
                    CertificateId = created.CertificateId,
                    ProductCode = productCode,
                    MachineId = machineId,
                    PayloadJson = created.PayloadJson,
                    Signature = created.Signature,
                    OfflineCode = created.OfflineCode,
                    IssuedAt = DateTime.Now,
                    ExpiresAt = license.EndDate
                };

                _context.OfflineLicenseCertificates.Add(certificate);
                license.OfflineCertificates.Add(certificate);
            }

            return new
            {
                certificateId = certificate.CertificateId,
                fileName = $"{productCode}-{certificate.CertificateId}.nsxlic",
                offlineCode = certificate.OfflineCode,
                payloadJson = certificate.PayloadJson,
                signature = certificate.Signature,
                issuedAt = certificate.IssuedAt,
                expiresAt = certificate.ExpiresAt,
                isLifetime = !license.EndDate.HasValue
            };
        }

        private async Task<IActionResult> Deny(License? license, string key, string requestEmail, string productCode, string machineId, LicenseV2Request request, string status, string message)
        {
            await AddCheckLog(license, key, requestEmail, productCode, machineId, request, false, status, message);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                success = false,
                allowed = false,
                active = false,
                valid = false,
                status,
                activationMode = "OnlineRejected",
                message,
                requiresAdminApproval = status is "DifferentDevice" or "DeviceLimit",
                primaryMachineProtected = status is "DifferentDevice" or "DeviceLimit",
                licenseKey = string.IsNullOrWhiteSpace(key) ? null : key,
                requestEmail,
                productCode,
                machineId,
                serverTime = DateTime.Now
            });
        }

        private async Task AddCheckLog(License? license, string key, string requestEmail, string productCode, string machineId, LicenseV2Request request, bool success, string status, string message)
        {
            var now = DateTime.Now;
            var ipAddress = GetIpAddress();

            // Başarılı online doğrulama program her açıldığında tekrar gelebilir.
            // Admin panel logları şişmesin diye aynı lisans + aynı cihaz + aynı ürün için
            // son 30 dakika içindeki OnlineVerified kaydını çoğaltmak yerine güncelliyoruz.
            if (success &&
                string.Equals(status, "OnlineVerified", StringComparison.OrdinalIgnoreCase) &&
                license?.Id > 0 &&
                !string.IsNullOrWhiteSpace(machineId))
            {
                var recentSuccessLog = await _context.LicenseCheckLogs
                    .Where(x => x.LicenseId == license.Id &&
                                x.Success &&
                                x.Status == status &&
                                x.MachineId == machineId &&
                                x.ProductCode == productCode &&
                                x.CreatedAt >= now.AddMinutes(-30))
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync();

                if (recentSuccessLog != null)
                {
                    recentSuccessLog.RequestEmail = requestEmail;
                    recentSuccessLog.DeviceName = TrimTo(request.DeviceName, 120);
                    recentSuccessLog.OsVersion = TrimTo(request.OsVersion, 120);
                    var resolvedLogVersion = TrimTo(request.AppVersion, 50);
                    if (!string.IsNullOrWhiteSpace(resolvedLogVersion))
                        recentSuccessLog.AppVersion = resolvedLogVersion;
                    recentSuccessLog.IpAddress = ipAddress;
                    recentSuccessLog.Message = message;
                    recentSuccessLog.CreatedAt = now;
                    return;
                }
            }

            _context.LicenseCheckLogs.Add(new LicenseCheckLog
            {
                LicenseId = license?.Id,
                LicenseKeyMasked = MaskLicenseKey(key),
                ProductCode = productCode,
                RequestEmail = requestEmail,
                MachineId = machineId,
                DeviceName = TrimTo(request.DeviceName, 120),
                OsVersion = TrimTo(request.OsVersion, 120),
                AppVersion = TrimTo(request.AppVersion, 50),
                IpAddress = ipAddress,
                Success = success,
                Status = status,
                Message = message,
                CreatedAt = now
            });
        }

        private Task AddSecurityLog(License? license, string key, string requestEmail, string productCode, string machineId, LicenseV2Request request, string eventType, string severity, string message)
        {
            _context.LicenseSecurityLogs.Add(new LicenseSecurityLog
            {
                LicenseId = license?.Id,
                LicenseKeyMasked = MaskLicenseKey(key),
                ProductCode = productCode,
                RequestEmail = requestEmail,
                MachineId = machineId,
                Severity = severity,
                EventType = eventType,
                Message = message,
                AppVersion = TrimTo(request.AppVersion, 50),
                IpAddress = GetIpAddress(),
                CreatedAt = DateTime.Now
            });

            return Task.CompletedTask;
        }

        private Task TrackRejectedDevice(License license, string requestEmail, string productCode, string machineId, LicenseV2Request request, string ipAddress, string status, string reason)
        {
            var now = DateTime.Now;
            var device = license.Devices.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
            if (device == null)
            {
                device = new LicenseDevice
                {
                    LicenseId = license.Id,
                    MachineId = machineId,
                    FirstIpAddress = ipAddress,
                    FirstActivatedAt = now
                };
                _context.LicenseDevices.Add(device);
                license.Devices.Add(device);
            }

            device.DeviceName = TrimTo(request.DeviceName, 120) ?? device.DeviceName;
            device.OsVersion = TrimTo(request.OsVersion, 120) ?? device.OsVersion;
            device.AppVersion = TrimTo(request.AppVersion, 50) ?? device.AppVersion;
            device.ProductCode = productCode;
            device.AttemptEmail = requestEmail;
            device.DeviceStatus = status;
            device.LastIpAddress = ipAddress;
            device.LastSeenAt = now;
            device.IsBlocked = true;
            device.IsRejected = true;
            device.BlockReason = reason;

            return Task.CompletedTask;
        }

        private static string ResolveExpectedProductCode(License license)
        {
            var code = NormalizeProductCode(license.ProductCode);
            if (!string.IsNullOrWhiteSpace(code)) return code;

            code = NormalizeProductCode(license.Product?.ProductCode);
            if (!string.IsNullOrWhiteSpace(code)) return code;

            code = NormalizeProductCode(license.Product?.Slug);
            if (!string.IsNullOrWhiteSpace(code)) return code;

            return NormalizeProductCode(license.Product?.Name);
        }

        private bool IsApiAuthorized(string? queryApiKey)
        {
            var expected = (_configuration["LicenseApi:ApiKey"] ?? string.Empty).Trim();

            // Canlı ortamda varsayılan/boş API anahtarını kabul etme.
            // Programlar V2 API çağrılarında x-api-key header göndermeli.
            if (!_environment.IsDevelopment() &&
                (string.IsNullOrWhiteSpace(expected) || string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected))), "527BC91276A939D9121819124D79FC6CD0CDB78BF985D16777CBE917BFF958B9", StringComparison.Ordinal)))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(expected)) return false;

            var headerApiKey = Request.Headers["x-api-key"].FirstOrDefault();
            var allowQueryApiKey = string.Equals(_configuration["LicenseApi:AllowQueryApiKey"], "true", StringComparison.OrdinalIgnoreCase);
            var incoming = !string.IsNullOrWhiteSpace(headerApiKey)
                ? headerApiKey
                : (allowQueryApiKey ? queryApiKey : null);

            return string.Equals(incoming, expected, StringComparison.Ordinal);
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

        private static string NormalizeLicenseKey(string? key)
        {
            return (key ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string NormalizeProductCode(string? value)
        {
            return new string((value ?? string.Empty)
                .Trim()
                .ToUpperInvariant()
                .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
                .ToArray());
        }

        private static string NormalizeEmail(string? value)
        {
            return (value ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string NormalizeStatus(string? status)
        {
            status = (status ?? "Active").Trim();
            if (string.Equals(status, "Askida", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase)) return "Suspended";
            if (string.Equals(status, "Iptal", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Revoked", StringComparison.OrdinalIgnoreCase)) return "Revoked";
            if (string.Equals(status, "Engelli", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase)) return "Blocked";
            return "Active";
        }

        private static string? TrimTo(string? value, int maxLength)
        {
            value = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value)) return null;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static string MaskMachineId(string? machineId)
        {
            machineId = (machineId ?? string.Empty).Trim();
            if (machineId.Length <= 12) return machineId;
            return machineId.Substring(0, 6) + "****" + machineId.Substring(machineId.Length - 6);
        }

        private static string MaskLicenseKey(string? key)
        {
            key = (key ?? string.Empty).Trim().ToUpperInvariant();
            if (key.Length <= 8) return key;
            return key.Substring(0, 4) + "****" + key.Substring(key.Length - 4);
        }
    }

    public class LicenseV2Request
    {
        public string? LicenseKey { get; set; }
        public string? Email { get; set; }
        public string? CustomerEmail { get; set; }
        public string? ProductCode { get; set; }
        public string? MachineId { get; set; }
        public string? DeviceName { get; set; }
        public string? AppVersion { get; set; }
        public string? OsVersion { get; set; }
        public string? SecurityHash { get; set; }
    }

    public class LicenseSecurityReportRequest
    {
        public string? LicenseKey { get; set; }
        public string? Email { get; set; }
        public string? CustomerEmail { get; set; }
        public string? ProductCode { get; set; }
        public string? MachineId { get; set; }
        public string? AppVersion { get; set; }
        public string? Severity { get; set; }
        public string? EventType { get; set; }
        public string? Message { get; set; }
    }
}
