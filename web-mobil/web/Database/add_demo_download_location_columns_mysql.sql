ALTER TABLE `DemoDownloads` ADD COLUMN `City` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `DemoDownloads` ADD COLUMN `Region` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `DemoDownloads` ADD COLUMN `Country` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `DemoDownloads` ADD COLUMN `GeoLookupAt` datetime(6) NULL;
