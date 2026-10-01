using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NSYazilim.Web.SalonTakipCloud.Models;

namespace NSYazilim.Web.SalonTakipCloud.Services;

/// <summary>
/// Salon Takip bulut verilerini ana MySQL veritabanından tamamen ayrı tutar.
/// Sınıf adı geriye uyumluluk için korunmuştur; veri sağlayıcısı SQLite'tır.
/// </summary>
public sealed class SalonTakipMySqlStore
{
    private const string DatabaseFileName = "nsx_salon_takip_cloud.db";
    private static readonly object SchemaLock = new();
    private static readonly object WriteLock = new();
    private static string _schemaReadyPath = string.Empty;

    private readonly string _appDataPath;
    private readonly string _databasePath;
    private readonly string _connectionString;

    public SalonTakipMySqlStore(IWebHostEnvironment environment)
    {
        _appDataPath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "App_Data"));
        Directory.CreateDirectory(_appDataPath);

        _databasePath = Path.Combine(_appDataPath, DatabaseFileName);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 20
        }.ToString();
    }

    public void EnsureSchema()
    {
        var fullDatabasePath = Path.GetFullPath(_databasePath);
        if (string.Equals(_schemaReadyPath, fullDatabasePath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(fullDatabasePath)
            && new FileInfo(fullDatabasePath).Length > 0)
        {
            return;
        }

        lock (SchemaLock)
        {
            if (string.Equals(_schemaReadyPath, fullDatabasePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(fullDatabasePath)
                && new FileInfo(fullDatabasePath).Length > 0)
            {
                return;
            }

            Directory.CreateDirectory(_appDataPath);
            EnsureAppDataWritable();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            ExecuteNonQuery(connection, transaction, """
CREATE TABLE IF NOT EXISTS st_tenants (
    tenant_id TEXT NOT NULL PRIMARY KEY,
    license_id INTEGER NOT NULL UNIQUE,
    company_name TEXT NOT NULL DEFAULT '',
    api_token_hash TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_seen_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS ix_st_tenants_active ON st_tenants(is_active);

CREATE TABLE IF NOT EXISTS st_qr_logins (
    token_hash TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    source_device_id TEXT NOT NULL DEFAULT '',
    source_user TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    used_at TEXT NULL,
    FOREIGN KEY(tenant_id) REFERENCES st_tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_st_qr_logins_tenant ON st_qr_logins(tenant_id, expires_at);
CREATE INDEX IF NOT EXISTS ix_st_qr_logins_expiry ON st_qr_logins(expires_at);

CREATE TABLE IF NOT EXISTS st_mobile_sessions (
    session_hash TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    csrf_hash TEXT NOT NULL,
    user_agent_hash TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    revoked_at TEXT NULL,
    FOREIGN KEY(tenant_id) REFERENCES st_tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_st_mobile_sessions_tenant ON st_mobile_sessions(tenant_id, expires_at);
CREATE INDEX IF NOT EXISTS ix_st_mobile_sessions_expiry ON st_mobile_sessions(expires_at);

CREATE TABLE IF NOT EXISTS st_entity_data (
    tenant_id TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1,
    PRIMARY KEY(tenant_id, entity_type, entity_id),
    FOREIGN KEY(tenant_id) REFERENCES st_tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_st_entity_data_type ON st_entity_data(tenant_id, entity_type, updated_at);

CREATE TABLE IF NOT EXISTS st_sync_changes (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    tenant_id TEXT NOT NULL,
    entity_type TEXT NOT NULL,
    entity_id TEXT NOT NULL,
    action TEXT NOT NULL DEFAULT 'upsert',
    payload_json TEXT NOT NULL,
    source_device_id TEXT NOT NULL DEFAULT '',
    source_user TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL,
    FOREIGN KEY(tenant_id) REFERENCES st_tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_st_sync_changes_tenant ON st_sync_changes(tenant_id, id);

CREATE TABLE IF NOT EXISTS st_audit_log (
    id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    tenant_id TEXT NOT NULL,
    event_type TEXT NOT NULL,
    detail_text TEXT NULL,
    created_at TEXT NOT NULL,
    FOREIGN KEY(tenant_id) REFERENCES st_tenants(tenant_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ix_st_audit_log_tenant ON st_audit_log(tenant_id, id);

CREATE TABLE IF NOT EXISTS st_meta (
    meta_key TEXT NOT NULL PRIMARY KEY,
    meta_value TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
""");

            ExecuteNonQuery(connection, transaction, """
INSERT INTO st_meta(meta_key, meta_value, updated_at)
VALUES('schema_version', '3-sqlite', @updated_at)
ON CONFLICT(meta_key) DO UPDATE SET
    meta_value = excluded.meta_value,
    updated_at = excluded.updated_at;
""", ("@updated_at", DbDate(DateTime.UtcNow)));

            ExecuteNonQuery(connection, transaction, """
INSERT INTO st_meta(meta_key, meta_value, updated_at)
VALUES('instance_id', @instance_id, @updated_at)
ON CONFLICT(meta_key) DO NOTHING;
""",
                ("@instance_id", Guid.NewGuid().ToString("N")),
                ("@updated_at", DbDate(DateTime.UtcNow)));

            transaction.Commit();
            _schemaReadyPath = fullDatabasePath;
        }
    }

    public (SalonTenantContext Tenant, string ApiToken, bool Rotated) CreateOrRefreshTenant(
        int licenseId,
        string licenseKey,
        string companyName,
        string currentApiToken)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            var normalizedCompany = NormalizeText(companyName, "NSX Düğün Salonu", 220);
            var tenantId = BuildTenantId(licenseId, licenseKey);
            var now = DateTime.UtcNow;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            var existing = GetTenantByLicenseId(connection, transaction, licenseId);
            if (existing is not null)
            {
                // Lisans kaydı daha önce oluşturulduysa TenantId değişmez.
                tenantId = existing.Value.TenantId;
                var currentTokenValid = !string.IsNullOrWhiteSpace(currentApiToken)
                    && TokenMatches(existing.Value.ApiTokenHash, currentApiToken.Trim());
                var apiToken = currentTokenValid ? currentApiToken.Trim() : CreateToken(40);

                ExecuteNonQuery(connection, transaction, """
UPDATE st_tenants
SET tenant_id = @tenant_id,
    company_name = @company_name,
    api_token_hash = @api_token_hash,
    is_active = 1,
    updated_at = @updated_at,
    last_seen_at = @last_seen_at
WHERE license_id = @license_id;
""",
                    ("@tenant_id", tenantId),
                    ("@company_name", normalizedCompany),
                    ("@api_token_hash", HashToken(apiToken)),
                    ("@updated_at", DbDate(now)),
                    ("@last_seen_at", DbDate(now)),
                    ("@license_id", licenseId));

                if (!currentTokenValid)
                {
                    RevokePendingQrLogins(connection, transaction, tenantId, now);
                    AddAudit(connection, transaction, tenantId, "ApiTokenRotated", "Masaüstü bağlantı anahtarı lisans doğrulamasıyla yenilendi.");
                }

                transaction.Commit();
                return (new SalonTenantContext(tenantId, normalizedCompany, licenseId), apiToken, !currentTokenValid);
            }

            var newApiToken = CreateToken(40);
            ExecuteNonQuery(connection, transaction, """
INSERT INTO st_tenants
(tenant_id, license_id, company_name, api_token_hash, is_active, created_at, updated_at, last_seen_at)
VALUES
(@tenant_id, @license_id, @company_name, @api_token_hash, 1, @created_at, @updated_at, @last_seen_at);
""",
                ("@tenant_id", tenantId),
                ("@license_id", licenseId),
                ("@company_name", normalizedCompany),
                ("@api_token_hash", HashToken(newApiToken)),
                ("@created_at", DbDate(now)),
                ("@updated_at", DbDate(now)),
                ("@last_seen_at", DbDate(now)));

            AddAudit(connection, transaction, tenantId, "TenantCreated", "Salon Takip firma alanı oluşturuldu.");
            transaction.Commit();
            return (new SalonTenantContext(tenantId, normalizedCompany, licenseId), newApiToken, false);
        }
    }

    public SalonTenantContext? ValidateTenant(string tenantId, string apiToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiToken))
        {
            return null;
        }

        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
