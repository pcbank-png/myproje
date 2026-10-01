using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class LicenseCheckLog
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

        [MaxLength(120)]
        public string? DeviceName { get; set; }

        [MaxLength(120)]
        public string? OsVersion { get; set; }

        [MaxLength(50)]
        public string? AppVersion { get; set; }

        [MaxLength(80)]
        public string? IpAddress { get; set; }

        public bool Success { get; set; }

        [MaxLength(40)]
        public string Status { get; set; } = "Unknown";

        [MaxLength(700)]
        public string Message { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public License? License { get; set; }
    }
}
