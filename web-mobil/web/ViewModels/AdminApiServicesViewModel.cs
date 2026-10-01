namespace NSYazilim.Web.ViewModels
{
    public class AdminApiServicesViewModel
    {
        public bool LicenseApiKeyConfigured { get; set; }
        public int TotalLicenses { get; set; }
        public int ActiveLicenses { get; set; }
        public int TotalUpdates { get; set; }
        public int ActiveUpdates { get; set; }
        public int TotalAdvertisements { get; set; }
        public int ActiveAdvertisements { get; set; }
        public List<AdminApiServiceItem> Services { get; set; } = new();
    }

    public class AdminApiServiceItem
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = string.Empty;
        public string Auth { get; set; } = string.Empty;
        public string Parameters { get; set; } = string.Empty;
        public string SampleUrl { get; set; } = string.Empty;
        public string StatusLabel { get; set; } = "Aktif";
        public bool IsEnabled { get; set; } = true;
    }
}
