namespace NSYazilim.Web.TeknikServisCloud.Models;

public sealed record TenantContext(string FirmaId, string FirmaKodu, string FirmaAdi);

public sealed class TenantCreateRequest
{
    public string FirmaId { get; set; } = string.Empty;
    public string FirmaKodu { get; set; } = string.Empty;
    public string FirmaAdi { get; set; } = string.Empty;
    public string LisansKey { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
}

public sealed class TenantCreateResponse
{
    public string FirmaId { get; set; } = string.Empty;
    public string FirmaKodu { get; set; } = string.Empty;
    public string FirmaAdi { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class TenantInfoResponse
{
    public string FirmaId { get; set; } = string.Empty;
    public string FirmaKodu { get; set; } = string.Empty;
    public string FirmaAdi { get; set; } = string.Empty;
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Now;
}

public sealed class TenantDataItem
{
    public string Id { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class TenantDataUpsertRequest
{
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SyncChangeRequest
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = "upsert";
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SyncChangeResponse
{
    public long ChangeId { get; set; }
    public string FirmaId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class TrackingPublishRequest
{
    public string TrackingToken { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ServiceNo { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string PhoneMasked { get; set; } = string.Empty;
    public string DeviceTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PriceText { get; set; } = string.Empty;
    public string PublicNote { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class TrackingRecordResponse
{
    public string TrackingToken { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string FirmaId { get; set; } = string.Empty;
    public string ServiceNo { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string PhoneMasked { get; set; } = string.Empty;
    public string DeviceTitle { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string PriceText { get; set; } = string.Empty;
    public string PublicNote { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public sealed class CustomerMessageRequest
{
    public string MessageType { get; set; } = "Bilgi";
    public string MessageText { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string ActionContextKey { get; set; } = string.Empty;
}

public sealed class CustomerMessageResponse
{
    public long Id { get; set; }
    public string FirmaId { get; set; } = string.Empty;
    public string TrackingToken { get; set; } = string.Empty;
    public string ServiceNo { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
    public string MessageText { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerPhone { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public bool IsPulled { get; set; }
    public string ActionContextKey { get; set; } = string.Empty;
}

public sealed class LiveChangeMessage
{
    public long ChangeId { get; set; }
    public string FirmaId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}
