using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using NSYazilim.Web.CaritakipCloud.Hubs;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipMobileNotificationDeliverer
{
    private readonly CaritakipNativeMobileStore _nativeStore;
    private readonly CaritakipNotificationOutboxStore _outbox;
    private readonly IHubContext<CaritakipCloudHub> _hub;
    private readonly ILogger<CaritakipMobileNotificationDeliverer> _logger;

    public CaritakipMobileNotificationDeliverer(
        CaritakipNativeMobileStore nativeStore,
        CaritakipNotificationOutboxStore outbox,
        IHubContext<CaritakipCloudHub> hub,
        ILogger<CaritakipMobileNotificationDeliverer> logger)
    {
        _nativeStore = nativeStore;
        _outbox = outbox;
        _hub = hub;
        _logger = logger;
    }

    public async Task DeliverAsync(
        string tenantId,
        string? excludeMobileDeviceId,
        string category,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? data,
        bool sendPush,
        CancellationToken cancellationToken,
        string? stableMessageId = null)
    {
        tenantId = (tenantId ?? string.Empty).Trim();
        if (tenantId.Length == 0) return;

        category = Limit(category, 32);
        title = Limit(title, 180);
        body = Limit(body, 512);
        var messageId = stableMessageId ?? Guid.NewGuid().ToString();
        var payloadJson = data is null || data.Count == 0
            ? null
            : JsonSerializer.Serialize(data);

        var prefsMap = await _nativeStore.GetNotificationPrefsMapAsync(tenantId, cancellationToken);
        var deviceIds = await _nativeStore.GetActiveNativeDeviceIdsAsync(
            tenantId, excludeMobileDeviceId, cancellationToken);
        foreach (var deviceId in deviceIds)
        {
            var prefs = prefsMap.GetValueOrDefault(deviceId) ?? CaritakipNativeMobileStore.DefaultNotificationPrefs();
            if (!CaritakipNativeMobileStore.AllowsNotificationCategory(prefs, category))
                continue;
            var inserted = await _nativeStore.InsertInboxMessageAsync(
                tenantId, deviceId, messageId, category, title, body, payloadJson, cancellationToken);
            if (!inserted) continue;
            // Only announce a notification after its inbox row is committed.
            // Device groups preserve category preferences and actor exclusions.
            try
            {
                await _hub.Clients.Group(CaritakipCloudHub.NativeDeviceGroup(tenantId, deviceId))
                    .SendAsync("notificationAvailable", new
                    {
                        id = messageId, category, title, body,
                        createdAtUtc = DateTime.UtcNow, read = false
                    }, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Mobil anlık bildirim iletilemedi (tenant={TenantId}).", tenantId);
            }
        }

        if (!sendPush) return;

        {
            var pushTargets = await _nativeStore.GetPushTargetsAsync(
                tenantId, excludeMobileDeviceId, cancellationToken);
            if (pushTargets.Count == 0) return;

            var pushData = data is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(data, StringComparer.Ordinal);
            pushData["tenantId"] = tenantId;
            pushData["messageId"] = messageId;
            pushData["category"] = category;

            var messages = new List<CaritakipExpoPushMessage>();
            foreach (var target in pushTargets)
            {
                var prefs = prefsMap.GetValueOrDefault(target.MobileDeviceId)
                    ?? CaritakipNativeMobileStore.DefaultNotificationPrefs();
                if (!CaritakipNativeMobileStore.AllowsNotificationCategory(prefs, category))
                    continue;
                var pushMessage = new CaritakipExpoPushMessage
                {
                    To = target.Token,
                    Title = title,
                    Body = body,
                    Data = new Dictionary<string,string>(pushData) { ["mobileDeviceId"] = target.MobileDeviceId },
                    Sound = "default",
                    Priority = "high",
                    // Tahsilatla aynı payload — timeSensitive iOS’ta entitlement olmadan banner düşmeyebilir.
                    ChannelId = category == "reminders" ? "nsx-reminders" : "nsx-cari",
                    InterruptionLevel = "active"
                };
                await _outbox.EnqueueAsync(tenantId,$"push:{messageId}:{target.MobileDeviceId}","push",
                    new PushDraft(tenantId,target.MobileDeviceId,pushMessage),cancellationToken);
                messages.Add(pushMessage);
            }

            if (messages.Count > 0)
            {
                _logger.LogInformation(
                    "Expo push kuyruğu tenant={TenantId} category={Category} targets={Count}",
                    tenantId, category, messages.Count);
                // A separate durable worker sends and checks Expo receipts.
            }
            else
            {
                _logger.LogWarning(
                    "Expo push hedefi yok/filtre tenant={TenantId} category={Category} devices={Devices} tokens={Tokens}",
                    tenantId, category, deviceIds.Count, pushTargets.Count);
            }
        }

    }

    private static string Limit(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }
}
