CREATE TABLE IF NOT EXISTS `ProductVideos` (
    `Id` int NOT NULL AUTO_INCREMENT,
    `ProductId` int NOT NULL,
    `YouTubeUrl` varchar(700) CHARACTER SET utf8mb4 NOT NULL,
    `YouTubeVideoId` varchar(32) CHARACTER SET utf8mb4 NOT NULL,
    `Title` varchar(220) CHARACTER SET utf8mb4 NULL,
    `VideoType` varchar(80) CHARACTER SET utf8mb4 NULL,
    `Slug` varchar(240) CHARACTER SET utf8mb4 NOT NULL,
    `SortOrder` int NOT NULL DEFAULT 0,
    `IsFeatured` tinyint(1) NOT NULL DEFAULT 0,
    `IsActive` tinyint(1) NOT NULL DEFAULT 1,
    `CreatedAt` datetime(6) NOT NULL,
    `UpdatedAt` datetime(6) NULL,
    PRIMARY KEY (`Id`),
    UNIQUE INDEX `IX_ProductVideos_Slug` (`Slug`),
    INDEX `IX_ProductVideos_ProductId` (`ProductId`),
    INDEX `IX_ProductVideos_IsActive_IsFeatured_SortOrder` (`IsActive`, `IsFeatured`, `SortOrder`),
    CONSTRAINT `FK_ProductVideos_Products_ProductId`
        FOREIGN KEY (`ProductId`) REFERENCES `Products` (`Id`) ON DELETE RESTRICT
) CHARACTER SET=utf8mb4;
