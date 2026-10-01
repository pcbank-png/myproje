-- NSX Yazılım SEO kolonları
-- Bu dosyayı Plesk > phpMyAdmin veya MySQL yönetim ekranında çalıştırabilirsiniz.
-- Kolonlar zaten varsa hata almamak için önce tablo yapısını kontrol edin.

ALTER TABLE Products ADD COLUMN Slug varchar(220) NOT NULL DEFAULT '';
ALTER TABLE Products ADD COLUMN MetaTitle varchar(200) NULL;
ALTER TABLE Products ADD COLUMN MetaDescription varchar(300) NULL;

-- Eski ürünlerde boş slug kalırsa admin panelden ürünü düzenleyip kaydetmeniz yeterli olur.
-- Slug alanları dolduktan sonra benzersiz index açabilirsiniz:
-- CREATE UNIQUE INDEX IX_Products_Slug ON Products (Slug);
