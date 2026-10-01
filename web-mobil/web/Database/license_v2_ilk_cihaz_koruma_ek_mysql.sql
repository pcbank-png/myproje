-- NSX Yazılım - Lisans V2 İlk Cihaz Koruma Ek Alanları
-- Bu dosya mevcut veritabanında eksik kolonları eklemek içindir.
-- Kolon zaten varsa MySQL hata verebilir; uygulama başlangıcındaki otomatik patch bu hataları sessizce geçer.

ALTER TABLE `LicenseDevices` ADD COLUMN `AttemptEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseDevices` ADD COLUMN `DeviceStatus` varchar(40) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'Active';
ALTER TABLE `LicenseDevices` ADD COLUMN `IsRejected` tinyint(1) NOT NULL DEFAULT 0;
ALTER TABLE `LicenseCheckLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseSecurityLogs` ADD COLUMN `RequestEmail` varchar(180) CHARACTER SET utf8mb4 NULL;
UPDATE `LicenseDevices` SET `DeviceStatus` = 'Active' WHERE `DeviceStatus` IS NULL OR `DeviceStatus` = '';
