using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.ViewModels
{
    public class BulkMailViewModel
    {
        [Required(ErrorMessage = "Alıcı grubu zorunludur.")]
        public string TargetGroup { get; set; } = "all";

        [Required(ErrorMessage = "Konu zorunludur.")]
        [MaxLength(180, ErrorMessage = "Konu en fazla 180 karakter olabilir.")]
        public string Subject { get; set; } = string.Empty;

        [Required(ErrorMessage = "Mesaj zorunludur.")]
        public string Message { get; set; } = string.Empty;

        public bool SendAsHtml { get; set; } = true;

        public int SentCount { get; set; }
        public int FailedCount { get; set; }
        public List<BulkMailLogItem> LatestLogs { get; set; } = new();
    }

    public class BulkMailLogItem
    {
        public int Id { get; set; }

        public string ToEmail { get; set; } = string.Empty;
        public string? ToName { get; set; }
        public string Subject { get; set; } = string.Empty;
        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
