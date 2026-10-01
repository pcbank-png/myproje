using System.Net;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Services;
using NSYazilim.Web.VeresiyeFreeCloud.Models;
using NSYazilim.Web.VeresiyeFreeCloud.Services;

namespace NSYazilim.Web.VeresiyeFreeCloud;

public static class VeresiyeFreeCloudEndpointExtensions
{
    private const string ProductCode = "NSXVERESIYETAKIPPROFREE";
    private const string MobileCookie = "NSX.VF.Cloud.Session";

    public static IServiceCollection AddNsxVeresiyeFreeCloud(this IServiceCollection services)
    {
        services.AddScoped<VeresiyeCloudMySqlStore>();
        return services;
    }

    public static void MapNsxVeresiyeFreeCloud(this WebApplication app)
    {
        app.MapGet("/api/veresiyefreecloud/health", () => Results.Ok(new
        {
            success = true,
            service = "NSX Veresiye Free Cloud",
            productCode = ProductCode,
            utc = DateTime.UtcNow
        }));

        app.MapPost("/api/veresiyefreecloud/setup/tenant", async Task<IResult> (
            VeresiyeTenantSetupRequest request,
            ApplicationDbContext db,
            VeresiyeCloudMySqlStore store,
            HttpContext http) =>
        {
            request ??= new VeresiyeTenantSetupRequest();
            var licenseKey = (request.LicenseKey ?? string.Empty).Trim().ToUpperInvariant();
            var machineId = (request.MachineId ?? string.Empty).Trim();
            var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
            var incomingProduct = (request.ProductCode ?? string.Empty).Trim().ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(licenseKey) || string.IsNullOrWhiteSpace(machineId))
                return Results.BadRequest(new { success = false, message = "Cloud bağlantısı için lisans ve cihaz bilgisi gereklidir." });
            if (!string.Equals(incomingProduct, ProductCode, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { success = false, message = "Bu Cloud alanı yalnızca NSX Veresiye Free içindir." });

            var license = await db.Licenses
                .Include(x => x.User)
                .Include(x => x.Product)
                .Include(x => x.Devices)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.LicenseKey == licenseKey);

            if (license is null)
                return Results.NotFound(new { success = false, message = "Lisans bulunamadı. Önce masaüstü programda ücretsiz lisans eşleştirmesini tamamlayın." });

            var now = DateTime.Now;
            if (LicenseTermService.NormalizeTerm(license, now)) await db.SaveChangesAsync();
            var storedProduct = (license.ProductCode ?? license.Product?.ProductCode ?? string.Empty).Trim().ToUpperInvariant();
            var status = (license.LicenseStatus ?? string.Empty).Trim();
            if (!license.IsActive || !string.Equals(storedProduct, ProductCode, StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Suspended", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Revoked", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ||
                (license.EndDate.HasValue && license.EndDate.Value.Date < now.Date))
            {
                return Results.Json(new { success = false, message = "Lisans Cloud kullanımı için aktif değil." }, statusCode: StatusCodes.Status403Forbidden);
            }

            var licenseEmail = (license.User?.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(licenseEmail) && !string.Equals(licenseEmail, email, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { success = false, message = "Lisans e-posta eşleşmesi doğrulanamadı." }, statusCode: StatusCodes.Status403Forbidden);

            var primaryMachineId = (license.MachineId ?? string.Empty).Trim();
            var matchingDevice = license.Devices.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
            if (matchingDevice?.IsBlocked == true)
                return Results.Json(new { success = false, message = "Bu cihaz lisans için engellenmiş." }, statusCode: StatusCodes.Status403Forbidden);

            var deviceAllowed =
                (string.IsNullOrWhiteSpace(primaryMachineId) || string.Equals(primaryMachineId, machineId, StringComparison.Ordinal)) &&
                (matchingDevice is null || !matchingDevice.IsRejected);

            if (!deviceAllowed && matchingDevice is not null && !matchingDevice.IsBlocked && !matchingDevice.IsRejected)
                deviceAllowed = true;

            if (!deviceAllowed)
            {
                var emailVerified = !string.IsNullOrWhiteSpace(licenseEmail) &&
                                    string.Equals(licenseEmail, email, StringComparison.OrdinalIgnoreCase);
                if (!emailVerified)
                    return Results.Json(new { success = false, message = "Bu cihaz lisansın aktif cihazı değil." }, statusCode: StatusCodes.Status403Forbidden);

                var maxDeviceCount = license.MaxDeviceCount <= 0 ? 1 : license.MaxDeviceCount;
                var activeDevices = license.Devices
                    .Where(x => !x.IsBlocked && !x.IsRejected)
                    .OrderBy(x => x.FirstActivatedAt)
                    .ThenBy(x => x.Id)
                    .ToList();

                if (maxDeviceCount <= 1 || activeDevices.Count >= maxDeviceCount)
                    LicenseDeviceReactivationService.TransferToVerifiedInstallation(license, machineId, maxDeviceCount, now);

                matchingDevice = license.Devices.FirstOrDefault(x => string.Equals(x.MachineId, machineId, StringComparison.Ordinal));
                if (matchingDevice is null)
                {
                    matchingDevice = new NSYazilim.Web.Models.LicenseDevice
                    {
                        LicenseId = license.Id,
                        MachineId = machineId,
                        ProductCode = storedProduct,
                        AttemptEmail = email,
                        DeviceStatus = "Active",
                        IsBlocked = false,
                        IsRejected = false,
                        FirstActivatedAt = now,
                        LastSeenAt = now
                    };
                    db.LicenseDevices.Add(matchingDevice);
                    license.Devices.Add(matchingDevice);
                }
                else
                {
                    matchingDevice.ProductCode = storedProduct;
                    matchingDevice.AttemptEmail = email;
                    matchingDevice.DeviceStatus = "Active";
                    matchingDevice.IsRejected = false;
                    matchingDevice.BlockReason = null;
                    matchingDevice.LastSeenAt = now;
                }

                if (maxDeviceCount <= 1 || string.IsNullOrWhiteSpace(license.MachineId))
                    license.MachineId = machineId;

                license.LastCheckedAt = now;
                license.LicenseStatus = "Active";
                await db.SaveChangesAsync();
                deviceAllowed = true;
            }
            else if (matchingDevice is not null)
            {
                matchingDevice.LastSeenAt = now;
                matchingDevice.DeviceStatus = "Active";
                license.LastCheckedAt = now;
                await db.SaveChangesAsync();
            }

            var companyName = string.IsNullOrWhiteSpace(request.CompanyName)
                ? (license.User?.FullName ?? "NSX Veresiye İşletmesi")
                : request.CompanyName.Trim();
            var result = store.CreateOrRefreshTenant(license.Id, license.LicenseKey, companyName, machineId, request.CurrentApiToken);
            var baseUrl = GetBaseUrl(http);
            return Results.Ok(new VeresiyeTenantSetupResponse
            {
                Success = true,
                TenantId = result.Tenant.TenantId,
                CompanyName = result.Tenant.CompanyName,
                ApiToken = result.ApiToken,
                ApiBaseUrl = baseUrl + "/api/veresiyefreecloud/v1",
                PanelUrl = baseUrl + "/VeresiyeFreeCloud",
                Message = "NSX Veresiye Free Cloud bağlantısı hazır."
            });
        });

        var desktop = app.MapGroup("/api/veresiyefreecloud/v1");

        desktop.MapGet("/tenant/me", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            return tenant is null ? Results.Unauthorized() : Results.Ok(new { success = true, tenant.TenantId, tenant.CompanyName, tenant.LicenseId });
        });

        desktop.MapPost("/qr/create", IResult (VeresiyeQrCreateRequest request, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            var qr = store.CreateQrLogin(tenant.TenantId, request?.SourceDeviceId ?? "", request?.SourceUser ?? "");
            var loginUrl = GetBaseUrl(http) + "/VeresiyeFreeCloud/giris/" + WebUtility.UrlEncode(qr.Token);
            return Results.Ok(new VeresiyeQrCreateResponse
            {
                Success = true, LoginUrl = loginUrl, QrValue = loginUrl, ExpiresAt = qr.ExpiresAt.ToString("O"),
                ExpiresInSeconds = Math.Max(0, (int)(qr.ExpiresAt - DateTime.Now).TotalSeconds),
                Message = "QR kodu 3 dakika geçerlidir ve yalnızca bir kez kullanılabilir."
            });
        });

        desktop.MapPost("/sync/snapshot", IResult (VeresiyeSnapshotRequest request, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            try { return Results.Ok(store.ApplySnapshot(tenant.TenantId, request)); }
            catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
        });

        desktop.MapGet("/sync/changes", IResult (long? afterId, int? take, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            return Results.Ok(new
            {
                success = true,
                items = store.GetChanges(tenant.TenantId, Math.Max(0, afterId ?? 0), take ?? 500),
                lastChangeId = store.GetLastChangeId(tenant.TenantId)
            });
        });

        desktop.MapGet("/sync/status", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            return Results.Ok(new { success = true, tenant.TenantId, tenant.CompanyName, lastChangeId = store.GetLastChangeId(tenant.TenantId), counts = store.GetEntityCounts(tenant.TenantId) });
        });

