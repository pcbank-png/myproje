namespace NSYazilim.Web.Services
{
    /// <summary>
    /// Indexes dynamic localization content and fills missing-language jobs after the web
    /// host has started. This work can grow with the number of active languages, so it must
    /// never sit on the IIS/Plesk cold-start critical path.
    /// </summary>
    public sealed class LocalizationStartupBootstrapWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LocalizationStartupBootstrapWorker> _logger;

        public LocalizationStartupBootstrapWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<LocalizationStartupBootstrapWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                // BackgroundService.StartAsync returns as soon as this first await is reached,
                // allowing IIS/Kestrel to begin serving the site immediately.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<LocalizationTranslationQueueService>();
                var indexed = await queue.IndexKnownDynamicContentAsync(stoppingToken);
                var queued = await queue.QueueAllMissingForActiveLanguagesAsync(stoppingToken);

                _logger.LogInformation(
                    "NSX Localization background bootstrap tamamlandı. Indexed={Indexed}, Queued={Queued}",
                    indexed,
                    queued);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal application shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NSX Localization background başlangıç taraması tamamlanamadı; normal worker çalışmaya devam edecek.");
            }
        }
    }
}
