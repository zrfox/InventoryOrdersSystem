namespace InventoryService.Models;

public class StockAdjustmentResponse
{
    public int ProductId { get; set; }
    public int StockQuantity { get; set; }
    public bool AlreadyApplied { get; set; }
    public string ReferenceKey { get; set; } = string.Empty;
}
