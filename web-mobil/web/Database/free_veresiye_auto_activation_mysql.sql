CREATE TABLE IF NOT EXISTS `FreeLicenseActivationTokens` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `UserId` int NOT NULL,
  `ProductId` int NOT NULL,
  `LicenseId` int NOT NULL,
  `TokenHash` varchar(64) CHARACTER SET ascii NOT NULL,
  `ProductCode` varchar(100) CHARACTER SET utf8mb4 NOT NULL DEFAULT 'NSXVERESIYETAKIPPROFREE',
  `CreatedAt` datetime(6) NOT NULL,
  `ExpiresAt` datetime(6) NOT NULL,
  `UsedAt` datetime(6) NULL,
  `UsedMachineId` varchar(300) CHARACTER SET utf8mb4 NULL,
  `CreatedIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
  `UsedIpAddress` varchar(80) CHARACTER SET utf8mb4 NULL,
  PRIMARY KEY (`Id`),
  UNIQUE INDEX `IX_FreeLicenseActivationTokens_TokenHash` (`TokenHash`),
  INDEX `IX_FreeLicenseActivationTokens_ProductCode_ExpiresAt_UsedAt` (`ProductCode`,`ExpiresAt`,`UsedAt`)
) CHARACTER SET=utf8mb4;
