namespace NSYazilim.Web.VeresiyeFreeCloud.Models;

public sealed record VeresiyeTenantContext(string TenantId, string CompanyName, int LicenseId);

public sealed record VeresiyeMobileSessionContext(
    string TenantId,
    string CompanyName,
    string SessionHash,
    string CsrfHash,
    DateTime ExpiresAt);

public sealed class VeresiyeTenantSetupRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string ProductCode { get; set; } = "NSXVERESIYETAKIPPROFREE";
    public string CompanyName { get; set; } = string.Empty;
    public string CurrentApiToken { get; set; } = string.Empty;
}

public sealed class VeresiyeTenantSetupResponse
{
    public bool Success { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string PanelUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class VeresiyeQrCreateRequest
{
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class VeresiyeQrCreateResponse
{
    public bool Success { get; set; }
    public string LoginUrl { get; set; } = string.Empty;
    public string QrValue { get; set; } = string.Empty;
    public string ExpiresAt { get; set; } = string.Empty;
    public int ExpiresInSeconds { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class VeresiyeEntityItem
{
    public string Id { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string CompanyCloudId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
    public long Version { get; set; }
}

public sealed class VeresiyeEntityUpsertRequest
{
    public string PayloadJson { get; set; } = string.Empty;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class VeresiyeSyncChangeResponse
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

public sealed class VeresiyeSnapshotItem
{
    public string Id { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
}

public sealed class VeresiyeSnapshotRequest
{
    public string EntityType { get; set; } = string.Empty;
    public List<VeresiyeSnapshotItem> Items { get; set; } = new();
    public bool ReplaceExisting { get; set; } = true;
    public string SourceDeviceId { get; set; } = string.Empty;
    public string SourceUser { get; set; } = string.Empty;
}

public sealed class VeresiyeSnapshotResponse
{
    public bool Success { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public int SavedCount { get; set; }
    public int DeletedCount { get; set; }
    public long LastChangeId { get; set; }
}
