namespace NSYazilim.Web.Models
{
    public class MailLog
    {
        public int Id { get; set; }

        public string ToEmail { get; set; } = string.Empty;
        public string? ToName { get; set; }

        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;

        public bool IsSuccess { get; set; }
        public string? ErrorMessage { get; set; }

        public string MailType { get; set; } = "Bulk";
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
