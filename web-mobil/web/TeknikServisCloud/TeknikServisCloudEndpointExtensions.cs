using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.TeknikServisCloud.Hubs;
using NSYazilim.Web.TeknikServisCloud.Models;
using NSYazilim.Web.TeknikServisCloud.Services;

namespace NSYazilim.Web.TeknikServisCloud;

public static class TeknikServisCloudEndpointExtensions
{
    public static IServiceCollection AddNsxTeknikServisCloud(this IServiceCollection services)
    {
        services.AddScoped<TeknikServisMySqlStore>();
        services.AddScoped<TenantAuthService>();
        return services;
    }

    public static void MapNsxTeknikServisCloud(this WebApplication app)
    {
        app.MapHub<LiveHub>("/hubs/live");

        app.MapGet("/api/health", () => Results.Ok(new
        {
            name = "NSX Teknik Servis API",
            status = "OK",
            storage = "MySQL",
            serverTime = DateTimeOffset.Now
        }));

        app.MapGet("/health", () => Results.Ok(new
        {
            name = "NSX Teknik Servis API",
            status = "OK",
            storage = "MySQL",
            serverTime = DateTimeOffset.Now
        }));

        app.MapGet("/api/teknikservis/health", (HttpContext http) => Results.Ok(new
        {
            name = "NSX Teknik Servis API",
            status = "OK",
            storage = "MySQL",
            baseUrl = GetBaseUrl(http),
            serverTime = DateTimeOffset.Now
        }));

        app.MapGet("/api/teknikservis/db-check", IResult (TeknikServisMySqlStore store) =>
        {
            try
            {
                store.EnsureSchema();
                return Results.Ok(new
                {
                    name = "NSX Teknik Servis API",
                    status = "OK",
                    storage = "MySQL",
                    databaseReady = true,
                    hasAnyTenant = store.HasAnyTenant(),
                    trackingCount = store.CountTrackingRecords(),
                    customerMessageCount = store.CountCustomerMessages(),
                    serverTime = DateTimeOffset.Now
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    name = "NSX Teknik Servis API",
                    status = "ERROR",
                    storage = "MySQL",
                    databaseReady = false,
                    errorType = ex.GetType().Name,
                    message = ex.Message,
                    innerDetail = ex.InnerException?.Message ?? string.Empty,
                    serverTime = DateTimeOffset.Now
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/takip", () => Results.Content(BuildTrackingWelcomeHtml(), "text/html; charset=utf-8"));
        app.MapGet("/takip/{trackingToken}", IResult (string trackingToken) =>
        {
            if (string.IsNullOrWhiteSpace(trackingToken))
            {
                return Results.Redirect("/takip");
            }

            return Results.Content(BuildTrackingHtml(trackingToken.Trim()), "text/html; charset=utf-8");
        });
        app.MapGet("/s/{trackingToken}", (string trackingToken) => Results.Redirect($"/takip/{WebUtility.UrlEncode(trackingToken)}"));

        app.MapPost("/api/setup/tenants", IResult (TenantCreateRequest request, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IConfiguration config, IWebHostEnvironment env) =>
        {
            try
            {
                store.EnsureSchema();
                request ??= new TenantCreateRequest();

                if (string.IsNullOrWhiteSpace(request.FirmaId))
                {
                    request.FirmaId = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-FirmaId", "firmaId");
                }

                if (string.IsNullOrWhiteSpace(request.ApiToken))
                {
                    request.ApiToken = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-ApiToken", "apiToken");
                }

                if (string.IsNullOrWhiteSpace(request.FirmaAdi) && string.IsNullOrWhiteSpace(request.FirmaKodu))
                {
                    return Results.BadRequest(new { ok = false, message = "Firma adı veya firma kodu gerekli." });
                }

                if (string.IsNullOrWhiteSpace(request.ApiToken))
                {
                    return Results.BadRequest(new { ok = false, message = "ApiToken zorunludur." });
                }

                if (!string.IsNullOrWhiteSpace(request.FirmaId) && store.TenantExists(request.FirmaId))
                {
                    var existingTenant = store.ValidateTenant(request.FirmaId, request.ApiToken);
                    if (existingTenant is not null)
                    {
                        return Results.Ok(new TenantCreateResponse
                        {
                            FirmaId = existingTenant.FirmaId,
                            FirmaKodu = existingTenant.FirmaKodu,
                            FirmaAdi = existingTenant.FirmaAdi,
                            ApiToken = request.ApiToken,
                            Message = "Firma cloud bağlantısı zaten hazır."
                        });
                    }

                    if (!IsTenantTokenRepairAllowed(http, config, env))
                    {
                        return Results.Unauthorized();
                    }
                }

                var response = store.CreateTenant(request);
                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                return Results.Json(new
                {
                    ok = false,
                    message = "Firma cloud bağlantısı hazırlanırken web tarafında hata oluştu.",
                    errorType = ex.GetType().Name,
                    detail = ex.Message,
                    innerDetail = ex.InnerException?.Message ?? string.Empty,
                    serverTime = DateTimeOffset.Now
                }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        var api = app.MapGroup("/api/v1");

        api.MapGet("/tenant/me", IResult (HttpContext http, TenantAuthService auth) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new TenantInfoResponse
            {
                FirmaId = tenant.FirmaId,
                FirmaKodu = tenant.FirmaKodu,
                FirmaAdi = tenant.FirmaAdi,
                ServerTime = DateTimeOffset.Now
            });
        });

        api.MapGet("/data/{entityType}", IResult (string entityType, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(store.GetDataItems(tenant.FirmaId, entityType));
        });

        api.MapPut("/data/{entityType}/{entityId}", async Task<IResult> (string entityType, string entityId, TenantDataUpsertRequest request, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IHubContext<LiveHub> hub) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            var changeId = store.UpsertDataItem(tenant.FirmaId, entityType, entityId, request);
            await hub.Clients.Group(LiveHub.GroupName(tenant.FirmaId)).SendAsync("nsxChange", new LiveChangeMessage
            {
                ChangeId = changeId,
                FirmaId = tenant.FirmaId,
                EntityType = entityType,
                EntityId = entityId,
                Action = "upsert",
                PayloadJson = request?.PayloadJson ?? string.Empty,
                SourceDeviceId = request?.SourceDeviceId ?? string.Empty,
                SourceUser = request?.SourceUser ?? string.Empty,
                CreatedAt = DateTimeOffset.Now
            });
            return Results.Ok(new { changeId });
        });

        api.MapDelete("/data/{entityType}/{entityId}", async Task<IResult> (string entityType, string entityId, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IHubContext<LiveHub> hub) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            var sourceDeviceId = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-DeviceId", "deviceId");
            var sourceUser = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-User", "user");
            var changeId = store.DeleteDataItem(tenant.FirmaId, entityType, entityId, sourceDeviceId, sourceUser);
            await hub.Clients.Group(LiveHub.GroupName(tenant.FirmaId)).SendAsync("nsxChange", new LiveChangeMessage
            {
                ChangeId = changeId,
                FirmaId = tenant.FirmaId,
                EntityType = entityType,
                EntityId = entityId,
                Action = "delete",
                SourceDeviceId = sourceDeviceId,
                SourceUser = sourceUser,
                CreatedAt = DateTimeOffset.Now
            });
            return Results.Ok(new { changeId });
        });

        api.MapPost("/sync/push", async Task<IResult> (SyncChangeRequest request, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IHubContext<LiveHub> hub) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            var changeId = store.AppendSyncChange(tenant.FirmaId, request);
            var message = new LiveChangeMessage
            {
                ChangeId = changeId,
                FirmaId = tenant.FirmaId,
                EntityType = request.EntityType,
                EntityId = request.EntityId,
                Action = request.Action,
                PayloadJson = request.PayloadJson,
                SourceDeviceId = request.SourceDeviceId,
                SourceUser = request.SourceUser,
                CreatedAt = DateTimeOffset.Now
            };
            await hub.Clients.Group(LiveHub.GroupName(tenant.FirmaId)).SendAsync("nsxChange", message);
            return Results.Ok(message);
        });

        api.MapGet("/sync/changes", IResult (long? afterId, int? take, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(store.GetChanges(tenant.FirmaId, afterId ?? 0, take ?? 100));
        });

        api.MapPost("/tracking/publish", async Task<IResult> (TrackingPublishRequest request, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IHubContext<LiveHub> hub, IConfiguration config, IWebHostEnvironment env) =>
        {
            try
            {
                var tenant = auth.TryResolve(http) ?? TryRepairTenantForPublish(http, request, store, config, env);
                if (tenant is null)
                {
                    return Results.Unauthorized();
                }

                var tracking = store.PublishTracking(tenant.FirmaId, request);
                await hub.Clients.Group(LiveHub.GroupName(tenant.FirmaId)).SendAsync("nsxTrackingUpdated", tracking);
                return Results.Ok(tracking);
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = "QR takip kaydı web tarafında yazılamadı.", errorType = ex.GetType().Name, detail = ex.Message, innerDetail = ex.InnerException?.Message ?? string.Empty }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/teknikservis/tracking/publish", async Task<IResult> (TrackingPublishRequest request, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store, IHubContext<LiveHub> hub, IConfiguration config, IWebHostEnvironment env) =>
        {
            try
            {
                var tenant = auth.TryResolve(http) ?? TryRepairTenantForPublish(http, request, store, config, env);
                if (tenant is null)
                {
                    return Results.Json(new { ok = false, message = "Firma doğrulaması yapılamadı. X-NSX-FirmaId ve X-NSX-ApiToken başlıklarını kontrol edin." }, statusCode: StatusCodes.Status401Unauthorized);
                }

                if (request is null || string.IsNullOrWhiteSpace(request.TrackingToken) || string.IsNullOrWhiteSpace(request.ServiceNo))
                {
                    return Results.BadRequest(new { ok = false, message = "TrackingToken ve ServiceNo zorunludur." });
                }

                var tracking = store.PublishTracking(tenant.FirmaId, request);
                await hub.Clients.Group(LiveHub.GroupName(tenant.FirmaId)).SendAsync("nsxTrackingUpdated", tracking);
                return Results.Ok(new { ok = true, message = "QR takip kaydı MySQL içine yazıldı.", trackingToken = tracking.TrackingToken, serviceNo = tracking.ServiceNo, firmaId = tracking.FirmaId, updatedAt = tracking.UpdatedAt });
            }
            catch (Exception ex)
            {
                return Results.Json(new { ok = false, message = "QR takip kaydı web tarafında yazılamadı.", errorType = ex.GetType().Name, detail = ex.Message, innerDetail = ex.InnerException?.Message ?? string.Empty, serverTime = DateTimeOffset.Now }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/api/teknikservis/tracking/exists/{trackingToken}", IResult (string trackingToken, TeknikServisMySqlStore store) =>
        {
            try
            {
                var tracking = store.GetTrackingByToken(trackingToken);
                return Results.Ok(new { found = tracking is not null, token = trackingToken, serviceNo = tracking?.ServiceNo ?? string.Empty, updatedAt = tracking?.UpdatedAt ?? string.Empty, serverTime = DateTimeOffset.Now });
            }
            catch (Exception ex)
            {
                return Results.Json(new { found = false, token = trackingToken, errorType = ex.GetType().Name, detail = ex.Message, innerDetail = ex.InnerException?.Message ?? string.Empty }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        api.MapGet("/tracking/messages", IResult (bool? onlyNew, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(store.GetCustomerMessages(tenant.FirmaId, onlyNew ?? true));
        });

        api.MapPost("/tracking/messages/mark-pulled", IResult (long[] ids, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            store.MarkMessagesPulled(tenant.FirmaId, ids);
            return Results.Ok(new { marked = ids?.Length ?? 0 });
        });

        api.MapPost("/tracking/messages/delete", IResult (long[] ids, HttpContext http, TenantAuthService auth, TeknikServisMySqlStore store) =>
        {
            var tenant = auth.TryResolve(http);
            if (tenant is null)
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { deleted = store.DeleteCustomerMessagesForDesktop(tenant.FirmaId, ids) });
        });

        app.MapGet("/api/public/tracking/{trackingToken}", IResult (string trackingToken, TeknikServisMySqlStore store) =>
        {
            try
            {
                var tracking = store.GetTrackingByToken(trackingToken);
                if (tracking is null)
                {
                    return Results.NotFound(new { message = "Takip kaydı henüz bulunamadı. Masaüstü programdan QR kayıtları gönder deyip tekrar deneyin." });
                }

                var latestAction = store.GetLatestCustomerAction(tracking.TrackingToken);
                tracking.Status = ToCustomerVisibleStatus(tracking.Status);
                var customerActionRequired = ShouldShowCustomerAction(tracking, latestAction);
                var actionState = GetActionState(tracking, latestAction, customerActionRequired);

                return Results.Ok(new
                {
                    trackingToken = tracking.TrackingToken,
                    companyName = tracking.CompanyName,
                    companyPhone = ExtractCompanyPhone(tracking.PayloadJson),
                    serviceNo = tracking.ServiceNo,
                    customerName = tracking.CustomerName,
                    phoneMasked = tracking.PhoneMasked,
                    deviceTitle = tracking.DeviceTitle,
                    status = tracking.Status,
                    priceText = tracking.PriceText,
                    publicNote = tracking.PublicNote,
                    updatedAt = tracking.UpdatedAt,
                    customerActionRequired,
                    actionContextKey = BuildActionContextKey(tracking),
                    actionState = actionState.State,
                    actionStateText = actionState.Text,
                    guidance = BuildGuidance(tracking),
                    latestCustomerAction = latestAction is null ? null : new { messageType = latestAction.MessageType, messageText = latestAction.MessageText, actionContextKey = latestAction.ActionContextKey, createdAt = latestAction.CreatedAt },
                    timeline = BuildTimeline(tracking, latestAction),
                    refreshSeconds = 5
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { message = "Takip kaydı okunamadı.", errorType = ex.GetType().Name, detail = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPost("/api/public/tracking/{trackingToken}/messages", async Task<IResult> (string trackingToken, CustomerMessageRequest request, TeknikServisMySqlStore store, IHubContext<LiveHub> hub) =>
        {
            try
            {
                var message = store.AddCustomerMessage(trackingToken, request);
                await hub.Clients.Group(LiveHub.GroupName(message.FirmaId)).SendAsync("nsxCustomerMessage", message);
                return Results.Ok(message);
            }
            catch (Exception ex)
            {
                return Results.Json(new { message = "Müşteri mesajı kaydedilemedi.", errorType = ex.GetType().Name, detail = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
    }

    private static TenantContext? TryRepairTenantForPublish(HttpContext http, TrackingPublishRequest? request, TeknikServisMySqlStore store, IConfiguration config, IWebHostEnvironment env)
    {
        if (!IsTenantTokenRepairAllowed(http, config, env))
        {
            return null;
        }

        var firmaId = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-FirmaId", "firmaId");
        var apiToken = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-ApiToken", "apiToken");
        var firmaKodu = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-FirmaKodu", "firmaKodu");
        var firmaAdi = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-FirmaAdi", "firmaAdi");

        if (string.IsNullOrWhiteSpace(firmaId) || string.IsNullOrWhiteSpace(apiToken))
        {
            return null;
        }

        var response = store.CreateTenant(new TenantCreateRequest
        {
            FirmaId = firmaId,
            FirmaKodu = string.IsNullOrWhiteSpace(firmaKodu) ? firmaId : firmaKodu,
            FirmaAdi = string.IsNullOrWhiteSpace(firmaAdi) ? request?.CompanyName ?? firmaId : firmaAdi,
            ApiToken = apiToken
        });
        return new TenantContext(response.FirmaId, response.FirmaKodu, response.FirmaAdi);
    }

    private static bool IsTenantTokenRepairAllowed(HttpContext http, IConfiguration config, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            return true;
        }

        if (IsSetupKeyValid(http, config))
        {
            return true;
        }

        return string.Equals(config["TeknikServisCloud:AllowTenantTokenRepair"], "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSetupKeyValid(HttpContext http, IConfiguration config)
    {
        var setupKey = config["TeknikServisCloud:SetupKey"] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(setupKey))
        {
            return false;
        }

        var incomingKey = TenantAuthService.ReadHeaderOrQuery(http, "X-NSX-SetupKey", "setupKey");
        return string.Equals(setupKey, incomingKey, StringComparison.Ordinal);
    }

    private static string GetBaseUrl(HttpContext http)
    {
        return $"{http.Request.Scheme}://{http.Request.Host}".TrimEnd('/');
    }

    private static string BuildTrackingWelcomeHtml()
    {
    return """
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>NSX Teknik Servis Takip</title>
  <style>
    :root{--bg:#eef7fa;--card:#fff;--line:#d7e8ee;--text:#083448;--muted:#607987;--accent:#0a7699;--accent2:#10b7c7;--shadow:0 22px 60px rgba(8,52,72,.14)}
    *{box-sizing:border-box}body{margin:0;font-family:Segoe UI,Arial,sans-serif;background:radial-gradient(circle at top left,#dff6fb 0,#f7fbfc 42%,#eef7fa 100%);color:var(--text)}
    .wrap{max-width:860px;margin:0 auto;padding:28px 16px 42px}.hero{background:linear-gradient(135deg,#08728d,#12bdd0);color:white;border-radius:28px;padding:28px;box-shadow:var(--shadow);overflow:hidden;position:relative}.hero:after{content:"";position:absolute;right:-70px;top:-80px;width:190px;height:190px;border-radius:50%;background:rgba(255,255,255,.14)}
    .badge{display:inline-flex;gap:8px;align-items:center;background:rgba(255,255,255,.16);border:1px solid rgba(255,255,255,.34);border-radius:999px;padding:8px 12px;font-weight:800;font-size:13px}.hero h1{margin:18px 0 8px;font-size:30px;letter-spacing:-.5px}.hero p{max-width:620px;line-height:1.6;margin:0;color:rgba(255,255,255,.88)}
    .card{background:var(--card);border:1px solid var(--line);border-radius:24px;padding:22px;margin-top:16px;box-shadow:0 12px 32px rgba(8,52,72,.08)}.card strong{color:var(--accent)}.hint{color:var(--muted);line-height:1.6;margin:0}
  </style>
</head>
<body>
  <main class="wrap">
    <section class="hero"><span class="badge">🔎 Canlı servis takip</span><h1>Takip ekranı hazır.</h1><p>Servis fişindeki QR kod okutulduğunda cihazın güncel servis durumu, onay işlemleri ve bilgilendirme akışı bu ekranda görüntülenir.</p></section>
    <section class="card"><p class="hint">Takip bağlantısı formatı: <strong>www.nsxyazilim.com/takip/TOKEN</strong></p></section>
  </main>
</body>
</html>
""";
}

    private static string BuildTrackingHtml(string trackingToken)
    {
    var safeToken = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(trackingToken);
    return """
<!DOCTYPE html>
<html lang="tr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>Servis Takip - NSX Teknik Servis</title>
<style>
:root{--bg:#edf7fa;--card:#ffffff;--line:#d6e8ee;--text:#083448;--muted:#5f7887;--accent:#0a7699;--accent2:#12bfd0;--ok:#159b66;--warn:#d9822b;--danger:#c8453b;--soft:#eff9fb;--shadow:0 18px 44px rgba(8,52,72,.10)}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent}body{margin:0;font-family:Segoe UI,Arial,sans-serif;background:radial-gradient(circle at top left,#dff8fc 0,#f6fbfc 42%,#edf7fa 100%);color:var(--text)}button,input,textarea{font:inherit}.wrap{max-width:960px;margin:0 auto;padding:12px 12px 32px}.hero{position:relative;overflow:hidden;border-radius:18px;padding:12px 16px;background:linear-gradient(135deg,#08728d,#10b7c7);color:#fff;box-shadow:0 10px 24px rgba(8,52,72,.12);text-align:center}.hero:before{display:none}.hero h1{position:relative;margin:0;font-size:21px;line-height:1.18;letter-spacing:-.25px}.grid{display:grid;grid-template-columns:1.1fr .9fr;gap:14px;margin-top:14px}.card{background:var(--card);border:1px solid var(--line);border-radius:24px;padding:18px;box-shadow:var(--shadow)}h2{font-size:19px;margin:0 0 12px}.muted{color:var(--muted);line-height:1.45}.status{display:inline-flex;align-items:center;gap:8px;border-radius:999px;padding:8px 12px;font-weight:950;background:#f1f7f9;border:1px solid var(--line);color:var(--accent);margin-bottom:13px}.status.ok{background:#effaf5;border-color:#cceedd;color:var(--ok)}.status.warn{background:#fff8ed;border-color:#f3d9ad;color:var(--warn)}.status.danger{background:#fff2f1;border-color:#efcac5;color:var(--danger)}.rows{display:grid;gap:9px}.row{display:grid;grid-template-columns:118px 1fr;gap:9px;padding:11px 12px;border:1px solid #edf4f6;background:#fbfdfe;border-radius:16px}.label{color:var(--muted);font-size:12.5px}.value{font-weight:850;word-break:break-word}.note{margin-top:12px;border-left:4px solid var(--accent);background:#f6fbfc;border-radius:14px;padding:12px;color:#315064;line-height:1.45;white-space:pre-line}.livebar{margin:0 0 13px;padding:11px;border-radius:16px;border:1px solid #d2e9ef;background:#f5fbfc;display:flex;align-items:center;gap:9px;color:#315064;font-weight:800;font-size:13px}.pulse{width:9px;height:9px;border-radius:50%;background:var(--ok);box-shadow:0 0 0 0 rgba(21,155,102,.45);animation:pulse 1.8s infinite}@keyframes pulse{70%{box-shadow:0 0 0 10px rgba(21,155,102,0)}}.guidance{margin-top:12px;border:1px solid #d5e7ed;background:#f6fbfc;border-radius:18px;padding:13px;display:grid;grid-template-columns:36px 1fr;gap:10px;align-items:start}.gicon{width:34px;height:34px;border-radius:13px;background:#eaf5f8;display:grid;place-items:center;font-size:17px}.gtitle{font-weight:950;margin-bottom:3px}.gtext{color:#315064;line-height:1.45;font-size:13px}.guidance.ok{border-color:#cceedd;background:#f3fbf7}.guidance.warn{border-color:#f5ddbd;background:#fffaf2}.guidance.danger{border-color:#f0cac5;background:#fff6f5}.actions{display:grid;gap:10px}.btn{width:100%;border:0;border-radius:17px;padding:14px 15px;font-weight:950;cursor:pointer;background:#eef5f8;color:#123246;box-shadow:0 8px 18px rgba(8,52,72,.07);touch-action:manipulation}.btn.primary{background:linear-gradient(135deg,var(--accent),var(--accent2));color:#fff}.btn.ok{background:#eaf8f0;color:var(--ok)}.btn.danger{background:#fff0ef;color:var(--danger)}.btn:disabled{opacity:.55;cursor:not-allowed}.msg{margin-top:12px;color:#08728d;font-size:13px;font-weight:850;line-height:1.45}.readonly,.warn{border:1px solid #dde9ee;background:#f7fafb;border-radius:18px;padding:14px;color:#315064;line-height:1.5}.warn{border-color:#f3d9ad;background:#fff8ed;color:#744c00}.timeline{display:grid;gap:10px}.tl{display:grid;grid-template-columns:34px 1fr;gap:10px}.dot{width:32px;height:32px;border-radius:13px;background:#eaf5f8;color:var(--accent);display:grid;place-items:center;font-weight:950}.tlbody{border:1px solid #edf4f6;background:#fbfdfe;border-radius:16px;padding:11px}.tltitle{font-weight:950}.tldate{font-size:12px;color:var(--muted);margin-top:3px}.tldesc{font-size:13px;color:#315064;margin-top:6px;line-height:1.4;white-space:pre-line}.loading{text-align:center;color:var(--muted);padding:28px 8px}.foot{text-align:center;color:var(--muted);font-size:12px;margin-top:16px}@media(max-width:760px){.wrap{padding:10px 10px 24px}.grid{grid-template-columns:1fr}.hero,.card{border-radius:18px}.hero{padding:11px 14px}.hero h1{font-size:20px}.row{grid-template-columns:1fr;gap:4px;padding:10px}.card{padding:16px}.btn{padding:15px}}
</style>
</head>
<body>
<div class="wrap">
  <section class="hero"><h1 id="companyNameTop">Servis takip ekranı</h1></section>
  <section class="grid"><div class="card" id="info"><div class="loading">Servis bilgisi yükleniyor...</div></div><aside class="card" id="action"><div class="loading">İşlem alanı hazırlanıyor...</div></aside></section>
  <section class="card" style="margin-top:14px"><h2>Canlı durum akışı</h2><div id="timeline" class="timeline"><div class="muted">Takip kaydı bekleniyor.</div></div></section>
  <div class="foot">NSX Teknik Servis Takip Sistemi</div>
</div>
<script>
var token='__NSX_TRACKING_TOKEN__';var current=null;var sending=false;var refreshTimer=null;
function safe(v){return(v===null||v===undefined)?'':String(v);}function esc(v){return safe(v).replace(/[&<>"']/g,function(c){return {'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c];});}function fmt(v){var d=new Date(v);return isNaN(d.getTime())?safe(v):d.toLocaleString('tr-TR');}function apiBase(){return window.location.origin||(window.location.protocol+'//'+window.location.host);}function statusClass(s){var t=safe(s).toLocaleLowerCase('tr-TR');if(t.indexOf('tamam')>=0||t.indexOf('teslim')>=0)return'ok';if(t.indexOf('red')>=0||t.indexOf('iptal')>=0)return'danger';if(t.indexOf('onay')>=0||t.indexOf('fiyat')>=0||t.indexOf('bekl')>=0)return'warn';return'';}function row(k,v){return'<div class="row"><div class="label">'+esc(k)+'</div><div class="value">'+esc(v||'-')+'</div></div>';}
function actionSentKey(t,type){return'nsx-ts-action-sent:'+token+':'+(t&&t.actionContextKey?t.actionContextKey:'default')+':'+type;}function hasSentAction(t){try{return localStorage.getItem(actionSentKey(t,'Onay'))||localStorage.getItem(actionSentKey(t,'Red'))||localStorage.getItem(actionSentKey(t,'Bilgi'));}catch(e){return false;}}
function scheduleRefresh(sec){if(refreshTimer){clearTimeout(refreshTimer);}if(!sending){refreshTimer=setTimeout(load,Math.max(4,sec||5)*1000);}}function updateHero(t){document.getElementById('companyNameTop').textContent=t.companyName||'Servis takip ekranı';}
function load(){if(refreshTimer){clearTimeout(refreshTimer);refreshTimer=null;}if(sending){return;}fetch(apiBase()+'/api/public/tracking/'+encodeURIComponent(token),{cache:'no-store'}).then(function(r){if(!r.ok){throw new Error('HTTP '+r.status);}return r.json();}).then(render).catch(function(){document.getElementById('info').innerHTML='<h2>Servis bilgisi henüz alınamadı.</h2><div class="warn">API çalışıyor ama bu QR token için takip kaydı bulunamadı veya okunamadı.<br><br><small>Token: '+esc(token)+'</small></div><button type="button" class="btn primary" id="btnRetry">🔄 Tekrar Dene</button>';document.getElementById('action').innerHTML='<div class="readonly"><b>Takip kaydı bekleniyor.</b><br>Servis bilgisi gelmeden işlem gönderilemez.</div>';document.getElementById('timeline').innerHTML='<div class="muted">Takip kaydı bekleniyor.</div>';var retry=document.getElementById('btnRetry');if(retry)retry.addEventListener('click',load,{once:true});scheduleRefresh(8);});}
function render(t){current=t;updateHero(t);var status=t.status||'Kayıt alındı';var html='<span class="status '+statusClass(status)+'">📌 '+esc(status)+'</span><div class="livebar"><span class="pulse"></span><span>Servis ekibi güncelledikçe bu ekran otomatik yenilenir.</span></div><div class="rows">'+row('Servis No',t.serviceNo)+row('Müşteri',t.customerName)+row('Telefon',t.phoneMasked)+row('Cihaz',t.deviceTitle)+row('Tutar / Onay',t.priceText)+row('Son Güncelleme',fmt(t.updatedAt))+'</div>';if(t.publicNote){html+='<div class="note">'+esc(t.publicNote)+'</div>';}html+=renderGuidance(t.guidance);document.getElementById('info').innerHTML=html;renderAction(t);renderTimeline(t.timeline||[]);scheduleRefresh(t.refreshSeconds||5);}
function renderGuidance(g){if(!g)return'';var tone=safe(g.tone||'');var cls=tone?' '+tone:'';return'<div class="guidance'+cls+'"><div class="gicon">'+esc(g.icon||'ℹ️')+'</div><div><div class="gtitle">'+esc(g.title||'Bilgilendirme')+'</div><div class="gtext">'+esc(g.text||'')+'</div></div></div>';}
function renderAction(t){if(t.customerActionRequired){if(hasSentAction(t)){document.getElementById('action').innerHTML='<h2>İşlem Onayı</h2><div class="warn">İşleminiz teknik servise iletildi. Yeni bir fiyat/onay talebi açılırsa bu alanda tekrar işlem butonları görünür.</div>';return;}document.getElementById('action').innerHTML='<h2>İşlem Onayı</h2><p class="muted">Servis ekibi bu aşama için sizden işlem tercihi bekliyor.</p><div class="actions"><button type="button" id="btnOnay" class="btn ok" data-action="Onay" data-text="Müşteri servis işlemini onayladı.">✅ Onaylıyorum</button><button type="button" id="btnRed" class="btn danger" data-action="Red" data-text="Müşteri servis işlemini reddetti.">❌ Reddediyorum</button><button type="button" id="btnBilgi" class="btn" data-action="Bilgi" data-text="Müşteri ek bilgi talep ediyor.">ℹ️ Bilgi İstiyorum</button></div><div id="result" class="msg"></div>';}else{document.getElementById('action').innerHTML='<h2>Servis Akışı</h2><div class="readonly"><b>'+esc(t.actionStateText||'Bu aşamada müşteri onayı gerekmiyor.')+'</b><br><br>Yeni bilgi, fiyat veya durum güncellemesi olduğunda ekran otomatik yenilenir.</div>';}}
function renderTimeline(items){var html='';for(var i=0;i<items.length;i++){var it=items[i]||{};html+='<div class="tl"><div class="dot">'+esc(it.icon||'•')+'</div><div class="tlbody"><div class="tltitle">'+esc(it.title||'Güncelleme')+'</div><div class="tldate">'+esc(fmt(it.createdAt))+'</div>'+(it.description?'<div class="tldesc">'+esc(it.description)+'</div>':'')+'</div></div>';}document.getElementById('timeline').innerHTML=html||'<div class="muted">Durum akışı bekleniyor.</div>';}
function lockButtons(lock){['btnOnay','btnRed','btnBilgi'].forEach(function(id){var b=document.getElementById(id);if(b)b.disabled=lock;});}function send(type,text){if(sending||!current)return;var key=actionSentKey(current,type);try{if(localStorage.getItem(key)){var already=document.getElementById('result');if(already)already.textContent='Bu işlem zaten teknik servise iletildi.';return;}}catch(e){}sending=true;if(refreshTimer){clearTimeout(refreshTimer);refreshTimer=null;}lockButtons(true);var result=document.getElementById('result');if(result)result.textContent='Gönderiliyor...';fetch(apiBase()+'/api/public/tracking/'+encodeURIComponent(token)+'/messages',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({messageType:type,messageText:text,customerName:(current&&current.customerName)||'',customerPhone:(current&&current.phoneMasked)||'',actionContextKey:(current&&current.actionContextKey)||''})}).then(function(r){if(!r.ok)throw new Error('HTTP '+r.status);return r.json();}).then(function(){try{localStorage.setItem(key,'1');}catch(e){}if(result)result.textContent='İşleminiz teknik servise iletildi.';current.customerActionRequired=false;renderAction(current);sending=false;scheduleRefresh(2);}).catch(function(){sending=false;lockButtons(false);if(result)result.textContent='İşlem gönderilemedi. Lütfen tekrar deneyin.';scheduleRefresh(5);});}
document.getElementById('action').addEventListener('click',function(e){var b=e.target.closest('button[data-action]');if(!b)return;e.preventDefault();send(b.getAttribute('data-action'),b.getAttribute('data-text')||'');});load();
</script>
</body>
</html>
""".Replace("__NSX_TRACKING_TOKEN__", safeToken);
}

    private static string MaskCustomerName(string customerName)
    {
        var trimmed = (customerName ?? string.Empty).Trim();
        if (trimmed.Length <= 2) return trimmed;
        var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts.Select(part => part.Length <= 2 ? part : part[..Math.Min(3, part.Length)] + "..."));
    }

    private static string ToCustomerVisibleStatus(string status)
    {
        var normalized = NormalizeText(status);
        if (normalized.Contains("tamamlandi") || normalized.Contains("tamamlandı"))
        {
            return "Teslim Edildi";
        }

        return string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim();
    }

    private static string BuildActionContextKey(TrackingRecordResponse tracking)
    {
        // Aynı QR token içinde servis yeniden onay/fiyat beklemeye alınabilir.
        // Sadece durum/metin aynı kaldığında eski müşteri cevabı yeni turu kilitlemesin diye UpdatedAt da anahtara dahil edildi.
        return NormalizeText($"{tracking.Status}|{tracking.PriceText}|{tracking.PublicNote}|{tracking.UpdatedAt}");
    }

    private static bool ShouldShowCustomerAction(TrackingRecordResponse tracking, CustomerMessageResponse? latestAction)
    {
        if (!IsCustomerActionRequired(tracking)) return false;
        if (latestAction is null) return true;
        if (string.IsNullOrWhiteSpace(latestAction.ActionContextKey)) return true;
        return !string.Equals(BuildActionContextKey(tracking), latestAction.ActionContextKey.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCustomerActionRequired(TrackingRecordResponse tracking)
    {
        var text = NormalizeText(tracking.Status + " " + tracking.PriceText + " " + tracking.PublicNote);

        // Sonuç/final durumlarda buton açma. Onarımda tek başına final değildir;
        // metinde onay/fiyat bekleme varsa butonlar yeniden görünmelidir.
        if (text.Contains("teslim edildi")
            || text.Contains("teslime hazir")
            || text.Contains("tamamlandi")
            || text.Contains("kapatildi")
            || text.Contains("iptal")
            || text.Contains("onaylandi"))
        {
            return false;
        }

        var hasApprovalOrPrice = text.Contains("onay") || text.Contains("fiyat") || text.Contains("ucret") || text.Contains("ücret");
        var hasWaitingWord = text.Contains("bekleniyor") || text.Contains("bekliyor") || text.Contains("gerekli") || text.Contains("gerekiyor") || text.Contains("talep");

        return hasApprovalOrPrice && hasWaitingWord;
    }

    private static (string State, string Text) GetActionState(TrackingRecordResponse tracking, CustomerMessageResponse? latestAction, bool customerActionRequired)
    {
        if (customerActionRequired) return ("waiting", "Servis ekibi bu aşamada sizden onay veya bilgi bekliyor.");
        if (latestAction is not null)
        {
            if (latestAction.MessageType.Equals("Onay", StringComparison.OrdinalIgnoreCase)) return ("approved", "Onayınız teknik servise iletildi.");
            if (latestAction.MessageType.Equals("Red", StringComparison.OrdinalIgnoreCase)) return ("rejected", "Red cevabınız teknik servise iletildi.");
            return ("info", "Bilgi talebiniz teknik servise iletildi.");
        }
        return ("readonly", "Bu aşamada müşteri onayı gerekmiyor.");
    }

    private static object[] BuildTimeline(TrackingRecordResponse tracking, CustomerMessageResponse? latestAction)
    {
        var items = new List<object>
        {
            new { icon = "•", title = string.IsNullOrWhiteSpace(tracking.Status) ? "Servis kaydı güncellendi" : tracking.Status.Trim(), description = string.IsNullOrWhiteSpace(tracking.PublicNote) ? "Servis ekibi güncel durumu paylaştı." : tracking.PublicNote.Trim(), createdAt = tracking.UpdatedAt }
        };

        if (latestAction is not null)
        {
            items.Insert(0, new { icon = latestAction.MessageType.Equals("Onay", StringComparison.OrdinalIgnoreCase) ? "✓" : latestAction.MessageType.Equals("Red", StringComparison.OrdinalIgnoreCase) ? "!" : "i", title = latestAction.MessageType.Equals("Onay", StringComparison.OrdinalIgnoreCase) ? "Müşteri onayı alındı" : latestAction.MessageType.Equals("Red", StringComparison.OrdinalIgnoreCase) ? "Müşteri red cevabı verdi" : "Müşteri bilgi talebi gönderdi", description = latestAction.MessageText, createdAt = latestAction.CreatedAt });
        }

        return items.ToArray();
    }

    private static object BuildGuidance(TrackingRecordResponse tracking)
    {
        var text = NormalizeText(tracking.Status + " " + tracking.PublicNote + " " + tracking.PriceText);
        if (((text.Contains("teslim") || text.Contains("teslime")) && (text.Contains("hazir") || text.Contains("hazır")))
            || text.Contains("teslim edildi")
            || text.Contains("tamamlandi")
            || text.Contains("tamamlandı"))
        {
            return new { icon = "📦", title = "Teslim Bilgisi", text = "Cihazınız teslim edildi veya teslim aşamasına alındı. Gerekli durumlarda teknik servis ekibi sizinle iletişime geçecektir.", tone = "ok" };
        }
        return new { icon = "🔧", title = "Servis Durumu", text = "Servis ekibi güncel durum bilgisini bu ekranda paylaşır.", tone = "info" };
    }

    private static string ExtractCompanyPhone(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return string.Empty;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            foreach (var path in new[]
            {
                "Company.Phone",
                "Company.Telefon",
                "Company.FirmaTelefonu",
                "Company.FirmaTelefon",
                "CompanyPhone",
                "FirmaTelefonu",
                "FirmaTelefon",
                "Phone",
                "Telefon"
            })
            {
                if (TryReadPayloadString(document.RootElement, path, out var value))
                {
                    return value;
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private static bool TryReadPayloadString(System.Text.Json.JsonElement root, string path, out string value)
    {
        value = string.Empty;
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return false;
            }

            var found = false;
            foreach (var property in current.EnumerateObject())
            {
                if (!string.Equals(property.Name, part, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                current = property.Value;
                found = true;
                break;
            }

            if (!found)
            {
                return false;
            }
        }

        if (current.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            value = current.GetString()?.Trim() ?? string.Empty;
        }
        else if (current.ValueKind is System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            value = current.ToString().Trim();
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static string NormalizeText(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('ğ', 'g').Replace('ü', 'u').Replace('ş', 's').Replace('ö', 'o').Replace('ç', 'c');
    }
}
