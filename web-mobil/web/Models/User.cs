using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class User
    {
        public int Id { get; set; }

        [Required, MaxLength(150)]
        public string FullName { get; set; } = string.Empty;

        [Required, MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        
        public string? Phone { get; set; }
[Required]
        public string PasswordHash { get; set; } = string.Empty;

        
        public string? PasswordResetToken { get; set; }
        public DateTime? PasswordResetTokenExpireDate { get; set; }
[MaxLength(30)]
        public string Role { get; set; } = "User";

        public bool IsActive { get; set; } = true;

        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        [MaxLength(80)]
        public string? LastIpAddress { get; set; }

        [MaxLength(120)]
        public string? LastCity { get; set; }

        [MaxLength(120)]
        public string? LastRegion { get; set; }

        [MaxLength(120)]
        public string? LastCountry { get; set; }

        public DateTime? LastGeoLookupAt { get; set; }

        public DateTime? LastLoginAt { get; set; }

        public ICollection<Order> Orders { get; set; } = new List<Order>();
        public ICollection<License> Licenses { get; set; } = new List<License>();
    }
}
