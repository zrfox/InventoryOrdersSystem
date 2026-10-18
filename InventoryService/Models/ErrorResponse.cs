namespace InventoryService.Models;

public sealed record ErrorResponse(string Error, int? StockQuantity = null);
