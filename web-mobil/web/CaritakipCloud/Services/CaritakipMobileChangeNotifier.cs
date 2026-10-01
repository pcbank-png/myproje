using System.Globalization;
using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.CaritakipCloud.Hubs;
using NSYazilim.Web.CaritakipCloud.Models;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipMobileChangeNotifier
{
    private static readonly HashSet<string> TransactionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "debt", "collection"
    };

    private readonly CaritakipNativeMobileStore _nativeStore;
    private readonly CaritakipMobileNotificationDeliverer _deliverer;
    private readonly ILogger<CaritakipMobileChangeNotifier> _logger;

    public CaritakipMobileChangeNotifier(
        CaritakipNativeMobileStore nativeStore,
        CaritakipMobileNotificationDeliverer deliverer,
        ILogger<CaritakipMobileChangeNotifier> logger)
    {
        _nativeStore = nativeStore;
        _deliverer = deliverer;
        _logger = logger;
    }

    public async Task NotifyAsync(
        IHubContext<CaritakipCloudHub> hub,
        string tenantId,
        string actorDeviceId,
        CariSyncPushResponse response,
        CancellationToken cancellationToken)
    {
        var applied = response.Results.Where(x => x.Status == "applied").ToArray();
        if (applied.Length == 0) return;

        try
        {
        await hub.Clients
            .Group(CaritakipCloudHub.TenantGroup(tenantId))
            .SendAsync("changesAvailable", new
            {
                cursor = applied.Max(x => x.Cursor),
                entities = applied.Select(x => new { x.EntityType, x.EntityId, x.Version, x.Cursor })
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Anlık veri güncellemesi iletilemedi (tenant={TenantId}).", tenantId);
        }

        // Notification intent is committed atomically by ApplyMutationAsync.
    }

}
