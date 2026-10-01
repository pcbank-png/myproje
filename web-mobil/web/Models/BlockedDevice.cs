using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class BlockedDevice
    {
        public int Id { get; set; }

        [Required, MaxLength(300)]
        public string MachineId { get; set; } = string.Empty;

        [MaxLength(100)]
        public string? ProductCode { get; set; }

        [MaxLength(500)]
        public string? Reason { get; set; }

        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime? DisabledAt { get; set; }
    }
}
