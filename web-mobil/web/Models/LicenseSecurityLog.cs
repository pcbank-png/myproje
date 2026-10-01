using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class LicenseSecurityLog
    {
        public int Id { get; set; }
        public int? LicenseId { get; set; }

        [MaxLength(120)]
        public string? LicenseKeyMasked { get; set; }

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(180)]
        public string? RequestEmail { get; set; }

        [MaxLength(300)]
        public string? MachineId { get; set; }

        [MaxLength(50)]
        public string Severity { get; set; } = "Warning";

        [MaxLength(120)]
        public string EventType { get; set; } = "Unknown";

        [MaxLength(700)]
        public string Message { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? AppVersion { get; set; }

        [MaxLength(80)]
        public string? IpAddress { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public License? License { get; set; }
    }
}
