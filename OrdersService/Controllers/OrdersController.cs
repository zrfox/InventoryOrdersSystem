using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrdersService.Data;
using OrdersService.Models;
using System.Net.Http.Json;

namespace OrdersService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrdersDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;

    public OrdersController(OrdersDbContext context, IHttpClientFactory httpClientFactory)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
    }

    // GET /api/orders
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrders()
    {
        return await _context.Orders.ToListAsync();
    }

    // GET /api/orders/5
    [HttpGet("{id}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var product = await _context.Orders.FindAsync(id);
        if (product == null) return NotFound();
        return product;
    }

    // POST /api/orders
    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(Order order)
    {
        // Get an HttpClient configured for InventoryService
        var client = _httpClientFactory.CreateClient("InventoryService");

        // Call InventoryService to get the product. Base address set for client in Program.cs
        var response = await client.GetAsync($"api/products/{order.ProductId}");

        if (!response.IsSuccessStatusCode)
        {
            return BadRequest($"Product {order.ProductId} not found in inventory.");
        }

        // Deserialize the product from the response
        // tell compiler what shape to expect, maps JSON fields onto C# properties ProductDto. Matches shape of Product type.
        // Dto = data transfer object. 
        // Microservice principle: do not reference code of other services/projects/modules.
        // (Don't reference InventoryService code)
        // If Product class (in InventoryService) changes, only breaks if field OrderService uses is changed. 
        var product = await response.Content.ReadFromJsonAsync<ProductDto>();

        if (product == null)
        {
            return BadRequest("Failed to read product data.");
        }

        // Set order date and check stock
        order.DateCreated = DateTime.UtcNow;

        if (product.StockQuantity >= order.Quantity)
        {
            // Enough stock — decrement and confirm
            product.StockQuantity -= order.Quantity;

            var updateResponse = await client.PutAsJsonAsync(
                $"api/products/{product.Id}", product);

            if (!updateResponse.IsSuccessStatusCode)
            {
                return StatusCode(500, "Failed to update inventory.");
            }

            order.Status = "Confirmed";
        }
        else
        {
            // Not enough stock — reject
            order.Status = "Rejected";
        }

        _context.Orders.Add(order);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }
}