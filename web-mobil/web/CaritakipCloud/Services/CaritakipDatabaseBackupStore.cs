using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace NSYazilim.Web.CaritakipCloud.Services;

public sealed record CariDatabaseBackupMetadata(
    string TenantId,
    string FileName,
    long SizeBytes,
    string Sha256,
    long SourceCursor,
    int SchemaVersion,
    long CustomerCount,
    long TransactionCount,
    DateTime? LastTransactionAtUtc,
    DateTime CreatedAtUtc,
    string SourceDeviceId);

public sealed class CaritakipDatabaseBackupStore
{
    public const long MaxBackupBytes = 150L * 1024L * 1024L;
    private const string DatabaseFileName = "security-latest.db";
    private const string MetadataFileName = "security-latest.meta.json";
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private readonly string _rootPath;
    private readonly ILogger<CaritakipDatabaseBackupStore> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tenantLocks = new(StringComparer.Ordinal);

    public CaritakipDatabaseBackupStore(
        IWebHostEnvironment environment,
        ILogger<CaritakipDatabaseBackupStore> logger)
    {
        _rootPath = Path.GetFullPath(Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "CaritakipBackups"));
        _logger = logger;
    }

    public void EnsureStorageReady()
    {
        Directory.CreateDirectory(_rootPath);
        var probe = Path.Combine(_rootPath, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "NSX");
        }
        finally
        {
            if (File.Exists(probe))
                File.Delete(probe);
        }
    }

    public async Task<CariDatabaseBackupMetadata?> GetMetadataAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var paths = Paths(tenantId);
        if (!File.Exists(paths.Database) || !File.Exists(paths.Metadata))
            return null;

        try
        {
            await using var stream = new FileStream(
                paths.Metadata,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var metadata = await JsonSerializer.DeserializeAsync<CariDatabaseBackupMetadata>(
                stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                cancellationToken);
            if (metadata is null ||
                !string.Equals(metadata.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
                return null;

            var fileLength = new FileInfo(paths.Database).Length;
            if (fileLength <= 0)
                return null;

            // Meta ile disk boyutu birebir uymasa da dosya varsa yedek mevcut sayılır.
            // Eski yüklemelerde yuvarlama / yarım upload artığı yüzünden strict eşitlik
            // available=false üretip programda "Yedek yok" gösteriyordu.
            if (fileLength != metadata.SizeBytes)
            {
                _logger.LogWarning(
                    "Cari Takip DB yedek boyutu meta ile farklı. Tenant: {TenantId}, Meta: {MetaSize}, Disk: {DiskSize}",
                    tenantId,
                    metadata.SizeBytes,
                    fileLength);
                metadata = metadata with { SizeBytes = fileLength };
            }

            return metadata;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Cari Takip DB yedek metadatası okunamadı. Tenant: {TenantId}", tenantId);
            return null;
        }
    }

    public async Task<CariDatabaseBackupMetadata> SaveAsync(
        string tenantId,
        string sourceDeviceId,
        Stream source,
        long? contentLength,
        string expectedSha256,
        long sourceCursor,
        CancellationToken cancellationToken)
    {
        if (contentLength is <= 0 or > MaxBackupBytes)
            throw new InvalidDataException($"Yedek boyutu 1 ile {MaxBackupBytes} bayt arasında olmalıdır.");
        expectedSha256 = NormalizeSha256(expectedSha256);
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Geçerli X-NSX-Backup-Sha256 başlığı zorunludur.");

        var paths = Paths(tenantId);
        Directory.CreateDirectory(paths.Directory);
        var gate = _tenantLocks.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        var tempId = Guid.NewGuid().ToString("N");
        var stagedDatabase = Path.Combine(paths.Directory, $".{DatabaseFileName}.{tempId}.upload");
        var stagedMetadata = Path.Combine(paths.Directory, $".{MetadataFileName}.{tempId}.upload");
        try
        {
            var (actualSize, actualHash) = await CopyAndHashAsync(
                source,
                stagedDatabase,
                cancellationToken);
            if (contentLength.HasValue && actualSize != contentLength.Value)
                throw new InvalidDataException("Yüklenen yedek bildirilen dosya boyutuyla eşleşmiyor.");
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedSha256)))
                throw new InvalidDataException("Yüklenen yedeğin SHA-256 doğrulaması başarısız.");

            var inspection = InspectDatabase(stagedDatabase);
            var metadata = new CariDatabaseBackupMetadata(
                tenantId,
                DatabaseFileName,
                actualSize,
                actualHash,
                Math.Max(0, sourceCursor),
                inspection.SchemaVersion,
                inspection.CustomerCount,
                inspection.TransactionCount,
                inspection.LastTransactionAtUtc,
                DateTime.UtcNow,
                sourceDeviceId);
            await File.WriteAllTextAsync(
                stagedMetadata,
                JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false),
                cancellationToken);

            File.Move(stagedDatabase, paths.Database, true);
            File.Move(stagedMetadata, paths.Metadata, true);
            return metadata;
        }
        finally
        {
            TryDelete(stagedDatabase);
            TryDelete(stagedMetadata);
            gate.Release();
        }
    }

    public async Task<(FileStream Stream, CariDatabaseBackupMetadata Metadata)?> OpenReadAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(tenantId, cancellationToken);
        if (metadata is null)
            return null;
        var paths = Paths(tenantId);
        try
        {
            var stream = new FileStream(
                paths.Database,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return (stream, metadata);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Cari Takip DB yedeği açılamadı. Tenant: {TenantId}", tenantId);
            return null;
        }
    }

    private async Task<(long Size, string Sha256)> CopyAndHashAsync(
        Stream source,
        string targetPath,
        CancellationToken cancellationToken)
    {
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaxBackupBytes)
                throw new InvalidDataException($"Yedek {MaxBackupBytes} bayt sınırını aşıyor.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await target.FlushAsync(cancellationToken);
        target.Flush(true);
        return (total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static DatabaseInspection InspectDatabase(string databasePath)
    {
        using (var headerStream = File.OpenRead(databasePath))
        {
            var header = new byte[SqliteHeader.Length];
            if (headerStream.Read(header, 0, header.Length) != header.Length ||
                !header.AsSpan().SequenceEqual(SqliteHeader))
                throw new InvalidDataException("Dosya geçerli bir SQLite veritabanı değil.");
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(Convert.ToString(check.ExecuteScalar())?.Trim(), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SQLite bütünlük kontrolü başarısız.");
        }

        var tables = ReadTableNames(connection);
        if (!new[] { "Customers", "ServiceRecords", "Payments" }.All(tables.Contains))
            throw new InvalidDataException("Dosya NSX Cari Takip temel tablolarını içermiyor.");

        var schemaVersion = ReadIntPragma(connection, "user_version");
        var customerCount = ReadCount(connection, "Customers");
        var transactionCount = ReadCount(connection, "ServiceRecords") + ReadCount(connection, "Payments");
        var lastTransaction = new[]
            {
                ReadMaximumDate(connection, tables, "ServiceRecords", "CreatedAt"),
                ReadMaximumDate(connection, tables, "Payments", "PaymentDate")
            }
            .Where(x => x.HasValue)
            .Max();
        return new DatabaseInspection(
            schemaVersion,
            customerCount,
            transactionCount,
            lastTransaction?.ToUniversalTime());
    }

    private static HashSet<string> ReadTableNames(SqliteConnection connection)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    private static int ReadIntPragma(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name};";
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static long ReadCount(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM [{table}];";
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static DateTime? ReadMaximumDate(
        SqliteConnection connection,
        ISet<string> tables,
        string table,
        string column)
    {
        if (!tables.Contains(table))
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX([{column}]) FROM [{table}];";
        var text = Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)?.Trim();
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ||
            DateTime.TryParse(text, CultureInfo.GetCultureInfo("tr-TR"), DateTimeStyles.AllowWhiteSpaces, out date))
            return date;
        return null;
    }

    private (string Directory, string Database, string Metadata) Paths(string tenantId)
    {
        if (!Guid.TryParse(tenantId, out var parsed))
            throw new ArgumentException("Geçersiz tenant kimliği.", nameof(tenantId));
        var directory = Path.GetFullPath(Path.Combine(_rootPath, parsed.ToString("D")));
        if (!directory.StartsWith(_rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Yedek yolu güvenli kök dışında.");
        return (
            directory,
            Path.Combine(directory, DatabaseFileName),
            Path.Combine(directory, MetadataFileName));
    }

    private static string NormalizeSha256(string value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Temporary upload cleanup is best-effort.
        }
    }

    private sealed record DatabaseInspection(
        int SchemaVersion,
        long CustomerCount,
        long TransactionCount,
        DateTime? LastTransactionAtUtc);
}
