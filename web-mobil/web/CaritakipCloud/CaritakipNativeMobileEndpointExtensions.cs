using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.CaritakipCloud.Hubs;
using NSYazilim.Web.CaritakipCloud.Models;
using NSYazilim.Web.CaritakipCloud.Services;

namespace NSYazilim.Web.CaritakipCloud;

public static class CaritakipNativeMobileEndpointExtensions
{
    public static void MapNsxCaritakipNativeMobile(this WebApplication app)
    {
        var api = app.MapGroup("/api/mobile").RequireRateLimiting("CaritakipCloud");
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
                return Results.Json(new { error = "SERVICE_UNAVAILABLE", message = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        api.MapPost("/pair/consume", async Task<IResult> (
            CariNativePairRequest request,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var issued = await nativeStore.ConsumeQrAsync(request?.Token ?? string.Empty,
                request?.DeviceInfo, cancellationToken);
            if (issued is null)
                return AuthError("QR_INVALID", "QR bağlantısı geçersiz, kullanılmış veya süresi dolmuş.");

            var companies = await nativeStore.GetCompaniesAsync(issued.Tenant.TenantId, cancellationToken);
            return Results.Ok(new
            {
                accessToken = issued.AccessToken,
                refreshToken = issued.RefreshToken,
                accessTokenExpiresAt = issued.AccessTokenExpiresAtUtc,
                refreshTokenExpiresAt = issued.RefreshTokenExpiresAtUtc,
                tenantId = issued.Tenant.TenantId,
                mobileDeviceId = issued.MobileDeviceId,
                companyName = issued.Tenant.CompanyName,
                pairedBy = new
                {
                    deviceId = issued.PairedByDeviceId,
                    displayName = issued.PairedByDisplayName
                },
                companies,
                defaultCompanyId = companies.FirstOrDefault()?.Id ?? string.Empty
            });
        });

        api.MapPost("/auth/refresh", async Task<IResult> (
            CariNativeRefreshRequest request,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var refreshed = await nativeStore.RefreshAsync(request?.RefreshToken ?? string.Empty, cancellationToken);
            if (refreshed.Result is null)
                return AuthError(refreshed.Error ?? "REFRESH_REVOKED", RefreshMessage(refreshed.Error));
            return Results.Ok(new
            {
                accessToken = refreshed.Result.AccessToken,
                refreshToken = refreshed.Result.RefreshToken,
                accessTokenExpiresAt = refreshed.Result.AccessTokenExpiresAtUtc,
                refreshTokenExpiresAt = refreshed.Result.RefreshTokenExpiresAtUtc
            });
        });

        api.MapPost("/auth/logout", async Task<IResult> (
            CariNativeLogoutRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            await nativeStore.LogoutAsync(auth.Session, request?.RefreshToken ?? string.Empty, cancellationToken);
            return Results.Ok(new { success = true });
        });

        api.MapPost("/pair/invite", async Task<IResult> (
            CariNativeInviteRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");

            var invite = await nativeStore.IssuePairInviteAsync(
                auth.Session, request.ExpiresInSeconds ?? 180, cancellationToken);
            return Results.Ok(new
            {
                pairCode = invite.PairCode,
                qrValue = invite.PairCode,
                expiresAtUtc = invite.ExpiresAtUtc,
                expiresInSeconds = Math.Max(0, (int)(invite.ExpiresAtUtc - DateTime.UtcNow).TotalSeconds),
                oneTime = true
            });
        });

        api.MapGet("/devices", async Task<IResult> (
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetNativeDevicesAsync(
                    auth.Session.TenantId, auth.Session.MobileDeviceId, cancellationToken));
        });

        api.MapPost("/devices/push-token", async Task<IResult> (
            CariNativePushTokenRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            if (string.IsNullOrWhiteSpace(request?.Token) || request.Token.Length > 512)
                return Results.BadRequest(new { message = "Geçerli push token zorunludur (max 512)." });

            var saved = await nativeStore.RegisterPushTokenAsync(
                auth.Session,
                request.Token,
                request.Provider ?? "expo",
                request.Platform ?? string.Empty,
                cancellationToken);
            return saved
                ? Results.Ok(new { success = true, registeredAtUtc = DateTime.UtcNow })
                : Results.NotFound(new { message = "Aktif mobil cihaz kaydı bulunamadı." });
        });

        api.MapGet("/devices/notification-prefs", async Task<IResult> (
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetNotificationPrefsAsync(auth.Session, cancellationToken));
        });

        api.MapPut("/devices/notification-prefs", async Task<IResult> (
            CariNativeNotificationPrefsDto request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            request ??= CaritakipNativeMobileStore.DefaultNotificationPrefs();
            var saved = await nativeStore.SaveNotificationPrefsAsync(auth.Session, request, cancellationToken);
            return saved
                ? Results.Ok(await nativeStore.GetNotificationPrefsAsync(auth.Session, cancellationToken))
                : Results.NotFound(new { message = "Aktif mobil cihaz kaydı bulunamadı." });
        });

        api.MapGet("/inbox", async Task<IResult> (
            int? take,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetInboxAsync(auth.Session, take ?? 50, cancellationToken));
        });

