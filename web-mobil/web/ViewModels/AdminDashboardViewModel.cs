namespace NSYazilim.Web.ViewModels
{
    public class AdminDashboardViewModel
    {
        public int TotalUsers { get; set; }
        public int ActiveUsers { get; set; }
        public int TotalProducts { get; set; }
        public int InStockProducts { get; set; }
        public int OutOfStockProducts { get; set; }
        public int TotalOrders { get; set; }
        public decimal TotalRevenue { get; set; }
        public decimal TodayRevenue { get; set; }
        public decimal ThisMonthRevenue { get; set; }
        public decimal CompletedRevenue { get; set; }
        public int PaidOrders { get; set; }
        public int TodayOrders { get; set; }
        public int ThisMonthOrders { get; set; }
        public int TotalLicenses { get; set; }
        public int ActiveLicenses { get; set; }
        public int TotalDemoDownloads { get; set; }
        public int PendingComments { get; set; }
        public int ApprovedComments { get; set; }

        public List<AdminDashboardOrderItem> LatestOrders { get; set; } = new();
        public List<AdminDashboardProductItem> LowStockProducts { get; set; } = new();
        public List<AdminDashboardLicenseItem> LatestLicenses { get; set; } = new();
    }

    public class AdminDashboardOrderItem
    {
        public int Id { get; set; }
        public string OrderNumber { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public decimal TotalAmount { get; set; }
        public string OrderStatus { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class AdminDashboardProductItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int StockQuantity { get; set; }
        public decimal YearlyPrice { get; set; }
        public decimal LifetimePrice { get; set; }
    }

    public class AdminDashboardLicenseItem
    {
        public int Id { get; set; }
        public string LicenseKey { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public DateTime? EndDate { get; set; }
    }
}
