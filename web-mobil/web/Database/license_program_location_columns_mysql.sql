-- NSX Lisans Program Konumu / Cihaz Konumu alanları
-- Not: Program.cs bu kolonları açılışta otomatik eklemeye çalışır.
-- Manuel ihtiyaç olursa HeidiSQL üzerinden bu dosyadaki satırları tek tek çalıştırabilirsiniz.

ALTER TABLE `Licenses` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Licenses` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;

ALTER TABLE `LicenseDevices` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseDevices` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseDevices` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `LicenseDevices` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;
