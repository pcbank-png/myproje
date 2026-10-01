CREATE TABLE IF NOT EXISTS ProductComments (
    Id INT NOT NULL AUTO_INCREMENT,
    ProductId INT NOT NULL,
    UserId INT NOT NULL,
    FullName LONGTEXT NOT NULL,
    Comment LONGTEXT NOT NULL,
    Rating INT NOT NULL DEFAULT 5,
    IsApproved TINYINT(1) NOT NULL DEFAULT 0,
    IsDeleted TINYINT(1) NOT NULL DEFAULT 0,
    CreatedAt DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (Id),
    INDEX IX_ProductComments_ProductId (ProductId),
    INDEX IX_ProductComments_UserId (UserId),
    CONSTRAINT FK_ProductComments_Products_ProductId
        FOREIGN KEY (ProductId) REFERENCES Products(Id)
        ON DELETE RESTRICT,
    CONSTRAINT FK_ProductComments_Users_UserId
        FOREIGN KEY (UserId) REFERENCES Users(Id)
        ON DELETE RESTRICT
);
