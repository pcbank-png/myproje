using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Data;

namespace NSYazilim.Web.Services
{
    public static class ShopierSchemaInitializer
    {
        public static async Task EnsureAsync(IServiceProvider services, ILogger logger)
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            // Sipariş ve ürün mapping şemalarını birbirinden bağımsız hazırla. Birindeki legacy
            // MySQL/Plesk uyumsuzluğu diğer Shopier özelliğini devre dışı bırakmasın.
            try
            {
                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `ShopierOrders` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `ShopierOrderId` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
                    `PaymentStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'pending',
                    `FulfillmentStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'unfulfilled',
                    `PaymentMethod` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
                    `IsInstallments` tinyint(1) NOT NULL DEFAULT 0,
                    `Currency` varchar(8) CHARACTER SET ascii NOT NULL DEFAULT 'TRY',
                    `TotalAmount` decimal(18,2) NOT NULL DEFAULT 0.00,
                    `CustomerFirstName` varchar(120) CHARACTER SET utf8mb4 NULL,
                    `CustomerLastName` varchar(120) CHARACTER SET utf8mb4 NULL,
                    `CustomerEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
                    `CustomerPhone` varchar(60) CHARACTER SET utf8mb4 NULL,
                    `Note` varchar(1000) CHARACTER SET utf8mb4 NULL,
                    `ShopierProductId` varchar(80) CHARACTER SET utf8mb4 NULL,
                    `ProductTitle` varchar(300) CHARACTER SET utf8mb4 NULL,
                    `LicenseSelection` varchar(120) CHARACTER SET utf8mb4 NULL,
                    `Quantity` int NOT NULL DEFAULT 1,
                    `LastWebhookId` varchar(80) CHARACTER SET utf8mb4 NULL,
                    `ShopierCreatedAt` datetime(6) NOT NULL,
                    `FirstSeenAt` datetime(6) NOT NULL,
                    `LastSyncedAt` datetime(6) NOT NULL,
                    `LocalOrderId` int NULL,
                    `LocalFulfillmentStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Pending',
                    `LocalFulfillmentMessage` varchar(1000) CHARACTER SET utf8mb4 NULL,
                    `LastFulfillmentAttemptAt` datetime(6) NULL,
                    `FulfilledAt` datetime(6) NULL,
                    `DeliveryEmailQueuedAt` datetime(6) NULL,
                    `IsAdminHidden` tinyint(1) NOT NULL DEFAULT 0,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_ShopierOrders_ShopierOrderId` (`ShopierOrderId`),
                    INDEX `IX_ShopierOrders_ShopierCreatedAt` (`ShopierCreatedAt`),
                    INDEX `IX_ShopierOrders_PaymentStatus` (`PaymentStatus`),
                    INDEX `IX_ShopierOrders_CustomerEmail` (`CustomerEmail`),
                    INDEX `IX_ShopierOrders_LocalOrderId` (`LocalOrderId`),
                    INDEX `IX_ShopierOrders_LocalFulfillmentStatus_LastFulfillmentAttemptAt` (`LocalFulfillmentStatus`, `LastFulfillmentAttemptAt`)
                ) CHARACTER SET=utf8mb4;");

                await EnsureShopierOrderColumnsAsync(db);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shopier sipariş tablosu hazırlanamadı. Site çalışmaya devam edecek.");
            }

            try
            {
                // Mapping tablosu özellikle FK'siz oluşturulur. Bazı Plesk/MySQL kurulumlarında
                // legacy Products tablosunun engine/collation geçmişi CREATE TABLE içindeki FK'yi
                // reddedebiliyor. Uygulama ProductId bütünlüğünü zaten kendisi yönetiyor.
                await db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS `ShopierProductMappings` (
                    `Id` int NOT NULL AUTO_INCREMENT,
                    `ProductId` int NOT NULL,
                    `ShopierProductId` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
                    `ShopierTitle` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
                    `ShopierUrl` varchar(500) CHARACTER SET utf8mb4 NOT NULL,
                    `MatchMethod` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'auto',
                    `MatchScore` int NOT NULL DEFAULT 0,
                    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
                    `CreatedAt` datetime(6) NOT NULL,
                    `LastSyncedAt` datetime(6) NOT NULL,
                    PRIMARY KEY (`Id`),
                    UNIQUE INDEX `IX_ShopierProductMappings_ProductId` (`ProductId`),
                    UNIQUE INDEX `IX_ShopierProductMappings_ShopierProductId` (`ShopierProductId`),
                    INDEX `IX_ShopierProductMappings_IsActive_LastSyncedAt` (`IsActive`, `LastSyncedAt`)
                ) CHARACTER SET=utf8mb4;");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shopier ürün eşleştirme tablosu hazırlanamadı. Checkout doğrudan güvenli eşleştirme fallback'i kullanacak.");
            }
        }

        private static async Task EnsureShopierOrderColumnsAsync(ApplicationDbContext db)
        {
            await EnsureColumnAsync(db, "ShopierOrders", "LocalOrderId", "int NULL");
            await EnsureColumnAsync(db, "ShopierOrders", "LocalFulfillmentStatus", "varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Pending'");
            await EnsureColumnAsync(db, "ShopierOrders", "LocalFulfillmentMessage", "varchar(1000) CHARACTER SET utf8mb4 NULL");
            await EnsureColumnAsync(db, "ShopierOrders", "LastFulfillmentAttemptAt", "datetime(6) NULL");
            await EnsureColumnAsync(db, "ShopierOrders", "FulfilledAt", "datetime(6) NULL");
            await EnsureColumnAsync(db, "ShopierOrders", "DeliveryEmailQueuedAt", "datetime(6) NULL");
            await EnsureColumnAsync(db, "ShopierOrders", "IsAdminHidden", "tinyint(1) NOT NULL DEFAULT 0");

            await EnsureIndexAsync(db, "ShopierOrders", "IX_ShopierOrders_LocalOrderId", "`LocalOrderId`");
            await EnsureIndexAsync(db, "ShopierOrders", "IX_ShopierOrders_LocalFulfillmentStatus_LastFulfillmentAttemptAt", "`LocalFulfillmentStatus`, `LastFulfillmentAttemptAt`");
        }

        private static async Task EnsureColumnAsync(ApplicationDbContext db, string tableName, string columnName, string sqlDefinition)
        {
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await connection.OpenAsync();

            try
            {
                await using var check = connection.CreateCommand();
                check.CommandText = @"SELECT COUNT(*)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = @columnName;";

                var tableParameter = check.CreateParameter();
                tableParameter.ParameterName = "@tableName";
                tableParameter.Value = tableName;
                check.Parameters.Add(tableParameter);

                var columnParameter = check.CreateParameter();
                columnParameter.ParameterName = "@columnName";
                columnParameter.Value = columnName;
                check.Parameters.Add(columnParameter);

                var count = Convert.ToInt32(await check.ExecuteScalarAsync());
                if (count > 0)
                    return;

                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {sqlDefinition};";
                await alter.ExecuteNonQueryAsync();
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }
        }

        private static async Task EnsureIndexAsync(ApplicationDbContext db, string tableName, string indexName, string columnSql)
        {
            var connection = db.Database.GetDbConnection();
            var shouldClose = connection.State != System.Data.ConnectionState.Open;
            if (shouldClose)
                await connection.OpenAsync();

            try
            {
                await using var check = connection.CreateCommand();
                check.CommandText = @"SELECT COUNT(*)
FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND INDEX_NAME = @indexName;";

                var tableParameter = check.CreateParameter();
                tableParameter.ParameterName = "@tableName";
                tableParameter.Value = tableName;
                check.Parameters.Add(tableParameter);

                var indexParameter = check.CreateParameter();
                indexParameter.ParameterName = "@indexName";
                indexParameter.Value = indexName;
                check.Parameters.Add(indexParameter);

                var count = Convert.ToInt32(await check.ExecuteScalarAsync());
                if (count > 0)
                    return;

                await using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE `{tableName}` ADD INDEX `{indexName}` ({columnSql});";
                await alter.ExecuteNonQueryAsync();
            }
            finally
            {
                if (shouldClose)
                    await connection.CloseAsync();
            }
        }
    }
}
