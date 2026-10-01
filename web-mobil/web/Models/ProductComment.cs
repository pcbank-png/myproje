namespace NSYazilim.Web.Models
{
    public class ProductComment
    {
        public int Id { get; set; }

        public int ProductId { get; set; }
        public int UserId { get; set; }

        public string FullName { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;

        public int Rating { get; set; } = 5;

        public bool IsApproved { get; set; } = false;
        public bool IsDeleted { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public Product? Product { get; set; }
        public User? User { get; set; }
    }
}