SELECT tenant_id, company_name, license_id
FROM st_tenants
WHERE tenant_id = @tenant_id
  AND api_token_hash = @api_token_hash
  AND is_active = 1
LIMIT 1;
""", ("@tenant_id", tenantId.Trim()), ("@api_token_hash", HashToken(apiToken.Trim())));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var tenant = new SalonTenantContext(ReadString(reader, 0), ReadString(reader, 1), Convert.ToInt32(reader.GetValue(2)));
        reader.Close();

        using var touch = CreateCommand(connection, null,
            "UPDATE st_tenants SET last_seen_at = @last_seen_at WHERE tenant_id = @tenant_id;",
            ("@last_seen_at", DbDate(DateTime.UtcNow)), ("@tenant_id", tenant.TenantId));
        touch.ExecuteNonQuery();
        return tenant;
    }

    public (string Token, DateTime ExpiresAt) CreateQrLogin(string tenantId, string sourceDeviceId, string sourceUser)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            var now = DateTime.UtcNow;
            var expiresAt = now.AddMinutes(3);
            var rawToken = CreateToken(36);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction, """
DELETE FROM st_qr_logins
WHERE expires_at < @cutoff OR (tenant_id = @tenant_id AND used_at IS NULL);
""", ("@cutoff", DbDate(now.AddHours(-1))), ("@tenant_id", tenantId));

            ExecuteNonQuery(connection, transaction, """
INSERT INTO st_qr_logins
(token_hash, tenant_id, source_device_id, source_user, created_at, expires_at, used_at)
VALUES
(@token_hash, @tenant_id, @source_device_id, @source_user, @created_at, @expires_at, NULL);
""",
                ("@token_hash", HashToken(rawToken)),
                ("@tenant_id", tenantId),
                ("@source_device_id", NormalizeText(sourceDeviceId, string.Empty, 180)),
                ("@source_user", NormalizeText(sourceUser, string.Empty, 180)),
                ("@created_at", DbDate(now)),
                ("@expires_at", DbDate(expiresAt)));

            AddAudit(connection, transaction, tenantId, "QrLoginCreated", "Tek kullanımlık mobil giriş bağlantısı üretildi.");
            transaction.Commit();
            return (rawToken, expiresAt.ToLocalTime());
        }
    }

    public SalonTenantContext? ConsumeQrLogin(string rawToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        lock (WriteLock)
        {
            var now = DateTime.UtcNow;
            var tokenHash = HashToken(rawToken.Trim());
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            string tenantId;
            using (var select = CreateCommand(connection, transaction, """
SELECT tenant_id
FROM st_qr_logins
WHERE token_hash = @token_hash
  AND used_at IS NULL
  AND expires_at >= @now
LIMIT 1;
""", ("@token_hash", tokenHash), ("@now", DbDate(now))))
            {
                tenantId = Convert.ToString(select.ExecuteScalar())?.Trim() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(tenantId))
            {
                transaction.Rollback();
                return null;
            }

            var changed = ExecuteNonQuery(connection, transaction, """
