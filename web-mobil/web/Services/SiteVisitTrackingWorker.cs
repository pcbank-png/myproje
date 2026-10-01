namespace NSYazilim.Web.Services
{
    public sealed class SiteVisitTrackingWorker : BackgroundService
    {
        private readonly SiteVisitTrackingQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SiteVisitTrackingWorker> _logger;

        public SiteVisitTrackingWorker(
            SiteVisitTrackingQueue queue,
            IServiceScopeFactory scopeFactory,
            ILogger<SiteVisitTrackingWorker> logger)
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
                        var trackingService = scope.ServiceProvider.GetRequiredService<SiteVisitTrackingService>();
                        await trackingService.TrackAsync(item, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ziyaretçi istatistiği background kuyruğunda kaydedilemedi.");
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
