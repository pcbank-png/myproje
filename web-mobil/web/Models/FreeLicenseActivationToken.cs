using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class FreeLicenseActivationToken
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int ProductId { get; set; }
        public int LicenseId { get; set; }
        [Required, MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
        [Required, MaxLength(100)] public string ProductCode { get; set; } = "NSXVERESIYETAKIPPROFREE";
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; }
        public DateTime? UsedAt { get; set; }
        [MaxLength(300)] public string? UsedMachineId { get; set; }
        [MaxLength(80)] public string? CreatedIpAddress { get; set; }
        [MaxLength(80)] public string? UsedIpAddress { get; set; }
        public User? User { get; set; }
        public Product? Product { get; set; }
        public License? License { get; set; }
    }
}
