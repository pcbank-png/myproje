using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class ProductCategory
    {
        public int Id { get; set; }

        [Required, MaxLength(40)]
        public string Key { get; set; } = string.Empty;

        [Required, MaxLength(120)]
        public string Label { get; set; } = string.Empty;

        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }
    }
}
