using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.CaritakipCloud.Hubs;
using NSYazilim.Web.CaritakipCloud.Models;
using NSYazilim.Web.CaritakipCloud.Services;
using NSYazilim.Web.Data;
using NSYazilim.Web.Services;

namespace NSYazilim.Web.CaritakipCloud;

public static class CaritakipCloudEndpointExtensions
{
    private const string SessionCookie = "NSX.CariTakip.Session";
    private const string CsrfCookie = "NSX.CariTakip.Csrf";
    private static readonly IReadOnlyDictionary<string, string> MobileAssetContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["index.html"] = "text/html; charset=utf-8",
            ["app.css"] = "text/css; charset=utf-8",
            ["app.js"] = "application/javascript; charset=utf-8",
            ["app-icon.svg"] = "image/svg+xml",
            ["manifest.webmanifest"] = "application/manifest+json; charset=utf-8",
            ["offline.html"] = "text/html; charset=utf-8",
            ["sw.js"] = "application/javascript; charset=utf-8"
        };
    private static readonly HashSet<string> ProductCodes = new(StringComparer.Ordinal)
    {
        "NSXCARITAKIPPROBULUT"
    };

    public static IServiceCollection AddNsxCaritakipCloud(this IServiceCollection services)
    {
        services.AddSingleton<CaritakipEntitlementValidator>();
        services.AddSingleton<CaritakipCloudStore>();
        services.AddSingleton<CaritakipNativeMobileStore>();
        services.AddSingleton<CaritakipDatabaseBackupStore>();
        services.AddHttpClient(nameof(CaritakipExpoPushService));
        services.AddSingleton<CaritakipExpoPushService>();
        services.AddSingleton<CaritakipMobileNotificationDeliverer>();
        services.AddSingleton<CaritakipMobileChangeNotifier>();
        services.AddSingleton<CaritakipNotificationOutboxStore>();
        services.AddHostedService<CaritakipNotificationOutboxWorker>();
        services.AddHostedService<CaritakipMobileReminderWorker>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("CaritakipCloud", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 400,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("CaritakipSetup", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("CaritakipRecovery", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("CaritakipTerminalEnroll", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.AddPolicy("CaritakipBackup", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 60,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });
        return services;
    }

    public static void MapNsxCaritakipCloud(this WebApplication app)
    {
        var api = app.MapGroup("/api/caritakipcloud").RequireRateLimiting("CaritakipCloud");
        api.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try
            {
                return await next(context);
            }
            catch (CariEntitlementUnavailableException ex)
            {
                context.HttpContext.Response.Headers.RetryAfter = "5";
                return Results.Json(new { code = "license_service_unavailable", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapGet("/health", async Task<IResult> (
            CaritakipCloudStore store,
            CaritakipDatabaseBackupStore backupStore,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await store.EnsureSchemaAsync(cancellationToken);
                backupStore.EnsureStorageReady();
                return Results.Ok(new
                {
                    name = "NSX Cari Takip Pro Bulut",
                    status = "healthy",
                    database = store.GetHealth(),
                    backupStorage = "ready",
                    serverTimeUtc = DateTime.UtcNow
                });
            }
            catch
            {
                return Results.Json(new
                {
                    name = "NSX Cari Takip Pro Bulut",
                    status = "unhealthy",
                    database = store.GetHealth(),
                    serverTimeUtc = DateTime.UtcNow
                }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        async Task<IResult> Setup(
            CariTenantSetupRequest request,
            ApplicationDbContext db,
            CaritakipCloudStore store,
            HttpContext http,
            CancellationToken cancellationToken)
        {
            request ??= new CariTenantSetupRequest();
            PopulateSetupFromHeaders(request, http);
            var recoveryRequested = http.Request.Path.Value?.EndsWith(
                "/tenant/recovery/setup",
                StringComparison.OrdinalIgnoreCase) == true;
            var licenseKey = NormalizeLicenseKey(request.LicenseKey);
            var email = NormalizeEmail(
                DecodeSetupIdentity(request.AccountIdentity)
                ?? DecodeSetupIdentity(request.EmailEncoded)
                ?? request.Email);
            var productCode = NormalizeProductCode(request.ProductCode);
            var requestedDeviceId = (request.DeviceId ?? string.Empty).Trim();
            var requestedMachineId = (string.IsNullOrWhiteSpace(request.HostRef)
                ? request.MachineId
                : request.HostRef).Trim();
            if (licenseKey.Length == 0 || email.Length == 0 || requestedDeviceId.Length == 0
                || requestedMachineId.Length == 0 || !ProductCodes.Contains(productCode))
                return Results.BadRequest(new { message = "Geçerli lisans, ürün, e-posta, deviceId ve machineId zorunludur." });

            var license = await db.Licenses
                .Include(x => x.User).Include(x => x.Product).Include(x => x.Devices)
                .Include(x => x.OfflineCertificates)
                .FirstOrDefaultAsync(x => x.LicenseKey == licenseKey, cancellationToken);
            if (license is null)
                return Results.Json(
                    new { message = "Lisans bulunamadı. Programdaki e-posta ve lisans anahtarını yeniden doğrulayın." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var now = DateTime.Now;
            if (LicenseTermService.NormalizeTerm(license, now))
                await db.SaveChangesAsync(cancellationToken);

            var actualProduct = NormalizeProductCode(
                license.ProductCode ?? license.Product?.ProductCode ?? license.Product?.Slug ?? license.Product?.Name);
            var status = (license.LicenseStatus ?? string.Empty).Trim();
            var blockedStatus = status.Equals("Suspended", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Revoked", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Blocked", StringComparison.OrdinalIgnoreCase);
            if (!license.IsActive || license.RevokedAt.HasValue || blockedStatus
                || license.EndDate.HasValue && license.EndDate.Value.Date < now.Date)
                return Results.Json(
                    new { message = "Lisans bulut kullanımı için aktif değil veya kullanım süresi dolmuş." },
                    statusCode: StatusCodes.Status401Unauthorized);
            if (license.User is null || !license.User.IsActive || license.User.IsDeleted
                || !string.Equals(NormalizeEmail(license.User.Email), email, StringComparison.Ordinal))
                return Results.Json(
                    new { message = "Lisans ile e-posta hesabı eşleşmiyor." },
                    statusCode: StatusCodes.Status401Unauthorized);
            if (!ProductCodes.Contains(actualProduct) || !ProductCodes.Contains(productCode))
                return Results.Json(
                    new { message = "Lisans NSX Cari Takip Pro Bulut ürününe ait değil." },
                    statusCode: StatusCodes.Status401Unauthorized);

            var matchingDevice = license.Devices.FirstOrDefault(x =>
                string.Equals(x.MachineId, requestedMachineId, StringComparison.Ordinal));
            if (matchingDevice?.IsBlocked == true)
                return Results.Json(
                    new { message = "Bu cihaz lisans için engellenmiş." },
                    statusCode: StatusCodes.Status403Forbidden);

            var machineAllowed = string.Equals(license.MachineId?.Trim(), requestedMachineId, StringComparison.Ordinal)
                || matchingDevice is { IsBlocked: false, IsRejected: false }
                   && !string.Equals(matchingDevice.DeviceStatus, "Blocked", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(matchingDevice.DeviceStatus, "Rejected", StringComparison.OrdinalIgnoreCase);
            if (!machineAllowed)
            {
                var maxDeviceCount = license.MaxDeviceCount <= 0 ? 1 : license.MaxDeviceCount;
                LicenseDeviceReactivationService.TransferToVerifiedInstallation(
                    license, requestedMachineId, maxDeviceCount, now);

                matchingDevice = license.Devices.FirstOrDefault(x =>
                    string.Equals(x.MachineId, requestedMachineId, StringComparison.Ordinal));
                if (matchingDevice is null)
                {
                    matchingDevice = new NSYazilim.Web.Models.LicenseDevice
                    {
                        LicenseId = license.Id,
                        MachineId = requestedMachineId,
                        ProductCode = actualProduct,
                        AttemptEmail = email,
                        DeviceName = Limit(request.DeviceName, 120),
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
                    matchingDevice.ProductCode = actualProduct;
                    matchingDevice.AttemptEmail = email;
                    matchingDevice.DeviceName = Limit(request.DeviceName, 120);
                    matchingDevice.DeviceStatus = "Active";
                    matchingDevice.IsRejected = false;
                    matchingDevice.BlockReason = null;
                    matchingDevice.LastSeenAt = now;
                }

                if (maxDeviceCount <= 1 || string.IsNullOrWhiteSpace(license.MachineId))
                    license.MachineId = requestedMachineId;
                license.LastCheckedAt = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            try
            {
                var company = string.IsNullOrWhiteSpace(request.CompanyName)
                    ? (license.User.FullName + " Cari Takip").Trim()
                    : request.CompanyName.Trim();
        var enrolled = await store.EnrollDeviceAsync(
            license.Id,
            company,
            requestedDeviceId,
            requestedMachineId,
            request.DeviceName ?? string.Empty,
            http.Request.Headers["X-NSX-ApiToken"].ToString(),
            cancellationToken);
                var isPrimaryDevice = enrolled.IsPrimary;
                if (recoveryRequested)
                {
                    await store.TransferPrimaryDeviceAsync(
                        enrolled.Tenant.TenantId,
                        requestedDeviceId,
                        cancellationToken);
                    isPrimaryDevice = true;
                }
                var baseUrl = BaseUrl(http);
                http.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new CariTenantSetupResponse
                {
                    Success = true,
                    IsPrimaryDevice = isPrimaryDevice,
                    TenantId = enrolled.Tenant.TenantId,
                    DeviceId = requestedDeviceId,
                    ApiToken = enrolled.ApiToken,
                    ApiBaseUrl = baseUrl + "/api/caritakipcloud",
                    PanelUrl = baseUrl + "/CaritakipCloud",
                    Message = recoveryRequested
                        ? "Felaket kurtarma cihazı doğrulandı ve ana cihaz yetkisi güvenle devralındı."
                        : enrolled.Rotated
                        ? "Cihaz doğrulandı. API anahtarı yalnızca bu yanıtta gösterilir."
                        : "Mevcut cihaz bağlantısı doğrulandı."
                });
            }
            catch (Exception ex)
            {
                app.Logger.LogError(ex, "Cari Takip lisanslı cihaz kaydı başarısız.");
                return Results.Json(new { message = "Cari Takip bulut bağlantısı hazırlanamadı." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        api.MapPost("/tenant/setup", Setup).RequireRateLimiting("CaritakipSetup");
        api.MapPost("/setup/tenant", Setup).RequireRateLimiting("CaritakipSetup");
        api.MapPost("/tenant/enroll", Setup).RequireRateLimiting("CaritakipSetup");
        api.MapPost("/tenant/recovery/setup", Setup).RequireRateLimiting("CaritakipRecovery");

        api.MapGet("/device/validate", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            return device is null ? Results.Unauthorized() : Results.Ok(new
            {
                valid = true, device.TenantId, device.DeviceId, device.CompanyName, serverTimeUtc = DateTime.UtcNow
            });
        });

        api.MapGet("/bootstrap", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            try
            {
                var data = await store.GetBootstrapAsync(device.TenantId, cancellationToken);
                return Results.Ok(new { data.Cursor, entities = data.Entities, serverTimeUtc = DateTime.UtcNow });
            }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        });

        api.MapPost("/sync/push", async Task<IResult> (
            CariSyncPushRequest request, HttpContext http, CaritakipCloudStore store,
            CaritakipMobileChangeNotifier notifier,
            IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            try
            {
                var result = await store.ApplyMutationsAsync(device.TenantId, device.DeviceId,
                    request?.Mutations ?? new List<CariMutationRequest>(), cancellationToken);
                await notifier.NotifyAsync(hub, device.TenantId, device.DeviceId, result, cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        }).WithMetadata(new RequestSizeLimitAttribute(600_000));

        api.MapGet("/sync/changes", async Task<IResult> (
            long? after, int? take, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            try { return Results.Ok(await store.GetChangesAsync(device.TenantId, after ?? 0, take ?? 200, cancellationToken)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        api.MapGet("/changes", async Task<IResult> (
            long? after, int? take, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            try { return Results.Ok(await store.GetChangesAsync(device.TenantId, after ?? 0, take ?? 200, cancellationToken)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        api.MapGet("/status", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            return device is null ? Results.Unauthorized()
                : Results.Ok(await store.GetStatusAsync(device.TenantId, cancellationToken));
        });

        api.MapGet("/backup/security/status", async Task<IResult> (
            HttpContext http,
            CaritakipCloudStore store,
            CaritakipDatabaseBackupStore backupStore,
            CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null)
                return Results.Unauthorized();
            var metadata = await backupStore.GetMetadataAsync(device.TenantId, cancellationToken);
            return Results.Ok(new
            {
                available = metadata is not null,
                backup = metadata
            });
        });

        async Task<IResult> SaveSecurityBackup(
            HttpContext http,
            CaritakipCloudStore store,
            CaritakipDatabaseBackupStore backupStore,
            CancellationToken cancellationToken)
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null)
                return Results.Unauthorized();
            if (!await store.IsPrimaryDeviceAsync(device.TenantId, device.DeviceId, cancellationToken))
                return Results.Json(
                    new { message = "Güvenlik veritabanı yedeğini yalnızca ana PC yükleyebilir." },
                    statusCode: StatusCodes.Status403Forbidden);
            if (http.Request.ContentType is null ||
                !http.Request.ContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { message = "Yedek application/octet-stream olarak gönderilmelidir." });

            _ = long.TryParse(
                http.Request.Headers["X-NSX-Backup-Cursor"].ToString(),
                out var sourceCursor);
            try
            {
                var metadata = await backupStore.SaveAsync(
                    device.TenantId,
                    device.DeviceId,
                    http.Request.Body,
                    http.Request.ContentLength,
                    http.Request.Headers["X-NSX-Backup-Sha256"].ToString(),
                    sourceCursor,
                    cancellationToken);
                await store.RecordAuditAsync(
                    device.TenantId,
                    device.DeviceId,
                    "SecurityBackupUploaded",
                    $"size={metadata.SizeBytes};sha256={metadata.Sha256};cursor={metadata.SourceCursor}",
                    cancellationToken);
                return Results.Ok(metadata);
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (IOException)
            {
                return Results.Json(
                    new { message = "Güvenlik yedeği sunucu depolamasına yazılamadı." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }

        // Plesk/IIS WebDAV PUT'u 405 ile kesiyor; yükleme POST ile yapılır.
        api.MapPost("/backup/security", SaveSecurityBackup)
            .WithMetadata(new RequestSizeLimitAttribute(CaritakipDatabaseBackupStore.MaxBackupBytes))
            .RequireRateLimiting("CaritakipBackup");
        api.MapPut("/backup/security", SaveSecurityBackup)
            .WithMetadata(new RequestSizeLimitAttribute(CaritakipDatabaseBackupStore.MaxBackupBytes))
            .RequireRateLimiting("CaritakipBackup");

        api.MapGet("/backup/security", async Task<IResult> (
            HttpContext http,
            CaritakipCloudStore store,
            CaritakipDatabaseBackupStore backupStore,
            CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null)
                return Results.Unauthorized();
            if (!await store.IsPrimaryDeviceAsync(device.TenantId, device.DeviceId, cancellationToken))
                return Results.Json(
                    new { message = "Güvenlik veritabanı yedeğini yalnızca doğrulanmış ana PC indirebilir." },
                    statusCode: StatusCodes.Status403Forbidden);

            var backup = await backupStore.OpenReadAsync(device.TenantId, cancellationToken);
            if (backup is null)
                return Results.NotFound(new { message = "Bu hesaba ait güvenlik veritabanı yedeği bulunamadı." });
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-NSX-Backup-Sha256"] = backup.Value.Metadata.Sha256;
            http.Response.Headers["X-NSX-Backup-Cursor"] = backup.Value.Metadata.SourceCursor.ToString();
            await store.RecordAuditAsync(
                device.TenantId,
                device.DeviceId,
                "SecurityBackupDownloaded",
                $"size={backup.Value.Metadata.SizeBytes};sha256={backup.Value.Metadata.Sha256}",
                cancellationToken);
            return Results.Stream(
                backup.Value.Stream,
                "application/octet-stream",
                "nsx_veresiye_defteri.db",
                new DateTimeOffset(backup.Value.Metadata.CreatedAtUtc),
                enableRangeProcessing: false);
        }).RequireRateLimiting("CaritakipBackup");

        api.MapPost("/terminal/invite", async Task<IResult> (
            CariTerminalInviteRequest request,
            HttpContext http,
            CaritakipCloudStore store,
            CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Json(new
            {
                code = "primary_auth_required",
                message = "Ana PC'nin bulut cihazı veya lisansı doğrulanamadı. Ana PC'de bulut bağlantısını yenileyin."
            }, statusCode: StatusCodes.Status401Unauthorized);
            if (!await store.IsPrimaryDeviceAsync(device.TenantId, device.DeviceId, cancellationToken))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            (string Token, DateTime ExpiresAtUtc) invite;
            try
            {
                invite = await store.IssueTerminalInviteAsync(device.TenantId, device.DeviceId,
                    request?.ExpiresInSeconds ?? 600, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new CariTerminalInviteResponse
            {
                InviteCode = invite.Token,
                InviteValue = "nsx-cari-terminal://enroll?code=" + Uri.EscapeDataString(invite.Token),
                ExpiresAtUtc = invite.ExpiresAtUtc,
                ExpiresInSeconds = Math.Max(0, (int)(invite.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)
            });
        }).WithMetadata(new RequestSizeLimitAttribute(16_384));

        api.MapPost("/terminal/enroll", async Task<IResult> (
            CariTerminalEnrollRequest request,
            HttpContext http,
            CaritakipCloudStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var enrolled = await store.ConsumeTerminalInviteAsync(
                    request?.InviteCode ?? string.Empty,
                    request?.DeviceId ?? string.Empty,
                    request?.MachineId ?? string.Empty,
                    request?.DeviceName ?? string.Empty,
                    cancellationToken,
                    request?.EnrollmentToken);
                if (enrolled is null) return Results.Json(new
                {
                    code = "invalid_code",
                    message = "Bağlantı kodu doğrulanamadı. Ana PC'den yeni 6 haneli kod oluşturun."
                }, statusCode: StatusCodes.Status401Unauthorized);
                http.Response.Headers.CacheControl = "no-store";
                var baseUrl = BaseUrl(http);
                return Results.Ok(new CariTenantSetupResponse
                {
                    Success = true,
                    TenantId = enrolled.Value.Tenant.TenantId,
                    DeviceId = enrolled.Value.DeviceId,
                    ApiToken = enrolled.Value.ApiToken,
                    ApiBaseUrl = baseUrl + "/api/caritakipcloud",
                    PanelUrl = baseUrl + "/CaritakipCloud",
                    Message = "Terminal kaydedildi. API anahtarı yalnızca bu yanıtta gösterilir."
                });
            }
            catch (CariTerminalPairingException ex)
            {
                return Results.Json(new { code = ex.Code, message = ex.Message },
                    statusCode: StatusCodes.Status401Unauthorized);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        }).RequireRateLimiting("CaritakipTerminalEnroll")
          .WithMetadata(new RequestSizeLimitAttribute(16_384));

        api.MapPost("/qr/issue", async Task<IResult> (
            CariQrIssueRequest request, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            var qr = await store.IssueQrAsync(device.TenantId, device.DeviceId,
                request?.ExpiresInSeconds ?? 180, cancellationToken);
            var loginUrl = BaseUrl(http) + "/CaritakipCloud/login/" + WebUtility.UrlEncode(qr.Token);
            return Results.Ok(new CariQrIssueResponse
            {
                LoginUrl = loginUrl, QrValue = loginUrl, ExpiresAtUtc = qr.ExpiresAtUtc,
                ExpiresInSeconds = Math.Max(0, (int)(qr.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds)
            });
        });

        api.MapPost("/qr/consume", async Task<IResult> (
            CariQrConsumeRequest request, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var consumed = await store.ConsumeQrAsync(request?.Token ?? string.Empty,
                http.Request.Headers.UserAgent.ToString(), cancellationToken);
            if (consumed is null) return Results.Unauthorized();
            SetMobileCookies(http, consumed.Value.SessionToken, consumed.Value.CsrfToken, consumed.Value.ExpiresAtUtc);
            return Results.Ok(new
            {
                success = true, consumed.Value.Tenant.TenantId, consumed.Value.Tenant.CompanyName,
                csrfToken = consumed.Value.CsrfToken, expiresAtUtc = consumed.Value.ExpiresAtUtc
            });
        });

        api.MapGet("/mobile/session/bootstrap", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            if (session is null) return Results.Unauthorized();
            var customers = await store.GetCustomersWithBalancesAsync(session.TenantId, string.Empty, 500, cancellationToken);
            var companies = await store.GetCompaniesAsync(session.TenantId, cancellationToken);
            var status = await store.GetStatusAsync(session.TenantId, cancellationToken);
            var recentTransactions = await store.GetRecentTransactionsAsync(session.TenantId, 30, cancellationToken);
            return Results.Ok(new
            {
                session.TenantId, session.CompanyName, status, customers, companies, recentTransactions, session.ExpiresAtUtc
            });
        });

        api.MapGet("/mobile/companies", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            return session is null ? Results.Unauthorized()
                : Results.Ok(await store.GetCompaniesAsync(session.TenantId, cancellationToken));
        });

        api.MapGet("/mobile/status", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            return session is null ? Results.Unauthorized()
                : Results.Ok(await store.GetCursorStatusAsync(session.TenantId, cancellationToken));
        });

        api.MapGet("/mobile/transactions/recent", async Task<IResult> (
            int? take, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            return session is null ? Results.Unauthorized()
                : Results.Ok(await store.GetRecentTransactionsAsync(session.TenantId, take ?? 50, cancellationToken));
        });

        api.MapGet("/mobile/customers", async Task<IResult> (
            string? search, int? take, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            return session is null ? Results.Unauthorized()
                : Results.Ok(await store.GetCustomersWithBalancesAsync(session.TenantId, search ?? string.Empty, take ?? 200, cancellationToken));
        });

        api.MapGet("/mobile/customers/{customerId}/transactions", async Task<IResult> (
            string customerId, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            if (session is null) return Results.Unauthorized();
            try { return Results.Ok(await store.GetCustomerTransactionsAsync(session.TenantId, customerId, cancellationToken)); }
            catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
        });

        api.MapPost("/mobile/customers", async Task<IResult> (
            CariCustomerWriteRequest request, HttpContext http, CaritakipCloudStore store,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
            await WriteCustomerAsync(null, request, http, store, changeNotifier, hub, cancellationToken));
        api.MapPut("/mobile/customers/{customerId}", async Task<IResult> (
            string customerId, CariCustomerWriteRequest request, HttpContext http, CaritakipCloudStore store,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
            await WriteCustomerAsync(customerId, request, http, store, changeNotifier, hub, cancellationToken));
        api.MapDelete("/mobile/customers/{customerId}", async Task<IResult> (
            string customerId, [FromBody] CariDeleteRequest request, HttpContext http, CaritakipCloudStore store,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
            await DeleteMobileAsync("customer", customerId, request, http, store, changeNotifier, hub, cancellationToken));

        foreach (var type in new[] { "debt", "collection" })
        {
            var capturedType = type;
            api.MapPost($"/mobile/{capturedType}s", async Task<IResult> (
                CariTransactionWriteRequest request, HttpContext http, CaritakipCloudStore store,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
                await WriteTransactionAsync(capturedType, null, request, http, store, changeNotifier, hub, cancellationToken));
            api.MapPut($"/mobile/{capturedType}s/{{entityId}}", async Task<IResult> (
                string entityId, CariTransactionWriteRequest request, HttpContext http, CaritakipCloudStore store,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
                await WriteTransactionAsync(capturedType, entityId, request, http, store, changeNotifier, hub, cancellationToken));
            api.MapDelete($"/mobile/{capturedType}s/{{entityId}}", async Task<IResult> (
                string entityId, [FromBody] CariDeleteRequest request, HttpContext http, CaritakipCloudStore store,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken) =>
                await DeleteMobileAsync(capturedType, entityId, request, http, store, changeNotifier, hub, cancellationToken));
        }

        api.MapPost("/mobile/session/revoke", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var session = await ResolveMobileAsync(http, store, cancellationToken);
            if (session is null) return Results.Unauthorized();
            if (!RequireCsrf(http, session)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            await store.RevokeMobileSessionsAsync(session.TenantId, session.SessionHash, cancellationToken);
            DeleteMobileCookies(http);
            return Results.Ok(new { success = true });
        });

        api.MapPost("/mobile/sessions/revoke-all", async Task<IResult> (
            HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            var device = await ResolveDeviceAsync(http, store, cancellationToken);
            if (device is null) return Results.Unauthorized();
            await store.RevokeMobileSessionsAsync(device.TenantId, null, cancellationToken);
            return Results.Ok(new { success = true });
        });

        app.MapHub<CaritakipCloudHub>("/Api/CaritakipCloud/hub").RequireRateLimiting("CaritakipCloud");

        app.MapGet("/CaritakipCloud/login/{token}", async Task<IResult> (
            string token, HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        {
            SetSecurityHeaders(http);
            var consumed = await store.ConsumeQrAsync(token, http.Request.Headers.UserAgent.ToString(), cancellationToken);
            if (consumed is null)
                return Results.Redirect(await ResolveMobileAsync(http, store, cancellationToken) is not null
                    ? "/CaritakipCloud" : "/CaritakipCloud?login=invalid");
            SetMobileCookies(http, consumed.Value.SessionToken, consumed.Value.CsrfToken, consumed.Value.ExpiresAtUtc);
            return Results.Redirect("/CaritakipCloud");
        }).RequireRateLimiting("CaritakipCloud")
          .AddEndpointFilter(async (context, next) =>
          {
              try { return await next(context); }
              catch (CariEntitlementUnavailableException)
              {
                  context.HttpContext.Response.Headers.RetryAfter = "5";
                  return Results.Content("Lisans hizmeti gecici olarak kullanilamiyor. QR baglantisini biraz sonra yeniden acin.",
                      "text/plain; charset=utf-8", statusCode: StatusCodes.Status503ServiceUnavailable);
              }
          });

        app.MapGet("/CaritakipCloud", (HttpContext http) =>
        {
            SetSecurityHeaders(http);
            return EmbeddedMobileAsset("index.html");
        }).RequireRateLimiting("CaritakipCloud");

        app.MapGet("/caritakip/{file}", (string file, HttpContext http) =>
        {
            SetSecurityHeaders(http);
            return EmbeddedMobileAsset(file);
        }).RequireRateLimiting("CaritakipCloud");

        // Native iOS/Android client: Bearer + rotating refresh token.
        // Existing cookie/CSRF mobile web routes above remain unchanged.
        app.MapNsxCaritakipNativeMobile();
    }

    private static async Task<IResult> WriteCustomerAsync(
        string? id, CariCustomerWriteRequest request, HttpContext http, CaritakipCloudStore store,
        CaritakipMobileChangeNotifier changeNotifier,
        IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken)
    {
        var session = await ResolveMobileAsync(http, store, cancellationToken);
        if (session is null) return Results.Unauthorized();
        if (!RequireCsrf(http, session)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220)
            return Results.BadRequest(new { message = "Müşteri adı zorunludur ve en fazla 220 karakter olabilir." });
        try
        {
            var payload = await store.BuildMobileCustomerPayloadAsync(session.TenantId, id, request, cancellationToken);
            return await ApplyMobileAsync(session, new CariMutationRequest
            {
                ClientMutationId = request.ClientMutationId, EntityType = "customer", EntityId = id ?? string.Empty,
                ExpectedVersion = request.ExpectedVersion ?? 0, Operation = "upsert", Payload = payload
            }, store, changeNotifier, hub, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> WriteTransactionAsync(
        string type, string? id, CariTransactionWriteRequest request, HttpContext http, CaritakipCloudStore store,
        CaritakipMobileChangeNotifier changeNotifier,
        IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken)
    {
        var session = await ResolveMobileAsync(http, store, cancellationToken);
        if (session is null) return Results.Unauthorized();
        if (!RequireCsrf(http, session)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (request is null || !Guid.TryParse(request.CustomerId, out var customerId)
            || request.Amount <= 0 || request.Amount > 999_999_999_999m)
            return Results.BadRequest(new { message = "Geçerli customerId ve pozitif amount zorunludur." });
        if (!await store.CustomerExistsAsync(session.TenantId, customerId.ToString(), cancellationToken))
            return Results.BadRequest(new { message = "Müşteri bulunamadı veya silinmiş." });
        var payload = JsonSerializer.SerializeToElement(new
        {
            customerId = customerId.ToString(), amount = decimal.Round(request.Amount, 2),
            transactionDateUtc = (request.TransactionDateUtc ?? DateTime.UtcNow).ToUniversalTime(),
            description = Limit(request.Description, 500)
        });
        return await ApplyMobileAsync(session, new CariMutationRequest
        {
            ClientMutationId = request.ClientMutationId, EntityType = type, EntityId = id ?? string.Empty,
            ExpectedVersion = request.ExpectedVersion, Operation = "upsert", Payload = payload
        }, store, changeNotifier, hub, cancellationToken);
    }

    private static async Task<IResult> DeleteMobileAsync(
        string type, string id, CariDeleteRequest request, HttpContext http, CaritakipCloudStore store,
        CaritakipMobileChangeNotifier changeNotifier,
        IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken)
    {
        var session = await ResolveMobileAsync(http, store, cancellationToken);
        if (session is null) return Results.Unauthorized();
        if (!RequireCsrf(http, session)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        return await ApplyMobileAsync(session, new CariMutationRequest
        {
            ClientMutationId = request?.ClientMutationId ?? string.Empty, EntityType = type, EntityId = id,
            ExpectedVersion = request?.ExpectedVersion, Operation = "delete"
        }, store, changeNotifier, hub, cancellationToken);
    }

    private static async Task<IResult> ApplyMobileAsync(
        CariMobileSessionContext session, CariMutationRequest mutation, CaritakipCloudStore store,
        CaritakipMobileChangeNotifier changeNotifier,
        IHubContext<CaritakipCloudHub> hub, CancellationToken cancellationToken)
    {
        try
        {
            var actor = "mobile:" + session.SessionHash[..16];
            var response = await store.ApplyMutationsAsync(session.TenantId, actor, new[] { mutation }, cancellationToken);
            await changeNotifier.NotifyAsync(hub, session.TenantId, actor, response, cancellationToken);
            var result = response.Results[0];
            return result.Status == "conflict"
                ? Results.Json(result, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(result);
        }
        catch (ArgumentException ex) { return Results.BadRequest(new { message = ex.Message }); }
    }

    private static Task<CariDeviceContext?> ResolveDeviceAsync(
        HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken) =>
        store.ValidateDeviceAsync(
            http.Request.Headers["X-NSX-TenantId"].ToString(),
            http.Request.Headers["X-NSX-ApiToken"].ToString(),
            http.Request.Headers["X-NSX-DeviceId"].ToString(),
            http.Request.Headers["X-NSX-MachineId"].ToString(),
            cancellationToken);

    private static async Task<CariMobileSessionContext?> ResolveMobileAsync(
        HttpContext http, CaritakipCloudStore store, CancellationToken cancellationToken)
    {
        var sessionToken = http.Request.Cookies[SessionCookie] ?? string.Empty;
        var session = await store.ValidateMobileSessionAsync(
            sessionToken, http.Request.Headers.UserAgent.ToString(), cancellationToken);
        if (session is not null && !string.IsNullOrWhiteSpace(sessionToken))
        {
            var csrf = http.Request.Cookies[CsrfCookie] ?? string.Empty;
            if (!CaritakipCloudStore.ValidateCsrf(session, csrf))
                (session, csrf) = await store.RestoreMobileCsrfAsync(session, sessionToken, cancellationToken);
            SetMobileCookies(http, sessionToken, csrf, session.ExpiresAtUtc);
        }
        return session;
    }

    private static bool RequireCsrf(HttpContext http, CariMobileSessionContext session) =>
        CaritakipCloudStore.ValidateCsrf(session, http.Request.Headers["X-NSX-CSRF"].ToString());

    private static void SetMobileCookies(HttpContext http, string session, string csrf, DateTime expiresUtc)
    {
        var secure = http.Request.IsHttps;
        http.Response.Cookies.Append(SessionCookie, session, new CookieOptions
        {
            HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax, IsEssential = true,
            Expires = new DateTimeOffset(expiresUtc), Path = "/"
        });
        http.Response.Cookies.Append(CsrfCookie, csrf, new CookieOptions
        {
            HttpOnly = false, Secure = secure, SameSite = SameSiteMode.Lax, IsEssential = true,
            Expires = new DateTimeOffset(expiresUtc), Path = "/"
        });
    }

    private static void DeleteMobileCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/" });
        http.Response.Cookies.Delete(CsrfCookie, new CookieOptions { Path = "/" });
    }

    private static void SetSecurityHeaders(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' wss: https:; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static IResult EmbeddedMobileAsset(string file)
    {
        if (!MobileAssetContentTypes.TryGetValue(file, out var contentType))
            return Results.NotFound();

        var suffix = ".wwwroot.caritakip." + file;
        var assembly = typeof(CaritakipCloudEndpointExtensions).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            return Results.Problem("Mobil panel kaynağı uygulama paketinde bulunamadı.");

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return Results.Problem("Mobil panel kaynağı okunamadı.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Results.File(buffer.ToArray(), contentType);
    }

    private static string NormalizeLicenseKey(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
    private static string NormalizeEmail(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static void PopulateSetupFromHeaders(CariTenantSetupRequest request, HttpContext http)
    {
        request.LicenseKey = FirstValue(request.LicenseKey, http.Request.Headers["X-NSX-License"]);
        request.AccountIdentity = FirstValue(request.AccountIdentity, http.Request.Headers["X-NSX-Account"]);
        request.ProductCode = FirstValue(request.ProductCode, http.Request.Headers["X-NSX-Product"]);
        request.DeviceId = FirstValue(request.DeviceId, http.Request.Headers["X-NSX-DeviceId"]);
        request.HostRef = FirstValue(request.HostRef, http.Request.Headers["X-NSX-MachineId"]);
        request.CompanyName = FirstValue(request.CompanyName, DecodeSetupIdentity(http.Request.Headers["X-NSX-Company"]));
        request.DeviceName = FirstValue(request.DeviceName, DecodeSetupIdentity(http.Request.Headers["X-NSX-DeviceName"]));
    }

    private static string FirstValue(string? primary, string? fallback) =>
        string.IsNullOrWhiteSpace(primary) ? fallback?.Trim() ?? string.Empty : primary.Trim();

    private static string? DecodeSetupIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException)
        {
            return null;
        }
    }
    private static string NormalizeProductCode(string? value) => new((value ?? string.Empty).ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static string Limit(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}".TrimEnd('/');
}
