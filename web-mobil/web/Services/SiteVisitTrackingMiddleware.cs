namespace NSYazilim.Web.Services
{
    public sealed class SiteVisitTrackingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<SiteVisitTrackingMiddleware> _logger;

        public SiteVisitTrackingMiddleware(RequestDelegate next, ILogger<SiteVisitTrackingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            SiteVisitTrackingService trackingService,
            SiteVisitTrackingQueue queue)
        {
            // Cookie ve hafif request metadata'sını response başlamadan al. DB ve GeoIP yok.
            var item = trackingService.Capture(context);

            await _next(context);

            if (item == null || context.Response.StatusCode >= 400)
                return;

            if (!queue.TryQueue(item))
                _logger.LogDebug(
                    "Ziyaretçi istatistiği {TrafficType} kuyruğu dolu olduğu için yeni kayıt atlandı.",
                    item.TrafficType);
        }
    }
}
