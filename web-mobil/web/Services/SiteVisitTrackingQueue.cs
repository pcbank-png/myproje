using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace NSYazilim.Web.Services
{
    public sealed record SiteVisitTrackingItem(
        string VisitorId,
        string Path,
        string? Referrer,
        string? IpAddress,
        string DeviceType,
        string Browser,
        string OperatingSystem,
        string TrafficType,
        string? RobotName,
        bool IsAuthenticated,
        int? UserId,
        DateTime VisitedAtUtc);

    public sealed class SiteVisitTrackingQueue
    {
        // Robot taramasi bir anda yogunlasabilse de gercek ziyaretci kuyrugunu dolduramaz.
        // Iki ayri kanal ayni worker tarafindan, insan trafigine oncelik verilerek tuketilir.
        private readonly Channel<SiteVisitTrackingItem> _humanChannel = Channel.CreateBounded<SiteVisitTrackingItem>(
            new BoundedChannelOptions(2048)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        private readonly Channel<SiteVisitTrackingItem> _robotChannel = Channel.CreateBounded<SiteVisitTrackingItem>(
            new BoundedChannelOptions(4096)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

        private readonly SemaphoreSlim _signal = new(0);

        public bool TryQueue(SiteVisitTrackingItem item)
        {
            var isHuman = string.IsNullOrWhiteSpace(item.TrafficType)
                          || item.TrafficType.Equals(SiteVisitTrackingService.HumanTrafficType, StringComparison.OrdinalIgnoreCase);
            var queued = isHuman
                ? _humanChannel.Writer.TryWrite(item)
                : _robotChannel.Writer.TryWrite(item);

            if (queued)
                _signal.Release();

            return queued;
        }

        public async IAsyncEnumerable<SiteVisitTrackingItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _signal.WaitAsync(cancellationToken);

                // Her uyandigimizda once gercek ziyaretciyi tuket. Robot yogunlugu gercek
                // trafik kayitlarinin arkasinda bekler ve kullanici istatistigini bozmaz.
                if (_humanChannel.Reader.TryRead(out var human))
                {
                    yield return human;
                    continue;
                }

                if (_robotChannel.Reader.TryRead(out var robot))
                    yield return robot;
            }
        }
    }
}
