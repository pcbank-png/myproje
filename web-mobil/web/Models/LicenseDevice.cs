using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class LicenseDevice
    {
        public int Id { get; set; }
        public int LicenseId { get; set; }

        [Required, MaxLength(300)]
        public string MachineId { get; set; } = string.Empty;

        [MaxLength(120)]
        public string? DeviceName { get; set; }

        [MaxLength(120)]
        public string? OsVersion { get; set; }

        [MaxLength(50)]
        public string? AppVersion { get; set; }

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(180)]
        public string? AttemptEmail { get; set; }

        [MaxLength(40)]
        public string DeviceStatus { get; set; } = "Active";

        [MaxLength(80)]
        public string? FirstIpAddress { get; set; }

        [MaxLength(80)]
        public string? LastIpAddress { get; set; }

        [MaxLength(120)]
        public string? LastCity { get; set; }

        [MaxLength(120)]
        public string? LastRegion { get; set; }

        [MaxLength(120)]
        public string? LastCountry { get; set; }

        public DateTime? LastGeoLookupAt { get; set; }

        public bool IsBlocked { get; set; } = false;

        public bool IsRejected { get; set; } = false;

        [MaxLength(500)]
        public string? BlockReason { get; set; }

        public DateTime FirstActivatedAt { get; set; } = DateTime.Now;
        public DateTime LastSeenAt { get; set; } = DateTime.Now;

        public License? License { get; set; }
    }
}
