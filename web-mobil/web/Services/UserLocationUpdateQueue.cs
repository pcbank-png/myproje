using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    public sealed record UserLocationUpdateItem(int UserId, string IpAddress, bool ForceLookup);

    public sealed class UserLocationUpdateQueue
    {
        private readonly Channel<UserLocationUpdateItem> _channel = Channel.CreateBounded<UserLocationUpdateItem>(
            new BoundedChannelOptions(512)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        public bool TryQueue(UserLocationUpdateItem item)
            => _channel.Writer.TryWrite(item);

        public IAsyncEnumerable<UserLocationUpdateItem> ReadAllAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }

    public sealed class UserLocationUpdateWorker : BackgroundService
    {
        private readonly UserLocationUpdateQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<UserLocationUpdateWorker> _logger;

        public UserLocationUpdateWorker(
            UserLocationUpdateQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<UserLocationUpdateWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var item in _queue.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var clientIpService = scope.ServiceProvider.GetRequiredService<ClientIpService>();

                        if (!clientIpService.IsPublicIp(item.IpAddress))
                            continue;

                        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == item.UserId, stoppingToken);
                        if (user == null || !string.Equals(user.LastIpAddress, item.IpAddress, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var needsLookup = item.ForceLookup
                            || string.IsNullOrWhiteSpace(user.LastCity)
                            || string.IsNullOrWhiteSpace(user.LastCountry)
                            || user.LastGeoLookupAt == null
                            || user.LastGeoLookupAt.Value < DateTime.Now.AddDays(-7);

                        if (!needsLookup)
                            continue;

                        var geolocation = scope.ServiceProvider.GetRequiredService<IpGeolocationService>();
                        var location = await geolocation.ResolveAsync(item.IpAddress, stoppingToken);
                        if (location == null)
                            continue;

                        user.LastCity = location.City;
                        user.LastRegion = location.Region;
                        user.LastCountry = location.Country;
                        user.LastGeoLookupAt = DateTime.Now;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Kullanıcı konumu background kuyruğunda güncellenemedi. UserId: {UserId}", item.UserId);
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
