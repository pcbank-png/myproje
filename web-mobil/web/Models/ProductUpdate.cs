using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace NSYazilim.Web.Models
{
    public class ProductUpdate
    {
        public int Id { get; set; }
        [Required, MaxLength(80)] public string ProductCode { get; set; } = string.Empty;
        [Required, MaxLength(30)] public string Version { get; set; } = string.Empty;
        [Required, MaxLength(500)] public string DownloadUrl { get; set; } = string.Empty;
        [Column("Notes")]
        [MaxLength(2000)] public string? ReleaseNotes { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsRequired { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
