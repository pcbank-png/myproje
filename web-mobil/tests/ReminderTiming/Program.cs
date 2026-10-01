using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSYazilim.Web.CaritakipCloud.Hubs;
using NSYazilim.Web.CaritakipCloud.Models;
using NSYazilim.Web.CaritakipCloud.Services;

var turkey = TimeZoneInfo.FindSystemTimeZoneById(
    OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");
var now = new DateTime(2026, 10, 1, 15, 0, 0);
var flags = BindingFlags.NonPublic | BindingFlags.Static;
var parser = typeof(CaritakipNativeMobileStore).GetMethod("TryReadDueLocalDateTime", flags)!;
var isDue = typeof(CaritakipNativeMobileStore).GetMethod("IsReminderDue", flags)!;
var interval = typeof(CaritakipMobileReminderWorker).GetMethod("GetPollingInterval", flags)!;

void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
}

(bool Valid, DateTime Due) Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    object?[] args = [document.RootElement, turkey, default(DateTime)];
    return ((bool)parser.Invoke(null, args)!, (DateTime)args[2]!);
}

bool Due(DateTime deadline) => (bool)isDue.Invoke(null, [deadline, now, TimeSpan.FromHours(72)])!;

Check(!Due(now.AddMinutes(2)), "Two minutes ahead must not notify on registration");
Check(!Due(now.AddMinutes(1)), "Next worker interval must not notify early");
Check(!Due(now.AddTicks(1)), "Even one tick ahead must not notify");
Check(Due(now), "Exact scheduled instant becomes due");
Check(Due(now.AddSeconds(-1)), "Recently due reminder is eligible");
Check(!Due(now.AddHours(-73)), "Outside recovery window must not notify");

var scheduled = Parse("""
{"dueAt":"2026-10-01T16:00:00","date":"2026-10-01","time":"14:00",
 "createdAt":"2026-10-01T14:00:00","updatedAt":"2026-10-01T14:59:00"}
""");
Check(scheduled.Valid && scheduled.Due == now.AddHours(1) && !Due(scheduled.Due),
    "Scheduled dueAt must win over generic record and audit dates");
var utc = Parse("""{"dueAtUtc":"2026-10-01T13:00:00Z"}""");
Check(utc.Valid && utc.Due == now.AddHours(1), "UTC deadline converts to Turkey time");
var offset = Parse("""{"dueAt":"2026-10-01T16:00:00+03:00"}""");
Check(offset.Valid && offset.Due == now.AddHours(1), "Offset deadline preserves its instant");
var split = Parse("""{"date":"2026-10-01","time":"16:00"}""");
Check(split.Valid && split.Due == now.AddHours(1), "Separate reminder date and time still supported");
Check(!Parse("""{"createdAt":"2026-10-01T14:00:00","updatedAt":"2026-10-01T14:30:00"}""").Valid,
    "Audit timestamps alone are not a reminder deadline");
Check(!Parse("""{"dueAt":"invalid","createdAt":"2026-10-01T14:00:00"}""").Valid,
    "Invalid deadline must not fall back to creation time");

TimeSpan Poll(params (string Key, string Value)[] values)
{
    var config = new ConfigurationBuilder().AddInMemoryCollection(
        values.Select(x => new KeyValuePair<string, string?>("Caritakip:" + x.Key, x.Value))).Build();
    return (TimeSpan)interval.Invoke(null, [config])!;
}
Check(Poll() == TimeSpan.FromSeconds(5), "Default scan interval is five seconds");
Check(Poll(("ReminderIntervalSeconds", "5"), ("ReminderIntervalMinutes", "1")) == TimeSpan.FromSeconds(5),
    "Seconds configuration overrides old minutes setting");
Check(Poll(("ReminderIntervalMinutes", "2")) == TimeSpan.FromMinutes(2), "Explicit legacy polling setting preserved");

// Reminder-only creates/updates may broadcast data, but must not access the
// notification store or delivery service on the mutation request path.
var logger = new RecordingLogger<CaritakipMobileChangeNotifier>();
var notifier = new CaritakipMobileChangeNotifier(null!, null!, logger);
var hub = new RecordingHub();
await notifier.NotifyAsync(hub, "tenant", "desktop", new CariSyncPushResponse
{
    Results = [new CariMutationResult { EntityType = "reminder", EntityId = "r1", Status = "applied", Version = 1, Cursor = 1 }]
}, CancellationToken.None);
Check(logger.Warnings == 0, "Reminder registration must not attempt notification delivery");
Check(hub.Proxy.Events.SequenceEqual(["changesAvailable"]), "Only data refresh event on reminder registration");
Console.WriteLine("PASS: no early deadlines, exact due boundary, recovery window, scheduled-field precedence, UTC/offset conversion, split date/time, audit rejection, polling configuration, reminder registration without delivery");

sealed class RecordingLogger<T> : ILogger<T>
{
    public int Warnings { get; private set; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) { if (logLevel >= LogLevel.Warning) Warnings++; }
}
sealed class RecordingProxy : IClientProxy
{
    public List<string> Events { get; } = [];
    public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
    { Events.Add(method); return Task.CompletedTask; }
}
sealed class RecordingClients(RecordingProxy proxy) : IHubClients
{
    public IClientProxy All => proxy;
    public IClientProxy AllExcept(IReadOnlyList<string> ids) => proxy;
    public IClientProxy Client(string id) => proxy;
    public IClientProxy Clients(IReadOnlyList<string> ids) => proxy;
    public IClientProxy Group(string name) => proxy;
    public IClientProxy GroupExcept(string name, IReadOnlyList<string> ids) => proxy;
    public IClientProxy Groups(IReadOnlyList<string> names) => proxy;
    public IClientProxy User(string id) => proxy;
    public IClientProxy Users(IReadOnlyList<string> ids) => proxy;
}
sealed class RecordingHub : IHubContext<CaritakipCloudHub>
{
    public RecordingProxy Proxy { get; } = new();
    public IHubClients Clients => new RecordingClients(Proxy);
    public IGroupManager Groups => throw new NotSupportedException();
}
