-- NSX Lisans V2 + Offline Lisans Altyapısı
-- Mevcut V1 lisans sistemini bozmaz. Sadece yeni alan ve tabloları ekler.

ALTER TABLE `Licenses` ADD COLUMN `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `LicenseStatus` varchar(30) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active';
ALTER TABLE `Licenses` ADD COLUMN `MaxDeviceCount` int NOT NULL DEFAULT 1;
ALTER TABLE `Licenses` ADD COLUMN `OfflineAllowed` tinyint(1) NOT NULL DEFAULT 1;
ALTER TABLE `Licenses` ADD COLUMN `LastCheckedAt` datetime(6) NULL;
ALTER TABLE `Licenses` ADD COLUMN `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `LastAppVersion` varchar(50) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `RevokedAt` datetime(6) NULL;
ALTER TABLE `Licenses` ADD COLUMN `RevokedReason` varchar(500) CHARACTER SET utf8mb4 NULL;

UPDATE `Licenses` SET `LicenseStatus` = 'Active' WHERE `LicenseStatus` IS NULL OR `LicenseStatus` = '';
UPDATE `Licenses` SET `MaxDeviceCount` = 1 WHERE `MaxDeviceCount` IS NULL OR `MaxDeviceCount` <= 0;

CREATE TABLE IF NOT EXISTS `LicenseDevices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `LicenseId` int NOT NULL,
    `MachineId` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
    `DeviceName` varchar(120) CHARACTER SET utf8mb4 NULL,
    `OsVersion` varchar(120) CHARACTER SET utf8mb4 NULL,
    `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `AttemptEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
    `DeviceStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active',
    `FirstIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
    `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
    `IsBlocked` tinyint(1) NOT NULL DEFAULT 0,
    `IsRejected` tinyint(1) NOT NULL DEFAULT 0,
    `BlockReason` varchar(500) CHARACTER SET utf8mb4 NULL,
    `FirstActivatedAt` datetime(6) NOT NULL,
    `LastSeenAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    UNIQUE INDEX `IX_LicenseDevices_LicenseId_MachineId` (`LicenseId`, `MachineId`),
    INDEX `IX_LicenseDevices_MachineId_IsBlocked` (`MachineId`, `IsBlocked`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `LicenseCheckLogs` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `LicenseId` int NULL,
    `LicenseKeyMasked` varchar(120) CHARACTER SET utf8mb4 NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
    `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
    `DeviceName` varchar(120) CHARACTER SET utf8mb4 NULL,
    `OsVersion` varchar(120) CHARACTER SET utf8mb4 NULL,
    `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
    `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
    `Success` tinyint(1) NOT NULL DEFAULT 0,
    `Status` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Unknown',
    `Message` varchar(700) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    INDEX `IX_LicenseCheckLogs_LicenseId_CreatedAt` (`LicenseId`, `CreatedAt`),
    INDEX `IX_LicenseCheckLogs_ProductMachineCreated` (`ProductCode`, `MachineId`, `CreatedAt`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `LicenseSecurityLogs` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `LicenseId` int NULL,
    `LicenseKeyMasked` varchar(120) CHARACTER SET utf8mb4 NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL,
    `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
    `Severity` varchar(50) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Warning',
    `EventType` varchar(120) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Unknown',
    `Message` varchar(700) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
    `AppVersion` varchar(50) CHARACTER SET utf8mb4 NULL,
    `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
    `CreatedAt` datetime(6) NOT NULL,
    PRIMARY KEY (`Id`),
    INDEX `IX_LicenseSecurityLogs_LicenseId_CreatedAt` (`LicenseId`, `CreatedAt`),
    INDEX `IX_LicenseSecurityLogs_ProductMachineCreated` (`ProductCode`, `MachineId`, `CreatedAt`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `OfflineLicenseCertificates` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `LicenseId` int NOT NULL,
    `CertificateId` varchar(80) CHARACTER SET utf8mb4 NOT NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `MachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
    `PayloadJson` longtext CHARACTER SET utf8mb4 NOT NULL,
    `Signature` longtext CHARACTER SET utf8mb4 NOT NULL,
    `OfflineCode` longtext CHARACTER SET utf8mb4 NOT NULL,
    `IssuedAt` datetime(6) NOT NULL,
    `ExpiresAt` datetime(6) NULL,
    `IsRevoked` tinyint(1) NOT NULL DEFAULT 0,
    `RevokedAt` datetime(6) NULL,
    `RevokedReason` varchar(500) CHARACTER SET utf8mb4 NULL,
    PRIMARY KEY (`Id`),
    UNIQUE INDEX `IX_OfflineLicenseCertificates_CertificateId` (`CertificateId`),
    INDEX `IX_OfflineLicenseCertificates_LicenseMachine` (`LicenseId`, `MachineId`)
) CHARACTER SET=utf8mb4;

CREATE TABLE IF NOT EXISTS `BlockedDevices` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `MachineId` varchar(300) CHARACTER SET utf8mb4 NOT NULL,
    `ProductCode` varchar(100) CHARACTER SET utf8mb4 NULL,
    `Reason` varchar(500) CHARACTER SET utf8mb4 NULL,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `CreatedAt` datetime(6) NOT NULL,
    `DisabledAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    INDEX `IX_BlockedDevices_MachineProductActive` (`MachineId`, `ProductCode`, `IsActive`)
) CHARACTER SET=utf8mb4;


-- ILK CIHAZ KORUMA EK ALANLARI (mevcut veritabanları için; kolon varsa hata alınması normaldir)
ALTER TABLE `LicenseDevices` ADD COLUMN `AttemptEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseDevices` ADD COLUMN `DeviceStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active';
ALTER TABLE `LicenseDevices` ADD COLUMN `IsRejected` tinyint(1) NOT NULL DEFAULT 0;
ALTER TABLE `LicenseCheckLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseSecurityLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
UPDATE `LicenseDevices` SET `DeviceStatus` = 'Active' WHERE `DeviceStatus` IS NULL OR `DeviceStatus` = '';
