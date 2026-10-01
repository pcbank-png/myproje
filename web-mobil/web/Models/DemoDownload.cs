using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class DemoDownload
    {
        public int Id { get; set; }

        public int ProductId { get; set; }
        public Product? Product { get; set; }

        public int UserId { get; set; }
        public User? User { get; set; }

        [MaxLength(150)]
        public string FullName { get; set; } = string.Empty;

        [MaxLength(180)]
        public string Email { get; set; } = string.Empty;

        [MaxLength(50)]
        public string? Phone { get; set; }

        [MaxLength(80)]
        public string? IpAddress { get; set; }

        [MaxLength(120)]
        public string? City { get; set; }

        [MaxLength(120)]
        public string? Region { get; set; }

        [MaxLength(120)]
        public string? Country { get; set; }

        public DateTime? GeoLookupAt { get; set; }

        [MaxLength(500)]
        public string? UserAgent { get; set; }

        public DateTime DownloadedAt { get; set; } = DateTime.Now;
    }
}
