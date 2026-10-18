using System.ComponentModel.DataAnnotations;

namespace InventoryService.Models
{
    public class Product
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        // Concurrency token: an UPDATE only succeeds if the stock is still the value we read.
        // This makes deduct/restore safe when several requests hit the same product at once.
        [ConcurrencyCheck]
        [Range(0, int.MaxValue, ErrorMessage = "Stock quantity cannot be negative.")]
        public int StockQuantity { get; set; }

        [Range(0.01, double.MaxValue, ErrorMessage = "Price must be greater than zero.")]
        public decimal Price { get; set; }
    }
}
