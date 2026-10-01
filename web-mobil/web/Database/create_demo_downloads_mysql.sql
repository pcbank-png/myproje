CREATE TABLE IF NOT EXISTS `DemoDownloads` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ProductId` int NOT NULL,
  `UserId` int NOT NULL,
  `FullName` varchar(150) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Email` varchar(180) CHARACTER SET utf8mb4 NOT NULL DEFAULT '',
  `Phone` varchar(50) CHARACTER SET utf8mb4 NULL,
  `IpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
  `City` varchar(120) CHARACTER SET utf8mb4 NULL,
  `Region` varchar(120) CHARACTER SET utf8mb4 NULL,
  `Country` varchar(120) CHARACTER SET utf8mb4 NULL,
  `GeoLookupAt` datetime(6) NULL,
  `UserAgent` varchar(500) CHARACTER SET utf8mb4 NULL,
  `DownloadedAt` datetime(6) NOT NULL,
  PRIMARY KEY (`Id`),
  INDEX `IX_DemoDownloads_ProductId` (`ProductId`),
  INDEX `IX_DemoDownloads_UserId` (`UserId`),
  INDEX `IX_DemoDownloads_DownloadedAt` (`DownloadedAt`)
) CHARACTER SET=utf8mb4;
