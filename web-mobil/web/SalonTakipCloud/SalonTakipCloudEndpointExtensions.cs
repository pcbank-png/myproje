using System.Net;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.SalonTakipCloud.Models;
using NSYazilim.Web.SalonTakipCloud.Services;

namespace NSYazilim.Web.SalonTakipCloud;

public static class SalonTakipCloudEndpointExtensions
{
    private const string ProductCode = "NSXDUGUNSALONUPRO";
    private const string SessionCookie = "NSX.SalonTakip.Session";
    private const string CsrfCookie = "NSX.SalonTakip.Csrf";

    public static IServiceCollection AddNsxSalonTakipCloud(this IServiceCollection services)
    {
        services.AddSingleton<SalonTakipMySqlStore>();
        return services;
    }

    public static void MapNsxSalonTakipCloud(this WebApplication app)
    {
        app.MapGet("/api/salontakip/health", (HttpContext http) => Results.Ok(new
        {
            name = "NSX Salon Takip API",
            status = "OK",
            productCode = ProductCode,
            panelUrl = GetBaseUrl(http) + "/salontakip",
            serverTime = DateTimeOffset.Now
        }));

        app.MapGet("/api/salontakip/db-check", IResult (SalonTakipMySqlStore store) =>
        {
            try
            {
                return Results.Ok(store.GetDatabaseStatus());
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    databaseReady = false,
                    message = "Salon Takip veritabanı hazırlanamadı.",
                    errorType = ex.GetType().Name,
                    detail = ex.Message,
                    nativeSqlite = GetNativeSqliteDiagnostic(),
                    serverTime = DateTimeOffset.Now
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/salontakip/setup/tenant", async Task<IResult> (
            SalonTenantSetupRequest request,
            ApplicationDbContext db,
            SalonTakipMySqlStore store,
            HttpContext http) =>
        {
            try
            {
                request ??= new SalonTenantSetupRequest();
                var key = NormalizeLicenseKey(request.LicenseKey);
                var email = NormalizeEmail(request.Email);
                var machineId = (request.MachineId ?? string.Empty).Trim();
                var requestedProduct = CanonicalProductCode(request.ProductCode);

                if (string.IsNullOrWhiteSpace(key)
                    || string.IsNullOrWhiteSpace(email)
                    || string.IsNullOrWhiteSpace(machineId))
                {
                    return Results.BadRequest(new { success = false, message = "Lisans anahtarı, e-posta ve Makine ID zorunludur." });
                }

                if (!string.Equals(requestedProduct, ProductCode, StringComparison.Ordinal))
                {
                    return Results.BadRequest(new { success = false, message = "Ürün kodu NSX Düğün Salonu Pro ile eşleşmiyor." });
                }

                var license = await db.Licenses
                    .AsNoTracking()
                    .Include(x => x.User)
                    .Include(x => x.Product)
                    .Include(x => x.Devices)
                    .FirstOrDefaultAsync(x => x.LicenseKey == key);

                if (license is null)
                {
                    return Results.Unauthorized();
                }

                var licenseEmail = NormalizeEmail(license.User?.Email);
                var licenseProduct = CanonicalProductCode(ResolveLicenseProductCode(license));
                var status = NormalizeLicenseStatus(license.LicenseStatus);
                var now = DateTime.Now;

                if (!license.IsActive
                    || license.User is null
                    || !license.User.IsActive
                    || license.User.IsDeleted
                    || !string.Equals(licenseEmail, email, StringComparison.Ordinal)
                    || !string.Equals(licenseProduct, ProductCode, StringComparison.Ordinal)
                    || status is "Suspended" or "Revoked" or "Blocked"
                    || (license.EndDate.HasValue && license.EndDate.Value.Date < now.Date))
                {
                    return Results.Unauthorized();
                }

                var primaryMachineMatches = !string.IsNullOrWhiteSpace(license.MachineId)
                    && string.Equals(license.MachineId.Trim(), machineId, StringComparison.Ordinal);
                var activeDeviceMatches = license.Devices.Any(x =>
                    string.Equals(x.MachineId, machineId, StringComparison.Ordinal)
                    && !x.IsBlocked
                    && !x.IsRejected
                    && !string.Equals(x.DeviceStatus, "Blocked", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(x.DeviceStatus, "Rejected", StringComparison.OrdinalIgnoreCase));

                if (!primaryMachineMatches && !activeDeviceMatches)
                {
                    return Results.Unauthorized();
                }

                var companyName = string.IsNullOrWhiteSpace(request.CompanyName)
                    ? (license.User.FullName + " Düğün Salonu").Trim()
                    : request.CompanyName.Trim();

                var result = store.CreateOrRefreshTenant(
                    license.Id,
                    key,
                    companyName,
                    request.CurrentApiToken ?? string.Empty);

                var baseUrl = GetBaseUrl(http);
                return Results.Ok(new SalonTenantSetupResponse
                {
                    Success = true,
                    TenantId = result.Tenant.TenantId,
                    CompanyName = result.Tenant.CompanyName,
                    ApiToken = result.ApiToken,
                    ApiBaseUrl = baseUrl + "/api/salontakip/v1",
                    PanelUrl = baseUrl + "/salontakip",
                    DatabaseInstanceId = store.GetDatabaseInstanceId(),
                    Message = result.Rotated
                        ? "Firma bağlantısı doğrulandı ve güvenlik anahtarı yenilendi. Eski mobil oturumlar kapatıldı."
                        : "Firma mobil Salon Takip bağlantısı hazır."
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Salon Takip firma bağlantısı hazırlanırken hata oluştu.",
                    errorType = ex.GetType().Name,
                    detail = ex.Message
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        var desktop = app.MapGroup("/api/salontakip/v1");

        desktop.MapGet("/tenant/me", IResult (HttpContext http, SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            return tenant is null
                ? Results.Unauthorized()
                : Results.Ok(new
                {
                    tenantId = tenant.TenantId,
                    companyName = tenant.CompanyName,
                    productCode = ProductCode,
                    serverTime = DateTimeOffset.Now
                });
        });

        desktop.MapPost("/qr/create", IResult (
            SalonQrCreateRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            request ??= new SalonQrCreateRequest();
            var qr = store.CreateQrLogin(tenant.TenantId, request.SourceDeviceId, request.SourceUser);
            var loginUrl = GetBaseUrl(http) + "/salontakip/giris/" + WebUtility.UrlEncode(qr.Token);
            return Results.Ok(new SalonQrCreateResponse
            {
                Success = true,
                LoginUrl = loginUrl,
                QrValue = loginUrl,
                ExpiresAt = qr.ExpiresAt.ToString("O"),
                ExpiresInSeconds = Math.Max(0, (int)(qr.ExpiresAt - DateTime.Now).TotalSeconds),
                Message = "QR bağlantısı 3 dakika geçerlidir ve yalnızca bir kez kullanılabilir."
            });
        });

        desktop.MapGet("/data/{entityType}", IResult (
            string entityType,
            int? take,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(store.GetDataItems(tenant.TenantId, entityType, take ?? 5000));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        desktop.MapPost("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            SalonEntityUpsertRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var changeId = store.UpsertDataItem(tenant.TenantId, entityType, entityId, request);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        desktop.MapPut("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            SalonEntityUpsertRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var changeId = store.UpsertDataItem(tenant.TenantId, entityType, entityId, request);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        desktop.MapDelete("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                var sourceDeviceId = ReadHeader(http, "X-NSX-Source-Device");
                var sourceUser = ReadHeader(http, "X-NSX-Source-User");
                var changeId = store.DeleteDataItem(tenant.TenantId, entityType, entityId, sourceDeviceId, sourceUser);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        desktop.MapPost("/sync/push", IResult (
            SalonSyncChangeRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                request ??= new SalonSyncChangeRequest();
                long changeId;
                if (string.Equals(request.Action, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    changeId = store.DeleteDataItem(
                        tenant.TenantId,
                        request.EntityType,
                        request.EntityId,
                        request.SourceDeviceId,
                        request.SourceUser);
                }
                else
                {
                    changeId = store.UpsertDataItem(
                        tenant.TenantId,
                        request.EntityType,
                        request.EntityId,
                        new SalonEntityUpsertRequest
                        {
                            PayloadJson = request.PayloadJson,
                            SourceDeviceId = request.SourceDeviceId,
                            SourceUser = request.SourceUser
                        });
                }

                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        desktop.MapPost("/sync/snapshot", IResult (
            SalonSnapshotRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(store.ApplySnapshot(tenant.TenantId, request));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        desktop.MapGet("/sync/changes", IResult (
            long? afterId,
            int? take,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            return tenant is null
                ? Results.Unauthorized()
                : Results.Ok(store.GetChanges(tenant.TenantId, afterId ?? 0, take ?? 200));
        });

        desktop.MapGet("/sync/status", IResult (
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            var tenant = ResolveDesktopTenant(http, store);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            var desktopCounts = store.GetDesktopSyncEntityCounts(tenant.TenantId);
            var webCounts = store.GetEntityCounts(tenant.TenantId);
            var pendingWebCustomers = store.GetPendingWebCustomerCount(tenant.TenantId);

            return Results.Ok(new
            {
                success = true,
                tenantId = tenant.TenantId,
                // Masaüstü ile sayım karşılaştırmasında henüz içeri aktarılmamış
                // web müşterileri ayrı kuyruk olarak tutulur. Bu kayıtlar changes
                // endpoint'inden masaüstüne teslim edilmeye devam eder.
                counts = desktopCounts,
                webCounts,
                pendingWebCustomers,
                lastChangeId = store.GetLastChangeId(tenant.TenantId),
                serverTime = DateTimeOffset.Now
            });
        });

        app.MapGet("/salontakip", IResult (HttpContext http, SalonTakipMySqlStore store) =>
        {
            ApplyHtmlSecurityHeaders(http);
            var session = ResolveMobileSession(http, store);
            if (session is not null)
            {
                return Results.Redirect("/salontakip/panel");
            }

            return Results.Content(SalonTakipHtml.BuildWelcomeHtml(), "text/html; charset=utf-8");
        });

        app.MapGet("/salontakip/giris/{token}", IResult (
            string token,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyHtmlSecurityHeaders(http);
            var tenant = store.ConsumeQrLogin(token);
            if (tenant is null)
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Content(SalonTakipHtml.BuildInvalidLoginHtml(), "text/html; charset=utf-8");
            }

            var session = store.CreateMobileSession(tenant.TenantId, GetUserAgent(http));
            var secure = http.Request.IsHttps || !IsLocalHost(http.Request.Host.Host);

            // Mobil Chrome tamamen kapatılıp yeniden açıldığında oturumun korunması için
            // cookie'yi hem Max-Age hem de UTC Expires ile kalıcı olarak yazıyoruz.
            // Çok uzun cookie süreleri bazı mobil tarayıcılarda reddedilebildiğinden 1 yıl kullanılır.
            var persistentCookieLifetime = TimeSpan.FromDays(365);
            var persistentCookieExpires = DateTimeOffset.UtcNow.Add(persistentCookieLifetime);

            http.Response.Cookies.Append(SessionCookie, session.SessionToken, new CookieOptions
            {
                HttpOnly = true,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                MaxAge = persistentCookieLifetime,
                Expires = persistentCookieExpires
            });
            http.Response.Cookies.Append(CsrfCookie, session.CsrfToken, new CookieOptions
            {
                HttpOnly = false,
                Secure = secure,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                MaxAge = persistentCookieLifetime,
                Expires = persistentCookieExpires
            });

            return Results.Redirect("/salontakip/panel");
        });

        app.MapGet("/salontakip/panel", IResult (HttpContext http, SalonTakipMySqlStore store) =>
        {
            ApplyHtmlSecurityHeaders(http);
            var session = ResolveMobileSession(http, store);
            return session is null
                ? Results.Redirect("/salontakip")
                : Results.Content(SalonTakipHtml.BuildPanelHtml(), "text/html; charset=utf-8");
        });

        app.MapPost("/salontakip/cikis", IResult (HttpContext http, SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var sessionToken = http.Request.Cookies[SessionCookie] ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(sessionToken))
            {
                store.RevokeMobileSession(sessionToken);
            }

            DeleteMobileCookies(http);
            return Results.Ok(new { success = true });
        });

        var mobile = app.MapGroup("/api/salontakip/mobile");

        mobile.MapGet("/bootstrap", IResult (HttpContext http, SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                DeleteMobileCookies(http);
                return Results.Unauthorized();
            }

            return Results.Ok(new
            {
                tenant = new
                {
                    companyName = session.CompanyName,
                    sessionExpiresAt = session.ExpiresAt.ToString("O")
                },
                lastChangeId = store.GetLastChangeId(session.TenantId),
                data = new
                {
                    company = store.GetDataItems(session.TenantId, "company", 20),
                    halls = store.GetDataItems(session.TenantId, "hall", 200),
                    reservations = store.GetDataItems(session.TenantId, "reservation", 5000),
                    customers = store.GetDataItems(session.TenantId, "customer", 5000),
                    finance = store.GetDataItems(session.TenantId, "finance", 5000),
                    payments = store.GetDataItems(session.TenantId, "payment", 5000),
                    expenses = store.GetDataItems(session.TenantId, "expense", 5000)
                },
                serverTime = DateTimeOffset.Now
            });
        });

        mobile.MapGet("/data/{entityType}", IResult (
            string entityType,
            int? take,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Ok(store.GetDataItems(session.TenantId, entityType, take ?? 5000));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        mobile.MapPost("/customers", IResult (
            SalonMobileCustomerCreateRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            if (!ValidateMobileCsrf(http, store, session))
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Mobil güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var saved = store.CreateMobileCustomer(session.TenantId, request ?? new SalonMobileCustomerCreateRequest());
                return Results.Ok(new
                {
                    success = true,
                    entityId = saved.Item.Id,
                    changeId = saved.ChangeId,
                    item = saved.Item,
                    message = "Müşteri sunucu veritabanına kaydedildi."
                });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        mobile.MapPost("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            SalonEntityUpsertRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            if (!ValidateMobileCsrf(http, store, session))
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Mobil güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                request ??= new SalonEntityUpsertRequest();
                request.SourceDeviceId = "MOBILE-WEB";
                request.SourceUser = string.IsNullOrWhiteSpace(request.SourceUser) ? "Mobil Panel" : request.SourceUser;
                var changeId = store.UpsertDataItem(session.TenantId, entityType, entityId, request);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        mobile.MapPut("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            SalonEntityUpsertRequest request,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            if (!ValidateMobileCsrf(http, store, session))
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Mobil güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                request ??= new SalonEntityUpsertRequest();
                request.SourceDeviceId = "MOBILE-WEB";
                request.SourceUser = string.IsNullOrWhiteSpace(request.SourceUser) ? "Mobil Panel" : request.SourceUser;
                var changeId = store.UpsertDataItem(session.TenantId, entityType, entityId, request);
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        mobile.MapDelete("/data/{entityType}/{entityId}", IResult (
            string entityType,
            string entityId,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            if (!ValidateMobileCsrf(http, store, session))
            {
                return Results.Json(new
                {
                    success = false,
                    message = "Mobil güvenlik doğrulaması başarısız. Sayfayı yenileyip tekrar deneyin."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                var changeId = store.DeleteDataItem(session.TenantId, entityType, entityId, "MOBILE-WEB", "Mobil Panel");
                return Results.Ok(new { success = true, changeId });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message });
            }
        });

        mobile.MapGet("/changes", IResult (
            long? afterId,
            int? take,
            HttpContext http,
            SalonTakipMySqlStore store) =>
        {
            ApplyApiNoCache(http);
            var session = ResolveMobileSession(http, store);
            return session is null
                ? Results.Unauthorized()
                : Results.Ok(store.GetChanges(session.TenantId, afterId ?? 0, take ?? 200));
        });
    }

    private static SalonTenantContext? ResolveDesktopTenant(HttpContext http, SalonTakipMySqlStore store)
    {
        var tenantId = ReadHeader(http, "X-NSX-Salon-TenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            tenantId = ReadHeader(http, "X-NSX-FirmaId");
        }

        var apiToken = ReadHeader(http, "X-NSX-Salon-ApiToken");
        if (string.IsNullOrWhiteSpace(apiToken))
        {
            apiToken = ReadHeader(http, "X-NSX-ApiToken");
        }

        return store.ValidateTenant(tenantId, apiToken);
    }

    private static SalonMobileSessionContext? ResolveMobileSession(HttpContext http, SalonTakipMySqlStore store)
    {
        var sessionToken = http.Request.Cookies[SessionCookie] ?? string.Empty;
        return store.ValidateMobileSession(sessionToken, GetUserAgent(http));
    }

    private static bool ValidateMobileCsrf(
        HttpContext http,
        SalonTakipMySqlStore store,
        SalonMobileSessionContext session)
    {
        var header = ReadHeader(http, "X-NSX-CSRF");
        var cookie = http.Request.Cookies[CsrfCookie] ?? string.Empty;
        return !string.IsNullOrWhiteSpace(header)
            && string.Equals(header, cookie, StringComparison.Ordinal)
            && store.ValidateCsrf(session, header);
    }

    private static string ReadHeaderOrQuery(HttpContext http, string headerName, string queryName)
    {
        var header = ReadHeader(http, headerName);
        if (!string.IsNullOrWhiteSpace(header))
        {
            return header;
        }

        return http.Request.Query.TryGetValue(queryName, out var value)
            ? value.ToString().Trim()
            : string.Empty;
    }

    private static string ReadHeader(HttpContext http, string headerName)
    {
        return http.Request.Headers.TryGetValue(headerName, out var value)
            ? value.ToString().Trim()
            : string.Empty;
    }

    private static void ApplyHtmlSecurityHeaders(HttpContext http)
    {
        ApplyApiNoCache(http);
        http.Response.Headers["X-Frame-Options"] = "DENY";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        http.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    }

    private static void ApplyApiNoCache(HttpContext http)
    {
        http.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        http.Response.Headers["Pragma"] = "no-cache";
        http.Response.Headers["Expires"] = "0";
    }

    private static void DeleteMobileCookies(HttpContext http)
    {
        var secure = http.Request.IsHttps || !IsLocalHost(http.Request.Host.Host);
        var options = new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Strict,
            IsEssential = true,
            Path = "/"
        };
        http.Response.Cookies.Delete(SessionCookie, options);
        options.HttpOnly = false;
        http.Response.Cookies.Delete(CsrfCookie, options);
    }

    private static string ResolveLicenseProductCode(NSYazilim.Web.Models.License license)
    {
        var code = NormalizeProductCode(license.ProductCode);
        if (!string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        code = NormalizeProductCode(license.Product?.ProductCode);
        if (!string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        code = NormalizeProductCode(license.Product?.Slug);
        if (!string.IsNullOrWhiteSpace(code))
        {
            return code;
        }

        return NormalizeProductCode(license.Product?.Name);
    }

    private static string NormalizeLicenseKey(string value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string NormalizeEmail(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeProductCode(string? value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .ToArray());
    }

    private static object GetNativeSqliteDiagnostic()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var runtimeDirectory = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.X64 => "win-x64",
            Architecture.Arm => "win-arm",
            Architecture.Arm64 => "win-arm64",
            _ => string.Empty
        };

        var rootPath = Path.Combine(baseDirectory, "e_sqlite3.dll");
        var runtimePath = string.IsNullOrWhiteSpace(runtimeDirectory)
            ? string.Empty
            : Path.Combine(baseDirectory, "runtimes", runtimeDirectory, "native", "e_sqlite3.dll");

        return new
        {
            baseDirectory,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            rootExists = File.Exists(rootPath),
            rootPath,
            runtimeExists = !string.IsNullOrWhiteSpace(runtimePath) && File.Exists(runtimePath),
            runtimePath
        };
    }

    private static string CanonicalProductCode(string value)
    {
        return new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static string NormalizeLicenseStatus(string value)
    {
        var status = (value ?? "Active").Trim();
        if (string.Equals(status, "Askida", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Suspended", StringComparison.OrdinalIgnoreCase))
        {
            return "Suspended";
        }

        if (string.Equals(status, "Iptal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Revoked", StringComparison.OrdinalIgnoreCase))
        {
            return "Revoked";
        }

        if (string.Equals(status, "Engelli", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "Blocked", StringComparison.OrdinalIgnoreCase))
        {
            return "Blocked";
        }

        return "Active";
    }

    private static string GetBaseUrl(HttpContext http)
    {
        var host = http.Request.Host.Value;
        var scheme = IsLocalHost(http.Request.Host.Host) ? http.Request.Scheme : "https";
        return $"{scheme}://{host}{http.Request.PathBase}".TrimEnd('/');
    }

    private static string GetUserAgent(HttpContext http)
    {
        return http.Request.Headers["User-Agent"].ToString();
    }

    private static bool IsLocalHost(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }
}