UPDATE st_qr_logins
SET used_at = @used_at
WHERE token_hash = @token_hash
  AND used_at IS NULL
  AND expires_at >= @used_at;
""", ("@used_at", DbDate(now)), ("@token_hash", tokenHash));

            if (changed != 1)
            {
                transaction.Rollback();
                return null;
            }

            var tenant = GetTenantById(connection, transaction, tenantId);
            if (tenant is not null)
            {
                AddAudit(connection, transaction, tenant.TenantId, "QrLoginConsumed", "Tek kullanımlık QR mobil oturuma dönüştürüldü.");
            }

            transaction.Commit();
            return tenant;
        }
    }

    public (string SessionToken, string CsrfToken, DateTime ExpiresAt) CreateMobileSession(string tenantId, string userAgent)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            var now = DateTime.UtcNow;
            // QR ile yetkilendirilen cihaz, kullanici acikca cikis yapana veya
            // oturum yonetiminden iptal edilene kadar kalici olarak bagli kalir.
            var expiresAt = now.AddYears(10);
            var sessionToken = CreateToken(48);
            var csrfToken = CreateToken(32);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(connection, transaction, """
DELETE FROM st_mobile_sessions
WHERE expires_at < @cutoff OR revoked_at IS NOT NULL;
""", ("@cutoff", DbDate(now.AddDays(-1))));

            ExecuteNonQuery(connection, transaction, """
