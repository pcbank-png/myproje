using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class LocalizationResource
    {
        public long Id { get; set; }

        [Required, MaxLength(64)]
        public string SourceKey { get; set; } = string.Empty;

        [Required]
        public string SourceText { get; set; } = string.Empty;

        [MaxLength(500)]
        public string? FirstSeenPath { get; set; }

        public DateTime FirstSeenAt { get; set; } = DateTime.Now;
        public DateTime LastSeenAt { get; set; } = DateTime.Now;
        public long HitCount { get; set; } = 1;
        public bool IsIgnored { get; set; }
    }
}
