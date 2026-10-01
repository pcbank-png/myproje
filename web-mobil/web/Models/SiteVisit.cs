using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class SiteVisit
    {
        public long Id { get; set; }

        [MaxLength(64)]
        public string VisitorId { get; set; } = string.Empty;

        [MaxLength(500)]
        public string Path { get; set; } = "/";

        [MaxLength(250)]
        public string? PageTitle { get; set; }

        [MaxLength(800)]
        public string? Referrer { get; set; }

        [MaxLength(80)]
        public string? IpAddress { get; set; }

        [MaxLength(80)]
        public string DeviceType { get; set; } = "Bilinmiyor";

        [MaxLength(120)]
        public string Browser { get; set; } = "Bilinmiyor";

        [MaxLength(120)]
        public string OperatingSystem { get; set; } = "Bilinmiyor";

        [MaxLength(24)]
        public string TrafficType { get; set; } = "Human";

        [MaxLength(120)]
        public string? RobotName { get; set; }

        [MaxLength(120)]
        public string? Country { get; set; }

        [MaxLength(120)]
        public string? City { get; set; }

        public DateTime? GeoLookupAtUtc { get; set; }
        public int GeoLookupAttemptCount { get; set; }

        public bool IsAuthenticated { get; set; }
        public int? UserId { get; set; }
        public DateTime VisitedAtUtc { get; set; }
    }
}
