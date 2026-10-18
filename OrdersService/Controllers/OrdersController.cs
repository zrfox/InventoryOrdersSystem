using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrdersService.Data;
using OrdersService.Models;
using OrdersService.Services;
using System.Net;
using System.Net.Http.Json;

namespace OrdersService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private const int MaxIdempotencyKeyLength = 100;

    private readonly OrdersDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OrderCancellationService _cancellation;
    private readonly IdempotencyLocks _idempotencyLocks;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        OrdersDbContext context,
        IHttpClientFactory httpClientFactory,
        OrderCancellationService cancellation,
        IdempotencyLocks idempotencyLocks,
        ILogger<OrdersController> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _cancellation = cancellation;
        _idempotencyLocks = idempotencyLocks;
        _logger = logger;
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
        var order = await _context.Orders.FindAsync(id);
        if (order == null) return NotFound();
        return order;
    }

    // POST /api/orders
    // pattern: request DTO/command model
    // Return type breakdown: Task == js promise, the type we await to unwrap for async return types
    // ActionResult<T> means the type COULD be EITHER type T OR an ActionResult
    //
    // Idempotency-Key (optional header): if the same key is sent again, the order created the
    // first time is returned (200) instead of a second order being created (201).
    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(
        CreateOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        idempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();

        if (idempotencyKey == null)
        {
            return await CreateOrderCoreAsync(request, null);
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            return BadRequest($"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters.");
        }

        // Only one request per key at a time; the second one waits and then finds the order below.
        using var keyLock = await _idempotencyLocks.AcquireAsync(idempotencyKey);

        var existing = await _context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey);

        if (existing != null)
        {
            if (existing.ProductId != request.ProductId || existing.Quantity != request.Quantity)
            {
                return Conflict("This Idempotency-Key was already used for a different order.");
            }

            _logger.LogInformation(
                "Duplicate submission ignored: Idempotency-Key already created order {OrderId}.", existing.Id);
            return Ok(existing);
        }

        return await CreateOrderCoreAsync(request, idempotencyKey);
    }

    // POST /api/orders/5/cancel
    // 200 = cancelled and stock returned, 202 = CancellationPending (InventoryService unavailable),
    // 404 = no such order, 409 = rejected / already cancelled / already pending, 429 = too many pending.
    [HttpPost("{id}/cancel")]
    public async Task<ActionResult<Order>> CancelOrder(int id)
    {
        try
        {
            var result = await _cancellation.RequestCancellationAsync(id);
            return ToResponse("Cancel", id, result);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentChange("Cancel", id);
        }
    }

    // POST /api/orders/5/cancel/undo
    // CancellationPending -> Confirmed. 409 if the order is not pending, or if a stock return
    // may already have reached InventoryService.
    [HttpPost("{id}/cancel/undo")]
    public async Task<ActionResult<Order>> UndoCancellation(int id)
    {
        try
        {
            var result = await _cancellation.UndoCancellationAsync(id);
            return ToResponse("Undo cancellation", id, result);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ConcurrentChange("Undo cancellation", id);
        }
    }

    private ActionResult<Order> ToResponse(string operation, int orderId, CancellationResult result)
    {
        switch (result.Kind)
        {
            case CancellationResultKind.Cancelled:
            case CancellationResultKind.Undone:
                _logger.LogInformation("{Operation}: {Message}", operation, result.Message);
                return Ok(result.Order);

            case CancellationResultKind.Pending:
                _logger.LogWarning("{Operation}: {Message}", operation, result.Message);
                return Accepted(result.Order);

            case CancellationResultKind.NotFound:
                _logger.LogError("{Operation} refused for order {OrderId}: {Reason}", operation, orderId, result.Message);
                return NotFound(result.Message);

            case CancellationResultKind.LimitReached:
                _logger.LogError("{Operation} refused for order {OrderId}: {Reason}", operation, orderId, result.Message);
                return StatusCode(StatusCodes.Status429TooManyRequests, result.Message);

            default:
                // The "cannot be cancelled again" errors land here: shown in the terminal
                // and returned to the caller (the front-end shows them as a pop-up).
                _logger.LogError("{Operation} refused for order {OrderId}: {Reason}", operation, orderId, result.Message);
                return Conflict(result.Message);
        }
    }

    private ActionResult<Order> ConcurrentChange(string operation, int orderId)
    {
        var message = $"Order {orderId} was changed by another request at the same time. Refresh and try again.";
        _logger.LogError("{Operation} refused for order {OrderId}: {Reason}", operation, orderId, message);
        return Conflict(message);
    }

    private async Task<ActionResult<Order>> CreateOrderCoreAsync(CreateOrderRequest request, string? idempotencyKey)
    {
        var order = new Order
        {
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            DateCreated = DateTime.UtcNow,
            Status = OrderStatus.Pending,
            IdempotencyKey = idempotencyKey
        };

        // Get an HttpClient configured for InventoryService
        var client = _httpClientFactory.CreateClient("InventoryService");

        try
        {
            // Call InventoryService to get the product. Base address set for client in Program.cs
            var response = await client.GetAsync($"api/products/{order.ProductId}");

            // early return/guard clause pattern. No else if, just ifs.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return NotFound($"Product {order.ProductId} not found in inventory.");
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return StatusCode(502, "Failed to authenticate with inventory service.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return StatusCode(502, "Inventory service returned an unexpected error.");
            }

            // Dto = data transfer object.
            // Microservice principle: do not reference code of other services/projects/modules.
            var product = await response.Content.ReadFromJsonAsync<ProductDto>();

            if (product == null)
            {
                return BadRequest("Failed to read product data.");
            }

            if (product.StockQuantity >= order.Quantity)
            {
                // Ask Inventory to take the stock in one atomic step. (Previously: PUT the whole
                // product with a new absolute quantity, which could overwrite a concurrent change.)
                var deductResponse = await client.PostAsJsonAsync(
                    $"api/products/{order.ProductId}/stock/deduct",
                    new DeductStockRequest { Quantity = order.Quantity });

                if (deductResponse.StatusCode == HttpStatusCode.Conflict)
                {
                    // Someone else took the stock between our GET and the deduct.
                    order.Status = OrderStatus.Rejected;
                }
                else if (deductResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return NotFound($"Product {order.ProductId} not found in inventory.");
                }
                else if (!deductResponse.IsSuccessStatusCode)
                {
                    return StatusCode(500, "Failed to update inventory.");
                }
                else
                {
                    order.Status = OrderStatus.Confirmed;
                }
            }
            else
            {
                // Not enough stock — reject
                order.Status = OrderStatus.Rejected;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogError(ex, "Order for product {ProductId} not placed: InventoryService is unavailable.", order.ProductId);
            return StatusCode(503, "InventoryService is unavailable, so the order could not be placed.");
        }

        // notice: only order gets added to sqlexpress (or InMemoryDB for testing)
        _context.Orders.Add(order);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }
}
