using Microsoft.Extensions.Options;

namespace NSYazilim.Web.Services
{
    public sealed class ShopierProductSyncWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptions<ShopierOptions> _options;
        private readonly ILogger<ShopierProductSyncWorker> _logger;

        public ShopierProductSyncWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<ShopierOptions> options,
            ILogger<ShopierProductSyncWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Uygulama ve veritabanı tamamen ayağa kalksın; ilk senkronu kısa bir gecikmeyle başlat.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var shopier = scope.ServiceProvider.GetRequiredService<ShopierService>();
                    if (shopier.IsConfigured)
                    {
                        var result = await shopier.SyncProductMappingsAsync(force: false, stoppingToken);
                        if (!result.Success)
                            _logger.LogWarning("Shopier otomatik ürün eşleştirme: {Message}", result.Message);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Shopier otomatik ürün eşleştirme worker turu başarısız oldu.");
                }

                var minutes = Math.Clamp(_options.Value.ProductSyncMinutes <= 0 ? 10 : _options.Value.ProductSyncMinutes, 2, 120);
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}