INSERT INTO st_mobile_sessions
(session_hash, tenant_id, csrf_hash, user_agent_hash, created_at, expires_at, last_seen_at, revoked_at)
VALUES
(@session_hash, @tenant_id, @csrf_hash, @user_agent_hash, @created_at, @expires_at, @last_seen_at, NULL);
""",
                ("@session_hash", HashToken(sessionToken)),
                ("@tenant_id", tenantId),
                ("@csrf_hash", HashToken(csrfToken)),
                ("@user_agent_hash", HashText(userAgent)),
                ("@created_at", DbDate(now)),
                ("@expires_at", DbDate(expiresAt)),
                ("@last_seen_at", DbDate(now)));

            transaction.Commit();
            return (sessionToken, csrfToken, expiresAt.ToLocalTime());
        }
    }

    public SalonMobileSessionContext? ValidateMobileSession(string sessionToken, string userAgent)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
SELECT s.tenant_id, t.company_name, s.session_hash, s.csrf_hash, s.expires_at, s.user_agent_hash
FROM st_mobile_sessions s
INNER JOIN st_tenants t ON t.tenant_id = s.tenant_id
WHERE s.session_hash = @session_hash
  AND s.revoked_at IS NULL
  AND s.expires_at >= @now
  AND t.is_active = 1
LIMIT 1;
""", ("@session_hash", HashToken(sessionToken.Trim())), ("@now", DbDate(now)));

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        // Mobil tarayicilar uygulama yeniden acildiginda, ekran kilidinden dondugunde
        // veya surum/masaustu modu degistiginde User-Agent metnini kucuk farklarla
        // yenileyebilir. Oturumu birebir User-Agent degerine kilitlemek ayni telefonda
        // gereksiz cikislara neden oluyordu. Guvenlik, yuksek entropili session tokeni,
        // Secure/HttpOnly cookie, CSRF tokeni ve sunucu tarafli iptal ile saglanir.
        // Kayitli User-Agent yalnizca denetim bilgisi olarak tutulur.

        var session = new SalonMobileSessionContext(
            ReadString(reader, 0),
            ReadString(reader, 1),
            ReadString(reader, 2),
            ReadString(reader, 3),
            ReadDate(reader, 4).ToLocalTime());
        reader.Close();

        using var touch = CreateCommand(connection, null,
            "UPDATE st_mobile_sessions SET last_seen_at = @last_seen_at WHERE session_hash = @session_hash;",
            ("@last_seen_at", DbDate(now)), ("@session_hash", session.SessionHash));
        touch.ExecuteNonQuery();
        return session;
    }

    public bool ValidateCsrf(SalonMobileSessionContext session, string csrfToken)
    {
        return session is not null
            && !string.IsNullOrWhiteSpace(csrfToken)
            && TokenMatches(session.CsrfHash, csrfToken.Trim());
    }

    public void RevokeMobileSession(string sessionToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            return;
        }

        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var command = CreateCommand(connection, null, """
UPDATE st_mobile_sessions
SET revoked_at = @revoked_at
WHERE session_hash = @session_hash AND revoked_at IS NULL;
""", ("@revoked_at", DbDate(DateTime.UtcNow)), ("@session_hash", HashToken(sessionToken.Trim())));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<SalonEntityItem> GetDataItems(string tenantId, string entityType, int take = 5000)
    {
        EnsureSchema();
        take = Math.Clamp(take, 1, 5000);
        var normalizedType = NormalizeEntityType(entityType);
        var items = new List<SalonEntityItem>();

        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, $"""
SELECT entity_id, entity_type, payload_json, updated_at, version
FROM st_entity_data
WHERE tenant_id = @tenant_id AND entity_type = @entity_type
ORDER BY updated_at DESC
LIMIT {take};
""", ("@tenant_id", tenantId), ("@entity_type", normalizedType));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SalonEntityItem
            {
                Id = ReadString(reader, 0),
                EntityType = ReadString(reader, 1),
                PayloadJson = ReadString(reader, 2),
                UpdatedAt = ReadDate(reader, 3).ToLocalTime().ToString("O"),
                Version = reader.GetInt64(4)
            });
        }

        return items;
    }

    public (SalonEntityItem Item, long ChangeId) CreateMobileCustomer(
        string tenantId,
        SalonMobileCustomerCreateRequest request)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            request ??= new SalonMobileCustomerCreateRequest();
            var fullName = NormalizeText(request.FullName, string.Empty, 180);
            var phone = NormalizeText(request.Phone, string.Empty, 40);
            var phoneDigits = NormalizePhone(phone);
            var secondPhone = NormalizeText(request.SecondPhone, string.Empty, 40);
            var email = NormalizeText(request.Email, string.Empty, 180).ToLowerInvariant();
            var address = NormalizeText(request.Address, string.Empty, 600);
            var identityNumber = NormalizeText(request.IdentityNumber, string.Empty, 30);
            var status = NormalizeText(request.Status, "Aktif", 40);
            var notes = NormalizeText(request.Notes, string.Empty, 1000);

            if (fullName.Length < 2)
            {
                throw new InvalidOperationException("Müşteri adı en az 2 karakter olmalıdır.");
            }

            if (phoneDigits.Length is < 10 or > 11)
            {
                throw new InvalidOperationException("Geçerli bir telefon numarası giriniz.");
            }

            if (!string.IsNullOrWhiteSpace(email)
                && (!email.Contains('@') || email.StartsWith('@') || email.EndsWith('@')))
            {
                throw new InvalidOperationException("E-posta adresi geçerli değil.");
            }

            var entityId = "WEB-CUSTOMER-" + Guid.NewGuid().ToString("N");
            var customerCode = "WEB-" + entityId[^8..].ToUpperInvariant();
            var now = DateTime.UtcNow;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            var existingRows = GetEntityRows(connection, transaction, tenantId, "customer");
            var duplicate = existingRows.FirstOrDefault(x =>
                string.Equals(NormalizePhone(ReadJsonText(x.PayloadJson, "Phone", "PhoneNumber", "Telephone")), phoneDigits, StringComparison.Ordinal));
            if (duplicate is not null)
            {
                var duplicateName = ReadJsonText(duplicate.PayloadJson, "FullName", "CustomerName", "Name");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(duplicateName)
                    ? "Bu telefon numarasıyla kayıtlı bir müşteri zaten var."
                    : $"Bu telefon numarasıyla kayıtlı bir müşteri zaten var: {duplicateName}");
            }

            var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["Id"] = 0,
                ["CustomerId"] = 0,
                ["CloudEntityId"] = entityId,
                ["ExternalId"] = entityId,
                ["ClientReferenceId"] = entityId,
                ["CustomerCode"] = customerCode,
                ["Code"] = customerCode,
                ["FullName"] = fullName,
                ["CustomerName"] = fullName,
                ["Name"] = fullName,
                ["Phone"] = phone,
                ["PhoneNumber"] = phone,
                ["Telephone"] = phone,
                ["MobilePhone"] = phone,
                ["SecondPhone"] = secondPhone,
                ["Phone2"] = secondPhone,
                ["Email"] = email,
                ["EmailAddress"] = email,
                ["Address"] = address,
                ["IdentityNumber"] = identityNumber,
                ["TaxNumber"] = identityNumber,
                ["Status"] = status,
                ["Notes"] = notes,
                ["Note"] = notes,
                ["CreatedAt"] = now.ToString("O"),
                ["CreatedDate"] = now.ToString("O"),
                ["UpdatedAt"] = now.ToString("O"),
                ["UpdatedDate"] = now.ToString("O"),
                ["IsActive"] = true,
                ["CreatedFrom"] = "MOBILE-WEB",
                ["SyncOrigin"] = "MOBILE-WEB",
                ["PendingDesktopImport"] = true
            });

            UpsertDataItemCore(connection, transaction, tenantId, "customer", entityId, payload, now);
            var changeId = AppendSyncChangeCore(
                connection,
                transaction,
                tenantId,
                "customer",
                entityId,
                "upsert",
                payload,
                "MOBILE-WEB",
                "Mobil Panel",
                now);

            var saved = GetDataItemCore(connection, transaction, tenantId, "customer", entityId)
                ?? throw new InvalidOperationException("Müşteri veritabanına yazıldıktan sonra doğrulanamadı.");

            transaction.Commit();
            return (saved, changeId);
        }
    }

    public long UpsertDataItem(string tenantId, string entityType, string entityId, SalonEntityUpsertRequest request)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            request ??= new SalonEntityUpsertRequest();
            var normalizedType = NormalizeEntityType(entityType);
            var normalizedId = NormalizeEntityId(entityId, Guid.NewGuid().ToString("N"));
            var payload = ValidatePayloadJson(request.PayloadJson);
            var now = DateTime.UtcNow;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            UpsertDataItemCore(connection, transaction, tenantId, normalizedType, normalizedId, payload, now);
            var changeId = AppendSyncChangeCore(connection, transaction, tenantId, normalizedType, normalizedId, "upsert", payload, request.SourceDeviceId, request.SourceUser, now);
            transaction.Commit();
            return changeId;
        }
    }

    public long DeleteDataItem(string tenantId, string entityType, string entityId, string sourceDeviceId, string sourceUser)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            var normalizedType = NormalizeEntityType(entityType);
            var normalizedId = NormalizeEntityId(entityId, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedId))
            {
                throw new InvalidOperationException("Kayıt kimliği boş olamaz.");
            }

            var now = DateTime.UtcNow;
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            DeleteDataItemCore(connection, transaction, tenantId, normalizedType, normalizedId);
            var changeId = AppendSyncChangeCore(connection, transaction, tenantId, normalizedType, normalizedId, "delete", "{}", sourceDeviceId, sourceUser, now);
            transaction.Commit();
            return changeId;
        }
    }

    public SalonSnapshotResponse ApplySnapshot(string tenantId, SalonSnapshotRequest request)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            request ??= new SalonSnapshotRequest();
            var entityType = NormalizeEntityType(request.EntityType);
            var incoming = (request.Items ?? new List<SalonSnapshotItem>())
                .Where(x => x is not null)
                .Take(5000)
                .Select(x => new SalonSnapshotItem
                {
                    Id = NormalizeEntityId(x.Id, Guid.NewGuid().ToString("N")),
                    PayloadJson = ValidatePayloadJson(x.PayloadJson)
                })
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Last())
                .ToList();

            var now = DateTime.UtcNow;
            var deletedCount = 0;
            var lastChangeId = 0L;

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            if (request.ReplaceExisting)
            {
                var keepIds = incoming.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var existingRows = GetEntityRows(connection, transaction, tenantId, entityType);
                foreach (var existing in existingRows.Where(x => !keepIds.Contains(x.EntityId)))
                {
                    if (string.Equals(entityType, "customer", StringComparison.Ordinal)
                        && IsPendingWebCustomer(existing))
                    {
                        // Mobil panelde oluşturulan müşteri masaüstü değişiklik akışından henüz
                        // içeri alınmadıysa, masaüstünün tam snapshot'ı bu kaydı silemez.
                        // Masaüstü müşteriyi içeri alıp kendi yerel kimliğiyle snapshot'a eklediğinde
                        // aynı telefon/e-posta-ad eşleşmesi bulunur ve geçici web kaydı sessizce birleştirilir.
                        if (HasEquivalentIncomingCustomer(existing.PayloadJson, incoming))
                        {
                            DeleteDataItemCore(connection, transaction, tenantId, entityType, existing.EntityId);
                        }

                        continue;
                    }

                    DeleteDataItemCore(connection, transaction, tenantId, entityType, existing.EntityId);
                    lastChangeId = AppendSyncChangeCore(connection, transaction, tenantId, entityType, existing.EntityId, "delete", "{}", request.SourceDeviceId, request.SourceUser, now);
                    deletedCount++;
                }
            }

            foreach (var item in incoming)
            {
                UpsertDataItemCore(connection, transaction, tenantId, entityType, item.Id, item.PayloadJson, now);
                lastChangeId = AppendSyncChangeCore(connection, transaction, tenantId, entityType, item.Id, "upsert", item.PayloadJson, request.SourceDeviceId, request.SourceUser, now);
            }

            transaction.Commit();
            return new SalonSnapshotResponse
            {
                Success = true,
                EntityType = entityType,
                SavedCount = incoming.Count,
                DeletedCount = deletedCount,
                LastChangeId = lastChangeId
            };
        }
    }

    public long AppendSyncChange(string tenantId, SalonSyncChangeRequest request)
    {
        EnsureSchema();
        lock (WriteLock)
        {
            request ??= new SalonSyncChangeRequest();
            var action = string.Equals(request.Action, "delete", StringComparison.OrdinalIgnoreCase) ? "delete" : "upsert";
            var entityType = NormalizeEntityType(request.EntityType);
            var entityId = NormalizeEntityId(request.EntityId, Guid.NewGuid().ToString("N"));
            var payload = action == "delete" ? "{}" : ValidatePayloadJson(request.PayloadJson);

            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var id = AppendSyncChangeCore(connection, transaction, tenantId, entityType, entityId, action, payload, request.SourceDeviceId, request.SourceUser, DateTime.UtcNow);
            transaction.Commit();
            return id;
        }
    }

    public IReadOnlyList<SalonSyncChangeResponse> GetChanges(string tenantId, long afterId, int take)
    {
        EnsureSchema();
        take = Math.Clamp(take, 1, 500);
        afterId = Math.Max(0, afterId);

        // Web panelinden oluşturulan müşteri, masaüstünün eski senkron imlecinin
        // gerisinde kalmış olabilir. Masaüstü kaydı snapshot ile geri gönderene
        // kadar bu değişikliği imlecin önüne yeniden koyarız. Böylece 0 / 1
        // sayım farkı kalıcı bir senkron kilidine dönüşmez.
        RequeuePendingWebCustomersAfterCursor(tenantId, afterId);

        var items = new List<SalonSyncChangeResponse>();

        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, $"""
SELECT id, entity_type, entity_id, action, payload_json, source_device_id, source_user, created_at
FROM st_sync_changes
WHERE tenant_id = @tenant_id AND id > @after_id
ORDER BY id ASC
LIMIT {take};
""", ("@tenant_id", tenantId), ("@after_id", afterId));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SalonSyncChangeResponse
            {
                ChangeId = reader.GetInt64(0),
                EntityType = ReadString(reader, 1),
                EntityId = ReadString(reader, 2),
                Action = ReadString(reader, 3),
                PayloadJson = ReadString(reader, 4),
                SourceDeviceId = ReadString(reader, 5),
                SourceUser = ReadString(reader, 6),
                CreatedAt = ReadDate(reader, 7).ToLocalTime().ToString("O")
            });
        }

        return items;
    }

    public long GetLastChangeId(string tenantId)
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null,
            "SELECT COALESCE(MAX(id), 0) FROM st_sync_changes WHERE tenant_id = @tenant_id;",
            ("@tenant_id", tenantId));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyDictionary<string, int> GetEntityCounts(string tenantId)
    {
        EnsureSchema();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["company"] = 0,
            ["hall"] = 0,
            ["reservation"] = 0,
            ["customer"] = 0,
            ["finance"] = 0,
            ["payment"] = 0,
            ["expense"] = 0
        };

        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
