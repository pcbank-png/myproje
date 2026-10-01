using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    public sealed class DownloadLocationUpdateQueue
    {
        private readonly Channel<int> _channel = Channel.CreateBounded<int>(
            new BoundedChannelOptions(1024)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        public bool TryQueue(int demoDownloadId)
            => _channel.Writer.TryWrite(demoDownloadId);

        public IAsyncEnumerable<int> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public sealed class DownloadLocationUpdateWorker : BackgroundService
    {
        private readonly DownloadLocationUpdateQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DownloadLocationUpdateWorker> _logger;

        public DownloadLocationUpdateWorker(
            DownloadLocationUpdateQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<DownloadLocationUpdateWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var downloadId in _queue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var row = await db.DemoDownloads.FirstOrDefaultAsync(x => x.Id == downloadId, stoppingToken);
                        if (row == null || string.IsNullOrWhiteSpace(row.IpAddress) || row.GeoLookupAt != null)
                            continue;

                        var clientIpService = scope.ServiceProvider.GetRequiredService<ClientIpService>();
                        if (!clientIpService.IsPublicIp(row.IpAddress))
                            continue;

                        var geolocation = scope.ServiceProvider.GetRequiredService<IpGeolocationService>();
                        var location = await geolocation.ResolveAsync(row.IpAddress, stoppingToken);
                        if (location == null)
                            continue;

                        row.City = location.City;
                        row.Region = location.Region;
                        row.Country = location.Country;
                        row.GeoLookupAt = DateTime.Now;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "İndirme konumu background kuyruğunda güncellenemedi. DownloadId: {DownloadId}", downloadId);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
        }
    }
}
