namespace NSYazilim.Web.ViewModels
{
    public class AdminActiveInstallationsViewModel
    {
        public int TotalMatchedCustomers { get; set; }
        public int TotalMatchedLicenses { get; set; }
        public int ActiveToday { get; set; }
        public int ActiveLast7Days { get; set; }
        public int ActiveLast30Days { get; set; }
        public int InactiveOrNever { get; set; }
        public int ActiveDevices { get; set; }
        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public string Search { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string Activity { get; set; } = string.Empty;

        public List<AdminActiveInstallationRow> Rows { get; set; } = new();
        public List<AdminActiveInstallationProductOption> Products { get; set; } = new();
        public List<AdminActiveInstallationProductStat> ProductStats { get; set; } = new();
    }

    public class AdminActiveInstallationRow
    {
        public int LicenseId { get; set; }
        public int UserId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string ProductCode { get; set; } = string.Empty;
        public string LicenseKeyMasked { get; set; } = string.Empty;
        public string AppVersion { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public DateTime? LastSeenAt { get; set; }
        public int DeviceCount { get; set; }
        public int ActiveDeviceCount { get; set; }
        public bool LicenseIsActive { get; set; }
        public DateTime? LicenseEndDate { get; set; }
        public string ActivityStatus { get; set; } = string.Empty;
        public int ActivityLevel { get; set; }
    }

    public class AdminActiveInstallationProductOption
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
    }

    public class AdminActiveInstallationProductStat
    {
        public string ProductCode { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public int MatchedCustomers { get; set; }
        public int ActiveLast30Days { get; set; }
    }
}
