namespace NSYazilim.Web.SalonTakipCloud.Models;

public sealed record SalonTenantContext(string TenantId, string CompanyName, int LicenseId);

public sealed record SalonMobileSessionContext(
    string TenantId,
    string CompanyName,
    string SessionHash,
    string CsrfHash,
    DateTime ExpiresAt);

public sealed class SalonTenantSetupRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = "NSXDUGUNSALONUPRO";
    public string CompanyName { get; set; } = string.Empty;
    public string CurrentApiToken { get; set; } = string.Empty;
}

public sealed class SalonTenantSetupResponse
{
    public bool Success { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string PanelUrl { get; set; } = string.Empty;
    public string DatabaseInstanceId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class SalonQrCreateRequest
{
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SalonQrCreateResponse
{
    public bool Success { get; set; }
    public string LoginUrl { get; set; } = string.Empty;
    public string QrValue { get; set; } = string.Empty;
    public string ExpiresAt { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class SalonEntityItem
{
    public string Id { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class SalonMobileCustomerCreateRequest
{
    public string FullName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string SecondPhone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string IdentityNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Aktif";
    public string Notes { get; set; } = string.Empty;
}

public sealed class SalonEntityUpsertRequest
{
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SalonSyncChangeRequest
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = "upsert";
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SalonSyncChangeResponse
{
    public long ChangeId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

public sealed class SalonSnapshotItem
{
    public string Id { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class SalonSnapshotRequest
{
    public string EntityType { get; set; } = string.Empty;
    public List<SalonSnapshotItem> Items { get; set; } = new();
    public bool ReplaceExisting { get; set; }
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class SalonSnapshotResponse
{
    public bool Success { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int SavedCount { get; set; }
    public int DeletedCount { get; set; }
    public long LastChangeId { get; set; }
}
