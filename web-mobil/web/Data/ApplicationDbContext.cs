using Microsoft.EntityFrameworkCore;
using NSYazilim.Web.Models;

namespace NSYazilim.Web.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<User> Users => Set<User>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<ProductCategory> ProductCategories => Set<ProductCategory>();
        public DbSet<ProductImage> ProductImages => Set<ProductImage>();
        public DbSet<ProductFile> ProductFiles => Set<ProductFile>();
        public DbSet<Order> Orders => Set<Order>();
        public DbSet<OrderItem> OrderItems => Set<OrderItem>();
        public DbSet<License> Licenses => Set<License>();
        public DbSet<MailLog> MailLogs => Set<MailLog>();
        public DbSet<ProductComment> ProductComments => Set<ProductComment>();
        public DbSet<CampaignCoupon> CampaignCoupons => Set<CampaignCoupon>();
        public DbSet<Campaign> Campaigns => Set<Campaign>();
        public DbSet<ProductUpdate> ProductUpdates => Set<ProductUpdate>();
        public DbSet<DemoDownload> DemoDownloads => Set<DemoDownload>();
        public DbSet<BankTransferNotification> BankTransferNotifications => Set<BankTransferNotification>();
        public DbSet<ShopierOrderRecord> ShopierOrders => Set<ShopierOrderRecord>();
        public DbSet<ShopierProductMapping> ShopierProductMappings => Set<ShopierProductMapping>();
        public DbSet<Advertisement> Advertisements => Set<Advertisement>();
        public DbSet<ProductVideo> ProductVideos => Set<ProductVideo>();
        public DbSet<LicenseDevice> LicenseDevices => Set<LicenseDevice>();
        public DbSet<LicenseCheckLog> LicenseCheckLogs => Set<LicenseCheckLog>();
        public DbSet<LicenseSecurityLog> LicenseSecurityLogs => Set<LicenseSecurityLog>();
        public DbSet<OfflineLicenseCertificate> OfflineLicenseCertificates => Set<OfflineLicenseCertificate>();
        public DbSet<BlockedDevice> BlockedDevices => Set<BlockedDevice>();
        public DbSet<SiteVisit> SiteVisits => Set<SiteVisit>();
        public DbSet<OnlineVisitorPresence> OnlineVisitorPresences => Set<OnlineVisitorPresence>();
        public DbSet<FreeLicenseActivationToken> FreeLicenseActivationTokens => Set<FreeLicenseActivationToken>();
        public DbSet<Dealer> Dealers => Set<Dealer>();
        public DbSet<DealerSale> DealerSales => Set<DealerSale>();
        public DbSet<SiteLanguage> SiteLanguages => Set<SiteLanguage>();
        public DbSet<LocalizationResource> LocalizationResources => Set<LocalizationResource>();
        public DbSet<LocalizationTranslation> LocalizationTranslations => Set<LocalizationTranslation>();
        public DbSet<LocalizationTranslationJob> LocalizationTranslationJobs => Set<LocalizationTranslationJob>();
        public DbSet<UserLanguagePreference> UserLanguagePreferences => Set<UserLanguagePreference>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<User>()
                .HasIndex(x => x.Email)
                .IsUnique();

            modelBuilder.Entity<Dealer>(entity =>
            {
                entity.HasIndex(x => x.Email).IsUnique();
                entity.HasIndex(x => x.DealerCode).IsUnique();
                entity.HasIndex(x => new { x.Province, x.IsActive });
                entity.Property(x => x.CommissionPercent).HasPrecision(18, 2);
                entity.Property(x => x.CommissionFixedAmount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<DealerSale>(entity =>
            {
                entity.HasIndex(x => new { x.DealerId, x.CreatedAt });
                entity.HasIndex(x => x.CustomerUserId);
                entity.HasIndex(x => x.OrderId).IsUnique();
                entity.HasIndex(x => x.BankTransferNotificationId).IsUnique();
                entity.Property(x => x.SaleAmount).HasPrecision(18, 2);
                entity.Property(x => x.CommissionPercentSnapshot).HasPrecision(18, 2);
                entity.Property(x => x.CommissionFixedAmountSnapshot).HasPrecision(18, 2);
                entity.Property(x => x.CommissionAmount).HasPrecision(18, 2);

                entity.HasOne(x => x.Dealer)
                    .WithMany(x => x.Sales)
                    .HasForeignKey(x => x.DealerId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.CustomerUser)
                    .WithMany()
                    .HasForeignKey(x => x.CustomerUserId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.Order)
                    .WithMany()
                    .HasForeignKey(x => x.OrderId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasOne(x => x.BankTransferNotification)
                    .WithMany()
                    .HasForeignKey(x => x.BankTransferNotificationId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<Product>()
                .HasIndex(x => x.Slug)
                .IsUnique();

            modelBuilder.Entity<Product>()
                .HasIndex(x => x.ProductCode)
                .IsUnique();

            modelBuilder.Entity<ProductCategory>(entity =>
            {
                entity.HasIndex(x => x.Key).IsUnique();
                entity.HasIndex(x => new { x.IsActive, x.SortOrder });
            });

            modelBuilder.Entity<License>()
                .HasIndex(x => x.LicenseKey)
                .IsUnique();

            modelBuilder.Entity<CampaignCoupon>()
                .HasIndex(x => x.Code)
                .IsUnique();

            modelBuilder.Entity<License>()
                .HasIndex(x => new { x.ProductCode, x.LicenseStatus });

            modelBuilder.Entity<LicenseDevice>(entity =>
            {
                entity.HasIndex(x => new { x.LicenseId, x.MachineId }).IsUnique();
                entity.HasIndex(x => new { x.MachineId, x.IsBlocked });

                entity.HasOne(x => x.License)
                    .WithMany(x => x.Devices)
                    .HasForeignKey(x => x.LicenseId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<LicenseCheckLog>(entity =>
            {
                entity.HasIndex(x => new { x.LicenseId, x.CreatedAt });
                entity.HasIndex(x => new { x.ProductCode, x.MachineId, x.CreatedAt });

                entity.HasOne(x => x.License)
                    .WithMany(x => x.CheckLogs)
                    .HasForeignKey(x => x.LicenseId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<LicenseSecurityLog>(entity =>
            {
                entity.HasIndex(x => new { x.LicenseId, x.CreatedAt });
                entity.HasIndex(x => new { x.ProductCode, x.MachineId, x.CreatedAt });

                entity.HasOne(x => x.License)
                    .WithMany()
                    .HasForeignKey(x => x.LicenseId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<OfflineLicenseCertificate>(entity =>
            {
                entity.HasIndex(x => x.CertificateId).IsUnique();
                entity.HasIndex(x => new { x.LicenseId, x.MachineId });

                entity.HasOne(x => x.License)
                    .WithMany(x => x.OfflineCertificates)
                    .HasForeignKey(x => x.LicenseId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<BlockedDevice>(entity =>
            {
                entity.HasIndex(x => new { x.MachineId, x.ProductCode, x.IsActive });
            });

            modelBuilder.Entity<SiteVisit>(entity =>
            {
                entity.HasIndex(x => x.VisitedAtUtc);
                entity.HasIndex(x => new { x.VisitorId, x.VisitedAtUtc });
                entity.HasIndex(x => new { x.Path, x.VisitedAtUtc });
            });

            modelBuilder.Entity<OnlineVisitorPresence>(entity =>
            {
                entity.HasIndex(x => x.VisitorId).IsUnique();
                entity.HasIndex(x => x.LastSeenAtUtc);
            });

            modelBuilder.Entity<FreeLicenseActivationToken>(entity =>
            {
                entity.HasIndex(x => x.TokenHash).IsUnique();
                entity.HasIndex(x => new { x.ProductCode, x.ExpiresAt, x.UsedAt });
            });

            modelBuilder.Entity<SiteLanguage>(entity =>
            {
                entity.HasIndex(x => x.Code).IsUnique();
                entity.HasIndex(x => x.UrlCode).IsUnique();
                entity.HasIndex(x => new { x.IsActive, x.SortOrder });
            });

            modelBuilder.Entity<LocalizationResource>(entity =>
            {
                entity.HasIndex(x => x.SourceKey).IsUnique();
                entity.HasIndex(x => x.LastSeenAt);
            });

            modelBuilder.Entity<LocalizationTranslation>(entity =>
            {
                entity.HasIndex(x => new { x.SourceKey, x.LanguageCode }).IsUnique();
                entity.HasIndex(x => x.LanguageCode);
            });

            modelBuilder.Entity<LocalizationTranslationJob>(entity =>
            {
                entity.HasIndex(x => new { x.SourceKey, x.LanguageCode }).IsUnique();
                entity.HasIndex(x => new { x.Status, x.NextAttemptAt });
                entity.HasIndex(x => x.LanguageCode);
            });

            modelBuilder.Entity<UserLanguagePreference>(entity =>
            {
                entity.HasKey(x => x.UserId);
                entity.HasIndex(x => x.LanguageCode);
            });

            modelBuilder.Entity<Product>()
                .Property(x => x.YearlyPrice)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Product>()
                .Property(x => x.LifetimePrice)
                .HasPrecision(18, 2);

            modelBuilder.Entity<Order>()
                .Property(x => x.TotalAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<OrderItem>()
                .Property(x => x.UnitPrice)
                .HasPrecision(18, 2);

            modelBuilder.Entity<BankTransferNotification>()
                .Property(x => x.Amount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<ShopierOrderRecord>(entity =>
            {
                entity.HasIndex(x => x.ShopierOrderId).IsUnique();
                entity.HasIndex(x => x.ShopierCreatedAt);
                entity.HasIndex(x => x.PaymentStatus);
                entity.HasIndex(x => x.CustomerEmail);
                entity.HasIndex(x => x.LocalOrderId);
                entity.HasIndex(x => new { x.LocalFulfillmentStatus, x.LastFulfillmentAttemptAt });
                entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            });

            modelBuilder.Entity<ShopierProductMapping>(entity =>
            {
                entity.HasIndex(x => x.ProductId).IsUnique();
                entity.HasIndex(x => x.ShopierProductId).IsUnique();
                entity.HasIndex(x => new { x.IsActive, x.LastSyncedAt });

                entity.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            modelBuilder.Entity<CampaignCoupon>()
                .Property(x => x.DiscountValue)
                .HasPrecision(18, 2);

            modelBuilder.Entity<CampaignCoupon>()
                .Property(x => x.MinimumCartAmount)
                .HasPrecision(18, 2);

            modelBuilder.Entity<ProductUpdate>()
                .HasIndex(x => new { x.ProductCode, x.Version })
                .IsUnique();

            modelBuilder.Entity<ProductImage>()
                .HasOne(x => x.Product)
                .WithMany(x => x.Images)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProductFile>()
                .HasOne(x => x.Product)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderItem>()
                .HasOne(x => x.Order)
                .WithMany(x => x.OrderItems)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderItem>()
                .HasOne(x => x.Product)
                .WithMany(x => x.OrderItems)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<License>()
                .HasOne(x => x.Product)
                .WithMany(x => x.Licenses)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<License>()
                .HasOne(x => x.User)
                .WithMany(x => x.Licenses)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<License>()
                .HasOne(x => x.Order)
                .WithMany(x => x.Licenses)
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<ProductComment>()
                .HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<ProductComment>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<DemoDownload>()
                .HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<DemoDownload>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<BankTransferNotification>()
                .HasIndex(x => new { x.Status, x.CreatedAt });

            modelBuilder.Entity<BankTransferNotification>()
                .HasOne(x => x.Product)
                .WithMany()
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<BankTransferNotification>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<BankTransferNotification>()
                .HasOne(x => x.Order)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.SetNull);

            modelBuilder.Entity<Advertisement>(entity =>
            {
                entity.HasIndex(x => new { x.ProductCode, x.SlotCode, x.IsActive });
                entity.HasIndex(x => new { x.IsDeleted, x.StartDate, x.EndDate });

                entity.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            modelBuilder.Entity<ProductVideo>(entity =>
            {
                entity.ToTable("ProductVideos");
                entity.HasIndex(x => x.Slug).IsUnique();
                entity.HasIndex(x => x.ProductId);
                entity.HasIndex(x => new { x.IsActive, x.IsFeatured, x.SortOrder });

                entity.HasOne(x => x.Product)
                    .WithMany()
                    .HasForeignKey(x => x.ProductId)
                    .OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}