SELECT entity_type, COUNT(1)
FROM st_entity_data
WHERE tenant_id = @tenant_id
GROUP BY entity_type;
""", ("@tenant_id", tenantId));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            counts[ReadString(reader, 0)] = Convert.ToInt32(reader.GetValue(1));
        }

        return counts;
    }

    public IReadOnlyDictionary<string, int> GetDesktopSyncEntityCounts(string tenantId)
    {
        var counts = GetEntityCounts(tenantId)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        var pendingWebCustomers = GetPendingWebCustomerCount(tenantId);
        counts["customer"] = Math.Max(0, counts.GetValueOrDefault("customer") - pendingWebCustomers);
        return counts;
    }

    public int GetPendingWebCustomerCount(string tenantId)
    {
        EnsureSchema();
        var count = 0;

        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null, """
SELECT entity_id, payload_json
FROM st_entity_data
WHERE tenant_id = @tenant_id AND entity_type = 'customer';
""", ("@tenant_id", tenantId));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = new EntityDataRow(ReadString(reader, 0), ReadString(reader, 1));
            if (IsPendingWebCustomer(row))
            {
                count++;
            }
        }

        return count;
    }

    public string GetDatabaseInstanceId()
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var command = CreateCommand(connection, null,
            "SELECT meta_value FROM st_meta WHERE meta_key = 'instance_id' LIMIT 1;");
        var value = Convert.ToString(command.ExecuteScalar())?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        lock (WriteLock)
        {
            using var writeConnection = OpenConnection();
            using var transaction = writeConnection.BeginTransaction();
            value = Guid.NewGuid().ToString("N");
            ExecuteNonQuery(writeConnection, transaction, """
