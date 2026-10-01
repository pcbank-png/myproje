using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using NSYazilim.Web.CaritakipCloud.Models;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed class CaritakipCloudStore
{
    public const int MaxMutations = 100;
    public const int MaxPayloadBytes = 65_536;
    public const int MaxBatchPayloadBytes = 524_288;
    public const int MaxChanges = 500;
    public static readonly HashSet<string> EntityTypes = new(StringComparer.Ordinal)
    {
        "company", "customer", "debt", "collection", "user", "stockItem",
        "stockMovement", "reminder", "setting", "action", "part", "expense", "yearEnd"
    };

    private readonly string _connectionString;
    private readonly ILogger<CaritakipCloudStore> _logger;
    private readonly CaritakipEntitlementValidator _entitlementValidator;
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;
    private string? _schemaError;
    private DateTime? _schemaCheckedAtUtc;

    public CaritakipCloudStore(
        IConfiguration configuration,
        ILogger<CaritakipCloudStore> logger,
        CaritakipEntitlementValidator entitlementValidator)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("DefaultConnection yapılandırılmamış.");
        // IDs are CHAR(36) strings; automatic Guid conversion breaks GetString reads.
        _connectionString = new MySqlConnectionStringBuilder(connectionString)
        {
            GuidFormat = MySqlGuidFormat.None
        }.ConnectionString;
        _logger = logger;
        _entitlementValidator = entitlementValidator;
    }

    public object GetHealth() => new
    {
        databaseReady = _schemaReady,
        error = _schemaReady ? null : _schemaError ?? "Şema henüz denetlenmedi.",
        checkedAtUtc = _schemaCheckedAtUtc
    };

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady) return;
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady) return;
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
CREATE TABLE IF NOT EXISTS ct_tenants (
  tenant_id CHAR(36) NOT NULL PRIMARY KEY,
  license_id INT NOT NULL UNIQUE,
  company_name VARCHAR(220) NOT NULL,
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at_utc DATETIME(6) NOT NULL,
  updated_at_utc DATETIME(6) NOT NULL,
  last_seen_at_utc DATETIME(6) NULL
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_devices (
  tenant_id CHAR(36) NOT NULL,
  device_id VARCHAR(120) NOT NULL,
  machine_hash CHAR(64) NOT NULL,
  token_hash CHAR(64) NOT NULL,
  device_name VARCHAR(180) NOT NULL DEFAULT '',
  is_active TINYINT(1) NOT NULL DEFAULT 1,
  created_at_utc DATETIME(6) NOT NULL,
  updated_at_utc DATETIME(6) NOT NULL,
  last_seen_at_utc DATETIME(6) NULL,
  PRIMARY KEY (tenant_id, device_id),
  UNIQUE KEY ux_ct_device_token (token_hash),
  CONSTRAINT fk_ct_devices_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_customer_code_counters (
  tenant_id CHAR(36) NOT NULL PRIMARY KEY,
  last_number BIGINT NOT NULL,
  CONSTRAINT fk_ct_customer_code_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_primary_devices (
  tenant_id CHAR(36) NOT NULL,
  device_id VARCHAR(120) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (tenant_id, device_id),
  CONSTRAINT fk_ct_primary_device FOREIGN KEY (tenant_id, device_id)
    REFERENCES ct_devices(tenant_id, device_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_terminal_invites (
  token_hash CHAR(64) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  issued_by_device_id VARCHAR(120) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  expires_at_utc DATETIME(6) NOT NULL,
  consumed_at_utc DATETIME(6) NULL,
  consumed_by_device_id VARCHAR(120) NULL,
  KEY ix_ct_terminal_invite_expiry (expires_at_utc),
  KEY ix_ct_terminal_invite_tenant (tenant_id, expires_at_utc),
  CONSTRAINT fk_ct_terminal_invite_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_entities (
  tenant_id CHAR(36) NOT NULL,
  entity_type VARCHAR(32) NOT NULL,
  entity_id CHAR(36) NOT NULL,
  payload_json MEDIUMTEXT NULL,
  is_deleted TINYINT(1) NOT NULL DEFAULT 0,
  version BIGINT NOT NULL,
  change_cursor BIGINT NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  updated_at_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (tenant_id, entity_type, entity_id),
  KEY ix_ct_entities_cursor (tenant_id, change_cursor),
  KEY ix_ct_entities_type (tenant_id, entity_type, is_deleted),
  CONSTRAINT fk_ct_entities_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_changes (
  `cursor` BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  entity_type VARCHAR(32) NOT NULL,
  entity_id CHAR(36) NOT NULL,
  payload_json MEDIUMTEXT NULL,
  is_deleted TINYINT(1) NOT NULL,
  version BIGINT NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  updated_at_utc DATETIME(6) NOT NULL,
  KEY ix_ct_changes_tenant_cursor (tenant_id, `cursor`),
  CONSTRAINT fk_ct_changes_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mutations (
  tenant_id CHAR(36) NOT NULL,
  actor_device_id VARCHAR(140) NOT NULL,
  client_mutation_id VARCHAR(120) NOT NULL,
  entity_type VARCHAR(32) NOT NULL,
  entity_id CHAR(36) NOT NULL,
  result_status VARCHAR(24) NOT NULL,
  result_version BIGINT NOT NULL,
  result_cursor BIGINT NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  PRIMARY KEY (tenant_id, actor_device_id, client_mutation_id),
  CONSTRAINT fk_ct_mutations_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_qr_tokens (
  token_hash CHAR(64) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  issued_by_device_id VARCHAR(120) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  expires_at_utc DATETIME(6) NOT NULL,
  consumed_at_utc DATETIME(6) NULL,
  KEY ix_ct_qr_expiry (expires_at_utc),
  CONSTRAINT fk_ct_qr_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_mobile_sessions (
  session_hash CHAR(64) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  csrf_hash CHAR(64) NOT NULL,
  user_agent_hash CHAR(64) NOT NULL,
  created_at_utc DATETIME(6) NOT NULL,
  expires_at_utc DATETIME(6) NOT NULL,
  last_seen_at_utc DATETIME(6) NOT NULL,
  revoked_at_utc DATETIME(6) NULL,
  KEY ix_ct_sessions_tenant (tenant_id, expires_at_utc),
  CONSTRAINT fk_ct_sessions_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;

CREATE TABLE IF NOT EXISTS ct_audit_log (
  id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  actor_type VARCHAR(24) NOT NULL,
  actor_id VARCHAR(140) NOT NULL,
  action_name VARCHAR(80) NOT NULL,
  entity_type VARCHAR(32) NULL,
  entity_id CHAR(36) NULL,
  detail_text VARCHAR(500) NULL,
  created_at_utc DATETIME(6) NOT NULL,
  KEY ix_ct_audit_tenant (tenant_id, id),
  CONSTRAINT fk_ct_audit_tenant FOREIGN KEY (tenant_id) REFERENCES ct_tenants(tenant_id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
""";
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = CaritakipNotificationOutboxStore.SchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
            _schemaError = null;
            _schemaCheckedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _schemaReady = false;
            _schemaError = $"MySQL şeması hazırlanamadı ({ex.GetType().Name}).";
            _schemaCheckedAtUtc = DateTime.UtcNow;
            _logger.LogError(ex, "Cari Takip Cloud MySQL şeması hazırlanamadı.");
            throw;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    public async Task<(CariTenantContext Tenant, string ApiToken, bool Rotated, bool IsPrimary)> EnrollDeviceAsync(
        int licenseId,
        string companyName,
        string deviceId,
        string machineId,
        string deviceName,
        string currentApiToken,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        deviceId = RequireText(deviceId, nameof(deviceId), 120);
        machineId = RequireText(machineId, nameof(machineId), 300);
        companyName = CleanText(companyName, "NSX Cari Takip", 220);
        var machineHash = Hash(machineId);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var tenant = await GetTenantByLicenseAsync(connection, transaction, licenseId, cancellationToken);
        var tenantId = tenant?.TenantId ?? Guid.NewGuid().ToString();

        if (tenant is null)
        {
            await ExecuteAsync(connection, transaction, """
INSERT INTO ct_tenants
(tenant_id, license_id, company_name, is_active, created_at_utc, updated_at_utc, last_seen_at_utc)
VALUES (@tenant, @license, @company, 1, @now, @now, @now);
""", cancellationToken, ("@tenant", tenantId), ("@license", licenseId), ("@company", companyName), ("@now", now));
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
UPDATE ct_tenants SET company_name=@company, is_active=1, updated_at_utc=@now, last_seen_at_utc=@now
WHERE tenant_id=@tenant;
""", cancellationToken, ("@company", companyName), ("@now", now), ("@tenant", tenantId));
        }

        var existing = await GetDeviceSecretAsync(connection, transaction, tenantId, deviceId, cancellationToken);
        var currentValid = existing is not null
            && FixedHashEquals(existing.Value.TokenHash, Hash(currentApiToken))
            && FixedHashEquals(existing.Value.MachineHash, machineHash);
        var apiToken = currentValid ? currentApiToken.Trim() : CreateToken(48);

        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_devices
(tenant_id, device_id, machine_hash, token_hash, device_name, is_active, created_at_utc, updated_at_utc, last_seen_at_utc)
VALUES (@tenant, @device, @machine, @token, @name, 1, @now, @now, @now)
ON DUPLICATE KEY UPDATE machine_hash=VALUES(machine_hash), token_hash=VALUES(token_hash),
device_name=VALUES(device_name), is_active=1, updated_at_utc=VALUES(updated_at_utc), last_seen_at_utc=VALUES(last_seen_at_utc);
""", cancellationToken,
            ("@tenant", tenantId), ("@device", deviceId), ("@machine", machineHash),
            ("@token", Hash(apiToken)), ("@name", CleanText(deviceName, string.Empty, 180)), ("@now", now));
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_primary_devices(tenant_id,device_id,created_at_utc)
SELECT @tenant,@device,@now
WHERE NOT EXISTS(SELECT 1 FROM ct_primary_devices WHERE tenant_id=@tenant);
""", cancellationToken, ("@tenant", tenantId), ("@device", deviceId), ("@now", now));

        await using var primaryCommand = CreateCommand(connection, transaction, """
SELECT EXISTS(
  SELECT 1 FROM ct_primary_devices WHERE tenant_id=@tenant AND device_id=@device
);
""", ("@tenant", tenantId), ("@device", deviceId));
        var isPrimary = Convert.ToBoolean(await primaryCommand.ExecuteScalarAsync(cancellationToken));

        await AddAuditAsync(connection, transaction, tenantId, "device", deviceId,
            currentValid ? "DeviceValidated" : "DeviceEnrolled", null, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (new CariTenantContext(tenantId, companyName, licenseId), apiToken, !currentValid, isPrimary);
    }

    public async Task<CariDeviceContext?> ValidateDeviceAsync(
        string tenantId, string apiToken, string deviceId, string machineId, CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSchemaAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiToken)
                || string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(machineId)) return null;

            await using var connection = await OpenAsync(cancellationToken);
            await using var command = CreateCommand(connection, null, """
SELECT t.company_name, t.license_id, d.machine_hash, d.token_hash
FROM ct_devices d INNER JOIN ct_tenants t ON t.tenant_id=d.tenant_id
WHERE d.tenant_id=@tenant AND d.device_id=@device AND d.is_active=1 AND t.is_active=1 LIMIT 1;
""", ("@tenant", tenantId.Trim()), ("@device", deviceId.Trim()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            var company = reader.GetString(0);
            var licenseId = reader.GetInt32(1);
            var machineHash = reader.GetString(2);
            var tokenHash = reader.GetString(3);
            if (!FixedHashEquals(machineHash, Hash(machineId.Trim()))
                || !FixedHashEquals(tokenHash, Hash(apiToken.Trim()))) return null;
            await reader.DisposeAsync();

            if (!await ValidateEntitlementAsync(connection, tenantId.Trim(), licenseId, cancellationToken))
                return null;
            await ExecuteAsync(connection, null, """
UPDATE ct_devices SET last_seen_at_utc=@now WHERE tenant_id=@tenant AND device_id=@device;
UPDATE ct_tenants SET last_seen_at_utc=@now WHERE tenant_id=@tenant;
""", cancellationToken, ("@now", DateTime.UtcNow), ("@tenant", tenantId.Trim()), ("@device", deviceId.Trim()));
            return new CariDeviceContext(tenantId.Trim(), company, licenseId, deviceId.Trim(), machineHash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cari Takip cihaz doğrulaması başarısız.");
            throw;
        }
    }

    public async Task<bool> IsPrimaryDeviceAsync(
        string tenantId, string deviceId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT EXISTS(
  SELECT 1 FROM ct_primary_devices WHERE tenant_id=@tenant AND device_id=@device
);
""", ("@tenant", tenantId), ("@device", deviceId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task TransferPrimaryDeviceAsync(
        string tenantId,
        string deviceId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockTenantAsync(connection, transaction, tenantId, cancellationToken);
        await using (var deviceCommand = CreateCommand(connection, transaction, """
SELECT EXISTS(
  SELECT 1 FROM ct_devices WHERE tenant_id=@tenant AND device_id=@device AND is_active=1
);
""", ("@tenant", tenantId), ("@device", deviceId)))
        {
            if (!Convert.ToBoolean(await deviceCommand.ExecuteScalarAsync(cancellationToken)))
                throw new InvalidOperationException("Ana cihaz olarak atanacak aktif cihaz bulunamadı.");
        }

        await ExecuteAsync(connection, transaction, """
DELETE FROM ct_primary_devices WHERE tenant_id=@tenant;
INSERT INTO ct_primary_devices(tenant_id,device_id,created_at_utc)
VALUES(@tenant,@device,@now);
""", cancellationToken,
            ("@tenant", tenantId),
            ("@device", deviceId),
            ("@now", DateTime.UtcNow));
        await AddAuditAsync(
            connection,
            transaction,
            tenantId,
            "device",
            deviceId,
            "PrimaryDeviceTransferred",
            null,
            null,
            null,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RecordAuditAsync(
        string tenantId,
        string deviceId,
        string action,
        string? detail,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AddAuditAsync(
            connection,
            transaction,
            tenantId,
            "device",
            CleanText(deviceId, string.Empty, 140),
            CleanText(action, "Unknown", 80),
            null,
            null,
            CleanText(detail ?? string.Empty, string.Empty, 500),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<(string Token, DateTime ExpiresAtUtc)> IssueTerminalInviteAsync(
        string tenantId, string deviceId, int expiresInSeconds, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        expiresInSeconds = Math.Clamp(expiresInSeconds, 120, 600);
        var now = DateTime.UtcNow;
        var expires = now.AddSeconds(expiresInSeconds);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "DELETE FROM ct_terminal_invites WHERE expires_at_utc < @cutoff;",
            cancellationToken, ("@cutoff", now.AddHours(-1)));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockTenantAsync(connection, transaction, tenantId, cancellationToken);
        await ExecuteAsync(connection, transaction, """
UPDATE ct_terminal_invites SET expires_at_utc=@now
WHERE tenant_id=@tenant AND consumed_at_utc IS NULL AND expires_at_utc>@now;
""", cancellationToken, ("@now", now), ("@tenant", tenantId));
        string? token = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = RandomNumberGenerator.GetInt32(1_000_000)
                .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                await ExecuteAsync(connection, transaction, """
INSERT INTO ct_terminal_invites
(token_hash,tenant_id,issued_by_device_id,created_at_utc,expires_at_utc)
VALUES(@hash,@tenant,@device,@now,@expires);
""", cancellationToken, ("@hash", Hash(candidate)), ("@tenant", tenantId), ("@device", deviceId),
                    ("@now", now), ("@expires", expires));
                token = candidate;
                break;
            }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                // Numeric codes share a bounded namespace; never replace another invitation.
            }
        }
        if (token is null)
        {
            _logger.LogWarning("Cari Takip terminal kodu için benzersiz sayı ayrılamadı.");
            throw new InvalidOperationException("Bağlantı kodu şu anda oluşturulamadı. Biraz sonra yeniden deneyin.");
        }
        await AddAuditAsync(connection, transaction, tenantId, "device", deviceId,
            "TerminalInviteIssued", null, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (token, expires);
    }

    public async Task<(CariTenantContext Tenant, string DeviceId, string ApiToken)?> ConsumeTerminalInviteAsync(
        string inviteCode,
        string deviceId,
        string machineId,
        string deviceName,
        CancellationToken cancellationToken,
        string? enrollmentToken = null)
    {
        await EnsureSchemaAsync(cancellationToken);
        inviteCode = RequireText(inviteCode, "inviteCode", 160);
        deviceId = RequireText(deviceId, "deviceId", 120);
        machineId = RequireText(machineId, "machineId", 300);
        if (enrollmentToken is not null &&
            (enrollmentToken.Length != 64 || enrollmentToken.Any(c =>
                !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))))
            throw new ArgumentException("Terminal kayıt anahtarı geçersiz.");
        var machineHash = Hash(machineId);
        var now = DateTime.UtcNow;

        await using var connection = await OpenAsync(cancellationToken);
        string? invitedTenant;
        await using (var lookup = CreateCommand(connection, null,
            "SELECT tenant_id FROM ct_terminal_invites WHERE token_hash=@hash;", ("@hash", Hash(inviteCode))))
            invitedTenant = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        if (invitedTenant is null)
            throw new CariTerminalPairingException("invalid_code", "Bağlantı kodu bulunamadı. Ana PC'den yeni 6 haneli kod oluşturup tekrar deneyin.");
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockTenantAsync(connection, transaction, invitedTenant, cancellationToken);
        CariTenantContext? tenant = null;
        string issuedByDeviceId = string.Empty;
        DateTime? consumedAtUtc = null;
        string? consumedByDeviceId = null;
        DateTime expiresAtUtc = default;
        var tenantActive = false;
        await using (var command = CreateCommand(connection, transaction, """
SELECT t.tenant_id,t.company_name,t.license_id,i.issued_by_device_id,
       i.consumed_at_utc,i.expires_at_utc,t.is_active,i.consumed_by_device_id
FROM ct_terminal_invites i
INNER JOIN ct_tenants t ON t.tenant_id=i.tenant_id
WHERE i.token_hash=@hash AND i.tenant_id=@tenant
FOR UPDATE;
""", ("@hash", Hash(inviteCode)), ("@tenant", invitedTenant)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                tenant = new CariTenantContext(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
                issuedByDeviceId = reader.GetString(3);
                consumedAtUtc = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                expiresAtUtc = reader.GetDateTime(5);
                tenantActive = reader.GetBoolean(6);
                consumedByDeviceId = reader.IsDBNull(7) ? null : reader.GetString(7);
            }
        }
        if (tenant is null)
            throw new CariTerminalPairingException("invalid_code", "Bağlantı kodu bulunamadı. Ana PC'den yeni 6 haneli kod oluşturup tekrar deneyin.");
        if (consumedAtUtc.HasValue)
        {
            // A lost HTTP response may be retried only with the secret saved by the original terminal.
            var registered = consumedByDeviceId is null ? null :
                await GetDeviceSecretAsync(connection, transaction, tenant.TenantId, consumedByDeviceId, cancellationToken);
            if (enrollmentToken is null || registered is null ||
                !FixedHashEquals(registered.Value.TokenHash, Hash(enrollmentToken)) ||
                !FixedHashEquals(registered.Value.MachineHash, machineHash))
                throw new CariTerminalPairingException("used_code", "Bu bağlantı kodu daha önce kullanılmış. Eşleşmiş terminalde Bağlantıyı yenile seçeneğini kullanın veya ana PC'den YENİ kod oluşturun.");
            if (expiresAtUtc <= DateTime.UtcNow)
                throw new CariTerminalPairingException("expired_code", "Yarım kalan bağlantının kod süresi dolmuş. Ana PC'den yeni kod oluşturun.");
            await using var activeDevice = CreateCommand(connection, transaction,
                "SELECT is_active FROM ct_devices WHERE tenant_id=@tenant AND device_id=@device;",
                ("@tenant", tenant.TenantId), ("@device", consumedByDeviceId));
            if (!tenantActive || !Convert.ToBoolean(await activeDevice.ExecuteScalarAsync(cancellationToken)) ||
                !await ValidateEntitlementAsync(connection, tenant.TenantId, tenant.LicenseId, cancellationToken, transaction))
            {
                await transaction.CommitAsync(cancellationToken);
                throw new CariTerminalPairingException("license_invalid", "Terminal veya ana hesabın bulut lisansı aktif değil.");
            }
            await transaction.CommitAsync(cancellationToken);
            return (tenant, consumedByDeviceId!, enrollmentToken);
        }
        if (expiresAtUtc <= DateTime.UtcNow)
            throw new CariTerminalPairingException("expired_code", "Bağlantı kodunun süresi dolmuş veya yerine yeni kod oluşturulmuş. Ana PC'deki son kodu kullanın.");
        if (!tenantActive ||
            !await ValidateEntitlementAsync(connection, tenant.TenantId, tenant.LicenseId, cancellationToken, transaction))
        {
            await transaction.CommitAsync(cancellationToken);
            throw new CariTerminalPairingException("license_invalid", "Ana hesabın bulut lisansı aktif değil veya doğrulanamadı. Ana PC'deki lisans ve bulut durumunu kontrol edin.");
        }
        if (string.Equals(deviceId, issuedByDeviceId, StringComparison.Ordinal))
            throw new ArgumentException("Terminal deviceId birincil cihazdan farklı olmalıdır.");

        var existingDevices = new List<(string Id, string MachineHash, bool IsPrimary)>();
        await using (var duplicate = CreateCommand(connection, transaction, """
SELECT d.device_id,d.machine_hash,(p.device_id IS NOT NULL)
FROM ct_devices d LEFT JOIN ct_primary_devices p
ON p.tenant_id=d.tenant_id AND p.device_id=d.device_id
WHERE d.tenant_id=@tenant AND (d.device_id=@device OR d.machine_hash=@machine)
FOR UPDATE;
""", ("@tenant", tenant.TenantId), ("@device", deviceId), ("@machine", machineHash)))
        await using (var reader = await duplicate.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                existingDevices.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
        }
        if (existingDevices.Any(x => x.IsPrimary))
            throw new ArgumentException("Ana PC bu kodla terminale dönüştürülemez. Kodu diğer bilgisayarda girin.");
        if (existingDevices.Count > 1 ||
            existingDevices.Any(x => !FixedHashEquals(x.MachineHash, machineHash)))
            throw new ArgumentException("Cihaz kaydı bu bilgisayarla eşleşmiyor. Başka PC'nin ayarlarını veya veritabanını kopyalamayın.");
        if (existingDevices.Count == 1)
            deviceId = existingDevices[0].Id;

        var consumed = await ExecuteAsync(connection, transaction, """
UPDATE ct_terminal_invites SET consumed_at_utc=@now,consumed_by_device_id=@device
WHERE token_hash=@hash AND consumed_at_utc IS NULL AND expires_at_utc>=@now;
""", cancellationToken, ("@now", now), ("@device", deviceId), ("@hash", Hash(inviteCode)));
        if (consumed != 1)
            throw new CariTerminalPairingException("used_code", "Bağlantı kodu artık kullanılamıyor. Ana PC'den yeni kod oluşturun.");

        var apiToken = enrollmentToken ?? CreateToken(48);
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_devices
(tenant_id,device_id,machine_hash,token_hash,device_name,is_active,created_at_utc,updated_at_utc,last_seen_at_utc)
VALUES(@tenant,@device,@machine,@token,@name,1,@now,@now,@now)
ON DUPLICATE KEY UPDATE token_hash=@token,device_name=@name,is_active=1,
updated_at_utc=@now,last_seen_at_utc=@now;
""", cancellationToken, ("@tenant", tenant.TenantId), ("@device", deviceId),
            ("@machine", machineHash), ("@token", Hash(apiToken)),
            ("@name", CleanText(deviceName, string.Empty, 180)), ("@now", now));
        await AddAuditAsync(connection, transaction, tenant.TenantId, "terminal", deviceId,
            "TerminalInviteConsumed", null, null, $"Invite issuer: {issuedByDeviceId}", cancellationToken);
        await AddAuditAsync(connection, transaction, tenant.TenantId, "terminal", deviceId,
            existingDevices.Count == 0 ? "TerminalEnrolled" : "TerminalRepaired",
            null, null, $"Invite issuer: {issuedByDeviceId}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (tenant, deviceId, apiToken);
    }

    public async Task<CariSyncPushResponse> ApplyMutationsAsync(
        string tenantId, string actorDeviceId, IReadOnlyList<CariMutationRequest> mutations, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        ValidateMutations(mutations);
        actorDeviceId = RequireText(actorDeviceId, nameof(actorDeviceId), 140);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await LockTenantAsync(connection, transaction, tenantId, cancellationToken);
        var response = new CariSyncPushResponse();
        foreach (var mutation in mutations)
        {
            var result = await ApplyMutationAsync(connection, transaction, tenantId, actorDeviceId, mutation, cancellationToken);
            response.Results.Add(result);
            response.Cursor = Math.Max(response.Cursor, result.Cursor);
        }
        if (response.Cursor == 0)
            response.Cursor = await GetCursorAsync(connection, transaction, tenantId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private async Task<CariMutationResult> ApplyMutationAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string actorDeviceId,
        CariMutationRequest mutation, CancellationToken cancellationToken)
    {
        var mutationId = RequireText(mutation.ClientMutationId, "clientMutationId", 120);
        var type = ValidateEntityType(mutation.EntityType);
        var entityId = string.IsNullOrWhiteSpace(mutation.EntityId)
            ? Guid.NewGuid().ToString()
            : RequireGuid(mutation.EntityId, "entityId");
        var delete = string.Equals(mutation.Operation, "delete", StringComparison.OrdinalIgnoreCase);
        if (!delete && !string.Equals(mutation.Operation, "upsert", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("operation yalnızca upsert veya delete olabilir.");

        var prior = await GetMutationAsync(connection, transaction, tenantId, actorDeviceId, mutationId, cancellationToken);
        if (prior is not null)
        {
            if (prior.Status == "conflict" || prior.EntityType == "customer")
                prior.Current = await GetEntityAsync(connection, transaction, tenantId,
                    prior.EntityType, prior.EntityId, false, cancellationToken);
            return prior;
        }

        var current = await GetEntityAsync(connection, transaction, tenantId, type, entityId, true, cancellationToken);
        var versionMatches = !mutation.ExpectedVersion.HasValue
            || (current is null && mutation.ExpectedVersion.Value == 0)
            || (current is not null && current.Version == mutation.ExpectedVersion.Value);
        if (!versionMatches)
        {
            var conflict = new CariMutationResult
            {
                ClientMutationId = mutationId, EntityType = type, EntityId = entityId,
                Status = "conflict", Version = current?.Version ?? 0, Cursor = current?.Cursor ?? 0, Current = current
            };
            await SaveMutationResultAsync(connection, transaction, tenantId, actorDeviceId, conflict, cancellationToken);
            return conflict;
        }

        var now = DateTime.UtcNow;
        var version = (current?.Version ?? 0) + 1;
        var createdAt = current?.CreatedAtUtc ?? now;
        string? payload = null;
        if (delete)
        {
            // Tombstone: mevcut payload'ı koru.
            // NULL yazmak bazı masaüstü senkronlarında kaydı "Pasif"e düşürebiliyor.
            if (current?.Payload is { ValueKind: JsonValueKind.Object } existing)
                payload = existing.GetRawText();
        }
        else
        {
            if (mutation.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new ArgumentException("upsert işlemi payload gerektirir.");
            var entityPayload = type == "customer"
                ? await EnsureCustomerCodeAsync(connection, transaction, tenantId, mutation.Payload, current?.Payload, cancellationToken)
                : mutation.Payload;
            payload = entityPayload.GetRawText();
            ValidatePayload(payload);
        }

        var cursor = await InsertChangeAsync(connection, transaction, tenantId, type, entityId,
            payload, delete, version, createdAt, now, cancellationToken);
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_entities
(tenant_id, entity_type, entity_id, payload_json, is_deleted, version, change_cursor, created_at_utc, updated_at_utc)
VALUES (@tenant,@type,@id,@payload,@deleted,@version,@cursor,@created,@updated)
ON DUPLICATE KEY UPDATE
payload_json=COALESCE(VALUES(payload_json), payload_json),
is_deleted=VALUES(is_deleted),
version=VALUES(version), change_cursor=VALUES(change_cursor), updated_at_utc=VALUES(updated_at_utc);
""", cancellationToken,
            ("@tenant", tenantId), ("@type", type), ("@id", entityId), ("@payload", payload),
            ("@deleted", delete ? 1 : 0), ("@version", version), ("@cursor", cursor),
            ("@created", createdAt), ("@updated", now));

        var result = new CariMutationResult
        {
            ClientMutationId = mutationId, EntityType = type, EntityId = entityId,
            Status = "applied", Version = version, Cursor = cursor,
            Current = !delete && type == "customer" && string.IsNullOrWhiteSpace(ReadJsonString(mutation.Payload, "barcodeNo"))
                ? new CariEntityDto
                {
                    EntityType = type, EntityId = entityId, Payload = JsonSerializer.Deserialize<JsonElement>(payload!),
                    Version = version, Cursor = cursor, CreatedAtUtc = createdAt, UpdatedAtUtc = now
                }
                : null
        };
        if (!delete && version == 1 && (type == "debt" || type == "collection"))
        {
            var source = JsonSerializer.Deserialize<JsonElement>(payload!);
            var customerId = ReadJsonString(source, "customerId") ?? "";
            var customer = Guid.TryParse(customerId,out _) ? await GetEntityAsync(connection,transaction,tenantId,"customer",customerId,false,cancellationToken) : null;
            var customerName = customer?.Payload is JsonElement customerPayload ? ReadJsonString(customerPayload,"name") ?? "Müşteri" : "Müşteri";
            var amount = 0m;
            if (source.TryGetProperty("amount",out var value)) {
                if (value.ValueKind == JsonValueKind.Number) value.TryGetDecimal(out amount);
                else if (value.ValueKind == JsonValueKind.String)
                    decimal.TryParse(value.GetString(),System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,out amount);
            }
            var description = ReadJsonString(source,"description") ?? "";
            var body = $"{customerName} · ₺{amount.ToString("N2",System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))}";
            if (!string.IsNullOrWhiteSpace(description)) body += " — " + description;
            var exclude = actorDeviceId.StartsWith("native:",StringComparison.OrdinalIgnoreCase) ? actorDeviceId[7..] : null;
            var draft = new NotificationDraft(tenantId,exclude,type == "collection" ? "collections" : "debts",
                type == "collection" ? "Yeni tahsilat" : "Yeni borç",body,
                new Dictionary<string,string> { ["type"]="transaction",["entityType"]=type,["entityId"]=entityId,["customerId"]=customerId });
            await CaritakipNotificationOutboxStore.EnqueueAsync(connection,transaction,tenantId,"tx:"+entityId,"inbox",draft,cancellationToken);
        }
        await SaveMutationResultAsync(connection, transaction, tenantId, actorDeviceId, result, cancellationToken);
        await AddAuditAsync(connection, transaction, tenantId,
            actorDeviceId.StartsWith("mobile:", StringComparison.Ordinal) ? "mobile"
                : actorDeviceId.StartsWith("system:", StringComparison.Ordinal) ? "system" : "device",
            actorDeviceId, delete ? "EntityDeleted" : "EntityUpserted", type, entityId, null, cancellationToken);
        return result;
    }

    private static async Task<JsonElement> EnsureCustomerCodeAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId,
        JsonElement payload, JsonElement? current, CancellationToken cancellationToken)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            payload.TryGetProperty("barcodeNo", out var barcode) &&
            barcode.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            throw new ArgumentException("Müşteri payload nesne, cari kod ise metin olmalıdır.");
        var code = ReadJsonString(payload, "barcodeNo");
        if (string.IsNullOrWhiteSpace(code) && current is { ValueKind: JsonValueKind.Object } previous)
            code = ReadJsonString(previous, "barcodeNo");
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_customer_code_counters(tenant_id,last_number)
SELECT @tenant,COALESCE(MAX(CAST(SUBSTRING(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.barcodeNo')),6) AS UNSIGNED)),0)
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='customer'
AND JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.barcodeNo')) REGEXP '^NSXCR[0-9]{1,18}$'
ON DUPLICATE KEY UPDATE tenant_id=@tenant;
""", cancellationToken, ("@tenant", tenantId));
        if (string.IsNullOrWhiteSpace(code))
        {
            await ExecuteAsync(connection, transaction, """
UPDATE ct_customer_code_counters SET last_number=last_number+1 WHERE tenant_id=@tenant;
""", cancellationToken, ("@tenant", tenantId));
            await using var next = CreateCommand(connection, transaction,
                "SELECT last_number FROM ct_customer_code_counters WHERE tenant_id=@tenant;", ("@tenant", tenantId));
            var number = Convert.ToInt64(await next.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Cari kod sayacı okunamadı."));
            code = "NSXCR" + number.ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (code.StartsWith("NSXCR", StringComparison.OrdinalIgnoreCase) &&
                 long.TryParse(code.AsSpan(5), System.Globalization.NumberStyles.None,
                     System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            await ExecuteAsync(connection, transaction, """
UPDATE ct_customer_code_counters SET last_number=GREATEST(last_number,@number) WHERE tenant_id=@tenant;
""", cancellationToken, ("@tenant", tenantId), ("@number", number));
        }
        var fields = payload.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        fields["barcodeNo"] = JsonSerializer.SerializeToElement(code);
        return JsonSerializer.SerializeToElement(fields);
    }

    private static async Task LockTenantAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT tenant_id FROM ct_tenants WHERE tenant_id=@tenant FOR UPDATE;", ("@tenant", tenantId));
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
            throw new ArgumentException("Bulut hesabı bulunamadı.");
    }

    private async Task BackfillCustomerCodesAsync(string tenantId, CancellationToken cancellationToken)
    {
        var pending = new List<CariMutationRequest>();
        await using (var connection = await OpenAsync(cancellationToken))
        await using (var command = CreateCommand(connection, null, """
SELECT entity_id,payload_json,version FROM ct_entities
WHERE tenant_id=@tenant AND entity_type='customer' AND is_deleted=0
AND (JSON_EXTRACT(payload_json,'$.barcodeNo') IS NULL
 OR JSON_TYPE(JSON_EXTRACT(payload_json,'$.barcodeNo'))='NULL'
 OR TRIM(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.barcodeNo')))='')
ORDER BY change_cursor LIMIT 100;
""", ("@tenant", tenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                pending.Add(new CariMutationRequest
                {
                    ClientMutationId = Guid.NewGuid().ToString(), EntityType = "customer",
                    EntityId = reader.GetString(0), ExpectedVersion = reader.GetInt64(2),
                    Payload = JsonSerializer.Deserialize<JsonElement>(reader.GetString(1))
                });
        }
        if (pending.Count > 0)
            await ApplyMutationsAsync(tenantId, "system:customer-code", pending, cancellationToken);
    }

    public async Task<CariChangesResponse> GetChangesAsync(
        string tenantId, long after, int take, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (after < 0) throw new ArgumentException("after negatif olamaz.");
        await BackfillCustomerCodesAsync(tenantId, cancellationToken);
        take = Math.Clamp(take, 1, MaxChanges);
        var changes = new List<CariEntityDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT `cursor`, entity_type, entity_id, payload_json, is_deleted, version, created_at_utc, updated_at_utc
FROM ct_changes WHERE tenant_id=@tenant AND `cursor`>@after ORDER BY `cursor` LIMIT @take;
""", ("@tenant", tenantId), ("@after", after), ("@take", take + 1));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            changes.Add(ReadEntity(reader, changeRow: true));
        var hasMore = changes.Count > take;
        if (hasMore) changes.RemoveAt(changes.Count - 1);
        var cursor = changes.Count == 0 ? after : changes[^1].Cursor;
        return new CariChangesResponse { After = after, Cursor = cursor, HasMore = hasMore, Changes = changes };
    }

    public async Task<(long Cursor, List<CariEntityDto> Entities)> GetBootstrapAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await BackfillCustomerCodesAsync(tenantId, cancellationToken);
        var entities = new List<CariEntityDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var command = CreateCommand(connection, transaction, """
SELECT entity_type, entity_id, payload_json, is_deleted, version, change_cursor, created_at_utc, updated_at_utc
FROM ct_entities WHERE tenant_id=@tenant AND is_deleted=0 ORDER BY change_cursor LIMIT 5001;
""", ("@tenant", tenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) entities.Add(ReadEntity(reader, false));
        }
        if (entities.Count > 5000) throw new InvalidOperationException("Bootstrap 5000 kayıt sınırını aşıyor; cursor senkronizasyonunu kullanın.");
        var cursor = await GetCursorAsync(connection, transaction, tenantId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (cursor, entities);
    }

    public async Task<(string Token, DateTime ExpiresAtUtc)> IssueQrAsync(
        string tenantId, string deviceId, int expiresInSeconds, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        expiresInSeconds = Math.Clamp(expiresInSeconds, 60, 300);
        var token = CreateToken(40);
        var now = DateTime.UtcNow;
        var expires = now.AddSeconds(expiresInSeconds);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction,
            "DELETE FROM ct_qr_tokens WHERE expires_at_utc < @cutoff OR (tenant_id=@tenant AND consumed_at_utc IS NULL);",
            cancellationToken, ("@cutoff", now.AddHours(-1)), ("@tenant", tenantId));
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_qr_tokens(token_hash,tenant_id,issued_by_device_id,created_at_utc,expires_at_utc)
VALUES(@hash,@tenant,@device,@now,@expires);
""", cancellationToken, ("@hash", Hash(token)), ("@tenant", tenantId), ("@device", deviceId),
            ("@now", now), ("@expires", expires));
        await AddAuditAsync(connection, transaction, tenantId, "device", deviceId,
            "QrIssued", null, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (token, expires);
    }

    public async Task<(CariTenantContext Tenant, string SessionToken, string CsrfToken, DateTime ExpiresAtUtc)?>
        ConsumeQrAsync(string rawToken, string userAgent, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var now = DateTime.UtcNow;
        var tokenHash = Hash(rawToken.Trim());
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        CariTenantContext? tenant = null;
        await using (var command = CreateCommand(connection, transaction, """
SELECT t.tenant_id,t.company_name,t.license_id
FROM ct_qr_tokens q
INNER JOIN ct_tenants t ON t.tenant_id=q.tenant_id
WHERE q.token_hash=@hash AND q.consumed_at_utc IS NULL AND q.expires_at_utc>=@now
AND t.is_active=1 LIMIT 1 FOR UPDATE;
""", ("@hash", tokenHash), ("@now", now)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
                tenant = new CariTenantContext(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
        }
        if (tenant is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (!await ValidateEntitlementAsync(connection, tenant.TenantId, tenant.LicenseId, cancellationToken, transaction))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var changed = await ExecuteAsync(connection, transaction, """
UPDATE ct_qr_tokens SET consumed_at_utc=@now
WHERE token_hash=@hash AND consumed_at_utc IS NULL AND expires_at_utc>=@now;
""", cancellationToken, ("@now", now), ("@hash", tokenHash));
        if (changed != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var sessionToken = CreateToken(48);
        var csrfToken = CreateToken(32);
        var expires = now.AddYears(10);
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_mobile_sessions
(session_hash,tenant_id,csrf_hash,user_agent_hash,created_at_utc,expires_at_utc,last_seen_at_utc)
VALUES(@session,@tenant,@csrf,@ua,@now,@expires,@now);
""", cancellationToken, ("@session", Hash(sessionToken)), ("@tenant", tenant.TenantId),
            ("@csrf", Hash(csrfToken)), ("@ua", Hash(userAgent ?? string.Empty)), ("@now", now), ("@expires", expires));
        await AddAuditAsync(connection, transaction, tenant.TenantId, "mobile", Hash(sessionToken)[..16],
            "QrConsumed", null, null, null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (tenant, sessionToken, csrfToken, expires);
    }

    public async Task<CariMobileSessionContext?> ValidateMobileSessionAsync(
        string sessionToken, string userAgent, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        var hash = Hash(sessionToken.Trim());
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT s.tenant_id,t.company_name,s.session_hash,s.csrf_hash,s.expires_at_utc,s.user_agent_hash,t.license_id
FROM ct_mobile_sessions s INNER JOIN ct_tenants t ON t.tenant_id=s.tenant_id
WHERE s.session_hash=@hash AND s.revoked_at_utc IS NULL AND s.expires_at_utc>@now
AND t.is_active=1 LIMIT 1;
""", ("@hash", hash), ("@now", DateTime.UtcNow));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var session = new CariMobileSessionContext(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
        var licenseId = reader.GetInt32(6);
        await reader.DisposeAsync();
        if (!await ValidateEntitlementAsync(connection, session.TenantId, licenseId, cancellationToken))
            return null;
        var refreshedExpiry = DateTime.UtcNow.AddYears(10);
        await ExecuteAsync(connection, null, """
UPDATE ct_mobile_sessions
SET last_seen_at_utc=@now, expires_at_utc=@expires
WHERE session_hash=@hash;
""", cancellationToken, ("@now", DateTime.UtcNow), ("@expires", refreshedExpiry), ("@hash", hash));
        return session with { ExpiresAtUtc = refreshedExpiry };
    }

    public static bool ValidateCsrf(CariMobileSessionContext session, string token) =>
        !string.IsNullOrWhiteSpace(token) && FixedHashEquals(session.CsrfHash, Hash(token.Trim()));

    public async Task<(CariMobileSessionContext Session, string Token)> RestoreMobileCsrfAsync(
        CariMobileSessionContext session, string sessionToken, CancellationToken cancellationToken)
    {
        var csrf = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(sessionToken), Encoding.UTF8.GetBytes("NSX.CariTakip.Csrf.v1")));
        var hash = Hash(csrf);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, """
UPDATE ct_mobile_sessions SET csrf_hash=@csrf WHERE session_hash=@session AND revoked_at_utc IS NULL;
""", cancellationToken, ("@csrf", hash), ("@session", session.SessionHash));
        return (session with { CsrfHash = hash }, csrf);
    }

    public async Task<List<CariEntityDto>> GetCompaniesAsync(string tenantId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT entity_type,entity_id,payload_json,is_deleted,version,change_cursor,created_at_utc,updated_at_utc
FROM ct_entities WHERE tenant_id=@tenant AND entity_type='company' AND is_deleted=0
ORDER BY created_at_utc,entity_id;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var companies = new List<CariEntityDto>();
        while (await reader.ReadAsync(cancellationToken))
            companies.Add(ReadEntity(reader, false));
        return companies;
    }

    public async Task<JsonElement> BuildMobileCustomerPayloadAsync(
        string tenantId, string? customerId, CariCustomerWriteRequest request, CancellationToken cancellationToken)
    {
        var companies = await GetCompaniesAsync(tenantId, cancellationToken);
        var payload = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (customerId is not null)
        {
            customerId = RequireGuid(customerId, "customerId");
            if (!request.ExpectedVersion.HasValue)
                throw new ArgumentException("Müşteriyi düzenlemek için kayıt sürümü zorunludur.");
            await using var connection = await OpenAsync(cancellationToken);
            var current = await GetEntityAsync(connection, null, tenantId, "customer", customerId, false, cancellationToken);
            if (current is null || current.IsDeleted)
                throw new ArgumentException("Müşteri bulunamadı veya silinmiş.");
            if (current.Payload is { ValueKind: JsonValueKind.Object } existing)
                payload = existing.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        }

        var companyId = request.CompanyId;
        if (string.IsNullOrWhiteSpace(companyId) &&
            payload.TryGetValue("companyId", out var existingCompany) && existingCompany.ValueKind == JsonValueKind.String)
            companyId = existingCompany.GetString();
        if (string.IsNullOrWhiteSpace(companyId))
        {
            if (companies.Count != 1)
                throw new ArgumentException(companies.Count == 0
                    ? "Önce masaüstündeki firma kaydını bulutla eşitleyin."
                    : "Müşterinin bağlı olacağı firmayı seçin.");
            companyId = companies[0].EntityId;
        }
        companyId = RequireGuid(companyId, "companyId");
        if (!companies.Any(x => string.Equals(x.EntityId, companyId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Seçilen firma bulunamadı veya bu hesaba ait değil.");

        payload["companyId"] = JsonSerializer.SerializeToElement(companyId);
        payload["name"] = JsonSerializer.SerializeToElement(request.Name.Trim());
        payload["phone"] = JsonSerializer.SerializeToElement(CleanText(request.Phone, "", 60));
        payload["email"] = JsonSerializer.SerializeToElement(CleanText(request.Email, "", 180));
        payload["address"] = JsonSerializer.SerializeToElement(CleanText(request.Address, "", 500));
        payload["notes"] = JsonSerializer.SerializeToElement(CleanText(request.Notes, "", 2000));
        return JsonSerializer.SerializeToElement(payload);
    }

    public async Task<List<object>> GetCustomersWithBalancesAsync(
        string tenantId, string search, int take, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await BackfillCustomerCodesAsync(tenantId, cancellationToken);
        take = Math.Clamp(take, 1, 500);
        search = (search ?? string.Empty).Trim();
        var customers = new List<(CariEntityDto Entity, string Name, string Phone)>();
        var totals = new Dictionary<string, (decimal Debt, decimal Collection)>(StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var command = CreateCommand(connection, null, """
SELECT entity_type,entity_id,payload_json,is_deleted,version,change_cursor,created_at_utc,updated_at_utc
FROM ct_entities WHERE tenant_id=@tenant AND entity_type='customer' AND is_deleted=0
AND (@search='' OR JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.name')) LIKE CONCAT('%',@search,'%')
 OR JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.phone')) LIKE CONCAT('%',@search,'%'))
ORDER BY JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.name')) LIMIT @take;
""", ("@tenant", tenantId), ("@search", search), ("@take", take)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var entity = ReadEntity(reader, false);
                var payload = entity.Payload!.Value;
                customers.Add((entity, ReadJsonString(payload, "name"), ReadJsonString(payload, "phone")));
            }
        }
        await using (var command = CreateCommand(connection, null, """
SELECT entity_type,JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.customerId')),
SUM(CAST(JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.amount')) AS DECIMAL(20,2)))
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type IN ('debt','collection') AND is_deleted=0
GROUP BY entity_type,JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.customerId'));
""", ("@tenant", tenantId)))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var customerId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(customerId)) continue;
                var amount = reader.IsDBNull(2) ? 0 : reader.GetDecimal(2);
                var old = totals.GetValueOrDefault(customerId);
                totals[customerId] = reader.GetString(0) == "debt" ? (old.Debt + amount, old.Collection) : (old.Debt, old.Collection + amount);
            }
        }
        return customers.Select(x =>
        {
            var total = totals.GetValueOrDefault(x.Entity.EntityId);
            return (object)new
            {
                x.Entity.EntityId, x.Entity.Payload, x.Entity.Version, x.Entity.Cursor,
                x.Entity.UpdatedAtUtc, debtTotal = total.Debt, collectionTotal = total.Collection,
                balance = total.Debt - total.Collection
            };
        }).ToList();
    }

    public async Task<List<CariEntityDto>> GetRecentTransactionsAsync(
        string tenantId, int take, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        take = Math.Clamp(take, 1, 200);
        var result = new List<CariEntityDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT entity_type,entity_id,payload_json,is_deleted,version,change_cursor,created_at_utc,updated_at_utc
FROM ct_entities
WHERE tenant_id=@tenant AND entity_type IN ('debt','collection') AND is_deleted=0
ORDER BY JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.transactionDateUtc')) DESC, change_cursor DESC
LIMIT @take;
""", ("@tenant", tenantId), ("@take", take));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(ReadEntity(reader, false));
        return result;
    }

    public async Task<List<CariEntityDto>> GetCustomerTransactionsAsync(
        string tenantId, string customerId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        customerId = RequireGuid(customerId, "customerId");
        var result = new List<CariEntityDto>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT entity_type,entity_id,payload_json,is_deleted,version,change_cursor,created_at_utc,updated_at_utc
FROM ct_entities WHERE tenant_id=@tenant AND entity_type IN ('debt','collection') AND is_deleted=0
AND JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.customerId'))=@customer
ORDER BY JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.transactionDateUtc')) DESC LIMIT 1000;
""", ("@tenant", tenantId), ("@customer", customerId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(ReadEntity(reader, false));
        return result;
    }

    public async Task<bool> CustomerExistsAsync(string tenantId, string customerId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        customerId = RequireGuid(customerId, "customerId");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT EXISTS(
  SELECT 1 FROM ct_entities
  WHERE tenant_id=@tenant AND entity_type='customer' AND entity_id=@customer AND is_deleted=0
);
""", ("@tenant", tenantId), ("@customer", customerId));
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task RevokeMobileSessionsAsync(string tenantId, string? sessionHash, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var sql = sessionHash is null
            ? "UPDATE ct_mobile_sessions SET revoked_at_utc=@now WHERE tenant_id=@tenant AND revoked_at_utc IS NULL;"
            : "UPDATE ct_mobile_sessions SET revoked_at_utc=@now WHERE tenant_id=@tenant AND session_hash=@session AND revoked_at_utc IS NULL;";
        await ExecuteAsync(connection, null, sql, cancellationToken,
            ("@now", DateTime.UtcNow), ("@tenant", tenantId), ("@session", sessionHash));
    }

    public async Task<object> GetStatusAsync(string tenantId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var cursor = await GetCursorAsync(connection, null, tenantId, cancellationToken);
        await using var command = CreateCommand(connection, null, """
SELECT COUNT(*) AS entity_count,
SUM(CASE WHEN is_deleted=1 THEN 1 ELSE 0 END) AS tombstone_count
FROM ct_entities WHERE tenant_id=@tenant;
""", ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new
        {
            cursor,
            entityCount = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            tombstoneCount = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            serverTimeUtc = DateTime.UtcNow
        };
    }

    public async Task<object> GetCursorStatusAsync(string tenantId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        return new
        {
            cursor = await GetCursorAsync(connection, null, tenantId, cancellationToken),
            serverTimeUtc = DateTime.UtcNow
        };
    }

    private static void ValidateMutations(IReadOnlyList<CariMutationRequest> mutations)
    {
        if (mutations is null || mutations.Count == 0 || mutations.Count > MaxMutations)
            throw new ArgumentException($"mutations 1-{MaxMutations} kayıt içermelidir.");
        var total = 0;
        foreach (var item in mutations)
        {
            if (item is null) throw new ArgumentException("mutation boş olamaz.");
            var size = item.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? 0 : Encoding.UTF8.GetByteCount(item.Payload.GetRawText());
            if (size > MaxPayloadBytes) throw new ArgumentException($"Tek payload en fazla {MaxPayloadBytes} bayt olabilir.");
            total += size;
        }
        if (total > MaxBatchPayloadBytes) throw new ArgumentException($"Push payload toplamı en fazla {MaxBatchPayloadBytes} bayt olabilir.");
    }

    private static void ValidatePayload(string payload)
    {
        if (Encoding.UTF8.GetByteCount(payload) > MaxPayloadBytes)
            throw new ArgumentException($"Payload en fazla {MaxPayloadBytes} bayt olabilir.");
        using var document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Payload bir JSON nesnesi olmalıdır.");
    }

    private static string ValidateEntityType(string value)
    {
        value = (value ?? string.Empty).Trim();
        if (!EntityTypes.Contains(value))
            throw new ArgumentException("Geçersiz entityType.");
        return value;
    }

    private async Task<CariMutationResult?> GetMutationAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string actor,
        string mutationId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
SELECT entity_type,entity_id,result_status,result_version,result_cursor FROM ct_mutations
WHERE tenant_id=@tenant AND actor_device_id=@actor AND client_mutation_id=@mutation LIMIT 1;
""", ("@tenant", tenantId), ("@actor", actor), ("@mutation", mutationId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new CariMutationResult
        {
            ClientMutationId = mutationId, EntityType = reader.GetString(0), EntityId = reader.GetString(1),
            Status = reader.GetString(2), Version = reader.GetInt64(3), Cursor = reader.GetInt64(4)
        };
    }

    private static async Task SaveMutationResultAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string actor,
        CariMutationResult result, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_mutations
(tenant_id,actor_device_id,client_mutation_id,entity_type,entity_id,result_status,result_version,result_cursor,created_at_utc)
VALUES(@tenant,@actor,@mutation,@type,@id,@status,@version,@cursor,@now);
""", cancellationToken, ("@tenant", tenantId), ("@actor", actor), ("@mutation", result.ClientMutationId),
            ("@type", result.EntityType), ("@id", result.EntityId), ("@status", result.Status),
            ("@version", result.Version), ("@cursor", result.Cursor), ("@now", DateTime.UtcNow));

    private static async Task<long> InsertChangeAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string type, string id,
        string? payload, bool deleted, long version, DateTime created, DateTime updated, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
INSERT INTO ct_changes
(tenant_id,entity_type,entity_id,payload_json,is_deleted,version,created_at_utc,updated_at_utc)
VALUES(@tenant,@type,@id,@payload,@deleted,@version,@created,@updated);
""", ("@tenant", tenantId), ("@type", type), ("@id", id), ("@payload", payload),
            ("@deleted", deleted), ("@version", version), ("@created", created), ("@updated", updated));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return command.LastInsertedId;
    }

    private static async Task<CariEntityDto?> GetEntityAsync(
        MySqlConnection connection, MySqlTransaction? transaction, string tenantId, string type,
        string id, bool forUpdate, CancellationToken cancellationToken)
    {
        var sql = """
SELECT entity_type,entity_id,payload_json,is_deleted,version,change_cursor,created_at_utc,updated_at_utc
FROM ct_entities WHERE tenant_id=@tenant AND entity_type=@type AND entity_id=@id
""" + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = CreateCommand(connection, transaction, sql,
            ("@tenant", tenantId), ("@type", type), ("@id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntity(reader, false) : null;
    }

    private static CariEntityDto ReadEntity(MySqlDataReader reader, bool changeRow)
    {
        var offset = changeRow ? 1 : 0;
        var payloadIndex = offset + 2;
        JsonElement? payload = null;
        if (!reader.IsDBNull(payloadIndex))
        {
            using var document = JsonDocument.Parse(reader.GetString(payloadIndex));
            payload = document.RootElement.Clone();
        }
        return new CariEntityDto
        {
            Cursor = changeRow ? reader.GetInt64(0) : reader.GetInt64(5),
            EntityType = reader.GetString(offset),
            EntityId = reader.GetString(offset + 1),
            Payload = payload,
            IsDeleted = reader.GetBoolean(offset + 3),
            Version = reader.GetInt64(offset + 4),
            CreatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc),
            UpdatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc)
        };
    }

    private static async Task<long> GetCursorAsync(
        MySqlConnection connection, MySqlTransaction? transaction, string tenantId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT COALESCE(MAX(`cursor`),0) FROM ct_changes WHERE tenant_id=@tenant;", ("@tenant", tenantId));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<CariTenantContext?> GetTenantByLicenseAsync(
        MySqlConnection connection, MySqlTransaction transaction, int licenseId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT tenant_id,company_name,license_id FROM ct_tenants WHERE license_id=@license FOR UPDATE;",
            ("@license", licenseId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CariTenantContext(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : null;
    }

    private static async Task<CariTenantContext?> GetTenantByIdAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT tenant_id,company_name,license_id FROM ct_tenants WHERE tenant_id=@tenant AND is_active=1;",
            ("@tenant", tenantId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CariTenantContext(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)) : null;
    }

    private async Task<bool> ValidateEntitlementAsync(
        MySqlConnection connection,
        string tenantId,
        int licenseId,
        CancellationToken cancellationToken,
        MySqlTransaction? transaction = null)
    {
        var entitlement = await _entitlementValidator.ValidateAsync(licenseId, cancellationToken);
        if (!entitlement.IsActive && entitlement.IsDefinitivelyInactive)
        {
            await ExecuteAsync(connection, transaction,
                "UPDATE ct_tenants SET is_active=0,updated_at_utc=@now WHERE tenant_id=@tenant;",
                cancellationToken, ("@now", DateTime.UtcNow), ("@tenant", tenantId));
        }
        return entitlement.IsActive;
    }

    private static async Task<(string MachineHash, string TokenHash)?> GetDeviceSecretAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string deviceId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT machine_hash,token_hash FROM ct_devices WHERE tenant_id=@tenant AND device_id=@device FOR UPDATE;",
            ("@tenant", tenantId), ("@device", deviceId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static async Task AddAuditAsync(
        MySqlConnection connection, MySqlTransaction transaction, string tenantId, string actorType,
        string actorId, string action, string? entityType, string? entityId, string? detail,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, """
INSERT INTO ct_audit_log
(tenant_id,actor_type,actor_id,action_name,entity_type,entity_id,detail_text,created_at_utc)
VALUES(@tenant,@actorType,@actor,@action,@type,@id,@detail,@now);
""", cancellationToken, ("@tenant", tenantId), ("@actorType", actorType), ("@actor", actorId),
            ("@action", action), ("@type", entityType), ("@id", entityId), ("@detail", detail), ("@now", DateTime.UtcNow));

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static MySqlCommand CreateCommand(
        MySqlConnection connection, MySqlTransaction? transaction, string sql, params (string Name, object? Value)[] values)
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
        MySqlConnection connection, MySqlTransaction? transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object? Value)[] values)
    {
        await using var command = CreateCommand(connection, transaction, sql, values);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    private static bool FixedHashEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    }

    private static string RequireText(string value, string name, int max)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length is 0 || value.Length > max) throw new ArgumentException($"{name} zorunludur ve en fazla {max} karakter olabilir.");
        return value;
    }

    private static string CleanText(string value, string fallback, int max)
    {
        value = (value ?? string.Empty).Trim();
        if (value.Length == 0) value = fallback;
        return value.Length <= max ? value : value[..max];
    }

    private static string RequireGuid(string value, string name) =>
        Guid.TryParse((value ?? string.Empty).Trim(), out var id) ? id.ToString() : throw new ArgumentException($"{name} GUID olmalıdır.");

    private static string ReadJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty : string.Empty;
}
