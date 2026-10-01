-- Eğer kolon yoksa tek tek çalıştır.
-- "Duplicate column name" derse o satırı atla, kolon zaten vardır.

ALTER TABLE Users ADD COLUMN PasswordResetToken LONGTEXT NULL;

ALTER TABLE Users ADD COLUMN PasswordResetTokenExpireDate DATETIME NULL;
