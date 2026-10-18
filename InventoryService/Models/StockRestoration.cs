namespace InventoryService.Models;

// One row per stock return that has been applied. OperationId is unique, which is
// what guarantees the same cancellation can never put stock back twice.
public class StockRestoration
{
    public int Id { get; set; }
    public Guid OperationId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public DateTime AppliedAt { get; set; }
}
