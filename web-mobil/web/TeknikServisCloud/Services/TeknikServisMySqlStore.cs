using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;
using NSYazilim.Web.TeknikServisCloud.Models;

namespace NSYazilim.Web.TeknikServisCloud.Services;

public sealed class TeknikServisMySqlStore
{
    private static readonly object SchemaLock = new();
    private static bool _schemaReady;

    private readonly ApplicationDbContext _db;

    public TeknikServisMySqlStore(ApplicationDbContext db)
    {
        _db = db;
    }

    public void EnsureSchema()
    {
        if (_schemaReady)
        {
            return;
        }

        lock (SchemaLock)
        {
            if (_schemaReady)
            {
                return;
            }

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `ts_tenants` (
    `firma_id` varchar(80) NOT NULL,
    `firma_kodu` varchar(80) NOT NULL DEFAULT '',
    `firma_adi` varchar(180) NOT NULL DEFAULT '',
    `lisans_key` varchar(180) NOT NULL DEFAULT '',
    `api_token_hash` varchar(128) NOT NULL DEFAULT '',
    `is_active` tinyint(1) NOT NULL DEFAULT 1,
    `created_at` datetime(6) NOT NULL,
    `last_seen_at` datetime(6) NULL,
    PRIMARY KEY (`firma_id`),
    KEY `IX_ts_tenants_firma_kodu` (`firma_kodu`),
    KEY `IX_ts_tenants_active` (`is_active`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `ts_tenant_data` (
    `firma_id` varchar(80) NOT NULL,
    `entity_type` varchar(100) NOT NULL,
    `entity_id` varchar(120) NOT NULL,
    `payload_json` longtext NOT NULL,
    `updated_at` datetime(6) NOT NULL,
    PRIMARY KEY (`firma_id`, `entity_type`, `entity_id`),
    KEY `IX_ts_tenant_data_type` (`firma_id`, `entity_type`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `ts_sync_changes` (
    `id` bigint NOT NULL AUTO_INCREMENT,
    `firma_id` varchar(80) NOT NULL,
    `entity_type` varchar(100) NOT NULL,
    `entity_id` varchar(120) NOT NULL,
    `action` varchar(40) NOT NULL DEFAULT 'upsert',
    `payload_json` longtext NOT NULL,
    `source_device_id` varchar(160) NOT NULL DEFAULT '',
    `source_user` varchar(160) NOT NULL DEFAULT '',
    `created_at` datetime(6) NOT NULL,
    PRIMARY KEY (`id`),
    KEY `IX_ts_sync_changes_firma_id` (`firma_id`, `id`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `ts_service_tracking_records` (
    `tracking_token` varchar(160) NOT NULL,
    `firma_id` varchar(80) NOT NULL,
    `company_name` varchar(220) NOT NULL DEFAULT '',
    `service_no` varchar(120) NOT NULL DEFAULT '',
    `customer_name` varchar(220) NOT NULL DEFAULT '',
    `phone_masked` varchar(80) NOT NULL DEFAULT '',
    `device_title` varchar(260) NOT NULL DEFAULT '',
    `status` varchar(180) NOT NULL DEFAULT '',
    `price_text` varchar(180) NOT NULL DEFAULT '',
    `public_note` text NULL,
    `payload_json` longtext NULL,
    `updated_at` datetime(6) NOT NULL,
    PRIMARY KEY (`tracking_token`),
    KEY `IX_ts_service_tracking_firma_id` (`firma_id`),
    KEY `IX_ts_service_tracking_service_no` (`service_no`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            ExecuteNonQuery(@"
CREATE TABLE IF NOT EXISTS `ts_customer_messages` (
    `id` bigint NOT NULL AUTO_INCREMENT,
    `firma_id` varchar(80) NOT NULL,
    `tracking_token` varchar(160) NOT NULL,
    `service_no` varchar(120) NOT NULL DEFAULT '',
    `message_type` varchar(40) NOT NULL DEFAULT 'Bilgi',
    `message_text` text NOT NULL,
    `customer_name` varchar(220) NOT NULL DEFAULT '',
    `customer_phone` varchar(80) NOT NULL DEFAULT '',
    `action_context_key` varchar(600) NOT NULL DEFAULT '',
    `created_at` datetime(6) NOT NULL,
    `is_pulled` tinyint(1) NOT NULL DEFAULT 0,
    `is_deleted` tinyint(1) NOT NULL DEFAULT 0,
    PRIMARY KEY (`id`),
    KEY `IX_ts_customer_messages_firma` (`firma_id`, `id`),
    KEY `IX_ts_customer_messages_tracking` (`tracking_token`)
) CHARACTER SET=utf8mb4 COLLATE=utf8mb4_unicode_ci;");

            _schemaReady = true;
        }
    }

    public bool HasAnyTenant()
    {
        EnsureSchema();
        using var command = CreateCommand("SELECT COUNT(1) FROM `ts_tenants`;");
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public bool TenantExists(string firmaId)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(firmaId))
        {
            return false;
        }

        using var command = CreateCommand("SELECT COUNT(1) FROM `ts_tenants` WHERE `firma_id` = @firma_id;");
        Add(command, "@firma_id", firmaId.Trim());
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    public TenantCreateResponse CreateTenant(TenantCreateRequest request)
    {
        EnsureSchema();

        request ??= new TenantCreateRequest();
        var firmaId = NormalizeKey(request.FirmaId, "FIRMA-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        var firmaKodu = NormalizeKey(request.FirmaKodu, firmaId);
        var firmaAdi = string.IsNullOrWhiteSpace(request.FirmaAdi) ? firmaKodu : request.FirmaAdi.Trim();
        var apiToken = string.IsNullOrWhiteSpace(request.ApiToken) ? CreateToken() : request.ApiToken.Trim();
        var now = DateTime.Now;

        using var command = CreateCommand(@"
INSERT INTO `ts_tenants` (`firma_id`, `firma_kodu`, `firma_adi`, `lisans_key`, `api_token_hash`, `is_active`, `created_at`, `last_seen_at`)
VALUES (@firma_id, @firma_kodu, @firma_adi, @lisans_key, @api_token_hash, 1, @created_at, @last_seen_at)
ON DUPLICATE KEY UPDATE
    `firma_kodu` = VALUES(`firma_kodu`),
    `firma_adi` = VALUES(`firma_adi`),
    `lisans_key` = VALUES(`lisans_key`),
    `api_token_hash` = VALUES(`api_token_hash`),
    `is_active` = 1,
    `last_seen_at` = VALUES(`last_seen_at`);");
        Add(command, "@firma_id", firmaId);
        Add(command, "@firma_kodu", firmaKodu);
        Add(command, "@firma_adi", firmaAdi);
        Add(command, "@lisans_key", request.LisansKey?.Trim() ?? string.Empty);
        Add(command, "@api_token_hash", HashToken(apiToken));
        Add(command, "@created_at", now);
        Add(command, "@last_seen_at", now);
        command.ExecuteNonQuery();

        return new TenantCreateResponse
        {
            FirmaId = firmaId,
            FirmaKodu = firmaKodu,
            FirmaAdi = firmaAdi,
            ApiToken = apiToken,
            Message = "Firma cloud bağlantısı hazırlandı."
        };
    }

    public TenantContext? ValidateTenant(string firmaId, string apiToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(firmaId) || string.IsNullOrWhiteSpace(apiToken))
        {
            return null;
        }

        TenantContext? tenant = null;
        using (var command = CreateCommand(@"
SELECT `firma_id`, `firma_kodu`, `firma_adi`
FROM `ts_tenants`
WHERE `firma_id` = @firma_id AND `api_token_hash` = @api_token_hash AND `is_active` = 1
LIMIT 1;"))
        {
            Add(command, "@firma_id", firmaId.Trim());
            Add(command, "@api_token_hash", HashToken(apiToken.Trim()));

            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                tenant = new TenantContext(ReadString(reader, 0), ReadString(reader, 1), ReadString(reader, 2));
            }
        }

        // MySqlConnector aynı bağlantı üzerinde açık DataReader/Command varken ikinci komut çalıştırılmasına izin vermez.
        // Bu yüzden last_seen güncellemesi reader ve command kapatıldıktan sonra yapılır.
        if (tenant is not null)
        {
            TouchTenant(tenant.FirmaId);
        }

        return tenant;
    }

    public IReadOnlyList<TenantDataItem> GetDataItems(string firmaId, string entityType)
    {
        EnsureSchema();
        var items = new List<TenantDataItem>();
        using var command = CreateCommand(@"
SELECT `entity_id`, `entity_type`, `payload_json`, `updated_at`
FROM `ts_tenant_data`
WHERE `firma_id` = @firma_id AND `entity_type` = @entity_type
ORDER BY `updated_at` DESC;");
        Add(command, "@firma_id", firmaId);
        Add(command, "@entity_type", NormalizeEntity(entityType));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new TenantDataItem
            {
                Id = ReadString(reader, 0),
                EntityType = ReadString(reader, 1),
                PayloadJson = ReadString(reader, 2),
                UpdatedAt = ReadDate(reader, 3).ToString("O")
            });
        }

        return items;
    }

    public long UpsertDataItem(string firmaId, string entityType, string entityId, TenantDataUpsertRequest request)
    {
        EnsureSchema();
        entityType = NormalizeEntity(entityType);
        entityId = NormalizeKey(entityId, Guid.NewGuid().ToString("N"));
        var now = DateTime.Now;

        using (var command = CreateCommand(@"
INSERT INTO `ts_tenant_data` (`firma_id`, `entity_type`, `entity_id`, `payload_json`, `updated_at`)
VALUES (@firma_id, @entity_type, @entity_id, @payload_json, @updated_at)
ON DUPLICATE KEY UPDATE
    `payload_json` = VALUES(`payload_json`),
    `updated_at` = VALUES(`updated_at`);") )
        {
            Add(command, "@firma_id", firmaId);
            Add(command, "@entity_type", entityType);
            Add(command, "@entity_id", entityId);
            Add(command, "@payload_json", request?.PayloadJson ?? string.Empty);
            Add(command, "@updated_at", now);
            command.ExecuteNonQuery();
        }

        return AppendSyncChange(firmaId, new SyncChangeRequest
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = "upsert",
            PayloadJson = request?.PayloadJson ?? string.Empty,
            SourceDeviceId = request?.SourceDeviceId ?? string.Empty,
            SourceUser = request?.SourceUser ?? string.Empty
        });
    }

    public long DeleteDataItem(string firmaId, string entityType, string entityId, string sourceDeviceId, string sourceUser)
    {
        EnsureSchema();
        entityType = NormalizeEntity(entityType);
        entityId = NormalizeKey(entityId, string.Empty);

        using (var command = CreateCommand(@"DELETE FROM `ts_tenant_data` WHERE `firma_id` = @firma_id AND `entity_type` = @entity_type AND `entity_id` = @entity_id;"))
        {
            Add(command, "@firma_id", firmaId);
            Add(command, "@entity_type", entityType);
            Add(command, "@entity_id", entityId);
            command.ExecuteNonQuery();
        }

        return AppendSyncChange(firmaId, new SyncChangeRequest
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = "delete",
            PayloadJson = string.Empty,
            SourceDeviceId = sourceDeviceId,
            SourceUser = sourceUser
        });
    }

    public long AppendSyncChange(string firmaId, SyncChangeRequest request)
    {
        EnsureSchema();
        request ??= new SyncChangeRequest();
        var now = DateTime.Now;
        using (var command = CreateCommand(@"
INSERT INTO `ts_sync_changes` (`firma_id`, `entity_type`, `entity_id`, `action`, `payload_json`, `source_device_id`, `source_user`, `created_at`)
VALUES (@firma_id, @entity_type, @entity_id, @action, @payload_json, @source_device_id, @source_user, @created_at);"))
        {
            Add(command, "@firma_id", firmaId);
            Add(command, "@entity_type", NormalizeEntity(request.EntityType));
            Add(command, "@entity_id", NormalizeKey(request.EntityId, Guid.NewGuid().ToString("N")));
            Add(command, "@action", string.IsNullOrWhiteSpace(request.Action) ? "upsert" : request.Action.Trim());
            Add(command, "@payload_json", request.PayloadJson ?? string.Empty);
            Add(command, "@source_device_id", request.SourceDeviceId ?? string.Empty);
            Add(command, "@source_user", request.SourceUser ?? string.Empty);
            Add(command, "@created_at", now);
            command.ExecuteNonQuery();
        }

        using var idCommand = CreateCommand("SELECT LAST_INSERT_ID();");
        return Convert.ToInt64(idCommand.ExecuteScalar());
    }

    public IReadOnlyList<SyncChangeResponse> GetChanges(string firmaId, long afterId, int take)
    {
        EnsureSchema();
        take = Math.Clamp(take, 1, 500);
        var items = new List<SyncChangeResponse>();
        using var command = CreateCommand(@"
SELECT `id`, `firma_id`, `entity_type`, `entity_id`, `action`, `payload_json`, `source_device_id`, `source_user`, `created_at`
FROM `ts_sync_changes`
WHERE `firma_id` = @firma_id AND `id` > @after_id
ORDER BY `id` ASC
LIMIT " + take + ";");
        Add(command, "@firma_id", firmaId);
        Add(command, "@after_id", afterId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new SyncChangeResponse
            {
                ChangeId = Convert.ToInt64(reader.GetValue(0)),
                FirmaId = ReadString(reader, 1),
                EntityType = ReadString(reader, 2),
                EntityId = ReadString(reader, 3),
                Action = ReadString(reader, 4),
                PayloadJson = ReadString(reader, 5),
                SourceDeviceId = ReadString(reader, 6),
                SourceUser = ReadString(reader, 7),
                CreatedAt = new DateTimeOffset(ReadDate(reader, 8))
            });
        }

        return items;
    }

    public TrackingRecordResponse PublishTracking(string firmaId, TrackingPublishRequest request)
    {
        EnsureSchema();
        request ??= new TrackingPublishRequest();
        var trackingToken = NormalizeKey(request.TrackingToken, string.Empty);
        if (string.IsNullOrWhiteSpace(trackingToken))
        {
            throw new InvalidOperationException("TrackingToken boş olamaz.");
        }

        var serviceNo = NormalizeKey(request.ServiceNo, trackingToken);
        var now = DateTime.Now;

        using (var command = CreateCommand(@"
INSERT INTO `ts_service_tracking_records`
(`tracking_token`, `firma_id`, `company_name`, `service_no`, `customer_name`, `phone_masked`, `device_title`, `status`, `price_text`, `public_note`, `payload_json`, `updated_at`)
VALUES
(@tracking_token, @firma_id, @company_name, @service_no, @customer_name, @phone_masked, @device_title, @status, @price_text, @public_note, @payload_json, @updated_at)
ON DUPLICATE KEY UPDATE
    `firma_id` = VALUES(`firma_id`),
    `company_name` = VALUES(`company_name`),
    `service_no` = VALUES(`service_no`),
    `customer_name` = VALUES(`customer_name`),
    `phone_masked` = VALUES(`phone_masked`),
    `device_title` = VALUES(`device_title`),
    `status` = VALUES(`status`),
    `price_text` = VALUES(`price_text`),
    `public_note` = VALUES(`public_note`),
    `payload_json` = VALUES(`payload_json`),
    `updated_at` = VALUES(`updated_at`);") )
        {
            Add(command, "@tracking_token", trackingToken);
            Add(command, "@firma_id", firmaId);
            Add(command, "@company_name", request.CompanyName ?? string.Empty);
            Add(command, "@service_no", serviceNo);
            Add(command, "@customer_name", request.CustomerName ?? string.Empty);
            Add(command, "@phone_masked", request.PhoneMasked ?? string.Empty);
            Add(command, "@device_title", request.DeviceTitle ?? string.Empty);
            Add(command, "@status", request.Status ?? string.Empty);
            Add(command, "@price_text", request.PriceText ?? string.Empty);
            Add(command, "@public_note", request.PublicNote ?? string.Empty);
            Add(command, "@payload_json", request.PayloadJson ?? string.Empty);
            Add(command, "@updated_at", now);
            command.ExecuteNonQuery();
        }

        return GetTrackingByToken(trackingToken) ?? new TrackingRecordResponse
        {
            TrackingToken = trackingToken,
            FirmaId = firmaId,
            ServiceNo = serviceNo,
            CompanyName = request.CompanyName ?? string.Empty,
            CustomerName = request.CustomerName ?? string.Empty,
            PhoneMasked = request.PhoneMasked ?? string.Empty,
            DeviceTitle = request.DeviceTitle ?? string.Empty,
            Status = request.Status ?? string.Empty,
            PriceText = request.PriceText ?? string.Empty,
            PublicNote = request.PublicNote ?? string.Empty,
            PayloadJson = request.PayloadJson ?? string.Empty,
            UpdatedAt = now.ToString("O")
        };
    }

    public TrackingRecordResponse? GetTrackingByToken(string trackingToken)
    {
        EnsureSchema();
        if (string.IsNullOrWhiteSpace(trackingToken))
        {
            return null;
        }

        using var command = CreateCommand(@"
SELECT `tracking_token`, `firma_id`, `company_name`, `service_no`, `customer_name`, `phone_masked`, `device_title`, `status`, `price_text`, `public_note`, `payload_json`, `updated_at`
FROM `ts_service_tracking_records`
WHERE `tracking_token` = @tracking_token
LIMIT 1;");
        Add(command, "@tracking_token", trackingToken.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return ReadTracking(reader);
    }

    public CustomerMessageResponse? GetLatestCustomerAction(string trackingToken)
    {
        EnsureSchema();
        using var command = CreateCommand(@"
SELECT `id`, `firma_id`, `tracking_token`, `service_no`, `message_type`, `message_text`, `customer_name`, `customer_phone`, `action_context_key`, `created_at`, `is_pulled`
FROM `ts_customer_messages`
WHERE `tracking_token` = @tracking_token AND `is_deleted` = 0
ORDER BY `id` DESC
LIMIT 1;");
        Add(command, "@tracking_token", trackingToken.Trim());
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCustomerMessage(reader) : null;
    }

    public CustomerMessageResponse AddCustomerMessage(string trackingToken, CustomerMessageRequest request)
    {
        EnsureSchema();
        request ??= new CustomerMessageRequest();
        var tracking = GetTrackingByToken(trackingToken) ?? throw new InvalidOperationException("Takip kaydı bulunamadı.");
        var now = DateTime.Now;
        var customerName = string.IsNullOrWhiteSpace(request.CustomerName)
            ? FirstNonEmpty(tracking.CustomerName, ExtractPayloadValue(tracking.PayloadJson, "Service.CustomerName", "CustomerName", "MusteriAdi", "MüşteriAdı"))
            : request.CustomerName.Trim();
        var requestCustomerPhone = request.CustomerPhone?.Trim() ?? string.Empty;
        var customerPhone = string.IsNullOrWhiteSpace(requestCustomerPhone) || requestCustomerPhone.Contains('*')
            ? FirstNonEmpty(ExtractPayloadValue(tracking.PayloadJson, "Service.Phone", "CustomerPhone", "Phone", "Telefon", "MusteriTelefon", "MüşteriTelefon"), tracking.PhoneMasked)
            : requestCustomerPhone;
        var messageType = NormalizeMessageType(request.MessageType);
        var messageText = string.IsNullOrWhiteSpace(request.MessageText) ? "Müşteri işlem tercihini iletti." : request.MessageText.Trim();
        var actionContextKey = request.ActionContextKey?.Trim() ?? string.Empty;

        using (var existingCommand = CreateCommand(@"
SELECT `id`, `firma_id`, `tracking_token`, `service_no`, `message_type`, `message_text`, `customer_name`, `customer_phone`, `action_context_key`, `created_at`, `is_pulled`
FROM `ts_customer_messages`
WHERE `tracking_token` = @tracking_token
  AND `message_type` = @message_type
  AND `is_deleted` = 0
  AND (
      (@action_context_key <> '' AND `action_context_key` = @action_context_key)
      OR (@action_context_key = '' AND `message_text` = @message_text AND `created_at` >= DATE_SUB(NOW(6), INTERVAL 10 MINUTE))
  )
ORDER BY `id` DESC
LIMIT 1;"))
        {
            Add(existingCommand, "@tracking_token", tracking.TrackingToken);
            Add(existingCommand, "@message_type", messageType);
            Add(existingCommand, "@message_text", messageText);
            Add(existingCommand, "@action_context_key", actionContextKey);
            using var existingReader = existingCommand.ExecuteReader();
            if (existingReader.Read())
            {
                return ReadCustomerMessage(existingReader);
            }
        }

        using (var command = CreateCommand(@"
INSERT INTO `ts_customer_messages`
(`firma_id`, `tracking_token`, `service_no`, `message_type`, `message_text`, `customer_name`, `customer_phone`, `action_context_key`, `created_at`, `is_pulled`, `is_deleted`)
VALUES
(@firma_id, @tracking_token, @service_no, @message_type, @message_text, @customer_name, @customer_phone, @action_context_key, @created_at, 0, 0);"))
        {
            Add(command, "@firma_id", tracking.FirmaId);
            Add(command, "@tracking_token", tracking.TrackingToken);
            Add(command, "@service_no", tracking.ServiceNo);
            Add(command, "@message_type", messageType);
            Add(command, "@message_text", messageText);
            Add(command, "@customer_name", customerName);
            Add(command, "@customer_phone", customerPhone);
            Add(command, "@action_context_key", actionContextKey);
            Add(command, "@created_at", now);
            command.ExecuteNonQuery();
        }

        using var idCommand = CreateCommand("SELECT LAST_INSERT_ID();");
        var id = Convert.ToInt64(idCommand.ExecuteScalar());

        return new CustomerMessageResponse
        {
            Id = id,
            FirmaId = tracking.FirmaId,
            TrackingToken = tracking.TrackingToken,
            ServiceNo = tracking.ServiceNo,
            MessageType = messageType,
            MessageText = messageText,
            CustomerName = customerName,
            CustomerPhone = customerPhone,
            ActionContextKey = actionContextKey,
            CreatedAt = now.ToString("O"),
            IsPulled = false
        };
    }

    public IReadOnlyList<CustomerMessageResponse> GetCustomerMessages(string firmaId, bool onlyNew)
    {
        EnsureSchema();
        var items = new List<CustomerMessageResponse>();
        var sql = @"
SELECT `id`, `firma_id`, `tracking_token`, `service_no`, `message_type`, `message_text`, `customer_name`, `customer_phone`, `action_context_key`, `created_at`, `is_pulled`
FROM `ts_customer_messages`
WHERE `firma_id` = @firma_id AND `is_deleted` = 0" + (onlyNew ? " AND `is_pulled` = 0" : string.Empty) + @"
ORDER BY `id` DESC
LIMIT 200;";
        using var command = CreateCommand(sql);
        Add(command, "@firma_id", firmaId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(ReadCustomerMessage(reader));
        }

        return items;
    }

    public void MarkMessagesPulled(string firmaId, long[] ids)
    {
        EnsureSchema();
        if (ids is null || ids.Length == 0)
        {
            return;
        }

        foreach (var id in ids.Distinct())
        {
            using var command = CreateCommand("UPDATE `ts_customer_messages` SET `is_pulled` = 1 WHERE `firma_id` = @firma_id AND `id` = @id;");
            Add(command, "@firma_id", firmaId);
            Add(command, "@id", id);
            command.ExecuteNonQuery();
        }
    }

    public int DeleteCustomerMessagesForDesktop(string firmaId, long[] ids)
    {
        EnsureSchema();
        if (ids is null || ids.Length == 0)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var id in ids.Distinct())
        {
            using var command = CreateCommand("UPDATE `ts_customer_messages` SET `is_deleted` = 1 WHERE `firma_id` = @firma_id AND `id` = @id;");
            Add(command, "@firma_id", firmaId);
            Add(command, "@id", id);
            deleted += command.ExecuteNonQuery();
        }

        return deleted;
    }

    public int CountTrackingRecords()
    {
        EnsureSchema();
        using var command = CreateCommand("SELECT COUNT(1) FROM `ts_service_tracking_records`;");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int CountCustomerMessages()
    {
        EnsureSchema();
        using var command = CreateCommand("SELECT COUNT(1) FROM `ts_customer_messages` WHERE `is_deleted` = 0;");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static string ExtractPayloadValue(string payloadJson, params string[] paths)
    {
        if (string.IsNullOrWhiteSpace(payloadJson) || paths is null || paths.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            foreach (var path in paths)
            {
                if (TryReadJsonPath(document.RootElement, path, out var value))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Payload eski/bozuk formatta olsa bile müşteri mesajı kaydı engellenmez.
        }

        return string.Empty;
    }

    private static bool TryReadJsonPath(JsonElement root, string path, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (current.ValueKind != JsonValueKind.Object || !TryGetPropertyIgnoreCase(current, part, out current))
            {
                return false;
            }
        }

        if (current.ValueKind == JsonValueKind.String)
        {
            value = current.GetString()?.Trim() ?? string.Empty;
        }
        else if (current.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
        {
            value = current.ToString().Trim();
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private void TouchTenant(string firmaId)
    {
        using var command = CreateCommand("UPDATE `ts_tenants` SET `last_seen_at` = @last_seen_at WHERE `firma_id` = @firma_id;");
        Add(command, "@last_seen_at", DateTime.Now);
        Add(command, "@firma_id", firmaId);
        command.ExecuteNonQuery();
    }

    private TrackingRecordResponse ReadTracking(DbDataReader reader)
    {
        return new TrackingRecordResponse
        {
            TrackingToken = ReadString(reader, 0),
            FirmaId = ReadString(reader, 1),
            CompanyName = ReadString(reader, 2),
            ServiceNo = ReadString(reader, 3),
            CustomerName = ReadString(reader, 4),
            PhoneMasked = ReadString(reader, 5),
            DeviceTitle = ReadString(reader, 6),
            Status = ReadString(reader, 7),
            PriceText = ReadString(reader, 8),
            PublicNote = ReadString(reader, 9),
            PayloadJson = ReadString(reader, 10),
            UpdatedAt = ReadDate(reader, 11).ToString("O")
        };
    }

    private static CustomerMessageResponse ReadCustomerMessage(DbDataReader reader)
    {
        return new CustomerMessageResponse
        {
            Id = Convert.ToInt64(reader.GetValue(0)),
            FirmaId = ReadString(reader, 1),
            TrackingToken = ReadString(reader, 2),
            ServiceNo = ReadString(reader, 3),
            MessageType = ReadString(reader, 4),
            MessageText = ReadString(reader, 5),
            CustomerName = ReadString(reader, 6),
            CustomerPhone = ReadString(reader, 7),
            ActionContextKey = ReadString(reader, 8),
            CreatedAt = ReadDate(reader, 9).ToString("O"),
            IsPulled = Convert.ToInt32(reader.GetValue(10)) == 1
        };
    }

    private void ExecuteNonQuery(string sql)
    {
        using var command = CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    private DbCommand CreateCommand(string sql)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            connection.Open();
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 60;
        return command;
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string ReadString(DbDataReader reader, int index)
    {
        return reader.IsDBNull(index) ? string.Empty : Convert.ToString(reader.GetValue(index)) ?? string.Empty;
    }

    private static DateTime ReadDate(DbDataReader reader, int index)
    {
        if (reader.IsDBNull(index))
        {
            return DateTime.Now;
        }

        var value = reader.GetValue(index);
        if (value is DateTime dateTime)
        {
            return dateTime;
        }

        return DateTime.TryParse(Convert.ToString(value), out var parsed) ? parsed : DateTime.Now;
    }

    private static string NormalizeEntity(string value)
    {
        return NormalizeKey(value, "general").ToLowerInvariant();
    }

    private static string NormalizeMessageType(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Equals("Onay", StringComparison.OrdinalIgnoreCase)) return "Onay";
        if (text.Equals("Red", StringComparison.OrdinalIgnoreCase)) return "Red";
        return "Bilgi";
    }

    public static string NormalizeKey(string value, string fallback)
    {
        value = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            value = fallback ?? string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
            {
                builder.Append(ch);
            }
        }

        return builder.Length == 0 ? fallback ?? string.Empty : builder.ToString();
    }

    private static string CreateToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    }

    private static string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty));
        return Convert.ToHexString(bytes);
    }
}
