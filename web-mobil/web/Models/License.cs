using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class License
    {
        public int Id { get; set; }

        [Required, MaxLength(120)]
        public string LicenseKey { get; set; } = string.Empty;

        public int ProductId { get; set; }

        public int UserId { get; set; }

        public int? OrderId { get; set; }

        [MaxLength(30)]
        public string LicenseType { get; set; } = "Yearly";

        [MaxLength(300)]
        public string? MachineId { get; set; }

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(30)]
        public string LicenseStatus { get; set; } = "Active";

        public int MaxDeviceCount { get; set; } = 1;

        public bool OfflineAllowed { get; set; } = true;

        public DateTime? LastCheckedAt { get; set; }

        [MaxLength(80)]
        public string? LastIpAddress { get; set; }

        [MaxLength(120)]
        public string? LastCity { get; set; }

        [MaxLength(120)]
        public string? LastRegion { get; set; }

        [MaxLength(120)]
        public string? LastCountry { get; set; }

        public DateTime? LastGeoLookupAt { get; set; }

        [MaxLength(50)]
        public string? LastAppVersion { get; set; }

        public DateTime? RevokedAt { get; set; }

        [MaxLength(500)]
        public string? RevokedReason { get; set; }

        public DateTime StartDate { get; set; } = DateTime.Now;

        public DateTime? EndDate { get; set; }

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public Product? Product { get; set; }

        public User? User { get; set; }

        public Order? Order { get; set; }

        public ICollection<LicenseDevice> Devices { get; set; } = new List<LicenseDevice>();
        public ICollection<LicenseCheckLog> CheckLogs { get; set; } = new List<LicenseCheckLog>();
        public ICollection<OfflineLicenseCertificate> OfflineCertificates { get; set; } = new List<OfflineLicenseCertificate>();
    }
}
