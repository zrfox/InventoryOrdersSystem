using System.ComponentModel.DataAnnotations;

namespace OrdersService.Models;

// created to prevent user from inputting id, ProductId, DateCreated, and Status
public class CreateOrderRequest
{
    [Range(1, int.MaxValue)]
    public int ProductId { get; set; }

    [Range(1, int.MaxValue)]
    public int Quantity { get; set; }
}