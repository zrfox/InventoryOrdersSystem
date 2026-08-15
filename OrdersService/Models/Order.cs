namespace OrdersService.Models
{
    public class Order
    {
        public int Id { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public DateTime DateCreated { get; set; }
        public string Status { get; set; } = string.Empty;

    }
}
