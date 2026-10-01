using System.Text.Json;
using MySqlConnector;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed record NotificationDraft(string TenantId, string? ExcludeDeviceId, string Category,
    string Title, string Body, Dictionary<string, string> Data);
public sealed record PushDraft(string TenantId, string DeviceId, CaritakipExpoPushMessage Message,
    string? ReceiptId = null, DateTime? AcceptedAtUtc = null);
public sealed record NotificationJob(string Id, string TenantId, string Kind, string Payload, string Lease, int Attempts);

public sealed class CaritakipNotificationOutboxStore(IConfiguration configuration, CaritakipCloudStore cloudStore)
{
    public const string SchemaSql = """
CREATE TABLE IF NOT EXISTS ct_notification_outbox (
  id CHAR(36) NOT NULL PRIMARY KEY,
  tenant_id CHAR(36) NOT NULL,
  dedupe_key VARCHAR(190) NOT NULL,
  kind VARCHAR(16) NOT NULL,
  payload_json JSON NOT NULL,
  state TINYINT NOT NULL DEFAULT 0,
  attempts INT NOT NULL DEFAULT 0,
  available_at_utc DATETIME(6) NOT NULL,
  lease_id CHAR(36) NULL,
  lease_until_utc DATETIME(6) NULL,
  last_error VARCHAR(120) NULL,
  created_at_utc DATETIME(6) NOT NULL,
  completed_at_utc DATETIME(6) NULL,
  UNIQUE KEY ux_ct_notification_outbox_dedupe (tenant_id,dedupe_key),
  KEY ix_ct_notification_outbox_due (state,available_at_utc,lease_until_utc)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
""";

    private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
    {
        await cloudStore.EnsureSchemaAsync(ct);
        var connection = new MySqlConnection(new MySqlConnectionStringBuilder(
            configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("DefaultConnection missing")) { GuidFormat = MySqlGuidFormat.None }.ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static MySqlCommand Command(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = new MySqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    // Called inside the SAME transaction as a newly created debt/collection.
    public static async Task EnqueueAsync(MySqlConnection connection, MySqlTransaction? transaction,
        string tenantId, string dedupeKey, string kind, object payload, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
INSERT INTO ct_notification_outbox(id,tenant_id,dedupe_key,kind,payload_json,available_at_utc,created_at_utc)
VALUES(@id,@tenant,@key,@kind,@payload,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
ON DUPLICATE KEY UPDATE id=id;
""", ("@id", Guid.NewGuid().ToString()), ("@tenant", tenantId), ("@key", dedupeKey),
            ("@kind", kind), ("@payload", JsonSerializer.Serialize(payload)));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueAsync(string tenant, string key, string kind, object payload, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await EnqueueAsync(connection, null, tenant, key, kind, payload, ct);
    }

    public async Task<NotificationJob?> ClaimAsync(CancellationToken ct, string? lane = null)
    {
        await using var connection = await OpenAsync(ct);
        var lease = Guid.NewGuid().ToString();
        // Atomic UPDATE works on MySQL 5.7/8 and prevents two workers owning one job.
        await using (var command = Command(connection, null, """
UPDATE ct_notification_outbox SET lease_id=@lease,
lease_until_utc=DATE_ADD(UTC_TIMESTAMP(6),INTERVAL 5 MINUTE), attempts=attempts+1
WHERE state=0 AND available_at_utc<=UTC_TIMESTAMP(6)
AND (@lane IS NULL OR (@lane='inbox' AND kind='inbox') OR (@lane='push' AND kind IN ('push','receipt')))
AND (lease_until_utc IS NULL OR lease_until_utc<UTC_TIMESTAMP(6))
ORDER BY available_at_utc,created_at_utc LIMIT 1;
""", ("@lease", lease),("@lane",lane))) await command.ExecuteNonQueryAsync(ct);
        await using var select = Command(connection, null,
            "SELECT id,tenant_id,kind,payload_json,attempts FROM ct_notification_outbox WHERE lease_id=@lease;", ("@lease", lease));
        await using var reader = await select.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),lease,reader.GetInt32(4)) : null;
    }

    public async Task CompleteAsync(NotificationJob job, CancellationToken ct, bool failed = false, string? error = null)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(connection, null, """
UPDATE ct_notification_outbox SET state=@state,completed_at_utc=UTC_TIMESTAMP(6),
lease_id=NULL,lease_until_utc=NULL,last_error=@error WHERE id=@id AND lease_id=@lease;
""", ("@state",failed ? 2 : 1),("@error",error),("@id",job.Id),("@lease",job.Lease));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RetryAsync(NotificationJob job, CancellationToken ct, string error,
        TimeSpan? delay = null, string? kind = null, object? payload = null)
    {
        await using var connection = await OpenAsync(ct);
        var seconds = (delay ?? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(job.Attempts,8))))).TotalSeconds;
        await using var command = Command(connection, null, """
UPDATE ct_notification_outbox SET available_at_utc=@next,lease_id=NULL,lease_until_utc=NULL,
last_error=@error,kind=COALESCE(@kind,kind),payload_json=COALESCE(@payload,payload_json)
WHERE id=@id AND lease_id=@lease;
""", ("@next",DateTime.UtcNow.AddSeconds(seconds)),("@error",error.Length>120 ? error[..120] : error),
            ("@kind",kind),("@payload",payload is null ? null : JsonSerializer.Serialize(payload)),("@id",job.Id),("@lease",job.Lease));
        await command.ExecuteNonQueryAsync(ct);
    }
}
