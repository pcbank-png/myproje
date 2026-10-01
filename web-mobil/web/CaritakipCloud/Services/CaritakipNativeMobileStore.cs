using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using NSYazilim.Web.CaritakipCloud.Models;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipNativeMobileStore
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(10);
    // Kullanıcı isteği: explicit logout/revoke olmadıkça oturum kalıcı kalsın.
    // Refresh her rotasyonda yeniden 10 yıl uzatılır; tokenlar server-side revoke edilebilir.
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(3650);

    private static readonly JsonSerializerOptions NotifPrefsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _connectionString;
    private readonly ILogger<CaritakipNativeMobileStore> _logger;
    private readonly CaritakipEntitlementValidator _entitlementValidator;
    private readonly CaritakipCloudStore _cloudStore;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public CaritakipNativeMobileStore(
        IConfiguration configuration,
        ILogger<CaritakipNativeMobileStore> logger,
        CaritakipEntitlementValidator entitlementValidator,
        CaritakipCloudStore cloudStore)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection yapılandırılmamış.");
        _connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.None
        }.ConnectionString;
        _logger = logger;
        _entitlementValidator = entitlementValidator;
        _cloudStore = cloudStore;
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await _cloudStore.EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
CREATE TABLE IF NOT EXISTS ct_mobile_native_devices (
  tenant_id CHAR(36) NOT NULL,
  mobile_device_id CHAR(36) NOT NULL,
  platform VARCHAR(24) NOT NULL DEFAULT '',
  app_version VARCHAR(48) NOT NULL DEFAULT '',
  device_name VARCHAR(180) NOT NULL DEFAULT '',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at_utc DATETIME(6) NOT NULL,
  updated_at_utc DATETIME(6) NOT NULL,
  last_seen_at_utc DATETIME(6) NULL,
  PRIMARY KEY (tenant_id, mobile_device_id),
  KEY ix_ct_mobile_native_device_active (tenant_id, is_active),
  CONSTRAINT fk_ct_mobile_native_device_tenant FOREIGN KEY (tenant_id)
    REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mobile_access_tokens (
  token_hash CHAR(64) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  mobile_device_id CHAR(36) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  expires_at_utc DATETIME(6) NOT NULL,
  last_seen_at_utc DATETIME(6) NULL,
  revoked_at_utc DATETIME(6) NULL,
  KEY ix_ct_mobile_access_device (tenant_id, mobile_device_id, expires_at_utc),
  CONSTRAINT fk_ct_mobile_access_device FOREIGN KEY (tenant_id, mobile_device_id)
    REFERENCES ct_mobile_native_devices(tenant_id, mobile_device_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mobile_refresh_tokens (
  token_hash CHAR(64) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  mobile_device_id CHAR(36) NOT NULL,
  family_id CHAR(36) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  expires_at_utc DATETIME(6) NOT NULL,
  last_used_at_utc DATETIME(6) NULL,
  revoked_at_utc DATETIME(6) NULL,
  replaced_by_hash CHAR(64) NULL,
  KEY ix_ct_mobile_refresh_device (tenant_id, mobile_device_id, expires_at_utc),
  KEY ix_ct_mobile_refresh_family (tenant_id, family_id),
  CONSTRAINT fk_ct_mobile_refresh_device FOREIGN KEY (tenant_id, mobile_device_id)
    REFERENCES ct_mobile_native_devices(tenant_id, mobile_device_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
""";
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsurePushTokenColumnsAsync(connection, cancellationToken);
            await EnsureInboxTablesAsync(connection, cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static async Task EnsurePushTokenColumnsAsync(
        System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "ALTER TABLE ct_mobile_native_devices ADD COLUMN push_expo_token VARCHAR(512) NULL",
            "ALTER TABLE ct_mobile_native_devices ADD COLUMN push_provider VARCHAR(24) NOT NULL DEFAULT 'expo'",
            "ALTER TABLE ct_mobile_native_devices ADD COLUMN push_platform VARCHAR(24) NOT NULL DEFAULT ''",
            "ALTER TABLE ct_mobile_native_devices ADD COLUMN notif_prefs_json JSON NULL",
        })
        {
            try
            {
                await using var alter = connection.CreateCommand();
                alter.CommandText = sql;
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                // sütun zaten var
            }
        }
    }

    public async Task<bool> RegisterPushTokenAsync(
        CariNativeAccessContext session,
        string token,
        string provider,
        string platform,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        token = (token ?? string.Empty).Trim();
        if (token.Length == 0 || token.Length > 512) return false;
        provider = Limit(provider, 24);
        platform = Limit(platform, 24);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable,cancellationToken);
        await ExecuteAsync(connection,transaction,"UPDATE ct_mobile_native_devices SET push_expo_token=NULL WHERE push_expo_token=@token AND NOT (tenant_id=@tenant AND mobile_device_id=@device);",cancellationToken,("@token",token),("@tenant",session.TenantId),("@device",session.MobileDeviceId));
        await using var command = CreateCommand(connection, transaction, """
UPDATE ct_mobile_native_devices
SET push_expo_token=@token, push_provider=@provider, push_platform=@platform,
    updated_at_utc=@now, last_seen_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device AND is_active=1;
""", ("@token", token), ("@provider", provider), ("@platform", platform),
            ("@now", now), ("@tenant", session.TenantId), ("@device", session.MobileDeviceId));
        var saved = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (saved) await transaction.CommitAsync(cancellationToken);
        return saved;
    }

    public static CariNativeNotificationPrefsDto DefaultNotificationPrefs() => new();

    public async Task<bool> SaveNotificationPrefsAsync(
        CariNativeAccessContext session,
        CariNativeNotificationPrefsDto prefs,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var json = JsonSerializer.Serialize(NormalizePrefs(prefs), NotifPrefsJson);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
UPDATE ct_mobile_native_devices
SET notif_prefs_json=@json, updated_at_utc=@now, last_seen_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device AND is_active=1;
""", ("@json", json), ("@now", now),
            ("@tenant", session.TenantId), ("@device", session.MobileDeviceId));
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<CariNativeNotificationPrefsDto> GetNotificationPrefsAsync(
        CariNativeAccessContext session,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT notif_prefs_json FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND mobile_device_id=@device AND is_active=1 LIMIT 1;
""", ("@tenant", session.TenantId), ("@device", session.MobileDeviceId));
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is null or DBNull) return DefaultNotificationPrefs();
        var text = Convert.ToString(scalar, CultureInfo.InvariantCulture)?.Trim();
        if (string.IsNullOrEmpty(text)) return DefaultNotificationPrefs();
        try
        {
            return NormalizePrefs(JsonSerializer.Deserialize<CariNativeNotificationPrefsDto>(text, NotifPrefsJson)
                ?? DefaultNotificationPrefs());
        }
        catch
        {
            return DefaultNotificationPrefs();
        }
    }

    public async Task<Dictionary<string, CariNativeNotificationPrefsDto>> GetNotificationPrefsMapAsync(
        string tenantId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var map = new Dictionary<string, CariNativeNotificationPrefsDto>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT mobile_device_id, notif_prefs_json FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND is_active=1;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceId = reader.GetString(0);
            CariNativeNotificationPrefsDto prefs = DefaultNotificationPrefs();
            if (!reader.IsDBNull(1))
            {
                var text = reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        prefs = NormalizePrefs(JsonSerializer.Deserialize<CariNativeNotificationPrefsDto>(text, NotifPrefsJson)
                            ?? DefaultNotificationPrefs());
                    }
                    catch
                    {
                        // varsayılan
                    }
                }
            }
            map[deviceId] = prefs;
        }
        return map;
    }

    public static bool AllowsNotificationCategory(
        CariNativeNotificationPrefsDto prefs, string category)
    {
        prefs = NormalizePrefs(prefs);
        if (!prefs.Master) return false;
        category = (category ?? string.Empty).Trim().ToLowerInvariant();
        return category switch
        {
            "reminders" => prefs.Reminders,
            "collections" => prefs.Collections,
            "debts" => prefs.Debts,
            "system" => prefs.System,
            _ => prefs.System
        };
    }

    private static CariNativeNotificationPrefsDto NormalizePrefs(CariNativeNotificationPrefsDto prefs) =>
        prefs ?? DefaultNotificationPrefs();

    public sealed record CariNativePushTarget(string MobileDeviceId, string Token);

    public async Task<bool> IsPushTargetEligibleAsync(string tenant, string device, string token, string category, CancellationToken ct, string? messageId = null)
    {
        var targets = await GetPushTargetsAsync(tenant,null,ct);
        if (!targets.Any(target => target.MobileDeviceId == device && target.Token == token)) return false;
        var prefs = await GetNotificationPrefsMapAsync(tenant,ct);
        if (!AllowsNotificationCategory(prefs.GetValueOrDefault(device) ?? DefaultNotificationPrefs(),category)) return false;
        if (string.IsNullOrWhiteSpace(messageId)) return false;
        await using var connection = await OpenAsync(ct);
        await using var command = CreateCommand(connection,null,"""
SELECT 1 FROM ct_mobile_inbox WHERE tenant_id=@tenant AND mobile_device_id=@device
AND message_id=@id AND read_at_utc IS NULL;
""",("@tenant",tenant),("@device",device),("@id",messageId));
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    public async Task RemovePushTokenAsync(string tenant, string device, string token, CancellationToken ct)
    {
        await EnsureSchemaAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var command = CreateCommand(connection,null,"""
UPDATE ct_mobile_native_devices SET push_expo_token=NULL
WHERE tenant_id=@tenant AND mobile_device_id=@device AND push_expo_token=@token;
""",("@tenant",tenant),("@device",device),("@token",token));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<CariNativePushTarget>> GetPushTargetsAsync(
        string tenantId,
        string? excludeMobileDeviceId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        tenantId = (tenantId ?? string.Empty).Trim();
        if (tenantId.Length == 0) return Array.Empty<CariNativePushTarget>();

        var result = new List<CariNativePushTarget>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT mobile_device_id, push_expo_token
FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND is_active=1
AND push_expo_token IS NOT NULL AND TRIM(push_expo_token)<>'' 
ORDER BY COALESCE(last_seen_at_utc,updated_at_utc) DESC;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceId = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(excludeMobileDeviceId) &&
                string.Equals(deviceId, excludeMobileDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            var token = reader.GetString(1).Trim();
            if (token.Length == 0) continue;
            result.Add(new CariNativePushTarget(deviceId, token));
        }
        return result;
    }

    private static async Task EnsureInboxTablesAsync(
        System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
CREATE TABLE IF NOT EXISTS ct_mobile_inbox (
  tenant_id CHAR(36) NOT NULL,
  mobile_device_id CHAR(36) NOT NULL,
  message_id CHAR(36) NOT NULL,
  category VARCHAR(32) NOT NULL DEFAULT 'system',
  title VARCHAR(180) NOT NULL,
  body VARCHAR(512) NOT NULL,
  payload_json JSON NULL,
  created_at_utc DATETIME(6) NOT NULL,
  read_at_utc DATETIME(6) NULL,
  PRIMARY KEY (tenant_id, mobile_device_id, message_id),
  KEY ix_ct_mobile_inbox_list (tenant_id, mobile_device_id, created_at_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mobile_notification_deliveries (
  tenant_id CHAR(36) NOT NULL, mobile_device_id CHAR(36) NOT NULL,
  message_id CHAR(36) NOT NULL, created_at_utc DATETIME(6) NOT NULL,
  PRIMARY KEY(tenant_id,mobile_device_id,message_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mobile_notification_dedupe (
  tenant_id CHAR(36) NOT NULL,
  dedupe_key VARCHAR(190) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (tenant_id, dedupe_key)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
""";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public sealed record CariNativeDueReminder(
        string EntityId,
        string CustomerId,
        string Title,
        string Body,
        DateTime DueLocal);

    public async Task<IReadOnlyList<string>> GetTenantsWithActiveNativeDevicesAsync(
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<string>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT DISTINCT tenant_id FROM ct_mobile_native_devices WHERE is_active=1;
""");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<string>> GetActiveNativeDeviceIdsAsync(
        string tenantId,
        string? excludeMobileDeviceId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<string>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT mobile_device_id FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND is_active=1
ORDER BY COALESCE(last_seen_at_utc,updated_at_utc) DESC;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(excludeMobileDeviceId) &&
                string.Equals(id, excludeMobileDeviceId, StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(id);
        }
        return result;
    }

    public async Task<bool> InsertInboxMessageAsync(
        string tenantId,
        string mobileDeviceId,
        string messageId,
        string category,
        string title,
        string body,
        string? payloadJson,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var marker = CreateCommand(connection,transaction,"""
INSERT IGNORE INTO ct_mobile_notification_deliveries(tenant_id,mobile_device_id,message_id,created_at_utc)
VALUES(@tenant,@device,@id,@now);
""",("@tenant",tenantId),("@device",mobileDeviceId),("@id",messageId),("@now",now));
        if (await marker.ExecuteNonQueryAsync(cancellationToken) == 0) return false;
        await using var command = CreateCommand(connection, transaction, """
INSERT INTO ct_mobile_inbox
(tenant_id,mobile_device_id,message_id,category,title,body,payload_json,created_at_utc)
VALUES(@tenant,@device,@id,@category,@title,@body,@payload,@now)
ON DUPLICATE KEY UPDATE title=VALUES(title), body=VALUES(body);
""", ("@tenant", tenantId), ("@device", mobileDeviceId), ("@id", messageId),
            ("@category", category), ("@title", title), ("@body", body),
            ("@payload", payloadJson), ("@now", now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<List<CariNativeInboxItemDto>> GetInboxAsync(
        CariNativeAccessContext session,
        int take,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        take = Math.Clamp(take, 1, 100);
        var result = new List<CariNativeInboxItemDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT message_id,category,title,body,created_at_utc,read_at_utc
FROM ct_mobile_inbox
WHERE tenant_id=@tenant AND mobile_device_id=@device
ORDER BY created_at_utc DESC LIMIT @take;
""", ("@tenant", session.TenantId), ("@device", session.MobileDeviceId), ("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CariNativeInboxItemDto
            {
                Id = reader.GetString(0),
                Category = reader.GetString(1),
                Title = reader.GetString(2),
                Body = reader.GetString(3),
                CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                Read = !reader.IsDBNull(5)
            });
        }
        return result;
    }

    public async Task<CariNativeInboxItemDto?> GetInboxMessageAsync(
        CariNativeAccessContext session,
        string messageId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        messageId = (messageId ?? string.Empty).Trim();
        if (!Guid.TryParse(messageId, out _)) return null;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT message_id,category,title,body,created_at_utc,read_at_utc
FROM ct_mobile_inbox
WHERE tenant_id=@tenant AND mobile_device_id=@device AND message_id=@id LIMIT 1;
""", ("@tenant", session.TenantId), ("@device", session.MobileDeviceId), ("@id", messageId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CariNativeInboxItemDto
        {
            Id = reader.GetString(0),
            Category = reader.GetString(1),
            Title = reader.GetString(2),
            Body = reader.GetString(3),
            CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
            Read = !reader.IsDBNull(5)
        };
    }

    public async Task<int> DeleteInboxAsync(
        CariNativeAccessContext session,
        IReadOnlyList<string>? ids,
        bool all,
        CancellationToken cancellationToken,
        DateTime? createdBeforeUtc = null)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        if (all)
        {
            await using var command = CreateCommand(connection, null, """
DELETE FROM ct_mobile_inbox
WHERE tenant_id=@tenant AND mobile_device_id=@device
  AND (@cutoff IS NULL OR created_at_utc<=@cutoff);
""", ("@tenant", session.TenantId), ("@device", session.MobileDeviceId), ("@cutoff", createdBeforeUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (ids is null || ids.Count == 0) return 0;
        var deleted = 0;
        foreach (var id in ids.Where(x => Guid.TryParse(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = CreateCommand(connection, null, """
DELETE FROM ct_mobile_inbox
WHERE tenant_id=@tenant AND mobile_device_id=@device AND message_id=@id;
""", ("@tenant", session.TenantId), ("@device", session.MobileDeviceId), ("@id", id));
            deleted += await command.ExecuteNonQueryAsync(cancellationToken);
        }
        return deleted;
    }

    public async Task MarkInboxReadAsync(
        CariNativeAccessContext session,
        IReadOnlyList<string>? ids,
        bool all,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        if (all)
        {
            await using var command = CreateCommand(connection, null, """
UPDATE ct_mobile_inbox SET read_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device AND read_at_utc IS NULL;
""", ("@now", now), ("@tenant", session.TenantId), ("@device", session.MobileDeviceId));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (ids is null || ids.Count == 0) return;
        foreach (var id in ids.Where(x => Guid.TryParse(x, out _)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = CreateCommand(connection, null, """
UPDATE ct_mobile_inbox SET read_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device AND message_id=@id AND read_at_utc IS NULL;
""", ("@now", now), ("@tenant", session.TenantId), ("@device", session.MobileDeviceId), ("@id", id));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<bool> TryRecordNotificationDedupeAsync(
        string tenantId,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        dedupeKey = Limit(dedupeKey, 190);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(connection, null, """
INSERT INTO ct_mobile_notification_dedupe(tenant_id,dedupe_key,created_at_utc)
VALUES(@tenant,@key,@now);
""", ("@tenant", tenantId), ("@key", dedupeKey), ("@now", now));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return true;
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            return false;
        }
    }

    public async Task<bool> HasNotificationDedupeAsync(
        string tenantId,
        string dedupeKey,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        dedupeKey = Limit(dedupeKey, 190);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT 1 FROM ct_mobile_notification_dedupe
WHERE tenant_id=@tenant AND dedupe_key=@key LIMIT 1;
""", ("@tenant", tenantId), ("@key", dedupeKey));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && value is not DBNull;
    }

    public async Task<List<CariNativeDueReminder>> GetDueRemindersAsync(
        string tenantId,
        DateTime nowLocal,
        TimeSpan lookback,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var turkey = TimeZoneInfo.FindSystemTimeZoneById(
            OperatingSystem.IsWindows() ? "Turkey Standard Time" : "Europe/Istanbul");
        var result = new List<CariNativeDueReminder>();
        var customerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = await OpenAsync(cancellationToken);
        await using (var customers = CreateCommand(connection, null, """
SELECT entity_id, JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.name'))
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='customer' AND is_deleted=0;
""", ("@tenant", tenantId)))
        await using (var customerReader = await customers.ExecuteReaderAsync(cancellationToken))
        {
            while (await customerReader.ReadAsync(cancellationToken))
            {
                var id = customerReader.GetString(0);
                var name = customerReader.IsDBNull(1) ? string.Empty : customerReader.GetString(1);
                if (id.Length > 0) customerNames[id] = name.Trim();
            }
        }

        await using var command = CreateCommand(connection, null, """
SELECT entity_id,entity_type,payload_json FROM ct_entities
WHERE tenant_id=@tenant AND is_deleted=0
  AND LOWER(entity_type) IN ('reminder','debt');
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entityId = reader.GetString(0);
            var entityType = reader.GetString(1);
            using var document = JsonDocument.Parse(reader.GetString(2));
            var payload = document.RootElement;
            var isReminderEntity = string.Equals(entityType, "reminder", StringComparison.OrdinalIgnoreCase);

            if (isReminderEntity && IsReminderCompleted(payload)) continue;
            // Borç: yalnız açık hatırlatma tarihi olanlar (reminderDate / notifyAt / alarm…).
            if (!isReminderEntity && !DebtHasExplicitReminder(payload)) continue;

            if (!TryReadDueLocalDateTime(payload, turkey, out var dueLocal)) continue;
            // isCompleted masaüstünde alert sonrası true olabilir — mobil push’u engelleme.
            if (!IsReminderDue(dueLocal, nowLocal, lookback)) continue;

            var customerId = ReadJsonStringCI(payload, "customerId", "CustomerId");
            var customerName = customerNames.GetValueOrDefault(customerId);
            if (string.IsNullOrWhiteSpace(customerName)) customerName = "Müşteri";

            var typeLabel = FirstNonEmpty(
                ReadJsonStringCI(payload, "type", "Type", "reminderType", "ReminderType", "kind", "Kind"),
                ReadJsonStringCI(payload, "typeName", "TypeName", "tur", "Tur"));
            var note = FirstNonEmpty(
                ReadJsonStringCI(payload, "notes", "Notes", "note", "Note"),
                ReadJsonStringCI(payload, "description", "Description"),
                ReadJsonStringCI(payload, "text", "Text", "message", "Message"),
                ReadJsonStringCI(payload, "title", "Title", "subject", "Subject"));
            var amount = ReadJsonDecimalCI(payload, "amount", "Amount");
            var amountPart = amount > 0 ? " · " + FormatTry(amount) : string.Empty;
            var dueText = dueLocal.ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("tr-TR"));

            // Başlık = Tür (Ödeme Hatırlatması vb.); yoksa varsayılan.
            var title = isReminderEntity
                ? (string.IsNullOrWhiteSpace(typeLabel) ? "Hatırlatma" : Limit(typeLabel, 80))
                : "Vade hatırlatması";
            var body = string.IsNullOrWhiteSpace(note)
                ? $"{customerName}{amountPart} — {dueText}"
                : $"{customerName}{amountPart} — {TrimReminderBody(note)} ({dueText})";

            result.Add(new CariNativeDueReminder(entityId, customerId, title, body, dueLocal));
        }

        return result;
    }

    private static bool DebtHasExplicitReminder(JsonElement payload)
    {
        foreach (var key in new[]
                 {
                     "reminderDateUtc", "ReminderDateUtc", "reminderDate", "ReminderDate",
                     "notifyAtUtc", "NotifyAtUtc", "notifyAt", "NotifyAt",
                     "alarmAtUtc", "AlarmAtUtc", "alarmAt", "AlarmAt",
                     "remindAtUtc", "RemindAtUtc", "remindAt", "RemindAt",
                     "fireAtUtc", "FireAtUtc", "hatirlatmaTarihi", "HatirlatmaTarihi"
                 })
        {
            if (TryGetPropertyCI(payload, key, out _)) return true;
        }
        return false;
    }

    private static bool IsReminderCompleted(JsonElement payload)
    {
        // Masaüstü bazen alert sonrası isCompleted=true yazar; yalnız iptal/silinmişi kesin at.
        var status = ReadJsonStringCI(payload, "status", "Status", "state", "State");
        if (status.Length > 0 &&
            (status.Contains("cancel", StringComparison.OrdinalIgnoreCase)
             || status.Contains("deleted", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (ReadJsonBoolCI(payload, false, "isCancelled", "IsCancelled", "cancelled", "Cancelled")) return true;
        return false;
    }

    private static bool IsReminderDue(DateTime dueLocal, DateTime nowLocal, TimeSpan lookback) =>
        dueLocal <= nowLocal && dueLocal >= nowLocal - lookback;

    private static bool TryReadDueLocalDateTime(JsonElement payload, TimeZoneInfo tz, out DateTime dueLocal)
    {
        dueLocal = default;

        // A scheduled instant is authoritative over generic date/time fields.
        foreach (var key in new[] { "dueAtUtc", "dueAt" })
        {
            if (!TryGetPropertyCI(payload, key, out var value)) continue;
            return TryParseJsonDateTime(value, tz,
                preferUtc: key.EndsWith("Utc", StringComparison.OrdinalIgnoreCase), out dueLocal);
        }

        // 1) date + time ayrı alanlar ÖNCE — dueDate yalnız gün olunca saati yutmasın.
        if (TryCombineDateAndTime(payload, tz, out dueLocal))
            return true;

        // 2) Diğer desteklenen hatırlatma tarih-saat alanları.
        foreach (var key in new[]
                 {
                     "dueDateUtc", "reminderDateUtc", "scheduledAtUtc", "notifyAtUtc",
                     "alarmAtUtc", "fireAtUtc", "remindAtUtc", "dateUtc",
                     "dueDate", "reminderDate", "scheduledAt", "notifyAt",
                     "alarmAt", "fireAt", "remindAt", "reminderAt",
                     "tarihSaat", "vadeTarihi", "hatirlatmaTarihi", "DateTime", "dateTime"
                 })
        {
            if (!TryGetPropertyCI(payload, key, out var value)) continue;
            if (TryParseJsonDateTime(value, tz,
                    preferUtc: key.EndsWith("Utc", StringComparison.OrdinalIgnoreCase),
                    out dueLocal))
                return true;
        }

        // 3) Yalnız date.
        foreach (var key in new[] { "date", "tarih", "day" })
        {
            if (!TryGetPropertyCI(payload, key, out var value)) continue;
            if (!TryParseJsonDateTime(value, tz, preferUtc: false, out var day)) continue;
            dueLocal = day.Date;
            return true;
        }

        // Never infer a reminder deadline from createdAt/updatedAt or audit dates.
        return false;
    }

    private static bool TryCombineDateAndTime(JsonElement payload, TimeZoneInfo tz, out DateTime dueLocal)
    {
        dueLocal = default;
        DateTime? day = null;
        TimeSpan? clock = null;

        foreach (var key in new[] { "date", "Date", "tarih", "dueDate", "reminderDate", "day" })
        {
            if (!TryGetPropertyCI(payload, key, out var value)) continue;
            if (!TryParseJsonDateTime(value, tz, preferUtc: false, out var parsed)) continue;
            // Tam tarih-saat geldiyse ve time alanı yoksa combine sayma — üst katman alsın.
            day = parsed.Date;
            if (parsed.TimeOfDay != TimeSpan.Zero && !HasTimeField(payload))
            {
                dueLocal = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
                return true;
            }
            break;
        }

        foreach (var key in new[] { "time", "Time", "saat", "dueTime", "reminderTime", "clock", "hour" })
        {
            if (!TryGetPropertyCI(payload, key, out var value)) continue;
            if (TryParseTimeOfDay(value, out var tod))
            {
                clock = tod;
                break;
            }
        }

        if (day is null || clock is null) return false;

        dueLocal = day.Value.Date + clock.Value;
        return true;
    }

    private static bool HasTimeField(JsonElement payload)
    {
        foreach (var key in new[] { "time", "Time", "saat", "dueTime", "reminderTime", "clock", "hour" })
        {
            if (TryGetPropertyCI(payload, key, out _)) return true;
        }
        return false;
    }

    private static bool TryParseTimeOfDay(JsonElement value, out TimeSpan tod)
    {
        tod = default;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n))
        {
            if (n is >= 0 and <= 2359)
            {
                var h = (int)(n / 100);
                var m = (int)(n % 100);
                if (h is >= 0 and <= 23 && m is >= 0 and <= 59)
                {
                    tod = new TimeSpan(h, m, 0);
                    return true;
                }
            }
        }

        if (value.ValueKind != JsonValueKind.String) return false;
        var raw = (value.GetString() ?? string.Empty).Trim();
        if (raw.Length == 0) return false;
        if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out tod)) return true;
        if (DateTime.TryParse(raw, CultureInfo.GetCultureInfo("tr-TR"),
                DateTimeStyles.AllowWhiteSpaces, out var dt)
            || DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out dt))
        {
            tod = dt.TimeOfDay;
            return true;
        }
        return false;
    }

    private static bool TryParseJsonDateTime(
        JsonElement value, TimeZoneInfo tz, bool preferUtc, out DateTime dueLocal)
    {
        dueLocal = default;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var epoch))
        {
            var utc = epoch > 10_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(epoch).UtcDateTime
                : DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
            dueLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return true;
        }

        if (value.ValueKind != JsonValueKind.String) return false;
        var raw = value.GetString();
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // .NET /Date(1727654400000)/ veya /Date(1727654400000+0300)/
        var msDate = System.Text.RegularExpressions.Regex.Match(
            raw, @"^/Date\((-?\d+)([+-]\d{4})?\)/$");
        if (msDate.Success && long.TryParse(msDate.Groups[1].Value, out var msEpoch))
        {
            var utc = DateTimeOffset.FromUnixTimeMilliseconds(msEpoch).UtcDateTime;
            dueLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
            return true;
        }

        // Offset / Z varsa doğru çevir.
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dto)
            && (raw.Contains('Z', StringComparison.OrdinalIgnoreCase)
                || raw.Contains('+', StringComparison.Ordinal)
                || raw.LastIndexOf('-') > 10))
        {
            dueLocal = TimeZoneInfo.ConvertTime(dto, tz).DateTime;
            return true;
        }

        if (DateTime.TryParse(raw, CultureInfo.GetCultureInfo("tr-TR"),
                DateTimeStyles.AllowWhiteSpaces, out var localTr)
            || DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out localTr))
        {
            if (preferUtc)
            {
                var asUtc = DateTime.SpecifyKind(localTr, DateTimeKind.Utc);
                dueLocal = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
            }
            else
            {
                // Offset yok → Türkiye saati kabul et (Plesk UTC olsa bile kaymasın).
                dueLocal = DateTime.SpecifyKind(localTr, DateTimeKind.Unspecified);
            }
            return true;
        }

        return false;
    }

    private static bool TryGetPropertyCI(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }
        foreach (var prop in element.EnumerateObject())
        {
            if (!string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = prop.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string ReadJsonStringCI(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCI(element, name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                _ => string.Empty
            };
        }
        return string.Empty;
    }

    private static decimal ReadJsonDecimalCI(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCI(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var n)) return n;
            if (value.ValueKind == JsonValueKind.String &&
                decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out n))
                return n;
        }
        return 0m;
    }

    private static bool ReadJsonBoolCI(JsonElement element, bool fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetPropertyCI(element, name, out var value)) continue;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
                _ => fallback
            };
        }
        return fallback;
    }

    private static string FormatTry(decimal amount) =>
        "₺" + decimal.Round(amount, 2).ToString("N2", CultureInfo.GetCultureInfo("tr-TR"));

    private static string TrimReminderBody(string value)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= 100 ? value : value[..97] + "…";
    }

    public async Task<CariNativeIssuedSession?> ConsumeQrAsync(
        string rawToken, CariNativeDeviceInfo? deviceInfo, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        rawToken = (rawToken ?? string.Empty).Trim();
        if (rawToken.Length == 0) return null;

        var now = DateTime.UtcNow;
        var qrHash = Hash(rawToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        CariTenantContext? tenant = null;
        string issuedByDeviceId = string.Empty;
        await using (var command = CreateCommand(connection, transaction, """
SELECT t.tenant_id,t.company_name,t.license_id,q.issued_by_device_id
FROM ct_qr_tokens q
INNER JOIN ct_tenants t ON t.tenant_id=q.tenant_id
WHERE q.token_hash=@hash AND q.consumed_at_utc IS NULL AND q.expires_at_utc>=@now
AND t.is_active=1 LIMIT 1 FOR UPDATE;
""", ("@hash", qrHash), ("@now", now)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                tenant = new CariTenantContext(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
                issuedByDeviceId = reader.GetString(3);
            }
        }

        if (tenant is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var entitlement = await _entitlementValidator.ValidateAsync(tenant.LicenseId, cancellationToken);
        if (!entitlement.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var changed = await ExecuteAsync(connection, transaction, """
UPDATE ct_qr_tokens SET consumed_at_utc=@now
WHERE token_hash=@hash AND consumed_at_utc IS NULL AND expires_at_utc>=@now;
""", cancellationToken, ("@now", now), ("@hash", qrHash));
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var platform = Limit(deviceInfo?.Platform, 24).ToLowerInvariant();
        if (platform is not ("ios" or "android" or "web")) platform = "unknown";
        var appVersion = Limit(deviceInfo?.AppVersion, 48);
        var deviceName = Limit(deviceInfo?.DeviceName, 180);
        var suppliedDeviceId = Limit(deviceInfo?.DeviceId, 64);
        var mobileDeviceId = Guid.TryParse(suppliedDeviceId, out var parsedDeviceId)
            ? parsedDeviceId.ToString()
            : Guid.NewGuid().ToString();
        var familyId = Guid.NewGuid().ToString();
        var accessToken = CreateToken(48);
        var refreshToken = CreateToken(64);
        var accessExpiry = now.Add(AccessLifetime);
        var refreshExpiry = now.Add(RefreshLifetime);

        // Re-pairing the same app installation must rotate its credentials, not
        // create another active device registration. Older clients that do not
        // send DeviceId keep the previous new-GUID behavior.
        await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_access_tokens
SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device AND revoked_at_utc IS NULL;
UPDATE ct_mobile_refresh_tokens
SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device AND revoked_at_utc IS NULL;
""", cancellationToken,
            ("@tenant", tenant.TenantId), ("@device", mobileDeviceId), ("@now", now));

        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_mobile_native_devices
(tenant_id,mobile_device_id,platform,app_version,device_name,is_active,created_at_utc,updated_at_utc,last_seen_at_utc)
VALUES(@tenant,@device,@platform,@version,@name,1,@now,@now,@now)
ON DUPLICATE KEY UPDATE
  platform=@platform,
  app_version=@version,
  device_name=CASE WHEN @name<>'' THEN @name ELSE device_name END,
  is_active=1,
  updated_at_utc=@now,
  last_seen_at_utc=@now;
""", cancellationToken,
            ("@tenant", tenant.TenantId), ("@device", mobileDeviceId), ("@platform", platform),
            ("@version", appVersion), ("@name", deviceName), ("@now", now));

        await InsertAccessTokenAsync(connection, transaction, tenant.TenantId, mobileDeviceId,
            accessToken, now, accessExpiry, cancellationToken);
        await InsertRefreshTokenAsync(connection, transaction, tenant.TenantId, mobileDeviceId,
            familyId, refreshToken, now, refreshExpiry, cancellationToken);

        var pairedByDisplayName = issuedByDeviceId;
        if (issuedByDeviceId.StartsWith("mobile:", StringComparison.OrdinalIgnoreCase))
        {
            var issuerMobileId = issuedByDeviceId["mobile:".Length..];
            await using var mobileNameCommand = CreateCommand(connection, transaction, """
SELECT device_name FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND mobile_device_id=@device LIMIT 1;
""", ("@tenant", tenant.TenantId), ("@device", issuerMobileId));
            var value = await mobileNameCommand.ExecuteScalarAsync(cancellationToken);
            var display = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(display)) pairedByDisplayName = display;
        }
        else
        {
            await using var nameCommand = CreateCommand(connection, transaction, """
SELECT device_name FROM ct_devices
WHERE tenant_id=@tenant AND device_id=@device LIMIT 1;
""", ("@tenant", tenant.TenantId), ("@device", issuedByDeviceId));
            var value = await nameCommand.ExecuteScalarAsync(cancellationToken);
            var display = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrWhiteSpace(display)) pairedByDisplayName = display;
        }

        await AddAuditAsync(connection, transaction, tenant.TenantId, "mobile", mobileDeviceId,
            "QrConsumedNative", null, null,
            $"Issuer={issuedByDeviceId}; Platform={platform}; App={appVersion}", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CariNativeIssuedSession(tenant, mobileDeviceId, accessToken, refreshToken,
            accessExpiry, refreshExpiry, issuedByDeviceId, pairedByDisplayName);
    }

    public async Task<CariNativeAuthCheck> ValidateAccessTokenAsync(
        string rawAccessToken, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        rawAccessToken = (rawAccessToken ?? string.Empty).Trim();
        if (rawAccessToken.Length == 0) return new(null, "TOKEN_INVALID");

        var hash = Hash(rawAccessToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT a.tenant_id,t.company_name,t.license_id,a.mobile_device_id,a.expires_at_utc,
       a.revoked_at_utc,d.is_active
FROM ct_mobile_access_tokens a
INNER JOIN ct_tenants t ON t.tenant_id=a.tenant_id
INNER JOIN ct_mobile_native_devices d
  ON d.tenant_id=a.tenant_id AND d.mobile_device_id=a.mobile_device_id
WHERE a.token_hash=@hash AND t.is_active=1 LIMIT 1;
""", ("@hash", hash));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new(null, "TOKEN_INVALID");

        var tenantId = reader.GetString(0);
        var companyName = reader.GetString(1);
        var licenseId = reader.GetInt32(2);
        var mobileDeviceId = reader.GetString(3);
        var expiresAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
        var revoked = !reader.IsDBNull(5);
        var deviceActive = reader.GetBoolean(6);
        await reader.DisposeAsync();

        if (!deviceActive) return new(null, "DEVICE_UNAUTHORIZED");
        if (revoked) return new(null, "TOKEN_INVALID");
        if (expiresAt <= DateTime.UtcNow) return new(null, "TOKEN_EXPIRED");

        var entitlement = await _entitlementValidator.ValidateAsync(licenseId, cancellationToken);
        if (!entitlement.IsActive) return new(null, "DEVICE_UNAUTHORIZED");

        var now = DateTime.UtcNow;
        await ExecuteAsync(connection, null, """
UPDATE ct_mobile_access_tokens SET last_seen_at_utc=@now WHERE token_hash=@hash;
UPDATE ct_mobile_native_devices SET last_seen_at_utc=@now,updated_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", cancellationToken, ("@now", now), ("@hash", hash), ("@tenant", tenantId), ("@device", mobileDeviceId));

        return new(new CariNativeAccessContext(tenantId, companyName, licenseId,
            mobileDeviceId, hash, expiresAt), null);
    }

    public async Task<CariNativeRefreshCheck> RefreshAsync(
        string rawRefreshToken, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        rawRefreshToken = (rawRefreshToken ?? string.Empty).Trim();
        if (rawRefreshToken.Length == 0) return new(null, "REFRESH_REVOKED");

        var now = DateTime.UtcNow;
        var oldHash = Hash(rawRefreshToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        string tenantId = string.Empty;
        string mobileDeviceId = string.Empty;
        string familyId = string.Empty;
        int licenseId = 0;
        DateTime expiresAt = default;
        DateTime? revokedAt = null;
        string? replacedByHash = null;
        bool deviceActive = false;
        var refreshTokenFound = false;

        await using (var command = CreateCommand(connection, transaction, """
SELECT r.tenant_id,r.mobile_device_id,r.family_id,t.license_id,r.expires_at_utc,
       r.revoked_at_utc,r.replaced_by_hash,d.is_active
FROM ct_mobile_refresh_tokens r
INNER JOIN ct_tenants t ON t.tenant_id=r.tenant_id
INNER JOIN ct_mobile_native_devices d
  ON d.tenant_id=r.tenant_id AND d.mobile_device_id=r.mobile_device_id
WHERE r.token_hash=@hash LIMIT 1 FOR UPDATE;
""", ("@hash", oldHash)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                tenantId = reader.GetString(0);
                mobileDeviceId = reader.GetString(1);
                familyId = reader.GetString(2);
                licenseId = reader.GetInt32(3);
                expiresAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc);
                revokedAt = reader.IsDBNull(5) ? null : DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc);
                replacedByHash = reader.IsDBNull(6) ? null : reader.GetString(6);
                deviceActive = reader.GetBoolean(7);
                refreshTokenFound = true;
            }
        }

        // The reader must be disposed before rolling the transaction back.
        // MySqlConnector rejects rollback while a DataReader is still active.
        if (!refreshTokenFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, "REFRESH_REVOKED");
        }

        if (!deviceActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, "DEVICE_UNAUTHORIZED");
        }

        if (revokedAt.HasValue)
        {
            if (!string.IsNullOrWhiteSpace(replacedByHash))
            {
                // Rotation replay: bu token ailesini ve aktif access tokenları komple kapat.
                await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_refresh_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND family_id=@family;
UPDATE ct_mobile_access_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", cancellationToken, ("@now", now), ("@tenant", tenantId), ("@family", familyId), ("@device", mobileDeviceId));
                await AddAuditAsync(connection, transaction, tenantId, "mobile", mobileDeviceId,
                    "RefreshReplayDetected", null, null, null, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            return new(null, "REFRESH_REVOKED");
        }

        if (expiresAt <= now)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE ct_mobile_refresh_tokens SET revoked_at_utc=@now WHERE token_hash=@hash AND revoked_at_utc IS NULL;",
                cancellationToken, ("@now", now), ("@hash", oldHash));
            await transaction.CommitAsync(cancellationToken);
            return new(null, "REFRESH_EXPIRED");
        }

        var entitlement = await _entitlementValidator.ValidateAsync(licenseId, cancellationToken);
        if (!entitlement.IsActive)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(null, "DEVICE_UNAUTHORIZED");
        }

        var newAccess = CreateToken(48);
        var newRefresh = CreateToken(64);
        var newAccessExpiry = now.Add(AccessLifetime);
        var newRefreshExpiry = now.Add(RefreshLifetime);
        var newRefreshHash = Hash(newRefresh);

        // Eski access tokenlar artık gerekli değil; sadece yeni access aktif kalsın.
        await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_access_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device AND revoked_at_utc IS NULL;
UPDATE ct_mobile_refresh_tokens
SET revoked_at_utc=@now,last_used_at_utc=@now,replaced_by_hash=@replacement
WHERE token_hash=@hash AND revoked_at_utc IS NULL;
""", cancellationToken,
            ("@now", now), ("@tenant", tenantId), ("@device", mobileDeviceId),
            ("@replacement", newRefreshHash), ("@hash", oldHash));

        await InsertAccessTokenAsync(connection, transaction, tenantId, mobileDeviceId,
            newAccess, now, newAccessExpiry, cancellationToken);
        await InsertRefreshTokenAsync(connection, transaction, tenantId, mobileDeviceId,
            familyId, newRefresh, now, newRefreshExpiry, cancellationToken);
        await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_native_devices SET last_seen_at_utc=@now,updated_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", cancellationToken, ("@now", now), ("@tenant", tenantId), ("@device", mobileDeviceId));
        await AddAuditAsync(connection, transaction, tenantId, "mobile", mobileDeviceId,
            "MobileTokenRefreshed", null, null, null, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(new CariNativeRefreshResult(newAccess, newRefresh, newAccessExpiry, newRefreshExpiry), null);
    }

    public async Task LogoutAsync(
        CariNativeAccessContext session, string rawRefreshToken, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        // Logout idempotent: verilen refresh bulunamasa bile bu native device'ın tüm tokenlarını kapat.
        await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_access_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device;
UPDATE ct_mobile_refresh_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device;
UPDATE ct_mobile_native_devices SET is_active=0,updated_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", cancellationToken, ("@now", now), ("@tenant", session.TenantId), ("@device", session.MobileDeviceId));

        await AddAuditAsync(connection, transaction, session.TenantId, "mobile", session.MobileDeviceId,
            "MobileLogout", null, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(string PairCode, DateTime ExpiresAtUtc)> IssuePairInviteAsync(
        CariNativeAccessContext session, int expiresInSeconds, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        expiresInSeconds = Math.Clamp(expiresInSeconds, 60, 300);
        var pairCode = CreatePairCode();
        var now = DateTime.UtcNow;
        var expires = now.AddSeconds(expiresInSeconds);
        var issuer = "mobile:" + session.MobileDeviceId;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_qr_tokens(token_hash,tenant_id,issued_by_device_id,created_at_utc,expires_at_utc)
VALUES(@hash,@tenant,@issuer,@now,@expires);
""", cancellationToken,
            ("@tenant", session.TenantId), ("@issuer", issuer),
            ("@hash", Hash(pairCode)), ("@now", now), ("@expires", expires));
        await AddAuditAsync(connection, transaction, session.TenantId, "mobile", session.MobileDeviceId,
            "MobilePairInviteIssued", null, null, $"Expires={expires:O}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (pairCode, expires);
    }

    public async Task<List<CariNativeDeviceDto>> GetNativeDevicesAsync(
        string tenantId, string currentDeviceId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<CariNativeDeviceDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT mobile_device_id,platform,app_version,device_name,is_active,created_at_utc,updated_at_utc,last_seen_at_utc
FROM ct_mobile_native_devices
WHERE tenant_id=@tenant
ORDER BY is_active DESC, COALESCE(last_seen_at_utc,created_at_utc) DESC, created_at_utc DESC;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var deviceId = reader.GetString(0);
            result.Add(new CariNativeDeviceDto
            {
                DeviceId = deviceId,
                Platform = reader.GetString(1),
                AppVersion = reader.GetString(2),
                DeviceName = reader.GetString(3),
                IsActive = reader.GetBoolean(4),
                IsCurrent = string.Equals(deviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase),
                CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                UpdatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
                LastSeenAtUtc = reader.IsDBNull(7) ? null : DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)
            });
        }
        return result;
    }

    public async Task<bool> RevokeNativeDeviceAsync(
        CariNativeAccessContext actor, string targetDeviceId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        targetDeviceId = (targetDeviceId ?? string.Empty).Trim();
        if (!Guid.TryParse(targetDeviceId, out _)) return false;
        if (string.Equals(actor.MobileDeviceId, targetDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Bu cihazı buradan kaldıramazsınız. Mevcut cihaz için Çıkış Yap kullanın.");

        var now = DateTime.UtcNow;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        await ExecuteAsync(connection, transaction, """
UPDATE ct_mobile_native_devices SET is_active=0,updated_at_utc=@now
WHERE tenant_id=@tenant AND mobile_device_id=@device AND is_active=1;
UPDATE ct_mobile_access_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device;
UPDATE ct_mobile_refresh_tokens SET revoked_at_utc=COALESCE(revoked_at_utc,@now)
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", cancellationToken, ("@now", now), ("@tenant", actor.TenantId), ("@device", targetDeviceId));

        // MySQL ExecuteNonQuery aggregates all statements, so verify the target exists before auditing success.
        await using var existsCommand = CreateCommand(connection, transaction, """
SELECT COUNT(*) FROM ct_mobile_native_devices
WHERE tenant_id=@tenant AND mobile_device_id=@device;
""", ("@tenant", actor.TenantId), ("@device", targetDeviceId));
        var exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
        if (!exists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await AddAuditAsync(connection, transaction, actor.TenantId, "mobile", actor.MobileDeviceId,
            "MobileDeviceRevoked", "mobile_device", targetDeviceId, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<List<CariNativeCompanyDto>> GetCompaniesAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var result = new List<CariNativeCompanyDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT entity_id,payload_json FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='company' AND is_deleted=0
ORDER BY created_at_utc,entity_id;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(1));
            var payload = document.RootElement;
            result.Add(new CariNativeCompanyDto
            {
                Id = reader.GetString(0),
                Name = ReadJsonString(payload, "name"),
                TaxNumber = ReadJsonString(payload, "taxNumber"),
                IsActive = ReadJsonBool(payload, "isActive", true)
            });
        }
        return result;
    }

    public async Task<List<CariNativeCustomerDto>> GetCustomersAsync(
        string tenantId, string companyId, string search, int take, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        take = Math.Clamp(take, 1, 500);
        companyId = (companyId ?? string.Empty).Trim();
        search = (search ?? string.Empty).Trim();

        // Tek firmada companyId boş/yanlış yazılmış müşteriler listeden düşmesin.
        var companies = await GetCompaniesAsync(tenantId, cancellationToken);
        var singleCompany = companies.Count <= 1;
        if (singleCompany && string.IsNullOrWhiteSpace(companyId))
            companyId = companies.FirstOrDefault()?.Id ?? string.Empty;

        var customers = new List<(string Id, JsonElement Payload, long Version, DateTime UpdatedAt)>();
        await using var connection = await OpenAsync(cancellationToken);
        // companyId: camelCase / PascalCase; boş companyId; tek-firma → tüm aktif müşteriler.
        await using (var command = CreateCommand(connection, null, """
SELECT entity_id,payload_json,version,updated_at_utc
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='customer' AND is_deleted=0
AND (
  @singleCompany=1
  OR @company=''
  OR LOWER(COALESCE(
       JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.companyId')),
       JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.CompanyId')),
       '')) = LOWER(@company)
  OR (
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.companyId')), '') IN ('', 'null')
    AND COALESCE(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.CompanyId')), '') IN ('', 'null')
  )
)
AND (@search='' OR JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.name')) LIKE CONCAT('%',@search,'%')
 OR JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.phone')) LIKE CONCAT('%',@search,'%'))
ORDER BY JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.name')) LIMIT @take;
""", ("@tenant", tenantId), ("@company", companyId), ("@singleCompany", singleCompany ? 1 : 0),
            ("@search", search), ("@take", take)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                using var document = JsonDocument.Parse(reader.GetString(1));
                customers.Add((reader.GetString(0), document.RootElement.Clone(), reader.GetInt64(2),
                    DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
            }
        }

        var balances = await GetCustomerBalanceMapAsync(connection, tenantId, cancellationToken);
        return customers.Select(x => ToCustomerDto(x.Id, x.Payload, x.Version, x.UpdatedAt,
            balances.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<CariNativeCustomerDto?> GetCustomerAsync(
        string tenantId, string customerId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        customerId = (customerId ?? string.Empty).Trim();
        if (!Guid.TryParse(customerId, out _)) return null;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT payload_json,version,updated_at_utc FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='customer' AND entity_id=@id AND is_deleted=0 LIMIT 1;
""", ("@tenant", tenantId), ("@id", customerId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        using var document = JsonDocument.Parse(reader.GetString(0));
        var payload = document.RootElement.Clone();
        var version = reader.GetInt64(1);
        var updatedAt = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
        await reader.DisposeAsync();
        var balance = (await GetCustomerBalanceMapAsync(connection, tenantId, cancellationToken))
            .GetValueOrDefault(customerId);
        return ToCustomerDto(customerId, payload, version, updatedAt, balance);
    }

    public async Task<CariNativeDashboardStatusDto> GetDashboardStatusAsync(
        string tenantId, string companyId, CancellationToken cancellationToken)
    {
        var companies = await GetCompaniesAsync(tenantId, cancellationToken);
        var company = companies.FirstOrDefault(x => string.Equals(x.Id, companyId, StringComparison.OrdinalIgnoreCase))
            ?? companies.FirstOrDefault();
        var resolvedCompanyId = company?.Id ?? string.Empty;
        var customers = await GetCustomersAsync(tenantId, resolvedCompanyId, string.Empty, 500, cancellationToken);
        var customerIds = customers.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // PC mantığı: Alacak = brüt borç kayıtları, Tahsilat = tahsilatlar, Net = Alacak − Tahsilat
        decimal totalAlacak = 0;
        decimal totalCollected = 0;

        if (customerIds.Count > 0)
        {
            await using var connection = await OpenAsync(cancellationToken);
            var balances = await GetCustomerBalanceMapAsync(connection, tenantId, cancellationToken);
            foreach (var id in customerIds)
            {
                if (!balances.TryGetValue(id, out var row)) continue;
                totalAlacak += row.Debt;
                totalCollected += row.Collection;
            }
        }

        return new CariNativeDashboardStatusDto
        {
            CompanyId = resolvedCompanyId,
            CompanyName = company?.Name ?? string.Empty,
            TotalReceivable = totalAlacak,
            TotalCollected = totalCollected,
            NetBalance = totalAlacak - totalCollected,
            CustomerCount = customers.Count,
            UpdatedAt = DateTime.UtcNow
        };
    }

    public async Task<List<CariNativeTransactionDto>> GetRecentTransactionsAsync(
        string tenantId, string companyId, int take, CancellationToken cancellationToken)
    {
        return await GetTransactionsCoreAsync(tenantId, companyId, null, take, cancellationToken);
    }

    public async Task<List<CariNativeTransactionDto>> GetCustomerTransactionsAsync(
        string tenantId, string customerId, CancellationToken cancellationToken)
    {
        return await GetTransactionsCoreAsync(tenantId, string.Empty, customerId, 1000, cancellationToken);
    }

    public async Task<CariNativeTransactionDto?> GetTransactionAsync(
        string tenantId, string entityId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        entityId = (entityId ?? string.Empty).Trim();
        if (!Guid.TryParse(entityId, out _)) return null;

        string kind;
        JsonElement payload;
        long version;
        DateTime createdAt;

        await using (var connection = await OpenAsync(cancellationToken))
        await using (var command = CreateCommand(connection, null, """
SELECT entity_type,payload_json,version,created_at_utc
FROM ct_entities
WHERE tenant_id=@tenant AND entity_id=@id AND entity_type IN ('debt','collection') AND is_deleted=0
LIMIT 1;
""", ("@tenant", tenantId), ("@id", entityId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            kind = reader.GetString(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            payload = document.RootElement.Clone();
            version = reader.GetInt64(2);
            createdAt = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
        }

        var customerId = ReadJsonString(payload, "customerId");
        var customer = await GetCustomerAsync(tenantId, customerId, cancellationToken);
        if (customer is null) return null;

        return new CariNativeTransactionDto
        {
            Kind = kind,
            Id = entityId,
            CompanyId = customer.CompanyId,
            CustomerId = customerId,
            CustomerName = customer.Name,
            Amount = ReadJsonDecimal(payload, "amount"),
            Description = ReadJsonString(payload, "description"),
            Date = ReadJsonDate(payload, "transactionDateUtc") ?? createdAt,
            CreatedAt = createdAt,
            Version = version
        };
    }

    private async Task<List<CariNativeTransactionDto>> GetTransactionsCoreAsync(
        string tenantId, string companyId, string? customerId, int take,
        CancellationToken cancellationToken, string? entityId = null)
    {
        await EnsureSchemaAsync(cancellationToken);
        take = Math.Clamp(take, 1, 1000);
        var customers = await GetCustomersAsync(tenantId, companyId, string.Empty, 500, cancellationToken);
        var customerMap = customers.ToDictionary(x => x.Id, x => x, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(customerId) && !customerMap.ContainsKey(customerId))
        {
            var single = await GetCustomerAsync(tenantId, customerId, cancellationToken);
            if (single is not null) customerMap[single.Id] = single;
        }
        if (customerMap.Count == 0) return new List<CariNativeTransactionDto>();

        var result = new List<CariNativeTransactionDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT entity_type,entity_id,payload_json,version,created_at_utc
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type IN ('debt','collection') AND is_deleted=0
AND (@entity='' OR entity_id=@entity)
ORDER BY JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.transactionDateUtc')) DESC, change_cursor DESC
LIMIT @take;
""", ("@tenant", tenantId), ("@entity", entityId ?? string.Empty), ("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            using var document = JsonDocument.Parse(reader.GetString(2));
            var payload = document.RootElement;
            var txCustomerId = ReadJsonString(payload, "customerId");
            if (!customerMap.TryGetValue(txCustomerId, out var customer)) continue;
            if (!string.IsNullOrWhiteSpace(customerId) &&
                !string.Equals(customerId, txCustomerId, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new CariNativeTransactionDto
            {
                Kind = reader.GetString(0),
                Id = reader.GetString(1),
                CompanyId = customer.CompanyId,
                CustomerId = txCustomerId,
                CustomerName = customer.Name,
                Amount = ReadJsonDecimal(payload, "amount"),
                Description = ReadJsonString(payload, "description"),
                Date = ReadJsonDate(payload, "transactionDateUtc") ??
                    DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                CreatedAt = DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc),
                Version = reader.GetInt64(3)
            });
        }
        return result;
    }

    private static CariNativeCustomerDto ToCustomerDto(
        string id, JsonElement payload, long version, DateTime updatedAt,
        (decimal Debt, decimal Collection) total)
    {
        return new CariNativeCustomerDto
        {
            Id = id,
            CompanyId = ReadJsonString(payload, "companyId"),
            Name = ReadJsonString(payload, "name"),
            Phone = ReadJsonString(payload, "phone"),
            Email = ReadJsonString(payload, "email"),
            Address = ReadJsonString(payload, "address"),
            Note = FirstNonEmpty(ReadJsonString(payload, "notes"), ReadJsonString(payload, "note")),
            NetBalance = total.Debt - total.Collection,
            UpdatedAt = updatedAt,
            Version = version
        };
    }

    private static async Task<Dictionary<string, (decimal Debt, decimal Collection)>> GetCustomerBalanceMapAsync(
        MySqlConnection connection, string tenantId, CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, (decimal Debt, decimal Collection)>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, null, """
SELECT entity_type,JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.customerId')),
SUM(CAST(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.amount')) AS DECIMAL(20,2)))
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type IN ('debt','collection') AND is_deleted=0
GROUP BY entity_type,JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.customerId'));
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var customerId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            if (customerId.Length == 0) continue;
            var amount = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
            var old = totals.GetValueOrDefault(customerId);
            totals[customerId] = reader.GetString(0) == "debt"
                ? (old.Debt + amount, old.Collection)
                : (old.Debt, old.Collection + amount);
        }
        return totals;
    }

    private static Task InsertAccessTokenAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string mobileDeviceId,
        string rawToken, DateTime now, DateTime expires, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
INSERT INTO ct_mobile_access_tokens
(token_hash,tenant_id,mobile_device_id,created_at_utc,expires_at_utc,last_seen_at_utc)
VALUES(@hash,@tenant,@device,@now,@expires,@now);
""", cancellationToken, ("@hash", Hash(rawToken)), ("@tenant", tenantId),
            ("@device", mobileDeviceId), ("@now", now), ("@expires", expires));

    private static Task InsertRefreshTokenAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string mobileDeviceId,
        string familyId, string rawToken, DateTime now, DateTime expires, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
INSERT INTO ct_mobile_refresh_tokens
(token_hash,tenant_id,mobile_device_id,family_id,created_at_utc,expires_at_utc)
VALUES(@hash,@tenant,@device,@family,@now,@expires);
""", cancellationToken, ("@hash", Hash(rawToken)), ("@tenant", tenantId),
            ("@device", mobileDeviceId), ("@family", familyId), ("@now", now), ("@expires", expires));

    private static Task AddAuditAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId,
        string actorType, string actorId, string action, string? entityType,
        string? entityId, string? detail, CancellationToken cancellationToken) =>
        ExecuteAsync(connection, transaction, """
INSERT INTO ct_audit_log
(tenant_id,actor_type,actor_id,action_name,entity_type,entity_id,detail_text,created_at_utc)
VALUES(@tenant,@actorType,@actor,@action,@type,@id,@detail,@now);
""", cancellationToken, ("@tenant", tenantId), ("@actorType", actorType), ("@actor", actorId),
            ("@action", action), ("@type", entityType), ("@id", entityId),
            ("@detail", detail), ("@now", DateTime.UtcNow));

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static MySqlCommand CreateCommand(
        MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 30;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }

    private static async Task<int> ExecuteAsync(
        MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] values)
    {
        await using var command = CreateCommand(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreatePairCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[12];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return $"NSX-{new string(chars[..4])}-{new string(chars[4..8])}-{new string(chars[8..12])}";
    }

    private static string CreateToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static string Limit(string? value, int max)
    {
        value = (value ?? string.Empty).Trim();
        return value.Length <= max ? value : value[..max];
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;

    private static string ReadJsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static decimal ReadJsonDecimal(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0m;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
        return 0m;
    }

    private static bool ReadJsonBool(JsonElement element, string property, bool fallback)
    {
        if (!element.TryGetProperty(property, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => fallback
        };
    }

    private static DateTime? ReadJsonDate(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;
    }
}
