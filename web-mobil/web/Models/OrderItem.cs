namespace NSYazilim.Web.Models
{
    public class OrderItem
    {
        public int Id { get; set; }

        public int OrderId { get; set; }

        public int ProductId { get; set; }

        public int Quantity { get; set; } = 1;

        public decimal UnitPrice { get; set; }

        public string LicenseType { get; set; } = "Yearly";

        public Order? Order { get; set; }

        public Product? Product { get; set; }
    }
}
