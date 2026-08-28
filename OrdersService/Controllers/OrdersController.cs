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

    // GET /api/orders/id
    // using a database id in the url is not best practice since it reveals information about the database
    // for instance, it makes the data enumerable. GUID can work as an alternative. 
    // However, fine for this application's purposes.
    [HttpGet("{id}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var product = await _context.Orders.FindAsync(id);
        if (product == null) return NotFound();
        return product;
    }

    // POST /api/orders
    // pattern: request DTO/command model
    // Return type breakdown: Task == js promise, the type we await to unwrap for async return types
    // ActionResult<Order> is a generic type. It is NOT an ActionResult
    // ActionResult<T> means the type COULD be EITHER type T OR an ActionResult
    // think of ActionResult<T> like:
    // "type is EITHER ActionResult derived subclass
    // (i.e CreatedAtActionResult, OkObjectResult, etc.)
    // OR T"
    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(CreateOrderRequest request)
    {
        var order = new Order
        {
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            DateCreated = DateTime.UtcNow,
            Status = "Pending"
        };

        // Get an HttpClient configured for InventoryService
        var client = _httpClientFactory.CreateClient("InventoryService");

        // Call InventoryService to get the product. Base address set for client in Program.cs
        var response = await client.GetAsync($"api/products/{order.ProductId}");

        // early return/guard clause pattern. No else if, just ifs.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound($"Product {order.ProductId} not found in inventory.");
        }
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            return StatusCode(502, "Failed to authenticate with inventory service.");
        }
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(502, "Inventory service returned an unexpected error.");
        }

        // Deserialize the product from the response of InventoryService
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

        // notice: only oorder gets added to sqlexpress (or InMemoryDB for testing)
        // ProductDto is just for deserializing JSON that comes back over HTTP from InventoryService
        _context.Orders.Add(order);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }
}