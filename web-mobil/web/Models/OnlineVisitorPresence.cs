using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class OnlineVisitorPresence
    {
        public long Id { get; set; }

        [MaxLength(80)]
        public string VisitorId { get; set; } = string.Empty;

        public DateTime FirstSeenAtUtc { get; set; }
        public DateTime LastSeenAtUtc { get; set; }

        [MaxLength(80)] public string? IpAddress { get; set; }
        [MaxLength(120)] public string? City { get; set; }
        [MaxLength(500)] public string PagePath { get; set; } = "/";
        [MaxLength(220)] public string? PageTitle { get; set; }
        [MaxLength(500)] public string? Referrer { get; set; }
        [MaxLength(120)] public string Browser { get; set; } = "Bilinmiyor";
        [MaxLength(120)] public string OperatingSystem { get; set; } = "Bilinmiyor";
        [MaxLength(80)] public string DeviceType { get; set; } = "Bilinmiyor";
        [MaxLength(500)] public string? UserAgent { get; set; }
        public bool IsAuthenticated { get; set; }
        [MaxLength(150)] public string? UserName { get; set; }
        [MaxLength(180)] public string? UserEmail { get; set; }
        [MaxLength(60)] public string? UserRole { get; set; }
    }
}
