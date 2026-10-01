using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class LocalizationTranslationJob
    {
        public long Id { get; set; }

        [Required, MaxLength(64)]
        public string SourceKey { get; set; } = string.Empty;

        [Required, MaxLength(16)]
        public string LanguageCode { get; set; } = string.Empty;

        [Required, MaxLength(20)]
        public string Status { get; set; } = "Pending";

        public int AttemptCount { get; set; }

        [MaxLength(1000)]
        public string? LastError { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public DateTime? NextAttemptAt { get; set; }
    }
}
