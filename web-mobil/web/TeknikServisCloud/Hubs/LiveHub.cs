using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.TeknikServisCloud.Services;

namespace NSYazilim.Web.TeknikServisCloud.Hubs;

public sealed class LiveHub : Hub
{
    private readonly TenantAuthService _tenantAuthService;

    public LiveHub(TenantAuthService tenantAuthService)
    {
        _tenantAuthService = tenantAuthService;
    }

    public static string GroupName(string firmaId) => "firma:" + (firmaId ?? string.Empty).Trim();

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var tenant = httpContext is null ? null : _tenantAuthService.TryResolve(httpContext);
        if (tenant is null)
        {
            Context.Abort();
            return;
        }

        Context.Items["firma_id"] = tenant.FirmaId;
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(tenant.FirmaId));
        await Clients.Caller.SendAsync("nsxConnected", new
        {
            tenant.FirmaId,
            tenant.FirmaKodu,
            tenant.FirmaAdi,
            ServerTime = DateTimeOffset.Now
        });
        await base.OnConnectedAsync();
    }

    public Task JoinFirma(string firmaId)
    {
        var currentFirmaId = Context.Items.TryGetValue("firma_id", out var value) ? value?.ToString() : string.Empty;
        return string.Equals(currentFirmaId, firmaId?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Groups.AddToGroupAsync(Context.ConnectionId, GroupName(currentFirmaId ?? string.Empty))
            : Task.CompletedTask;
    }

    public Task LeaveFirma(string firmaId)
    {
        var currentFirmaId = Context.Items.TryGetValue("firma_id", out var value) ? value?.ToString() : string.Empty;
        return string.Equals(currentFirmaId, firmaId?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(currentFirmaId ?? string.Empty))
            : Task.CompletedTask;
    }
}
