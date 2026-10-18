using System.ComponentModel.DataAnnotations;

namespace InventoryService.Models;

public class StockAdjustmentRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    // Must be non-zero. Negative reserves stock, positive restores it.
    public int QuantityChange { get; set; }

    [Required]
    [MaxLength(128)]
    public string ReferenceKey { get; set; } = string.Empty;
}