        desktop.MapPost("/mobile/revoke-all", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            store.RevokeAllMobileSessions(tenant.TenantId); return Results.Ok(new { success = true, message = "Tüm telefon oturumları kapatıldı." });
        });

        desktop.MapGet("/data/{entityType}", IResult (string entityType, int? take, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store); if (tenant is null) return Results.Unauthorized();
            try { return Results.Ok(store.GetDataItems(tenant.TenantId, entityType, take ?? 10000)); }
            catch (Exception ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        app.MapGet("/VeresiyeFreeCloud", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store);
            return session is not null ? Results.Redirect("/VeresiyeFreeCloud/panel") : Results.Content(VeresiyeFreeCloudHtml.Landing(), "text/html; charset=utf-8");
        });

        app.MapGet("/VeresiyeFreeCloud/giris/{token}", IResult (string token, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var tenant = store.ConsumeQrLogin(token);
            if (tenant is null) return Results.Content(VeresiyeFreeCloudHtml.InvalidQr(), "text/html; charset=utf-8", statusCode: StatusCodes.Status401Unauthorized);
            var mobile = store.CreateMobileSession(tenant.TenantId);
            http.Response.Cookies.Append(MobileCookie, mobile.SessionToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = new DateTimeOffset(mobile.ExpiresAt),
                Path = "/"
            });
            http.Response.Cookies.Append("NSX.VF.Cloud.Csrf", mobile.CsrfToken, new CookieOptions
            {
                HttpOnly = false,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Expires = new DateTimeOffset(mobile.ExpiresAt),
                Path = "/"
            });
            return Results.Redirect("/VeresiyeFreeCloud/panel");
        });

        app.MapGet("/VeresiyeFreeCloud/panel", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store);
            return session is null ? Results.Redirect("/VeresiyeFreeCloud") : Results.Content(VeresiyeFreeCloudHtml.Panel(), "text/html; charset=utf-8");
        });

        app.MapPost("/VeresiyeFreeCloud/cikis", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var raw = ReadCookie(http); if (!string.IsNullOrWhiteSpace(raw)) store.RevokeMobileSession(raw);
            http.Response.Cookies.Delete(MobileCookie, new CookieOptions { Path = "/" });
            http.Response.Cookies.Delete("NSX.VF.Cloud.Csrf", new CookieOptions { Path = "/" });
            return Results.Ok(new { success = true });
        });

        var mobileApi = app.MapGroup("/api/veresiyefreecloud/mobile");

        mobileApi.MapGet("/bootstrap", IResult (HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store); if (session is null) return Results.Unauthorized();
            var csrf = EnsureCsrfToken(http, session, store);
            return Results.Ok(new
            {
                success = true,
                companyName = session.CompanyName,
                csrfToken = csrf,
                lastChangeId = store.GetLastChangeId(session.TenantId),
                companies = store.GetDataItems(session.TenantId, "company"),
                customers = store.GetDataItems(session.TenantId, "customer"),
                debts = store.GetDataItems(session.TenantId, "debt"),
                payments = store.GetDataItems(session.TenantId, "payment")
            });
        });

        mobileApi.MapGet("/sync/status", IResult (long? afterId, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store); if (session is null) return Results.Unauthorized();
            var current = store.GetLastChangeId(session.TenantId);
            return Results.Ok(new { success = true, lastChangeId = current, hasChanges = current > Math.Max(0, afterId ?? 0) });
        });

        mobileApi.MapPost("/data/{entityType}/{entityId}", IResult (string entityType, string entityId, VeresiyeEntityUpsertRequest request, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store); if (session is null) return Results.Unauthorized();
            if (!ValidateMobileMutation(http, session, store)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try
            {
                request ??= new VeresiyeEntityUpsertRequest(); request.SourceDeviceId = "MOBILE-WEB"; request.SourceUser = "Mobil Panel";
                var changeId = store.UpsertDataItem(session.TenantId, entityType, entityId, request, appendChange: true);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
        });

        mobileApi.MapDelete("/data/{entityType}/{entityId}", IResult (string entityType, string entityId, HttpContext http, VeresiyeCloudMySqlStore store) =>
        {
            var session = ResolveMobileSession(http, store); if (session is null) return Results.Unauthorized();
            if (!ValidateMobileMutation(http, session, store)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { var changeId = store.DeleteDataItem(session.TenantId, entityType, entityId, "MOBILE-WEB", "Mobil Panel", appendChange: true); return Results.Ok(new { success = true, changeId }); }
            catch (Exception ex) { return Results.BadRequest(new { success = false, message = ex.Message }); }
        });
    }

    private static VeresiyeTenantContext? ResolveDesktopTenant(HttpContext http, VeresiyeCloudMySqlStore store)
    {
        var tenantId = http.Request.Headers["X-NSX-TenantId"].FirstOrDefault() ?? string.Empty;
        var apiToken = http.Request.Headers["X-NSX-ApiToken"].FirstOrDefault() ?? string.Empty;
        var machineId = http.Request.Headers["X-NSX-MachineId"].FirstOrDefault() ?? string.Empty;
        return store.ValidateTenant(tenantId, apiToken, machineId);
    }

    private static VeresiyeMobileSessionContext? ResolveMobileSession(HttpContext http, VeresiyeCloudMySqlStore store)
    {
        var raw = ReadCookie(http); return string.IsNullOrWhiteSpace(raw) ? null : store.ValidateMobileSession(raw);
    }

    private static string ReadCookie(HttpContext http) => http.Request.Cookies.TryGetValue(MobileCookie, out var value) ? value : string.Empty;

    private static string EnsureCsrfToken(HttpContext http, VeresiyeMobileSessionContext session, VeresiyeCloudMySqlStore store)
    {
        // CSRF tokenının ham hali yalnızca QR oturumu oluşturulurken bilinirdi. Bootstrap için
        // mevcut oturumu yenileyip yeni ham CSRF üretmek yerine, HttpOnly olmayan ayrı bir kısa token
        // kullanmıyoruz. Session hash'e bağlı tek kullanımlık olmayan güvenli değer tarayıcıya verilir.
        // Hash doğrulaması için token, session oluşturma sırasında vf_csrf cookie'sine yazılır.
        if (http.Request.Cookies.TryGetValue("NSX.VF.Cloud.Csrf", out var existing) && store.ValidateCsrf(session, existing)) return existing;
        // Eski/eksik CSRF cookie'li oturum güvenli şekilde yeniden QR gerektirir.
        return string.Empty;
    }

    private static bool ValidateMobileMutation(HttpContext http, VeresiyeMobileSessionContext session, VeresiyeCloudMySqlStore store)
    {
        var token = http.Request.Headers["X-NSX-CSRF"].FirstOrDefault() ?? string.Empty;
        return store.ValidateCsrf(session, token);
    }

    private static string GetBaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}";
}