        api.MapPost("/inbox/mark-read", async Task<IResult> (
            CariNativeInboxMarkReadRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            await nativeStore.MarkInboxReadAsync(
                auth.Session, request?.Ids, request?.All ?? false, cancellationToken);
            return Results.Ok(new { success = true });
        });

        api.MapGet("/inbox/{messageId}", async Task<IResult> (
            string messageId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            var item = await nativeStore.GetInboxMessageAsync(auth.Session, messageId, cancellationToken);
            return item is null
                ? Results.NotFound(new { message = "Bildirim bulunamadı." })
                : Results.Ok(item);
        });

        api.MapPost("/inbox/delete", async Task<IResult> (
            CariNativeInboxDeleteRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            if (request is null || (!request.All && (request.Ids is null || request.Ids.Count == 0)))
                return Results.BadRequest(new { message = "all=true veya ids listesi zorunludur." });
            var deleted = await nativeStore.DeleteInboxAsync(
                auth.Session, request.Ids, request.All, cancellationToken, request.CreatedBeforeUtc);
            return Results.Ok(new { success = true, deleted });
        });

        api.MapPost("/devices/{deviceId}/revoke", async Task<IResult> (
            string deviceId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            try
            {
                var revoked = await nativeStore.RevokeNativeDeviceAsync(auth.Session, deviceId, cancellationToken);
                return revoked
                    ? Results.Ok(new { success = true, deviceId })
                    : Results.NotFound(new { message = "Mobil cihaz bulunamadı." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPost("/devices/revoke", async Task<IResult> (
            CariNativeDeviceRevokeRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            try
            {
                var revoked = await nativeStore.RevokeNativeDeviceAsync(
                    auth.Session, request.DeviceId, cancellationToken);
                return revoked
                    ? Results.Ok(new { success = true, deviceId = request.DeviceId })
                    : Results.NotFound(new { message = "Mobil cihaz bulunamadı." });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapGet("/companies", async Task<IResult> (
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetCompaniesAsync(auth.Session.TenantId, cancellationToken));
        });

        api.MapGet("/status", async Task<IResult> (
            string? companyId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetDashboardStatusAsync(
                    auth.Session.TenantId, companyId ?? string.Empty, cancellationToken));
        });

        api.MapGet("/transactions/recent", async Task<IResult> (
            string? companyId,
            int? take,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetRecentTransactionsAsync(
                    auth.Session.TenantId, companyId ?? string.Empty, take ?? 20, cancellationToken));
        });

        api.MapGet("/customers", async Task<IResult> (
            string? companyId,
            string? search,
            int? take,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetCustomersAsync(auth.Session.TenantId,
                    companyId ?? string.Empty, search ?? string.Empty, take ?? 200, cancellationToken));
        });

        api.MapGet("/customers/{customerId}", async Task<IResult> (
            string customerId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            var customer = await nativeStore.GetCustomerAsync(auth.Session.TenantId, customerId, cancellationToken);
            return customer is null ? Results.NotFound(new { message = "Müşteri bulunamadı." }) : Results.Ok(customer);
        });

        api.MapGet("/customers/{customerId}/transactions", async Task<IResult> (
            string customerId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            return auth.Session is null
                ? AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.")
                : Results.Ok(await nativeStore.GetCustomerTransactionsAsync(
                    auth.Session.TenantId, customerId, cancellationToken));
        });

        api.MapPost("/customers", async Task<IResult> (
            CariNativeCustomerWriteRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CaritakipCloudStore cloudStore,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            if (request is null || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220)
                return Results.BadRequest(new { message = "Müşteri adı zorunludur ve en fazla 220 karakter olabilir." });

            try
            {
                var webRequest = new CariCustomerWriteRequest
                {
                    ClientMutationId = MutationId(request.ClientMutationId),
                    ExpectedVersion = request.ExpectedVersion ?? 0,
                    CompanyId = request.CompanyId,
                    Name = request.Name,
                    Phone = request.Phone,
                    Email = request.Email,
                    Notes = request.Note,
                    Address = request.Address ?? string.Empty
                };
                var payload = await cloudStore.BuildMobileCustomerPayloadAsync(
                    auth.Session.TenantId, null, webRequest, cancellationToken);
                var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    new[]
                    {
                        new CariMutationRequest
                        {
                            ClientMutationId = webRequest.ClientMutationId,
                            EntityType = "customer",
                            EntityId = string.Empty,
                            ExpectedVersion = 0,
                            Operation = "upsert",
                            Payload = payload
                        }
                    }, cancellationToken);
                await changeNotifier.NotifyAsync(
                    hub,
                    auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    response,
                    cancellationToken);
                var result = response.Results[0];
                if (result.Status == "conflict")
                    return Results.Json(result, statusCode: StatusCodes.Status409Conflict);
                var customer = await nativeStore.GetCustomerAsync(
                    auth.Session.TenantId, result.EntityId, cancellationToken);
                return customer is null
                    ? Results.Json(new { message = "Müşteri kaydedildi ancak tekrar okunamadı." }, statusCode: 500)
                    : Results.Ok(customer);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapPut("/customers/{customerId}", async Task<IResult> (
            string customerId,
            CariNativeCustomerWriteRequest request,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CaritakipCloudStore cloudStore,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            if (!Guid.TryParse(customerId, out _) || request is null ||
                string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 220)
                return Results.BadRequest(new { message = "Geçerli müşteri kimliği ve müşteri adı zorunludur." });

            var current = await nativeStore.GetCustomerAsync(auth.Session.TenantId, customerId, cancellationToken);
            if (current is null) return Results.NotFound(new { message = "Müşteri bulunamadı." });
            if (!request.ExpectedVersion.HasValue || request.ExpectedVersion.Value <= 0)
                return Results.Json(new { error = "EXPECTED_VERSION_REQUIRED", message = "Düzenleme için kayıt sürümü gereklidir." }, statusCode: 428);

            try
            {
                var webRequest = new CariCustomerWriteRequest
                {
                    ClientMutationId = MutationId(request.ClientMutationId),
                    ExpectedVersion = request.ExpectedVersion,
                    CompanyId = request.CompanyId,
                    Name = request.Name,
                    Phone = request.Phone,
                    Email = request.Email,
                    Notes = request.Note,
                    Address = request.Address ?? current.Address
                };
                var payload = await cloudStore.BuildMobileCustomerPayloadAsync(
                    auth.Session.TenantId, customerId, webRequest, cancellationToken);
                var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    new[]
                    {
                        new CariMutationRequest
                        {
                            ClientMutationId = webRequest.ClientMutationId,
                            EntityType = "customer",
                            EntityId = customerId,
                            ExpectedVersion = request.ExpectedVersion,
                            Operation = "upsert",
                            Payload = payload
                        }
                    }, cancellationToken);
                await changeNotifier.NotifyAsync(
                    hub,
                    auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    response,
                    cancellationToken);
                var result = response.Results[0];
                if (result.Status == "conflict")
                    return Results.Json(result, statusCode: StatusCodes.Status409Conflict);
                var updated = await nativeStore.GetCustomerAsync(auth.Session.TenantId, customerId, cancellationToken);
                return updated is null
                    ? Results.Json(new { message = "Müşteri güncellendi ancak tekrar okunamadı." }, statusCode: 500)
                    : Results.Ok(updated);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        api.MapDelete("/customers/{customerId}", async Task<IResult> (
            string customerId,
            HttpContext http,
            CaritakipNativeMobileStore nativeStore,
            CaritakipCloudStore cloudStore,
            CaritakipMobileChangeNotifier changeNotifier,
            IHubContext<CaritakipCloudHub> hub,
            CancellationToken cancellationToken) =>
        {
            var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
            if (auth.Session is null)
                return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
            var current = await nativeStore.GetCustomerAsync(auth.Session.TenantId, customerId, cancellationToken);
            if (current is null) return Results.NotFound(new { message = "Müşteri bulunamadı." });
            var request = await ReadDeleteRequestAsync(http, cancellationToken);
            var expectedVersion = request.ExpectedVersion;
            if (!expectedVersion.HasValue || expectedVersion.Value <= 0)
                return Results.Json(new { error = "EXPECTED_VERSION_REQUIRED", message = "Silme için kayıt sürümü gereklidir." }, statusCode: 428);

            var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                "native:" + auth.Session.MobileDeviceId,
                new[]
                {
                    new CariMutationRequest
                    {
                        ClientMutationId = MutationId(request.ClientMutationId),
                        EntityType = "customer",
                        EntityId = customerId,
                        ExpectedVersion = expectedVersion,
                        Operation = "delete"
                    }
                }, cancellationToken);
            await changeNotifier.NotifyAsync(
                hub,
                auth.Session.TenantId,
                "native:" + auth.Session.MobileDeviceId,
                response,
                cancellationToken);
            var result = response.Results[0];
            return result.Status == "conflict"
                ? Results.Json(result, statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(new { success = true, entityId = customerId, version = result.Version });
        });

        foreach (var type in new[] { "debt", "collection" })
        {
            var capturedType = type;
            api.MapPost($"/{capturedType}s", async Task<IResult> (
                CariNativeTransactionWriteRequest request,
                HttpContext http,
                CaritakipNativeMobileStore nativeStore,
                CaritakipCloudStore cloudStore,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub,
                CancellationToken cancellationToken) =>
            {
                var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
                if (auth.Session is null)
                    return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
                if (request is null || !Guid.TryParse(request.CustomerId, out var customerId)
                    || request.Amount <= 0 || request.Amount > 999_999_999_999m)
                    return Results.BadRequest(new { message = "Geçerli customerId ve pozitif amount zorunludur." });

                var customer = await nativeStore.GetCustomerAsync(
                    auth.Session.TenantId, customerId.ToString(), cancellationToken);
                if (customer is null)
                    return Results.BadRequest(new { message = "Müşteri bulunamadı veya silinmiş." });
                if (!string.IsNullOrWhiteSpace(request.CompanyId) &&
                    !string.Equals(customer.CompanyId, request.CompanyId, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { message = "Müşteri seçilen firmaya ait değil." });

                var transactionDate = (request.TransactionDateUtc ?? request.Date ?? DateTime.UtcNow).ToUniversalTime();
                var payload = JsonSerializer.SerializeToElement(new
                {
                    customerId = customer.Id,
                    amount = decimal.Round(request.Amount, 2),
                    transactionDateUtc = transactionDate,
                    description = Limit(request.Description, 500)
                });
                var mutationId = MutationId(request.ClientMutationId);
                var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    new[]
                    {
                        new CariMutationRequest
                        {
                            ClientMutationId = mutationId,
                            EntityType = capturedType,
                            EntityId = string.Empty,
                            ExpectedVersion = 0,
                            Operation = "upsert",
                            Payload = payload
                        }
                    }, cancellationToken);
                await changeNotifier.NotifyAsync(
                    hub,
                    auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    response,
                    cancellationToken);
                var result = response.Results[0];
                if (result.Status == "conflict")
                    return Results.Json(result, statusCode: StatusCodes.Status409Conflict);
                return Results.Ok(new CariNativeTransactionDto
                {
                    Id = result.EntityId,
                    CompanyId = customer.CompanyId,
                    CustomerId = customer.Id,
                    CustomerName = customer.Name,
                    Kind = capturedType,
                    Amount = decimal.Round(request.Amount, 2),
                    Description = Limit(request.Description, 500),
                    Date = transactionDate,
                    CreatedAt = DateTime.UtcNow,
                    Version = result.Version
                });
            });

            api.MapPut($"/{capturedType}s/{{entityId}}", async Task<IResult> (
                string entityId,
                CariNativeTransactionWriteRequest request,
                HttpContext http,
                CaritakipNativeMobileStore nativeStore,
                CaritakipCloudStore cloudStore,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub,
                CancellationToken cancellationToken) =>
            {
                var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
                if (auth.Session is null)
                    return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
                if (!Guid.TryParse(entityId, out _) || request is null ||
                    !Guid.TryParse(request.CustomerId, out var customerId) ||
                    request.Amount <= 0 || request.Amount > 999_999_999_999m)
                    return Results.BadRequest(new { message = "Geçerli işlem, customerId ve pozitif amount zorunludur." });
                if (!request.ExpectedVersion.HasValue || request.ExpectedVersion.Value <= 0)
                    return Results.Json(new { error = "EXPECTED_VERSION_REQUIRED", message = "Düzenleme için kayıt sürümü gereklidir." }, statusCode: 428);

                var current = await nativeStore.GetTransactionAsync(auth.Session.TenantId, entityId, cancellationToken);
                if (current is null || !string.Equals(current.Kind, capturedType, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { message = "İşlem bulunamadı." });
                var customer = await nativeStore.GetCustomerAsync(auth.Session.TenantId, customerId.ToString(), cancellationToken);
                if (customer is null)
                    return Results.BadRequest(new { message = "Müşteri bulunamadı veya silinmiş." });
                if (!string.IsNullOrWhiteSpace(request.CompanyId) &&
                    !string.Equals(customer.CompanyId, request.CompanyId, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { message = "Müşteri seçilen firmaya ait değil." });

                var transactionDate = (request.TransactionDateUtc ?? request.Date ?? current.Date).ToUniversalTime();
                var payload = JsonSerializer.SerializeToElement(new
                {
                    customerId = customer.Id,
                    amount = decimal.Round(request.Amount, 2),
                    transactionDateUtc = transactionDate,
                    description = Limit(request.Description, 500)
                });
                var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    new[]
                    {
                        new CariMutationRequest
                        {
                            ClientMutationId = MutationId(request.ClientMutationId),
                            EntityType = capturedType,
                            EntityId = entityId,
                            ExpectedVersion = request.ExpectedVersion,
                            Operation = "upsert",
                            Payload = payload
                        }
                    }, cancellationToken);
                await changeNotifier.NotifyAsync(
                    hub,
                    auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    response,
                    cancellationToken);
                var result = response.Results[0];
                if (result.Status == "conflict")
                    return Results.Json(result, statusCode: StatusCodes.Status409Conflict);
                var updated = await nativeStore.GetTransactionAsync(auth.Session.TenantId, entityId, cancellationToken);
                return updated is null
                    ? Results.Json(new { message = "İşlem güncellendi ancak tekrar okunamadı." }, statusCode: 500)
                    : Results.Ok(updated);
            });

            api.MapDelete($"/{capturedType}s/{{entityId}}", async Task<IResult> (
                string entityId,
                HttpContext http,
                CaritakipNativeMobileStore nativeStore,
                CaritakipCloudStore cloudStore,
                CaritakipMobileChangeNotifier changeNotifier,
                IHubContext<CaritakipCloudHub> hub,
                CancellationToken cancellationToken) =>
            {
                var auth = await ResolveNativeAsync(http, nativeStore, cancellationToken);
                if (auth.Session is null)
                    return AuthError(auth.Error ?? "TOKEN_INVALID", "Mobil oturum doğrulanamadı.");
                var current = await nativeStore.GetTransactionAsync(auth.Session.TenantId, entityId, cancellationToken);
                if (current is null || !string.Equals(current.Kind, capturedType, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound(new { message = "İşlem bulunamadı." });
                var request = await ReadDeleteRequestAsync(http, cancellationToken);
                var expectedVersion = request.ExpectedVersion;
                if (!expectedVersion.HasValue || expectedVersion.Value <= 0)
                    return Results.Json(new { error = "EXPECTED_VERSION_REQUIRED", message = "Silme için kayıt sürümü gereklidir." }, statusCode: 428);

                var response = await cloudStore.ApplyMutationsAsync(auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    new[]
                    {
                        new CariMutationRequest
                        {
                            ClientMutationId = MutationId(request.ClientMutationId),
                            EntityType = capturedType,
                            EntityId = entityId,
                            ExpectedVersion = expectedVersion,
                            Operation = "delete"
                        }
                    }, cancellationToken);
                await changeNotifier.NotifyAsync(
                    hub,
                    auth.Session.TenantId,
                    "native:" + auth.Session.MobileDeviceId,
                    response,
                    cancellationToken);
                var result = response.Results[0];
                return result.Status == "conflict"
                    ? Results.Json(result, statusCode: StatusCodes.Status409Conflict)
                    : Results.Ok(new { success = true, entityId, version = result.Version });
            });
        }
    }


    private static async Task<CariNativeDeleteRequest> ReadDeleteRequestAsync(
        HttpContext http, CancellationToken cancellationToken)
    {
        CariNativeDeleteRequest? request = null;
        var hasBody = (http.Request.ContentLength ?? 0) > 0
            || http.Request.Headers.ContainsKey("Transfer-Encoding");

        if (hasBody)
        {
            try
            {
                request = await http.Request.ReadFromJsonAsync<CariNativeDeleteRequest>(
                    cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                // Keep DELETE startup-safe and allow query-string fallback below.
            }
        }

        request ??= new CariNativeDeleteRequest();

        if (!request.ExpectedVersion.HasValue
            && long.TryParse(http.Request.Query["expectedVersion"].ToString(), out var expectedVersion))
        {
            request.ExpectedVersion = expectedVersion;
        }

        if (string.IsNullOrWhiteSpace(request.ClientMutationId))
            request.ClientMutationId = http.Request.Query["clientMutationId"].ToString();

        return request;
    }

    private static async Task<CariNativeAuthCheck> ResolveNativeAsync(
        HttpContext http, CaritakipNativeMobileStore nativeStore, CancellationToken cancellationToken)
    {
        var authorization = http.Request.Headers.Authorization.ToString().Trim();
        var token = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..].Trim()
            : string.Empty;
        return await nativeStore.ValidateAccessTokenAsync(token, cancellationToken);
    }

    private static IResult AuthError(string error, string message) =>
        Results.Json(new { error, message }, statusCode: StatusCodes.Status401Unauthorized);

    private static string RefreshMessage(string? error) => error switch
    {
        "REFRESH_EXPIRED" => "Mobil oturum yenileme anahtarının süresi dolmuş.",
        "DEVICE_UNAUTHORIZED" => "Bu mobil cihazın erişim yetkisi kaldırılmış.",
        _ => "Mobil oturum yenileme anahtarı geçersiz veya iptal edilmiş."
    };

    private static string MutationId(string? value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length == 0 ? Guid.NewGuid().ToString("N") : Limit(value, 120);
    }

    private static string Limit(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
