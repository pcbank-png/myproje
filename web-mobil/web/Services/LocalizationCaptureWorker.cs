using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Services
{
    public sealed class LocalizationCaptureWorker : BackgroundService
    {
        private readonly LocalizationCaptureService _capture;
        private readonly IServiceProvider _services;
        private readonly ILogger<LocalizationCaptureWorker> _logger;

        public LocalizationCaptureWorker(LocalizationCaptureService capture, IServiceProvider services, ILogger<LocalizationCaptureWorker> logger)
        {
            _capture = capture;
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (!await timer.WaitForNextTickAsync(stoppingToken))
                        break;

                    await FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Dil motoru kaynak yakalama kuyruğu bu turda yazılamadı.");
                }
            }
        }

        private async Task FlushAsync(CancellationToken cancellationToken)
        {
            var items = _capture.Drain(1000);
            if (items.Count == 0)
                return;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var keys = items.Select(x => x.SourceKey).ToArray();
            var existing = await db.LocalizationResources
                .Where(x => keys.Contains(x.SourceKey))
                .ToDictionaryAsync(x => x.SourceKey, cancellationToken);
            var now = DateTime.Now;

            foreach (var item in items)
            {
                if (existing.TryGetValue(item.SourceKey, out var row))
                {
                    row.LastSeenAt = now;
                    row.HitCount += item.Hits;
                    continue;
                }

                db.LocalizationResources.Add(new LocalizationResource
                {
                    SourceKey = item.SourceKey,
                    SourceText = item.SourceText,
                    FirstSeenPath = item.Path,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                    HitCount = Math.Max(1, item.Hits)
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            // Every key is fanned out once per application process. The in-memory known-key
            // cache prevents repeated DB work on later requests, while this first pass also
            // repairs an older resource that exists in LocalizationResources but somehow lost
            // one or more target-language jobs. Mark it known only AFTER fan-out succeeds so a
            // transient DB failure can self-heal on the next request instead of being stranded.
            var queue = scope.ServiceProvider.GetRequiredService<LocalizationTranslationQueueService>();
            await queue.QueueExistingKeysForActiveLanguagesAsync(keys, cancellationToken);
            _capture.MarkKnown(keys);
        }
    }
}
