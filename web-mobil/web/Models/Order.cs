using System.ComponentModel.DataAnnotations;

namespace NSYazilim.Web.Models
{
    public class Order
    {
        public int Id { get; set; }

        [Required, MaxLength(40)]
        public string OrderNumber { get; set; } = "NSX-" + DateTime.Now.ToString("yyyyMMddHHmmss");

        public int UserId { get; set; }

        public decimal TotalAmount { get; set; }

        [MaxLength(30)]
        public string PaymentStatus { get; set; } = "Pending";

        [MaxLength(30)]
        public string OrderStatus { get; set; } = "New";

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public User? User { get; set; }

        public ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();
        public ICollection<License> Licenses { get; set; } = new List<License>();
    }
}
