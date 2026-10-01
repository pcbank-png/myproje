using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class SiteLanguage
    {
        public int Id { get; set; }

        [Required, MaxLength(16)]
        public string Code { get; set; } = "tr-TR";

        [Required, MaxLength(10)]
        public string UrlCode { get; set; } = "tr";

        [Required, MaxLength(80)]
        public string NativeName { get; set; } = "Türkçe";

        [Required, MaxLength(80)]
        public string EnglishName { get; set; } = "Turkish";

        [MaxLength(20)]
        public string FlagEmoji { get; set; } = "🇹🇷";

        [Required, MaxLength(3)]
        public string Direction { get; set; } = "LTR";

        public bool IsActive { get; set; } = true;
        public bool IsDefault { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? UpdatedAt { get; set; }

        public bool IsRtl => string.Equals(Direction, "RTL", StringComparison.OrdinalIgnoreCase);
    }
}
