CREATE TABLE IF NOT EXISTS `homesliders` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `ProductId` int NULL,
  `Title` varchar(180) NOT NULL,
  `Subtitle` varchar(500) NULL,
  `ButtonText` varchar(80) NULL,
  `LinkUrl` varchar(300) NULL,
  `ImagePath` varchar(500) NOT NULL,
  `SortOrder` int NOT NULL DEFAULT 0,
  `IsActive` tinyint(1) NOT NULL DEFAULT 1,
  `CreatedAt` datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `UpdatedAt` datetime(6) NULL,
  PRIMARY KEY (`Id`),
  KEY `IX_homesliders_ProductId` (`ProductId`),
  KEY `IX_homesliders_IsActive_SortOrder` (`IsActive`, `SortOrder`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_turkish_ci;
