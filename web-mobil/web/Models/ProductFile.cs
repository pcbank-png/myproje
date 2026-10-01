using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class ProductFile
    {
        public int Id { get; set; }

        public int ProductId { get; set; }

        [Required, MaxLength(30)]
        public string FileType { get; set; } = "Demo";

        [Required, MaxLength(500)]
        public string FilePath { get; set; } = string.Empty;

        [MaxLength(250)]
        public string OriginalFileName { get; set; } = string.Empty;

        public DateTime UploadedAt { get; set; } = DateTime.Now;

        public Product? Product { get; set; }
    }
}