INSERT INTO st_meta(meta_key, meta_value, updated_at)
VALUES('instance_id', @instance_id, @updated_at)
ON CONFLICT(meta_key) DO NOTHING;
""", ("@instance_id", value), ("@updated_at", DbDate(DateTime.UtcNow)));
            transaction.Commit();
        }

        using var verifyConnection = OpenConnection();
        using var verifyCommand = CreateCommand(verifyConnection, null,
            "SELECT meta_value FROM st_meta WHERE meta_key = 'instance_id' LIMIT 1;");
        return Convert.ToString(verifyCommand.ExecuteScalar())?.Trim() ?? value;
    }

    public object GetDatabaseStatus()
    {
        EnsureSchema();
        using var connection = OpenConnection();
        using var tenantCommand = CreateCommand(connection, null, "SELECT COUNT(1) FROM st_tenants WHERE is_active = 1;");
        var tenantCount = Convert.ToInt64(tenantCommand.ExecuteScalar());
        using var dataCommand = CreateCommand(connection, null, "SELECT COUNT(1) FROM st_entity_data;");
        var dataCount = Convert.ToInt64(dataCommand.ExecuteScalar());
        using var changeCommand = CreateCommand(connection, null, "SELECT COUNT(1) FROM st_sync_changes;");
        var changeCount = Convert.ToInt64(changeCommand.ExecuteScalar());
        using var journalCommand = CreateCommand(connection, null, "PRAGMA journal_mode;");
        var journalMode = Convert.ToString(journalCommand.ExecuteScalar()) ?? string.Empty;
        var info = new FileInfo(_databasePath);

        return new
        {
            databaseReady = true,
            provider = "SQLite",
            databaseFile = $"App_Data/{DatabaseFileName}",
            databaseExists = info.Exists,
            databaseSizeBytes = info.Exists ? info.Length : 0,
            appDataWritable = CanWriteAppData(),
            journalMode,
            schemaVersion = "3-sqlite",
            databaseInstanceId = GetDatabaseInstanceId(),
            tenantCount,
            dataCount,
            changeCount,
            serverTime = DateTimeOffset.Now
        };
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        ExecutePragma(connection, "PRAGMA foreign_keys=ON;");
        ExecutePragma(connection, "PRAGMA busy_timeout=20000;");
        try
        {
            ExecutePragma(connection, "PRAGMA journal_mode=WAL;");
        }
        catch (SqliteException)
        {
            // Bazı hosting disklerinde WAL açılamazsa klasik journal ile çalışmaya devam et.
            ExecutePragma(connection, "PRAGMA journal_mode=DELETE;");
        }
        ExecutePragma(connection, "PRAGMA synchronous=NORMAL;");
        return connection;
    }

    private static void ExecutePragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 20;
        command.ExecuteNonQuery();
    }

    private void EnsureAppDataWritable()
    {
        if (!CanWriteAppData())
        {
            throw new UnauthorizedAccessException(
                "App_Data klasörüne yazılamıyor. Plesk'te httpdocs/App_Data klasörüne uygulama havuzu için Yazma/Modify izni verin.");
        }
    }

    private bool CanWriteAppData()
    {
        try
        {
            Directory.CreateDirectory(_appDataPath);
            var probePath = Path.Combine(_appDataPath, $".nsx-write-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
            File.Delete(probePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RequeuePendingWebCustomersAfterCursor(string tenantId, long afterId)
    {
        lock (WriteLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var pendingRows = GetEntityRows(connection, transaction, tenantId, "customer")
                .Where(IsPendingWebCustomer)
                .ToList();

            foreach (var pending in pendingRows)
            {
                long latestChangeId;
                using (var latestCommand = CreateCommand(connection, transaction, """
SELECT COALESCE(MAX(id), 0)
FROM st_sync_changes
WHERE tenant_id = @tenant_id
  AND entity_type = 'customer'
  AND entity_id = @entity_id;
