CREATE TABLE IF NOT EXISTS productcomments (
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
    INDEX IX_productcomments_ProductId (ProductId),
    INDEX IX_productcomments_UserId (UserId),
    CONSTRAINT FK_productcomments_Products_ProductId
        FOREIGN KEY (ProductId) REFERENCES Products(Id)
        ON DELETE RESTRICT,
    CONSTRAINT FK_productcomments_Users_UserId
        FOREIGN KEY (UserId) REFERENCES Users(Id)
        ON DELETE RESTRICT
);
