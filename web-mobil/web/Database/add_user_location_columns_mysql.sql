ALTER TABLE `Users` ADD COLUMN `LastIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Users` ADD COLUMN `LastCity` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Users` ADD COLUMN `LastRegion` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Users` ADD COLUMN `LastCountry` varchar(120) CHARACTER SET utf8mb4 NULL;
ALTER TABLE `Users` ADD COLUMN `LastGeoLookupAt` datetime(6) NULL;
ALTER TABLE `Users` ADD COLUMN `LastLoginAt` datetime(6) NULL;
