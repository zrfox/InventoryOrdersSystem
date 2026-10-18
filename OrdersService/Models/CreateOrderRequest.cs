using System.ComponentModel.DataAnnotations;

namespace OrdersService.Models;

public class CreateOrderRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }

    // Optional idempotency key. The front-end sends a fresh GUID per submission
    // and re-uses it if it retries, so a double-click or a retried request can
    // never create two orders. If omitted, the server generates one.
    [MaxLength(64)]
    public string? ClientRequestId { get; set; }
}
