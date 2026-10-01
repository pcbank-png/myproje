using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipExpoPushService
{
    private const string ExpoPushUrl = "https://exp.host/--/api/v2/push/send";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CaritakipExpoPushService> _logger;

    public CaritakipExpoPushService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<CaritakipExpoPushService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsEnabled =>
        _configuration.GetValue("Caritakip:ExpoPushEnabled", true);

    public sealed record PushResult(bool Ok, string? ReceiptId = null, string? Error = null);

    public async Task<PushResult> SendOneAsync(CaritakipExpoPushMessage message, CancellationToken ct)
    {
        using var document = await PostAsync(ExpoPushUrl,new[] { ToPayload(message) },ct);
        if (!document.RootElement.TryGetProperty("data",out var data)) throw new JsonException("Expo ticket missing");
        return ParseResult(data.ValueKind == JsonValueKind.Array ? data[0] : data, true);
    }

    public async Task<PushResult> GetReceiptAsync(string receiptId, CancellationToken ct)
    {
        using var document = await PostAsync("https://exp.host/--/api/v2/push/getReceipts",new { ids = new[] { receiptId } },ct);
        if (!document.RootElement.TryGetProperty("data",out var data) || !data.TryGetProperty(receiptId,out var receipt))
            return new(false,Error:"ReceiptPending");
        return ParseResult(receipt,false);
    }

    public static PushResult ParseResult(JsonElement result, bool ticket)
    {
        if (result.TryGetProperty("status",out var status) && status.GetString() == "ok")
        {
            var id = result.TryGetProperty("id",out var value) ? value.GetString() : null;
            if (ticket && string.IsNullOrWhiteSpace(id)) throw new JsonException("Expo ticket id missing");
            return new(true,id);
        }
        var error = result.TryGetProperty("details",out var details) && details.TryGetProperty("error",out var code)
            ? code.GetString() : "ExpoUnknown";
        return new(false,Error:error);
    }

    private async Task<JsonDocument> PostAsync(string url, object payload, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(nameof(CaritakipExpoPushService));
        client.Timeout = TimeSpan.FromSeconds(20);
        using var request = new HttpRequestMessage(HttpMethod.Post,url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var accessToken = _configuration["Caritakip:ExpoAccessToken"]?.Trim();
        if (!string.IsNullOrWhiteSpace(accessToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",accessToken);
        request.Content = new StringContent(JsonSerializer.Serialize(payload,JsonOptions),Encoding.UTF8,"application/json");
        using var response = await client.SendAsync(request,ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task SendAsync(
        IReadOnlyList<CaritakipExpoPushMessage> messages,
        CancellationToken cancellationToken)
    {
        if (!IsEnabled || messages.Count == 0) return;

        var client = _httpClientFactory.CreateClient(nameof(CaritakipExpoPushService));
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var accessToken = _configuration["Caritakip:ExpoAccessToken"]?.Trim();
        if (!string.IsNullOrEmpty(accessToken))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        for (var offset = 0; offset < messages.Count; offset += 100)
        {
            var chunk = messages.Skip(offset).Take(100).Select(ToPayload).ToArray();
            using var content = new StringContent(
                JsonSerializer.Serialize(chunk, JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await client.PostAsync(ExpoPushUrl, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Expo push HTTP {Status}: {Body}",
                    (int)response.StatusCode,
                    body.Length > 500 ? body[..500] : body);
                continue;
            }

            // Ticket hatalarını logla (DeviceNotRegistered vb.)
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data))
                {
                    foreach (var item in data.ValueKind == JsonValueKind.Array
                                 ? data.EnumerateArray()
                                 : Enumerable.Empty<JsonElement>().Append(data))
                    {
                        var status = item.TryGetProperty("status", out var st) ? st.GetString() : null;
                        if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
                        {
                            var message = item.TryGetProperty("message", out var msg) ? msg.GetString() : "";
                            var details = item.TryGetProperty("details", out var det) ? det.GetRawText() : "";
                            _logger.LogWarning("Expo push ticket error: {Message} {Details}", message, details);
                        }
                    }
                }
            }
            catch
            {
                // log parse opsiyonel
            }
        }
    }

    private static object ToPayload(CaritakipExpoPushMessage message) => new
    {
        to = message.To,
        title = message.Title,
        body = message.Body,
        sound = message.Sound ?? "default",
        priority = message.Priority ?? "high",
        channelId = message.ChannelId,
        interruptionLevel = message.InterruptionLevel,
        data = message.Data
    };
}

public sealed class CaritakipExpoPushMessage
{
    public required string To { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public Dictionary<string, string>? Data { get; init; }
    public string? Sound { get; init; } = "default";
    public string? Priority { get; init; } = "high";
    /** Android Expo channel — mobil tarafta oluşturulmalı. */
    public string? ChannelId { get; init; }
    /** iOS 15+ — hatırlatmalar için timeSensitive. */
    public string? InterruptionLevel { get; init; }
}
