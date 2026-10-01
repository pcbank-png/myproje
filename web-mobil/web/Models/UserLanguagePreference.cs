using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public sealed class UserLanguagePreference
    {
        public int UserId { get; set; }

        [Required, MaxLength(16)]
        public string LanguageCode { get; set; } = "tr-TR";

        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
