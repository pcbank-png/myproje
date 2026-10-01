using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Completes country/city data for historical SiteVisits without blocking startup.
    /// Work is durable: attempt counters and timestamps allow safe continuation after
    /// an IIS/Plesk recycle while preventing one unresolved IP from blocking the queue.
    /// </summary>
    public sealed class SiteVisitLocationBackfillWorker : BackgroundService
    {
        private const int BatchSize = 12;
        private const int MaximumAttempts = 3;
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ProviderThrottleDelay = TimeSpan.FromMilliseconds(1500);

        private readonly IServiceProvider _services;
        private readonly ILogger<SiteVisitLocationBackfillWorker> _logger;

        public SiteVisitLocationBackfillWorker(
            IServiceProvider services,
            ILogger<SiteVisitLocationBackfillWorker> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // Cold-start sırasında canlı ziyaretçi ile GeoIP/DB kaynakları için yarışma.
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var processed = await ProcessBatchAsync(stoppingToken);
                    var delay = processed > 0 ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(5);
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Geçmiş ziyaret konumları bu turda tamamlanamadı; işlem otomatik devam edecek.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var geolocation = scope.ServiceProvider.GetRequiredService<IpGeolocationService>();
            var retryBefore = DateTime.UtcNow.Subtract(RetryInterval);

            var ipAddresses = await db.SiteVisits.AsNoTracking()
                .Where(x => x.TrafficType == SiteVisitTrackingService.HumanTrafficType
                            && x.IpAddress != null
                            && x.IpAddress != ""
                            && (x.Country == null || x.Country == "" || x.City == null || x.City == "")
                            && x.GeoLookupAttemptCount < MaximumAttempts
                            && (x.GeoLookupAtUtc == null || x.GeoLookupAtUtc <= retryBefore))
                .Select(x => x.IpAddress!)
                .Distinct()
                .Take(BatchSize)
                .ToListAsync(cancellationToken);

            if (ipAddresses.Count == 0)
                return 0;

            var completed = 0;
            foreach (var ipAddress in ipAddresses)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var known = await db.SiteVisits.AsNoTracking()
                    .Where(x => x.TrafficType == SiteVisitTrackingService.HumanTrafficType
                                && x.IpAddress == ipAddress
                                && x.Country != null && x.Country != ""
                                && x.City != null && x.City != "")
                    .OrderByDescending(x => x.VisitedAtUtc)
                    .Select(x => new { x.Country, x.City })
                    .FirstOrDefaultAsync(cancellationToken);

                string? country = known?.Country;
                string? city = known?.City;
                var providerWasUsed = false;

                if (known == null)
                {
                    providerWasUsed = true;
                    try
                    {
                        var resolved = await geolocation.ResolveAsync(ipAddress, cancellationToken);
                        country = TrimTo(resolved?.Country, 120);
                        city = TrimTo(resolved?.City ?? resolved?.Region, 120);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Geçmiş ziyaret IP konumu bu denemede alınamadı. IP: {IpAddress}", ipAddress);
                    }
                }

                var rows = await db.SiteVisits
                    .Where(x => x.TrafficType == SiteVisitTrackingService.HumanTrafficType
                                && x.IpAddress == ipAddress
                                && (x.Country == null || x.Country == "" || x.City == null || x.City == ""))
                    .ToListAsync(cancellationToken);
                var attemptedAt = DateTime.UtcNow;

                foreach (var row in rows)
                {
                    if (string.IsNullOrWhiteSpace(row.Country) && !string.IsNullOrWhiteSpace(country))
                        row.Country = country;
                    if (string.IsNullOrWhiteSpace(row.City) && !string.IsNullOrWhiteSpace(city))
                        row.City = city;

                    row.GeoLookupAtUtc = attemptedAt;
                    row.GeoLookupAttemptCount = Math.Min(MaximumAttempts, row.GeoLookupAttemptCount + 1);
                }

                if (rows.Count > 0)
                    await db.SaveChangesAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(country) || !string.IsNullOrWhiteSpace(city))
                    completed++;

                if (providerWasUsed)
                    await Task.Delay(ProviderThrottleDelay, cancellationToken);
            }

            _logger.LogInformation(
                "Geçmiş ziyaret konum taraması: {ProcessedIpCount} IP işlendi, {CompletedIpCount} IP konumlandırıldı.",
                ipAddresses.Count,
                completed);
            return ipAddresses.Count;
        }

        private static string? TrimTo(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            return value.Length <= maxLength ? value : value[..maxLength];
        }
    }
}
