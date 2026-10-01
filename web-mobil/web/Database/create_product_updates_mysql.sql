CREATE TABLE IF NOT EXISTS ProductUpdates (
    Id INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
    ProductCode VARCHAR(100) NOT NULL,
    Version VARCHAR(50) NOT NULL,
    DownloadUrl VARCHAR(500) NOT NULL,
    Notes TEXT NULL,
    IsActive TINYINT(1) NOT NULL DEFAULT 1,
    IsRequired TINYINT(1) NOT NULL DEFAULT 0,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE KEY UX_ProductUpdates_ProductCode_Version (ProductCode, Version),
    INDEX IX_ProductUpdates_ProductCode_IsActive (ProductCode, IsActive),
    INDEX IX_ProductUpdates_CreatedAt (CreatedAt)
);
