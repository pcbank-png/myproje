using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class LocalizationTranslation
    {
        public long Id { get; set; }

        [Required, MaxLength(64)]
        public string SourceKey { get; set; } = string.Empty;

        [Required, MaxLength(16)]
        public string LanguageCode { get; set; } = string.Empty;

        [Required]
        public string Value { get; set; } = string.Empty;

        public bool IsMachineTranslated { get; set; }
        public bool IsReviewed { get; set; }
        public bool IsLocked { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
