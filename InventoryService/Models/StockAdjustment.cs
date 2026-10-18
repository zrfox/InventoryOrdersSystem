using System.ComponentModel.DataAnnotations;

namespace InventoryService.Models;

// Ledger of every stock change applied through the stock-adjustments endpoint.
// ReferenceKey is unique: applying the same key twice is a no-op (idempotency).
public class StockAdjustment
{
    public int Id { get; set; }

    [Required]
    [MaxLength(128)]
    public string ReferenceKey { get; set; } = string.Empty;

    public int ProductId { get; set; }

    // negative = reserve/decrement, positive = restore/increment
    public int QuantityChange { get; set; }

    public DateTime DateApplied { get; set; }
}