""", ("@tenant_id", tenantId), ("@entity_id", pending.EntityId)))
                {
                    latestChangeId = Convert.ToInt64(latestCommand.ExecuteScalar());
                }

                if (latestChangeId > afterId)
                {
                    continue;
                }

                AppendSyncChangeCore(
                    connection,
                    transaction,
                    tenantId,
                    "customer",
                    pending.EntityId,
                    "upsert",
                    pending.PayloadJson,
                    "MOBILE-WEB-RETRY",
                    "Mobil Panel",
                    DateTime.UtcNow);
            }

            transaction.Commit();
        }
    }

    private static void UpsertDataItemCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string entityType,
        string entityId,
        string payload,
        DateTime now)
    {
        ExecuteNonQuery(connection, transaction, """
INSERT INTO st_entity_data
(tenant_id, entity_type, entity_id, payload_json, updated_at, version)
VALUES
(@tenant_id, @entity_type, @entity_id, @payload_json, @updated_at, 1)
ON CONFLICT(tenant_id, entity_type, entity_id) DO UPDATE SET
    payload_json = excluded.payload_json,
    updated_at = excluded.updated_at,
    version = st_entity_data.version + 1;
""",
            ("@tenant_id", tenantId),
            ("@entity_type", entityType),
            ("@entity_id", entityId),
            ("@payload_json", payload),
            ("@updated_at", DbDate(now)));
    }

    private static void DeleteDataItemCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string entityType,
        string entityId)
    {
        ExecuteNonQuery(connection, transaction, """
DELETE FROM st_entity_data
WHERE tenant_id = @tenant_id AND entity_type = @entity_type AND entity_id = @entity_id;
""", ("@tenant_id", tenantId), ("@entity_type", entityType), ("@entity_id", entityId));
    }

    private static long AppendSyncChangeCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string entityType,
        string entityId,
        string action,
        string payload,
        string sourceDeviceId,
        string sourceUser,
        DateTime now)
    {
        long currentMax;
        using (var maxCommand = CreateCommand(connection, transaction, "SELECT COALESCE(MAX(id), 0) FROM st_sync_changes;"))
        {
            currentMax = Convert.ToInt64(maxCommand.ExecuteScalar());
        }

        // DB dosyası yeniden oluşturulsa bile masaüstündeki eski senkron imlecinden büyük
        // bir kimlikle başla. Böylece yeni mobil değişiklikler kaçırılmaz.
        var timeBasedId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000L;
        var changeId = Math.Max(currentMax + 1, timeBasedId);

        ExecuteNonQuery(connection, transaction, """
INSERT INTO st_sync_changes
(id, tenant_id, entity_type, entity_id, action, payload_json, source_device_id, source_user, created_at)
VALUES
(@id, @tenant_id, @entity_type, @entity_id, @action, @payload_json, @source_device_id, @source_user, @created_at);
""",
            ("@id", changeId),
            ("@tenant_id", tenantId),
            ("@entity_type", entityType),
            ("@entity_id", entityId),
            ("@action", action),
            ("@payload_json", payload),
            ("@source_device_id", NormalizeText(sourceDeviceId, string.Empty, 180)),
            ("@source_user", NormalizeText(sourceUser, string.Empty, 180)),
            ("@created_at", DbDate(now)));
        return changeId;
    }

    private static (string TenantId, string ApiTokenHash)? GetTenantByLicenseId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int licenseId)
    {
        using var command = CreateCommand(connection, transaction, """
SELECT tenant_id, api_token_hash
FROM st_tenants
WHERE license_id = @license_id
LIMIT 1;
""", ("@license_id", licenseId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (ReadString(reader, 0), ReadString(reader, 1)) : null;
    }

    private static SalonTenantContext? GetTenantById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId)
    {
        using var command = CreateCommand(connection, transaction, """
SELECT tenant_id, company_name, license_id
FROM st_tenants
WHERE tenant_id = @tenant_id AND is_active = 1
LIMIT 1;
""", ("@tenant_id", tenantId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SalonTenantContext(ReadString(reader, 0), ReadString(reader, 1), Convert.ToInt32(reader.GetValue(2)))
            : null;
    }

    private static SalonEntityItem? GetDataItemCore(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string entityType,
        string entityId)
    {
        using var command = CreateCommand(connection, transaction, """
SELECT entity_id, entity_type, payload_json, updated_at, version
FROM st_entity_data
WHERE tenant_id = @tenant_id AND entity_type = @entity_type AND entity_id = @entity_id
LIMIT 1;
""", ("@tenant_id", tenantId), ("@entity_type", entityType), ("@entity_id", entityId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new SalonEntityItem
            {
                Id = ReadString(reader, 0),
                EntityType = ReadString(reader, 1),
                PayloadJson = ReadString(reader, 2),
                UpdatedAt = ReadDate(reader, 3).ToLocalTime().ToString("O"),
                Version = reader.GetInt64(4)
            }
            : null;
    }

    private static IReadOnlyList<EntityDataRow> GetEntityRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string entityType)
    {
        var rows = new List<EntityDataRow>();
        using var command = CreateCommand(connection, transaction, """
