using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class OfflineLicenseCertificate
    {
        public int Id { get; set; }
        public int LicenseId { get; set; }

        [Required, MaxLength(80)]
        public string CertificateId { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(300)]
        public string? MachineId { get; set; }

        [Required]
        public string PayloadJson { get; set; } = string.Empty;

        [Required]
        public string Signature { get; set; } = string.Empty;

        [Required]
        public string OfflineCode { get; set; } = string.Empty;

        public DateTime IssuedAt { get; set; } = DateTime.Now;
        public DateTime? ExpiresAt { get; set; }
        public bool IsRevoked { get; set; } = false;
        public DateTime? RevokedAt { get; set; }

        [MaxLength(500)]
        public string? RevokedReason { get; set; }

        public License? License { get; set; }
    }
}
