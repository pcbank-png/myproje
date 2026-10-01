namespace NSYazilim.Web.ViewModels
{
    public class AdminPhoneLocationLookupViewModel
    {
        public string Phone { get; set; } = string.Empty;
        public bool IsSearched { get; set; }
        public bool HasMatch { get; set; }
        public bool HasPublicIp { get; set; }
        public bool HasLocation { get; set; }
        public string? ValidationMessage { get; set; }
        public string? StatusMessage { get; set; }
        public string? MatchedFullName { get; set; }
        public string? MaskedPhone { get; set; }
        public string? City { get; set; }
        public string? Region { get; set; }
        public string? Country { get; set; }
        public string? Source { get; set; }
        public DateTime? ObservedAt { get; set; }
    }
}
