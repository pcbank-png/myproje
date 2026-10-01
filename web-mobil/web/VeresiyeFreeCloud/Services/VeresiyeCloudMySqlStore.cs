using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.VeresiyeFreeCloud.Models;

namespace NSYazilim.Web.VeresiyeFreeCloud.Services;

public sealed class VeresiyeCloudMySqlStore
{
    private static readonly object SchemaLock = new();
    private static bool _schemaReady;
    private readonly ApplicationDbContext _db;

    private static readonly HashSet<string> AllowedEntityTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "company", "customer", "debt", "payment", "expense", "stock", "stockmovement", "action", "part"
    };

    public VeresiyeCloudMySqlStore(ApplicationDbContext db) => _db = db;

    public void EnsureSchema()
    {
        if (_schemaReady) return;
        lock (SchemaLock)
        {
            if (_schemaReady) return;

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_tenants` (
    `tenant_id` varchar(96) NOT NULL,
    `company_name` varchar(220) NOT NULL DEFAULT '',
    `license_id` int NOT NULL,
    `license_key_hash` varchar(128) NOT NULL DEFAULT '',
    `api_token_hash` varchar(128) NOT NULL DEFAULT '',
    `desktop_machine_hash` varchar(128) NOT NULL DEFAULT '',
    `is_active` tinyint(1) NOT NULL DEFAULT 1,
    `created_at` datetime(6) NOT NULL,
    `last_seen_at` datetime(6) NULL,
    PRIMARY KEY (`tenant_id`),
    UNIQUE KEY `UX_vf_tenants_license_id` (`license_id`),
    KEY `IX_vf_tenants_active` (`is_active`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_qr_logins` (
    `token_hash` varchar(128) NOT NULL,
    `tenant_id` varchar(96) NOT NULL,
    `source_device_id` varchar(180) NOT NULL DEFAULT '',
    `source_user` varchar(160) NOT NULL DEFAULT '',
    `created_at` datetime(6) NOT NULL,
    `expires_at` datetime(6) NOT NULL,
    `used_at` datetime(6) NULL,
    PRIMARY KEY (`token_hash`),
    KEY `IX_vf_qr_tenant_expires` (`tenant_id`, `expires_at`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_mobile_sessions` (
    `session_hash` varchar(128) NOT NULL,
    `tenant_id` varchar(96) NOT NULL,
    `csrf_hash` varchar(128) NOT NULL,
    `created_at` datetime(6) NOT NULL,
    `expires_at` datetime(6) NOT NULL,
    `last_seen_at` datetime(6) NULL,
    `revoked_at` datetime(6) NULL,
    PRIMARY KEY (`session_hash`),
    KEY `IX_vf_mobile_tenant` (`tenant_id`, `expires_at`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_entity_data` (
    `tenant_id` varchar(96) NOT NULL,
    `entity_type` varchar(60) NOT NULL,
    `entity_id` varchar(96) NOT NULL,
    `company_cloud_id` varchar(96) NOT NULL DEFAULT '',
    `payload_json` longtext NOT NULL,
    `updated_at` datetime(6) NOT NULL,
    `version` bigint NOT NULL DEFAULT 1,
    PRIMARY KEY (`tenant_id`, `entity_type`, `entity_id`),
    KEY `IX_vf_entity_type` (`tenant_id`, `entity_type`),
    KEY `IX_vf_entity_company` (`tenant_id`, `company_cloud_id`, `entity_type`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_sync_changes` (
    `id` bigint NOT NULL AUTO_INCREMENT,
    `tenant_id` varchar(96) NOT NULL,
    `entity_type` varchar(60) NOT NULL,
    `entity_id` varchar(96) NOT NULL,
    `action` varchar(20) NOT NULL DEFAULT 'upsert',
    `payload_json` longtext NOT NULL,
    `source_device_id` varchar(180) NOT NULL DEFAULT '',
    `source_user` varchar(160) NOT NULL DEFAULT '',
    `created_at` datetime(6) NOT NULL,
    PRIMARY KEY (`id`),
    KEY `IX_vf_sync_tenant_id` (`tenant_id`, `id`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `vf_audit_log` (
    `id` bigint NOT NULL AUTO_INCREMENT,
    `tenant_id` varchar(96) NOT NULL,
    `event_type` varchar(80) NOT NULL,
    `detail_text` varchar(1000) NOT NULL DEFAULT '',
    `created_at` datetime(6) NOT NULL,
    PRIMARY KEY (`id`),
    KEY `IX_vf_audit_tenant` (`tenant_id`, `id`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            EnsureColumn("vf_tenants", "desktop_machine_hash", "varchar(128) NOT NULL DEFAULT ''");
            _schemaReady = true;
        }
    }

    public (VeresiyeTenantContext Tenant, string ApiToken) CreateOrRefreshTenant(
        int licenseId,
        string licenseKey,
        string companyName,
        string machineId,
        string currentApiToken)
    {
        EnsureSchema();
        var tenantId = BuildTenantId(licenseId);
        var existing = GetTenantById(tenantId);
        var canReuse = existing is not null && !string.IsNullOrWhiteSpace(currentApiToken) && ValidateTenant(tenantId, currentApiToken, machineId) is not null;
        var apiToken = canReuse ? currentApiToken.Trim() : CreateToken(42);
        var now = DateTime.Now;

        using var command = CreateCommand(@"
INSERT INTO `vf_tenants` (`tenant_id`,`company_name`,`license_id`,`license_key_hash`,`api_token_hash`,`desktop_machine_hash`,`is_active`,`created_at`,`last_seen_at`)
VALUES (@tenant_id,@company_name,@license_id,@license_key_hash,@api_token_hash,@desktop_machine_hash,1,@created_at,@last_seen_at)
ON DUPLICATE KEY UPDATE
    `tenant_id`=VALUES(`tenant_id`),
    `company_name`=VALUES(`company_name`),
    `license_key_hash`=VALUES(`license_key_hash`),
    `api_token_hash`=VALUES(`api_token_hash`),
    `desktop_machine_hash`=VALUES(`desktop_machine_hash`),
    `is_active`=1,
    `last_seen_at`=VALUES(`last_seen_at`);");
        Add(command, "@tenant_id", tenantId);
        Add(command, "@company_name", NormalizeText(companyName, "NSX Veresiye İşletmesi", 220));
        Add(command, "@license_id", licenseId);
        Add(command, "@license_key_hash", HashToken(licenseKey.Trim().ToUpperInvariant()));
        Add(command, "@api_token_hash", HashToken(apiToken));
        Add(command, "@desktop_machine_hash", HashToken(machineId.Trim()));
        Add(command, "@created_at", now);
        Add(command, "@last_seen_at", now);
        command.ExecuteNonQuery();

        var tenant = GetTenantById(tenantId) ?? throw new InvalidOperationException("Cloud işletmesi oluşturulamadı.");
        AddAudit(tenantId, canReuse ? "TenantRefreshed" : "TenantTokenCreated", "Masaüstü Cloud bağlantısı hazırlandı.");
        return (tenant, apiToken);
    }

    public VeresiyeTenantContext? ValidateTenant(string tenantId, string apiToken, string machineId)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(apiToken) || string.IsNullOrWhiteSpace(machineId)) return null;
        using var command = CreateCommand(@"
SELECT `tenant_id`,`company_name`,`license_id`
FROM `vf_tenants`
WHERE `tenant_id`=@tenant_id AND `api_token_hash`=@api_token_hash AND `desktop_machine_hash`=@desktop_machine_hash AND `is_active`=1
LIMIT 1;");
        Add(command, "@tenant_id", tenantId.Trim());
        Add(command, "@api_token_hash", HashToken(apiToken.Trim()));
        Add(command, "@desktop_machine_hash", HashToken(machineId.Trim()));
        VeresiyeTenantContext? result = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read()) result = new VeresiyeTenantContext(ReadString(reader, 0), ReadString(reader, 1), Convert.ToInt32(reader.GetValue(2)));
        }
        if (result is not null)
        {
            using var touch = CreateCommand("UPDATE `vf_tenants` SET `last_seen_at`=@now WHERE `tenant_id`=@tenant_id;");
            Add(touch, "@now", DateTime.Now); Add(touch, "@tenant_id", result.TenantId); touch.ExecuteNonQuery();
        }
        return result;
    }

    public (string Token, DateTime ExpiresAt) CreateQrLogin(string tenantId, string sourceDeviceId, string sourceUser)
    {
        EnsureSchema();
        var token = CreateToken(36);
        var now = DateTime.Now;
        var expires = now.AddMinutes(3);
        using (var cleanup = CreateCommand("DELETE FROM `vf_qr_logins` WHERE `expires_at` < @now OR (`used_at` IS NOT NULL AND `used_at` < @old);") )
        { Add(cleanup, "@now", now); Add(cleanup, "@old", now.AddDays(-1)); cleanup.ExecuteNonQuery(); }
        using var command = CreateCommand(@"
INSERT INTO `vf_qr_logins` (`token_hash`,`tenant_id`,`source_device_id`,`source_user`,`created_at`,`expires_at`,`used_at`)
VALUES (@token_hash,@tenant_id,@source_device_id,@source_user,@created_at,@expires_at,NULL);");
        Add(command, "@token_hash", HashToken(token)); Add(command, "@tenant_id", tenantId);
        Add(command, "@source_device_id", NormalizeText(sourceDeviceId, "DESKTOP", 180));
        Add(command, "@source_user", NormalizeText(sourceUser, "Masaüstü", 160));
        Add(command, "@created_at", now); Add(command, "@expires_at", expires); command.ExecuteNonQuery();
        AddAudit(tenantId, "QrCreated", "Cep telefonu bağlantı QR kodu üretildi.");
        return (token, expires);
    }

    public VeresiyeTenantContext? ConsumeQrLogin(string rawToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(rawToken)) return null;
        var hash = HashToken(rawToken.Trim());
        var now = DateTime.Now;
        string tenantId = string.Empty;
        using (var command = CreateCommand(@"
SELECT `tenant_id` FROM `vf_qr_logins`
WHERE `token_hash`=@token_hash AND `used_at` IS NULL AND `expires_at` >= @now LIMIT 1;"))
        {
            Add(command, "@token_hash", hash); Add(command, "@now", now);
            var value = command.ExecuteScalar(); tenantId = value?.ToString() ?? string.Empty;
        }
        if (string.IsNullOrWhiteSpace(tenantId)) return null;
        using (var update = CreateCommand(@"
UPDATE `vf_qr_logins` SET `used_at`=@now
WHERE `token_hash`=@token_hash AND `used_at` IS NULL AND `expires_at` >= @now;"))
        { Add(update, "@now", now); Add(update, "@token_hash", hash); if (update.ExecuteNonQuery() != 1) return null; }
        AddAudit(tenantId, "QrConsumed", "QR bağlantısı telefon tarafından kullanıldı.");
        return GetTenantById(tenantId);
    }

    public (string SessionToken, string CsrfToken, DateTime ExpiresAt) CreateMobileSession(string tenantId)
    {
        EnsureSchema();
        var session = CreateToken(48); var csrf = CreateToken(32); var now = DateTime.Now; var expires = now.AddDays(30);
        using var command = CreateCommand(@"
INSERT INTO `vf_mobile_sessions` (`session_hash`,`tenant_id`,`csrf_hash`,`created_at`,`expires_at`,`last_seen_at`,`revoked_at`)
VALUES (@session_hash,@tenant_id,@csrf_hash,@created_at,@expires_at,@last_seen_at,NULL);");
        Add(command, "@session_hash", HashToken(session)); Add(command, "@tenant_id", tenantId); Add(command, "@csrf_hash", HashToken(csrf));
        Add(command, "@created_at", now); Add(command, "@expires_at", expires); Add(command, "@last_seen_at", now); command.ExecuteNonQuery();
        AddAudit(tenantId, "MobileSessionCreated", "Mobil yönetim oturumu açıldı.");
        return (session, csrf, expires);
    }

    public VeresiyeMobileSessionContext? ValidateMobileSession(string sessionToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        var now = DateTime.Now;
        using var command = CreateCommand(@"
SELECT s.`tenant_id`,t.`company_name`,s.`session_hash`,s.`csrf_hash`,s.`expires_at`
FROM `vf_mobile_sessions` s
INNER JOIN `vf_tenants` t ON t.`tenant_id`=s.`tenant_id`
WHERE s.`session_hash`=@session_hash AND s.`revoked_at` IS NULL AND s.`expires_at`>=@now AND t.`is_active`=1
LIMIT 1;");
        Add(command, "@session_hash", HashToken(sessionToken.Trim())); Add(command, "@now", now);
        VeresiyeMobileSessionContext? result = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read()) result = new VeresiyeMobileSessionContext(ReadString(reader, 0), ReadString(reader, 1), ReadString(reader, 2), ReadString(reader, 3), Convert.ToDateTime(reader.GetValue(4)));
        }
        if (result is not null)
        {
            using var touch = CreateCommand("UPDATE `vf_mobile_sessions` SET `last_seen_at`=@now WHERE `session_hash`=@session_hash;");
            Add(touch, "@now", now); Add(touch, "@session_hash", result.SessionHash); touch.ExecuteNonQuery();
        }
        return result;
    }

    public bool ValidateCsrf(VeresiyeMobileSessionContext session, string csrfToken) =>
        session is not null && !string.IsNullOrWhiteSpace(csrfToken) && FixedEquals(session.CsrfHash, HashToken(csrfToken.Trim()));

    public void RevokeAllMobileSessions(string tenantId)
    {
        EnsureSchema();
        using var command = CreateCommand("UPDATE `vf_mobile_sessions` SET `revoked_at`=@now WHERE `tenant_id`=@tenant_id AND `revoked_at` IS NULL;");
        Add(command, "@now", DateTime.Now); Add(command, "@tenant_id", tenantId); command.ExecuteNonQuery();
        AddAudit(tenantId, "MobileSessionsRevoked", "Tüm mobil oturumlar masaüstünden kapatıldı.");
    }

    public void RevokeMobileSession(string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return;
        EnsureSchema();
        using var command = CreateCommand("UPDATE `vf_mobile_sessions` SET `revoked_at`=@now WHERE `session_hash`=@session_hash;");
        Add(command, "@now", DateTime.Now); Add(command, "@session_hash", HashToken(sessionToken.Trim())); command.ExecuteNonQuery();
    }

    public IReadOnlyList<VeresiyeEntityItem> GetDataItems(string tenantId, string entityType, int take = 10000)
    {
        EnsureSchema();
        entityType = NormalizeEntityType(entityType); take = Math.Clamp(take, 1, 20000);
        var list = new List<VeresiyeEntityItem>();
        using var command = CreateCommand(@"
SELECT `entity_id`,`entity_type`,`company_cloud_id`,`payload_json`,`updated_at`,`version`
FROM `vf_entity_data`
WHERE `tenant_id`=@tenant_id AND `entity_type`=@entity_type
ORDER BY `updated_at` DESC LIMIT " + take + ";");
        Add(command, "@tenant_id", tenantId); Add(command, "@entity_type", entityType);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new VeresiyeEntityItem
            {
                Id = ReadString(reader, 0), EntityType = ReadString(reader, 1), CompanyCloudId = ReadString(reader, 2), PayloadJson = ReadString(reader, 3),
                UpdatedAt = Convert.ToDateTime(reader.GetValue(4)).ToString("O"), Version = Convert.ToInt64(reader.GetValue(5))
            });
        }
        return list;
    }

    public long UpsertDataItem(string tenantId, string entityType, string entityId, VeresiyeEntityUpsertRequest request, bool appendChange = true)
    {
        EnsureSchema();
        entityType = NormalizeEntityType(entityType); entityId = NormalizeEntityId(entityId); request ??= new VeresiyeEntityUpsertRequest();
        var payload = ValidatePayloadJson(request.PayloadJson); var companyCloudId = ExtractCompanyCloudId(entityType, entityId, payload); var now = DateTime.Now;
        using (var command = CreateCommand(@"
INSERT INTO `vf_entity_data` (`tenant_id`,`entity_type`,`entity_id`,`company_cloud_id`,`payload_json`,`updated_at`,`version`)
VALUES (@tenant_id,@entity_type,@entity_id,@company_cloud_id,@payload_json,@updated_at,1)
ON DUPLICATE KEY UPDATE
 `company_cloud_id`=VALUES(`company_cloud_id`),`payload_json`=VALUES(`payload_json`),`updated_at`=VALUES(`updated_at`),`version`=`version`+1;"))
        {
            Add(command, "@tenant_id", tenantId); Add(command, "@entity_type", entityType); Add(command, "@entity_id", entityId);
            Add(command, "@company_cloud_id", companyCloudId); Add(command, "@payload_json", payload); Add(command, "@updated_at", now); command.ExecuteNonQuery();
        }
        return appendChange ? AppendChange(tenantId, entityType, entityId, "upsert", payload, request.SourceDeviceId, request.SourceUser) : GetLastChangeId(tenantId);
    }

    public long DeleteDataItem(string tenantId, string entityType, string entityId, string sourceDeviceId, string sourceUser, bool appendChange = true)
    {
        EnsureSchema(); entityType = NormalizeEntityType(entityType); entityId = NormalizeEntityId(entityId);
        using (var command = CreateCommand("DELETE FROM `vf_entity_data` WHERE `tenant_id`=@tenant_id AND `entity_type`=@entity_type AND `entity_id`=@entity_id;"))
        { Add(command, "@tenant_id", tenantId); Add(command, "@entity_type", entityType); Add(command, "@entity_id", entityId); command.ExecuteNonQuery(); }
        return appendChange ? AppendChange(tenantId, entityType, entityId, "delete", "{}", sourceDeviceId, sourceUser) : GetLastChangeId(tenantId);
    }

    public VeresiyeSnapshotResponse ApplySnapshot(string tenantId, VeresiyeSnapshotRequest request)
    {
        EnsureSchema();
        request ??= new VeresiyeSnapshotRequest();
        var entityType = NormalizeEntityType(request.EntityType);
        var incoming = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existing = GetDataItems(tenantId, entityType, 20000)
            .ToDictionary(x => x.Id, x => x.PayloadJson, StringComparer.OrdinalIgnoreCase);
        var saved = 0;

        foreach (var item in request.Items ?? new List<VeresiyeSnapshotItem>())
        {
            var id = NormalizeEntityId(item.Id);
            if (!incoming.Add(id)) continue;

            var payload = ValidatePayloadJson(item.PayloadJson);
            if (existing.TryGetValue(id, out var currentPayload) && string.Equals(currentPayload, payload, StringComparison.Ordinal))
                continue;

            UpsertDataItem(tenantId, entityType, id, new VeresiyeEntityUpsertRequest
            {
                PayloadJson = payload,
                SourceDeviceId = request.SourceDeviceId,
                SourceUser = request.SourceUser
            }, appendChange: true);
            saved++;
        }

        var deleted = 0;
        if (request.ReplaceExisting)
        {
            foreach (var existingId in existing.Keys.Where(x => !incoming.Contains(x)).ToList())
            {
                DeleteDataItem(tenantId, entityType, existingId, request.SourceDeviceId, request.SourceUser, appendChange: true);
                deleted++;
            }
        }

        return new VeresiyeSnapshotResponse
        {
            Success = true,
            EntityType = entityType,
            SavedCount = saved,
            DeletedCount = deleted,
            LastChangeId = GetLastChangeId(tenantId)
        };
    }

    public IReadOnlyList<VeresiyeSyncChangeResponse> GetChanges(string tenantId, long afterId, int take)
    {
        EnsureSchema(); take = Math.Clamp(take, 1, 1000); var list = new List<VeresiyeSyncChangeResponse>();
        using var command = CreateCommand(@"
SELECT `id`,`entity_type`,`entity_id`,`action`,`payload_json`,`source_device_id`,`source_user`,`created_at`
FROM `vf_sync_changes` WHERE `tenant_id`=@tenant_id AND `id`>@after_id ORDER BY `id` LIMIT " + take + ";");
        Add(command, "@tenant_id", tenantId); Add(command, "@after_id", Math.Max(0, afterId));
        using var reader = command.ExecuteReader();
        while (reader.Read()) list.Add(new VeresiyeSyncChangeResponse { ChangeId = Convert.ToInt64(reader.GetValue(0)), EntityType = ReadString(reader,1), EntityId = ReadString(reader,2), Action = ReadString(reader,3), PayloadJson = ReadString(reader,4), SourceDeviceId = ReadString(reader,5), SourceUser = ReadString(reader,6), CreatedAt = Convert.ToDateTime(reader.GetValue(7)).ToString("O") });
        return list;
    }

    public long GetLastChangeId(string tenantId)
    {
        EnsureSchema(); using var command = CreateCommand("SELECT COALESCE(MAX(`id`),0) FROM `vf_sync_changes` WHERE `tenant_id`=@tenant_id;"); Add(command, "@tenant_id", tenantId); return Convert.ToInt64(command.ExecuteScalar());
    }

    public IReadOnlyDictionary<string,int> GetEntityCounts(string tenantId)
    {
        EnsureSchema(); var result = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        using var command = CreateCommand("SELECT `entity_type`,COUNT(1) FROM `vf_entity_data` WHERE `tenant_id`=@tenant_id GROUP BY `entity_type`;"); Add(command, "@tenant_id", tenantId);
        using var reader = command.ExecuteReader(); while (reader.Read()) result[ReadString(reader,0)] = Convert.ToInt32(reader.GetValue(1)); return result;
    }

    private long AppendChange(string tenantId, string entityType, string entityId, string action, string payload, string sourceDeviceId, string sourceUser)
    {
        using (var command = CreateCommand(@"
INSERT INTO `vf_sync_changes` (`tenant_id`,`entity_type`,`entity_id`,`action`,`payload_json`,`source_device_id`,`source_user`,`created_at`)
VALUES (@tenant_id,@entity_type,@entity_id,@action,@payload_json,@source_device_id,@source_user,@created_at);"))
        {
            Add(command, "@tenant_id", tenantId); Add(command, "@entity_type", entityType); Add(command, "@entity_id", entityId); Add(command, "@action", action);
            Add(command, "@payload_json", payload); Add(command, "@source_device_id", NormalizeText(sourceDeviceId, "MOBILE-WEB", 180)); Add(command, "@source_user", NormalizeText(sourceUser, "Mobil Panel", 160)); Add(command, "@created_at", DateTime.Now);
            command.ExecuteNonQuery();
        }
        using var idCommand = CreateCommand("SELECT LAST_INSERT_ID();");
        return Convert.ToInt64(idCommand.ExecuteScalar());
    }

    private VeresiyeTenantContext? GetTenantById(string tenantId)
    {
        EnsureSchema(); using var command = CreateCommand("SELECT `tenant_id`,`company_name`,`license_id` FROM `vf_tenants` WHERE `tenant_id`=@tenant_id AND `is_active`=1 LIMIT 1;"); Add(command, "@tenant_id", tenantId);
        using var reader = command.ExecuteReader(); return reader.Read() ? new VeresiyeTenantContext(ReadString(reader,0), ReadString(reader,1), Convert.ToInt32(reader.GetValue(2))) : null;
    }

    private void AddAudit(string tenantId, string eventType, string detail)
    {
        try { using var command = CreateCommand("INSERT INTO `vf_audit_log` (`tenant_id`,`event_type`,`detail_text`,`created_at`) VALUES (@tenant_id,@event_type,@detail_text,@created_at);"); Add(command,"@tenant_id",tenantId); Add(command,"@event_type",eventType); Add(command,"@detail_text",NormalizeText(detail,"",1000)); Add(command,"@created_at",DateTime.Now); command.ExecuteNonQuery(); } catch { }
    }

    private void EnsureColumn(string tableName, string columnName, string definition)
    {
        using var check = CreateCommand(@"
SELECT COUNT(1) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table_name AND COLUMN_NAME = @column_name;");
        Add(check, "@table_name", tableName);
        Add(check, "@column_name", columnName);
        var exists = Convert.ToInt32(check.ExecuteScalar()) > 0;
        if (exists) return;

        if (!tableName.All(ch => char.IsLetterOrDigit(ch) || ch == '_') || !columnName.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
            throw new InvalidOperationException("Cloud şema adı geçersiz.");
        ExecuteNonQuery($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definition};");
    }

    private DbCommand CreateCommand(string sql)
    {
        var connection = _db.Database.GetDbConnection(); if (connection.State != ConnectionState.Open) connection.Open(); var command = connection.CreateCommand(); command.CommandText = sql; return command;
    }
    private void ExecuteNonQuery(string sql) { using var command = CreateCommand(sql); command.ExecuteNonQuery(); }
    private static void Add(DbCommand command, string name, object? value) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; command.Parameters.Add(p); }
    private static string ReadString(DbDataReader reader, int index) => reader.IsDBNull(index) ? string.Empty : reader.GetValue(index)?.ToString() ?? string.Empty;

    public static string NormalizeEntityType(string value)
    {
        var clean = (value ?? string.Empty).Trim().ToLowerInvariant(); if (!AllowedEntityTypes.Contains(clean)) throw new InvalidOperationException("Desteklenmeyen Cloud veri tipi."); return clean;
    }
    private static string NormalizeEntityId(string value) { var clean = (value ?? string.Empty).Trim(); if (clean.Length < 8 || clean.Length > 96) throw new InvalidOperationException("Cloud kayıt kimliği geçersiz."); return clean; }
    private static string ValidatePayloadJson(string value) { value = string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim(); using var _ = JsonDocument.Parse(value); if (Encoding.UTF8.GetByteCount(value) > 512_000) throw new InvalidOperationException("Cloud kayıt boyutu çok büyük."); return value; }
    private static string ExtractCompanyCloudId(string entityType, string entityId, string payload)
    {
        if (entityType.Equals("company", StringComparison.OrdinalIgnoreCase)) return entityId;
        try { using var doc = JsonDocument.Parse(payload); foreach (var p in doc.RootElement.EnumerateObject()) if (p.Name.Equals("CompanyCloudId", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("companyCloudId", StringComparison.OrdinalIgnoreCase)) return (p.Value.GetString() ?? string.Empty).Trim(); } catch { }
        return string.Empty;
    }
    private static string NormalizeText(string value, string fallback, int max) { var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return text.Length <= max ? text : text[..max]; }
    private static string BuildTenantId(int licenseId) => $"VF-{licenseId:D10}";
    private static string CreateToken(int byteCount) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount)).TrimEnd('=').Replace('+','-').Replace('/','_');
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) { var a = Encoding.UTF8.GetBytes(left ?? ""); var b = Encoding.UTF8.GetBytes(right ?? ""); return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a,b); }
}
