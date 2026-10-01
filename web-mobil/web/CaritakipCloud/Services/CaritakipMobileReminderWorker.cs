using System.Globalization;

namespace NSYazilim.Web.CaritakipCloud.Services;

/// <summary>
/// Buluttaki <c>reminder</c> kayıtları için saat gelince inbox + Expo push.
/// Masaüstü <c>dueAt</c> alanını okur; varsayılan 5 sn aralıkla tarar.
/// </summary>
public sealed class CaritakipMobileReminderWorker : BackgroundService
{
    private static readonly TimeZoneInfo Turkey =
        TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");

    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CaritakipMobileReminderWorker> _logger;

    public CaritakipMobileReminderWorker(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<CaritakipMobileReminderWorker> logger)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Caritakip:ReminderWorkerEnabled", true))
        {
            _logger.LogInformation("Caritakip hatırlatma worker devre dışı.");
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var sent = await RunCycleAsync(stoppingToken);
                if (sent > 0)
                    _logger.LogInformation("Hatırlatma bildirimi gönderildi: {Count}", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Caritakip hatırlatma döngüsü hata verdi.");
            }

            await Task.Delay(GetPollingInterval(_configuration), stoppingToken);
        }
    }

    private static TimeSpan GetPollingInterval(IConfiguration configuration)
    {
        if (configuration.GetValue<int?>("Caritakip:ReminderIntervalSeconds") is int seconds)
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
        if (configuration.GetValue<int?>("Caritakip:ReminderIntervalMinutes") is int minutes)
            return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 60));
        if (configuration.GetValue<int?>("Caritakip:ReminderIntervalHours") is int hours)
            return TimeSpan.FromMinutes(Math.Clamp(hours * 60, 1, 60));
        return TimeSpan.FromSeconds(5);
    }

    private async Task<int> RunCycleAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var nativeStore = scope.ServiceProvider.GetRequiredService<CaritakipNativeMobileStore>();
        var outbox = scope.ServiceProvider.GetRequiredService<CaritakipNotificationOutboxStore>();

        var lookbackHours = Math.Clamp(_configuration.GetValue("Caritakip:ReminderLookbackHours", 72), 1, 168);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Turkey);
        var lookback = TimeSpan.FromHours(lookbackHours);
        var sent = 0;

        var tenants = await nativeStore.GetTenantsWithActiveNativeDevicesAsync(cancellationToken);
        foreach (var tenantId in tenants)
        {
            var reminders = await nativeStore.GetDueRemindersAsync(
                tenantId, nowLocal, lookback, cancellationToken);
            if (reminders.Count == 0) continue;

            foreach (var reminder in reminders)
            {
                var dedupeKey = $"reminder:{reminder.EntityId}:{reminder.DueLocal:yyyyMMddHHmm}";
                if (await nativeStore.HasNotificationDedupeAsync(tenantId, dedupeKey, cancellationToken))
                    continue;

                var data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["type"] = "reminder",
                    ["entityId"] = reminder.EntityId,
                    ["category"] = "reminders",
                    ["dueLocal"] = reminder.DueLocal.ToString("O", CultureInfo.InvariantCulture)
                };
                if (!string.IsNullOrWhiteSpace(reminder.CustomerId))
                    data["customerId"] = reminder.CustomerId;

                try
                {
                    await outbox.EnqueueAsync(tenantId,dedupeKey,"inbox",
                        new NotificationDraft(tenantId,null,"reminders",reminder.Title,reminder.Body,data),cancellationToken);
                    await nativeStore.TryRecordNotificationDedupeAsync(tenantId, dedupeKey, cancellationToken);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Hatırlatma iletilemedi entity={EntityId} tenant={TenantId}",
                        reminder.EntityId, tenantId);
                }
            }
        }

        return sent;
    }
}
