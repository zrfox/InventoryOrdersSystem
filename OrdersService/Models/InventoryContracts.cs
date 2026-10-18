namespace OrdersService.Models;

public class DeductStockRequest
{
    public int Quantity { get; set; }
}

public class RestoreStockRequest
{
    public Guid OperationId { get; set; }
    public int Quantity { get; set; }
}

public class InventoryErrorDto
{
    public string? Code { get; set; }
    public string? Message { get; set; }
}
