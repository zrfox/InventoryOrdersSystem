using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrdersService.Data;
using OrdersService.Models;
using OrdersService.Services;

namespace OrdersService.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrdersDbContext _context;
    private readonly IInventoryClient _inventory;
    private readonly OrderCancellationProcessor _cancellation;
    private readonly PendingCancellationRetrier _retrier;
    private readonly CancellationRetryOptions _options;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        OrdersDbContext context,
        IInventoryClient inventory,
        OrderCancellationProcessor cancellation,
        PendingCancellationRetrier retrier,
        IOptions<CancellationRetryOptions> options,
        ILogger<OrdersController> logger)
    {
        _context = context;
        _inventory = inventory;
        _cancellation = cancellation;
        _retrier = retrier;
        _options = options.Value;
        _logger = logger;
    }

    public static string ReservationReferenceKey(string clientRequestId) => $"order-{clientRequestId}-reserve";

    // GET /api/orders
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Order>>> GetOrders()
    {
        return await _context.Orders.OrderBy(o => o.Id).ToListAsync();
    }

    // GET /api/orders/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Order>> GetOrder(int id)
    {
        var order = await _context.Orders.FindAsync(id);
        if (order == null) return NotFound(new ErrorResponse($"Order {id} not found."));
        return order;
    }

    // GET /api/orders/pending-cancellations  -> { count, max }
    [HttpGet("pending-cancellations")]
    public async Task<ActionResult<PendingCancellationsResponse>> GetPendingCancellations()
    {
        var count = await _context.Orders.CountAsync(o => o.Status == OrderStatus.CancellationPending);
        return new PendingCancellationsResponse(count, _options.MaxPending);
    }

    // POST /api/orders
    // 201 new order (Confirmed or Rejected), 200 duplicate submission -> the order created earlier,
    // 404 unknown product, 503 inventory unavailable, 502 inventory error.
    [HttpPost]
    public async Task<ActionResult<Order>> CreateOrder(CreateOrderRequest request, CancellationToken ct = default)
    {
        var clientRequestId = string.IsNullOrWhiteSpace(request.ClientRequestId)
            ? Guid.NewGuid().ToString("N")
            : request.ClientRequestId.Trim();

        // Idempotency: the same submission (double-click, retried request) returns the same order.
        var existing = await _context.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.ClientRequestId == clientRequestId, ct);
        if (existing != null)
        {
            _logger.LogWarning("Duplicate order submission {ClientRequestId} ignored; returning existing order {OrderId}.",
                clientRequestId, existing.Id);
            return Ok(existing);
        }

        var order = new Order
        {
            ProductId = request.ProductId,
            Quantity = request.Quantity,
            DateCreated = DateTime.UtcNow,
            Status = OrderStatus.Pending,
            ClientRequestId = clientRequestId
        };

        // Ask Inventory to reserve the stock atomically. The reference key makes the
        // reservation idempotent, so a racing duplicate cannot decrement stock twice.
        var reservation = await _inventory.AdjustStockAsync(
            order.ProductId, -order.Quantity, ReservationReferenceKey(clientRequestId), ct);

        switch (reservation.Status)
        {
            case StockAdjustmentStatus.Applied:
            case StockAdjustmentStatus.AlreadyApplied:
                order.Status = OrderStatus.Confirmed;
                break;

            case StockAdjustmentStatus.InsufficientStock:
                order.Status = OrderStatus.Rejected;
                _logger.LogInformation("Order for product {ProductId} x{Quantity} rejected: {Reason}",
                    order.ProductId, order.Quantity, reservation.Message);
                break;

            case StockAdjustmentStatus.ProductNotFound:
                return NotFound(new ErrorResponse($"Product {order.ProductId} not found in inventory."));

            case StockAdjustmentStatus.Unauthorized:
                return StatusCode(502, new ErrorResponse("Failed to authenticate with inventory service."));

            case StockAdjustmentStatus.Unavailable:
                _logger.LogWarning("Order for product {ProductId} not placed: {Reason}", order.ProductId, reservation.Message);
                return StatusCode(503, new ErrorResponse("Inventory service is unavailable; the order was not placed. Please try again."));

            default:
                return StatusCode(502, new ErrorResponse(reservation.Message ?? "Inventory service returned an unexpected error."));
        }

        _context.Orders.Add(order);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Unique index on ClientRequestId: a concurrent duplicate got there first.
            _context.Entry(order).State = EntityState.Detached;
            var duplicate = await _context.Orders.AsNoTracking()
                .FirstOrDefaultAsync(o => o.ClientRequestId == clientRequestId, ct);
            if (duplicate == null) throw;

            _logger.LogWarning("Concurrent duplicate submission {ClientRequestId}; returning order {OrderId}.", clientRequestId, duplicate.Id);
            return Ok(duplicate);
        }

        _logger.LogInformation("Order {OrderId} created with status {Status}.", order.Id, order.Status);
        return CreatedAtAction(nameof(GetOrder), new { id = order.Id }, order);
    }

    // POST /api/orders/5/cancel
    // 200 cancelled (stock restored), 202 CancellationPending (inventory down),
    // 409 order cannot be cancelled, 503 pending limit reached, 502 inventory error.
    [HttpPost("{id:int}/cancel")]
    public async Task<ActionResult<Order>> CancelOrder(int id, CancellationToken ct = default)
    {
        var order = await _context.Orders.FindAsync(new object[] { id }, ct);
        if (order == null) return NotFound(new ErrorResponse($"Order {id} not found."));

        string? refusal = order.Status switch
        {
            OrderStatus.Confirmed => null,
            OrderStatus.Cancelled => $"Order {id} is already cancelled and cannot be cancelled again.",
            OrderStatus.Rejected => $"Order {id} was rejected and cannot be cancelled.",
            OrderStatus.CancellationPending => $"Order {id} already has a cancellation pending; it cannot be cancelled twice. Undo the pending cancellation if you want to keep the order.",
            _ => $"Order {id} is in status '{order.Status}' and cannot be cancelled."
        };

        if (refusal != null)
        {
            _logger.LogWarning("Cancel refused: {Reason}", refusal);
            return Conflict(new ErrorResponse(refusal));
        }

        var attempt = await _cancellation.TryCompleteCancellationAsync(order, ct);

        switch (attempt.Kind)
        {
            case CancellationAttemptKind.Completed:
                await _context.SaveChangesAsync(ct);
                return Ok(order);

            case CancellationAttemptKind.InventoryUnavailable:
            {
                var pendingCount = await _context.Orders.CountAsync(o => o.Status == OrderStatus.CancellationPending, ct);
                if (pendingCount >= _options.MaxPending)
                {
                    var message = $"Inventory service is unavailable and the maximum of {_options.MaxPending} pending cancellations has been reached. Order {id} was not cancelled; try again later.";
                    _logger.LogWarning("Cancel refused: {Reason}", message);
                    return StatusCode(503, new ErrorResponse(message));
                }

                order.Status = OrderStatus.CancellationPending;
                order.CancellationRequestedAt = DateTime.UtcNow;
                order.DateUpdated = order.CancellationRequestedAt;
                await _context.SaveChangesAsync(ct);

                _logger.LogWarning("Inventory unavailable; order {OrderId} marked CancellationPending ({Count}/{Max}). It will be retried automatically.",
                    id, pendingCount + 1, _options.MaxPending);
                return Accepted(order);
            }

            default:
                _logger.LogError("Order {OrderId} could not be cancelled: {Message}", id, attempt.Message);
                return StatusCode(502, new ErrorResponse($"Order {id} was not cancelled: {attempt.Message}"));
        }
    }

    // POST /api/orders/5/undo-cancellation
    // 200 back to Confirmed, 409 nothing to undo (or inventory already restored the stock).
    [HttpPost("{id:int}/undo-cancellation")]
    public async Task<ActionResult<Order>> UndoCancellation(int id, CancellationToken ct = default)
    {
        var order = await _context.Orders.FindAsync(new object[] { id }, ct);
        if (order == null) return NotFound(new ErrorResponse($"Order {id} not found."));

        if (order.Status != OrderStatus.CancellationPending)
        {
            var message = $"Order {id} is not pending cancellation (status: {order.Status}); there is nothing to undo.";
            _logger.LogWarning("Undo refused: {Reason}", message);
            return Conflict(new ErrorResponse(message));
        }

        // Guard against the window where Inventory restored the stock but we crashed before
        // recording it: if the cancel key was applied, the cancellation is already real.
        var state = await _inventory.GetAdjustmentStateAsync(OrderCancellationProcessor.CancellationReferenceKey(order.Id), ct);
        if (state == AdjustmentLookup.Applied)
        {
            order.Status = OrderStatus.Cancelled;
            order.DateUpdated = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            var message = $"Inventory had already restored the stock for order {id} before the undo arrived; the order is now Cancelled and cannot be reinstated.";
            _logger.LogWarning("Undo refused: {Reason}", message);
            return Conflict(new ErrorResponse(message));
        }

        order.Status = OrderStatus.Confirmed;
        order.CancellationRequestedAt = null;
        order.DateUpdated = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _logger.LogInformation("Pending cancellation of order {OrderId} undone; order is Confirmed again.", id);
        return Ok(order);
    }

    // POST /api/orders/retry-cancellations  (manual trigger; the worker does this automatically)
    [HttpPost("retry-cancellations")]
    public async Task<ActionResult<RetrySummary>> RetryCancellations(CancellationToken ct = default)
    {
        return Ok(await _retrier.RunOnceAsync(ct));
    }
}
