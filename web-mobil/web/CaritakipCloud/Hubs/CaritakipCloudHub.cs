using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.CaritakipCloud.Services;

namespace NSYazilim.Web.CaritakipCloud.Hubs;

public sealed class CaritakipCloudHub : Hub
{
    private readonly CaritakipCloudStore _store;
    private readonly CaritakipNativeMobileStore _nativeStore;

    public CaritakipCloudHub(
        CaritakipCloudStore store,
        CaritakipNativeMobileStore nativeStore)
    {
        _store = store;
        _nativeStore = nativeStore;
    }

    public static string TenantGroup(string tenantId) => "caritakip:" + tenantId;
    public static string NativeDeviceGroup(string tenantId, string deviceId) =>
        $"caritakip:{tenantId}:native:{deviceId}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        if (http is null)
        {
            Context.Abort();
            return;
        }

        // Existing Windows / desktop cloud clients keep using their current
        // X-NSX-* headers. This path is intentionally left compatible.
        var tenantId = http.Request.Headers["X-NSX-TenantId"].ToString();
        var apiToken = http.Request.Headers["X-NSX-ApiToken"].ToString();
        var deviceId = http.Request.Headers["X-NSX-DeviceId"].ToString();
        var machineId = http.Request.Headers["X-NSX-MachineId"].ToString();

        if (!string.IsNullOrWhiteSpace(tenantId)
            && !string.IsNullOrWhiteSpace(apiToken)
            && !string.IsNullOrWhiteSpace(deviceId)
            && !string.IsNullOrWhiteSpace(machineId))
        {
            var device = await _store.ValidateDeviceAsync(
                tenantId,
                apiToken,
                deviceId,
                machineId,
                Context.ConnectionAborted);

            if (device is not null)
            {
                await JoinTenantAsync(device.TenantId, device.DeviceId, "desktop");
                await base.OnConnectedAsync();
                return;
            }
        }

        // Native iOS / Android clients use the same short-lived Bearer access
        // token as /api/mobile. SignalR may transport it either in the
        // Authorization header or as access_token during WebSocket/SSE setup.
        var accessToken = ResolveNativeAccessToken(http);
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            var auth = await _nativeStore.ValidateAccessTokenAsync(
                accessToken,
                Context.ConnectionAborted);

            if (auth.Session is not null)
            {
                await JoinTenantAsync(
                    auth.Session.TenantId,
                    auth.Session.MobileDeviceId,
                    "native");
                await base.OnConnectedAsync();
                return;
            }
        }

        // Never let an unvalidated connection enter a tenant group.
        Context.Abort();
    }

    private async Task JoinTenantAsync(string tenantId, string deviceId, string clientType)
    {
        Context.Items["tenantId"] = tenantId;
        Context.Items["deviceId"] = deviceId;
        Context.Items["clientType"] = clientType;

        await Groups.AddToGroupAsync(
            Context.ConnectionId,
            TenantGroup(tenantId),
            Context.ConnectionAborted);

        if (clientType == "native")
            await Groups.AddToGroupAsync(Context.ConnectionId,
                NativeDeviceGroup(tenantId, deviceId), Context.ConnectionAborted);

        await Clients.Caller.SendAsync("connected", new
        {
            tenantId,
            deviceId,
            clientType,
            serverTimeUtc = DateTime.UtcNow
        });
    }

    private static string ResolveNativeAccessToken(HttpContext http)
    {
        var authorization = http.Request.Headers.Authorization.ToString().Trim();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization[7..].Trim();

        // Official SignalR clients use this query parameter when the active
        // transport cannot attach an Authorization header (notably WebSocket).
        return http.Request.Query["access_token"].ToString().Trim();
    }
}
