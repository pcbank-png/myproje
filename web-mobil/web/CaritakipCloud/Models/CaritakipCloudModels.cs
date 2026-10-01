using System.Text.Json;

namespace NSYazilim.Web.CaritakipCloud.Models;

public sealed record CariTenantContext(string TenantId, string CompanyName, int LicenseId);

public sealed record CariDeviceContext(
    string TenantId,
    string CompanyName,
    int LicenseId,
    string DeviceId,
    string MachineHash);

public sealed record CariMobileSessionContext(
    string TenantId,
    string CompanyName,
    string SessionHash,
    string CsrfHash,
    DateTime ExpiresAtUtc);

public sealed class CariTenantSetupRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string EmailEncoded { get; set; } = string.Empty;
    public string AccountIdentity { get; set; } = string.Empty;
    public string ProductCode { get; set; } = "NSXCARITAKIPPROBULUT";
    public string CompanyName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string HostRef { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string CurrentApiToken { get; set; } = string.Empty;
}

public sealed class CariTenantSetupResponse
{
    public bool Success { get; set; }
    public bool IsPrimaryDevice { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string ApiToken { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string PanelUrl { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class CariSyncPushRequest
{
    public List<CariMutationRequest> Mutations { get; set; } = new();
}

public sealed class CariMutationRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Operation { get; set; } = "upsert";
    public long? ExpectedVersion { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed class CariMutationResult
{
    public string ClientMutationId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long Version { get; set; }
    public long Cursor { get; set; }
    public CariEntityDto? Current { get; set; }
}

public sealed class CariSyncPushResponse
{
    public long Cursor { get; set; }
    public List<CariMutationResult> Results { get; set; } = new();
}

public sealed class CariEntityDto
{
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
    public bool IsDeleted { get; set; }
    public long Version { get; set; }
    public long Cursor { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class CariChangesResponse
{
    public long After { get; set; }
    public long Cursor { get; set; }
    public bool HasMore { get; set; }
    public List<CariEntityDto> Changes { get; set; } = new();
}

public sealed class CariQrIssueRequest
{
    public int ExpiresInSeconds { get; set; } = 180;
}

public sealed class CariQrIssueResponse
{
    public string LoginUrl { get; set; } = string.Empty;
    public string QrValue { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public int ExpiresInSeconds { get; set; }
}

public sealed class CariQrConsumeRequest
{
    public string Token { get; set; } = string.Empty;
}

public sealed class CariTerminalInviteRequest
{
    public int ExpiresInSeconds { get; set; } = 600;
}

public sealed class CariTerminalPairingException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class CariTerminalInviteResponse
{
    public string InviteCode { get; set; } = string.Empty;
    public string InviteValue { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public int ExpiresInSeconds { get; set; }
}

public sealed class CariTerminalEnrollRequest
{
    public string? EnrollmentToken { get; set; }
    public string InviteCode { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
}

public sealed class CariCustomerWriteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
    public string? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class CariTransactionWriteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
    public string CustomerId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTime? TransactionDateUtc { get; set; }
    public string Description { get; set; } = string.Empty;
}

public sealed class CariDeleteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
}
