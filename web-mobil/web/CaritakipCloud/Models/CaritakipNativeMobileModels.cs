namespace NSYazilim.Web.CaritakipCloud.Models;

public sealed class CariNativeDeviceInfo
{
    public string Platform { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    // Stable per-install id generated/persisted by the native client.
    // Optional for backward compatibility with older app builds.
    public string DeviceId { get; set; } = string.Empty;
}

public sealed class CariNativePairRequest
{
    public string Token { get; set; } = string.Empty;
    public CariNativeDeviceInfo? DeviceInfo { get; set; }
}

public sealed class CariNativeRefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed class CariNativeLogoutRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public sealed class CariNativeCustomerWriteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class CariNativeTransactionWriteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
    public string CompanyId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime? Date { get; set; }
    public DateTime? TransactionDateUtc { get; set; }
}


public sealed class CariNativeDeleteRequest
{
    public string ClientMutationId { get; set; } = string.Empty;
    public long? ExpectedVersion { get; set; }
}

public sealed class CariNativeInviteRequest
{
    public int? ExpiresInSeconds { get; set; }
}

public sealed class CariNativeDeviceRevokeRequest
{
    public string DeviceId { get; set; } = string.Empty;
}

public sealed class CariNativePushTokenRequest
{
    public string Token { get; set; } = string.Empty;
    public string Provider { get; set; } = "expo";
    public string Platform { get; set; } = string.Empty;
}

public sealed class CariNativeInboxItemDto
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = "system";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool Read { get; set; }
}

public sealed class CariNativeInboxMarkReadRequest
{
    public IReadOnlyList<string>? Ids { get; set; }
    public bool All { get; set; }
}

public sealed class CariNativeInboxDeleteRequest
{
    public IReadOnlyList<string>? Ids { get; set; }
    public bool All { get; set; }
    public DateTime? CreatedBeforeUtc { get; set; }
}

public sealed class CariNativeNotificationPrefsDto
{
    public bool Master { get; set; } = true;
    public bool Reminders { get; set; } = true;
    public bool Collections { get; set; } = true;
    public bool Debts { get; set; } = true;
    public bool System { get; set; } = true;
}

public sealed class CariNativeDeviceDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string AppVersion { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastSeenAtUtc { get; set; }
}

public sealed class CariNativeCompanyDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TaxNumber { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public sealed class CariNativeCustomerDto
{
    public string Id { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public decimal NetBalance { get; set; }
    public DateTime UpdatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class CariNativeTransactionDto
{
    public string Id { get; set; } = string.Empty;
    public string CompanyId { get; set; } = string.Empty;
    public string CustomerId { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime CreatedAt { get; set; }
    public long Version { get; set; }
}

public sealed class CariNativeDashboardStatusDto
{
    public string CompanyId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public decimal TotalReceivable { get; set; }
    public decimal TotalCollected { get; set; }
    public decimal NetBalance { get; set; }
    public int CustomerCount { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record CariNativeAccessContext(
    string TenantId,
    string CompanyName,
    int LicenseId,
    string MobileDeviceId,
    string AccessTokenHash,
    DateTime ExpiresAtUtc);

public sealed record CariNativeIssuedSession(
    CariTenantContext Tenant,
    string MobileDeviceId,
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAtUtc,
    DateTime RefreshTokenExpiresAtUtc,
    string PairedByDeviceId,
    string PairedByDisplayName);

public sealed record CariNativeRefreshResult(
    string AccessToken,
    string RefreshToken,
    DateTime AccessTokenExpiresAtUtc,
    DateTime RefreshTokenExpiresAtUtc);

public sealed record CariNativeAuthCheck(CariNativeAccessContext? Session, string? Error);
public sealed record CariNativeRefreshCheck(CariNativeRefreshResult? Result, string? Error);