SELECT entity_id, payload_json
FROM st_entity_data
WHERE tenant_id = @tenant_id AND entity_type = @entity_type;
""", ("@tenant_id", tenantId), ("@entity_type", entityType));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new EntityDataRow(ReadString(reader, 0), ReadString(reader, 1)));
        }

        return rows;
    }

    private static bool IsPendingWebCustomer(EntityDataRow row)
    {
        if (row.EntityId.StartsWith("WEB-CUSTOMER-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(ReadJsonText(row.PayloadJson, "CreatedFrom", "SyncOrigin"), "MOBILE-WEB", StringComparison.OrdinalIgnoreCase)
            && ReadJsonBoolean(row.PayloadJson, "PendingDesktopImport");
    }

    private static bool HasEquivalentIncomingCustomer(string pendingPayload, IReadOnlyList<SalonSnapshotItem> incoming)
    {
        var pendingPhone = NormalizePhone(ReadJsonText(pendingPayload, "Phone", "PhoneNumber", "Telephone"));
        var pendingEmail = ReadJsonText(pendingPayload, "Email", "EmailAddress").Trim().ToLowerInvariant();
        var pendingName = NormalizeCustomerName(ReadJsonText(pendingPayload, "FullName", "CustomerName", "Name"));
        var pendingCloudId = ReadJsonText(pendingPayload, "CloudEntityId", "ExternalId", "ClientReferenceId");

        foreach (var item in incoming)
        {
            var incomingCloudId = ReadJsonText(item.PayloadJson, "CloudEntityId", "ExternalId", "ClientReferenceId");
            if (!string.IsNullOrWhiteSpace(pendingCloudId)
                && string.Equals(pendingCloudId, incomingCloudId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var incomingPhone = NormalizePhone(ReadJsonText(item.PayloadJson, "Phone", "PhoneNumber", "Telephone"));
            if (!string.IsNullOrWhiteSpace(pendingPhone)
                && string.Equals(pendingPhone, incomingPhone, StringComparison.Ordinal))
            {
                return true;
            }

            var incomingEmail = ReadJsonText(item.PayloadJson, "Email", "EmailAddress").Trim().ToLowerInvariant();
            var incomingName = NormalizeCustomerName(ReadJsonText(item.PayloadJson, "FullName", "CustomerName", "Name"));
            if (!string.IsNullOrWhiteSpace(pendingEmail)
                && string.Equals(pendingEmail, incomingEmail, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pendingName)
                && string.Equals(pendingName, incomingName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadJsonText(string payloadJson, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || propertyNames.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!propertyNames.Any(x => string.Equals(x, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()?.Trim() ?? string.Empty,
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString().Trim(),
                    _ => string.Empty
                };
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }

    private static bool ReadJsonBoolean(string payloadJson, string propertyName)
    {
        var value = ReadJsonText(payloadJson, propertyName);
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string NormalizePhone(string value)
    {
        return new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
    }

    private static string NormalizeCustomerName(string value)
    {
        return string.Join(' ', (value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record EntityDataRow(string EntityId, string PayloadJson);

    private static void RevokePendingQrLogins(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        DateTime now)
    {
        ExecuteNonQuery(connection, transaction, """
UPDATE st_qr_logins
SET used_at = @used_at
WHERE tenant_id = @tenant_id AND used_at IS NULL;
""", ("@used_at", DbDate(now)), ("@tenant_id", tenantId));
    }

    private static void AddAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tenantId,
        string eventType,
        string detail)
    {
        ExecuteNonQuery(connection, transaction, """
INSERT INTO st_audit_log (tenant_id, event_type, detail_text, created_at)
VALUES (@tenant_id, @event_type, @detail_text, @created_at);
""",
            ("@tenant_id", tenantId),
            ("@event_type", NormalizeText(eventType, "Event", 80)),
            ("@detail_text", NormalizeText(detail, string.Empty, 2000)),
            ("@created_at", DbDate(DateTime.UtcNow)));
    }

    private static int ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 20;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }

        return command;
    }

    private static string ReadString(SqliteDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? string.Empty : reader.GetString(index).Trim();
    }

    private static DateTime ReadDate(SqliteDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return DateTime.MinValue;
        }

        var value = reader.GetString(index);
        return DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;
    }

    private static string DbDate(DateTime value)
    {
        return value.ToUniversalTime().ToString("O");
    }

    public static string NormalizeEntityType(string value)
    {
        var normalized = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            .Take(80)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException("Veri türü boş olamaz.");
        }

        return normalized switch
        {
            "rezervasyon" or "reservations" => "reservation",
            "musteri" or "müşteri" or "customers" => "customer",
            "salon" or "salons" or "halls" => "hall",
            "firma" or "companysettings" => "company",
            "odemeler" or "ödemeler" or "payments" => "payment",
            "finans" or "cari" => "finance",
            "giderler" or "expenses" => "expense",
            _ => normalized
        };
    }

    public static string NormalizeEntityId(string value, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var result = new string(source
            .Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            .Take(140)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? fallback : result;
    }

    public static string ValidatePayloadJson(string value)
    {
        var payload = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();
        if (Encoding.UTF8.GetByteCount(payload) > 524_288)
        {
            throw new InvalidOperationException("Tek kayıt 512 KB sınırını aşamaz.");
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array)
            {
                throw new InvalidOperationException("Payload JSON nesne veya dizi olmalıdır.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("PayloadJson geçerli JSON değil.");
        }

        return payload;
    }

    private static string NormalizeText(string value, string fallback, int maxLength)
    {
        var result = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        return result.Length <= maxLength ? result : result[..maxLength];
    }

    private static string BuildTenantId(int licenseId, string licenseKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"NSX-SALON-TAKIP|{licenseId}|{licenseKey.Trim().ToUpperInvariant()}"));
        return $"SALON-{licenseId}-{Convert.ToHexString(hash)[..16]}";
    }

    private static string CreateToken(int byteCount)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));
    }

    private static string HashText(string text)
    {
        return HashToken((text ?? string.Empty).Trim());
    }

    private static bool TokenMatches(string storedHash, string rawToken)
    {
        return FixedEquals(storedHash, HashToken(rawToken));
    }

    private static bool FixedEquals(string left, string right)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(left ?? string.Empty),
                Encoding.ASCII.GetBytes(right ?? string.Empty));
        }
        catch
        {
            return false;
        }
    }
}
