using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrdersService.Data;
using OrdersService.Models;

namespace OrdersService.Services;

public enum CancellationResultKind
{
    Cancelled,      // stock returned, order is Cancelled
    Pending,        // InventoryService could not confirm; order is CancellationPending
    Undone,         // CancellationPending -> Confirmed
    NotFound,
    InvalidState,   // e.g. already cancelled / rejected / already pending
    LimitReached    // too many orders already CancellationPending
}

public sealed record CancellationResult(CancellationResultKind Kind, Order? Order, string Message);

// All cancellation rules live here so the controller (user clicks) and the
// background worker (retries) behave identically.
public class OrderCancellationService
{
    private readonly OrdersDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CancellationGate _gate;
    private readonly CancellationOptions _options;
    private readonly ILogger<OrderCancellationService> _logger;

    public OrderCancellationService(
        OrdersDbContext context,
        IHttpClientFactory httpClientFactory,
        CancellationGate gate,
        IOptions<CancellationOptions> options,
        ILogger<OrderCancellationService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _gate = gate;
        _options = options.Value;
        _logger = logger;
    }

    private enum AttemptOutcome
    {
        Completed,    // Inventory confirmed the stock return (now or on an earlier attempt)
        ProductGone,  // Inventory says the product no longer exists: nothing to return
        NotSent,      // could not connect at all: the request definitely never arrived
        Refused,      // Inventory answered 4xx: definitely not applied
        Unknown       // timeout / 5xx / dropped connection: may or may not have been applied
    }

    public async Task<CancellationResult> RequestCancellationAsync(int orderId, CancellationToken ct = default)
    {
        using var lease = await _gate.EnterAsync(ct);

        var order = await LoadFreshAsync(orderId, ct);
        if (order == null)
        {
            return new CancellationResult(CancellationResultKind.NotFound, null, $"Order {orderId} not found.");
        }

        string? refusal = order.Status switch
        {
            OrderStatus.Confirmed => null,
            OrderStatus.Cancelled => $"Order {orderId} is already cancelled and cannot be cancelled again.",
            OrderStatus.Rejected => $"Order {orderId} was rejected and cannot be cancelled.",
            OrderStatus.CancellationPending => $"Order {orderId} already has a cancellation pending and cannot be cancelled again.",
            _ => $"Order {orderId} has status '{order.Status}' and cannot be cancelled."
        };
        if (refusal != null)
        {
            return new CancellationResult(CancellationResultKind.InvalidState, order, refusal);
        }

        var pendingCount = await _context.Orders.CountAsync(o => o.Status == OrderStatus.CancellationPending, ct);
        if (pendingCount >= _options.MaxPending)
        {
            return new CancellationResult(CancellationResultKind.LimitReached, order,
                $"{pendingCount} cancellations are already waiting for InventoryService (limit {_options.MaxPending}). " +
                "Try again once InventoryService is back and they have been processed.");
        }

        // Write-ahead: record the intent BEFORE calling InventoryService. If this process dies
        // mid-call, the order is already CancellationPending with its CancellationId saved, so
        // the retry after restart re-sends the same id and stock can't be returned twice.
        var wasInDoubt = order.CancellationInDoubt;
        order.Status = OrderStatus.CancellationPending;
        order.CancellationId ??= Guid.NewGuid();
        order.CancellationRequestedAt = DateTime.UtcNow;
        order.CancellationAttempts = 0;
        order.CancellationInDoubt = true;
        await _context.SaveChangesAsync(ct);

        var outcome = await TryReturnStockAsync(order, ct);
        await ApplyOutcomeAsync(order, outcome, wasInDoubt);

        if (order.Status == OrderStatus.Cancelled)
        {
            return new CancellationResult(CancellationResultKind.Cancelled, order,
                $"Order {orderId} cancelled; stock returned to inventory.");
        }

        return new CancellationResult(CancellationResultKind.Pending, order,
            $"InventoryService is unavailable. Order {orderId} is CancellationPending and will be cancelled automatically when InventoryService responds.");
    }

    public async Task<CancellationResult> UndoCancellationAsync(int orderId, CancellationToken ct = default)
    {
        using var lease = await _gate.EnterAsync(ct);

        var order = await LoadFreshAsync(orderId, ct);
        if (order == null)
        {
            return new CancellationResult(CancellationResultKind.NotFound, null, $"Order {orderId} not found.");
        }

        if (order.Status != OrderStatus.CancellationPending)
        {
            var reason = order.Status == OrderStatus.Cancelled
                ? $"Order {orderId} has already been cancelled; it is too late to undo."
                : $"Order {orderId} has no pending cancellation to undo (status: {order.Status}).";
            return new CancellationResult(CancellationResultKind.InvalidState, order, reason);
        }

        if (order.CancellationInDoubt)
        {
            return new CancellationResult(CancellationResultKind.InvalidState, order,
                $"The cancellation of order {orderId} may already have reached InventoryService, so it cannot be undone safely. " +
                "It will complete automatically once InventoryService responds.");
        }

        // CancellationId is kept on purpose: if this order is cancelled again later,
        // the same id is re-used and Inventory still returns the stock at most once.
        order.Status = OrderStatus.Confirmed;
        order.CancellationRequestedAt = null;
        order.CancellationAttempts = 0;
        await _context.SaveChangesAsync(ct);

        return new CancellationResult(CancellationResultKind.Undone, order,
            $"Cancellation of order {orderId} undone; the order is Confirmed again.");
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        return _context.Orders.CountAsync(o => o.Status == OrderStatus.CancellationPending, ct);
    }

    // One retry pass over every CancellationPending order. Returns how many were completed.
    // Called by CancellationRetryWorker forever; there is deliberately no attempt limit.
    public async Task<int> RetryPendingAsync(CancellationToken ct = default)
    {
        var pendingIds = await _context.Orders
            .AsNoTracking()
            .Where(o => o.Status == OrderStatus.CancellationPending)
            .OrderBy(o => o.CancellationRequestedAt)
            .ThenBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync(ct);

        var completed = 0;

        foreach (var orderId in pendingIds)
        {
            ct.ThrowIfCancellationRequested();

            using var lease = await _gate.EnterAsync(ct);

            // Re-read under the gate: the user may have undone it, or it may be done already.
            var order = await LoadFreshAsync(orderId, ct);
            if (order == null || order.Status != OrderStatus.CancellationPending) continue;

            // Same write-ahead idea as above: flag "a request may be out there" before sending,
            // so a crash mid-call can never leave an undoable order whose stock was returned.
            var wasInDoubt = order.CancellationInDoubt;
            if (!wasInDoubt)
            {
                order.CancellationInDoubt = true;
                await _context.SaveChangesAsync(ct);
            }

            var outcome = await TryReturnStockAsync(order, ct);
            await ApplyOutcomeAsync(order, outcome, wasInDoubt);

            if (order.Status == OrderStatus.Cancelled)
            {
                completed++;
                _logger.LogInformation(
                    "Pending cancellation completed: order {OrderId} is Cancelled after {Attempts} attempt(s).",
                    order.Id, order.CancellationAttempts);
            }
            else if (outcome == AttemptOutcome.NotSent)
            {
                // InventoryService is not running; the remaining orders would fail the same way.
                _logger.LogDebug("InventoryService still unreachable; will retry pending cancellations later.");
                break;
            }
        }

        return completed;
    }

    private async Task<Order?> LoadFreshAsync(int orderId, CancellationToken ct)
    {
        var order = await _context.Orders.FindAsync(new object[] { orderId }, ct);
        if (order == null) return null;

        // FindAsync returns an already-tracked instance as-is; make sure we see the database's values.
        await _context.Entry(order).ReloadAsync(ct);
        return _context.Entry(order).State == EntityState.Detached ? null : order;
    }

    private async Task ApplyOutcomeAsync(Order order, AttemptOutcome outcome, bool wasInDoubt)
    {
        order.CancellationAttempts++;

        switch (outcome)
        {
            case AttemptOutcome.Completed:
            case AttemptOutcome.ProductGone:
                order.Status = OrderStatus.Cancelled;
                order.CancelledAt = DateTime.UtcNow;
                order.CancellationInDoubt = false;
                break;

            case AttemptOutcome.NotSent:
            case AttemptOutcome.Refused:
                // This attempt definitely changed nothing; keep whatever earlier attempts left behind.
                order.CancellationInDoubt = wasInDoubt;
                break;

            case AttemptOutcome.Unknown:
                order.CancellationInDoubt = true;
                break;
        }

        // Not cancellable: once Inventory has answered we want the result recorded.
        await _context.SaveChangesAsync(CancellationToken.None);
    }

    private async Task<AttemptOutcome> TryReturnStockAsync(Order order, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.InventoryTimeoutSeconds));

        try
        {
            var client = _httpClientFactory.CreateClient("InventoryService");

            using var response = await client.PostAsJsonAsync(
                $"api/products/{order.ProductId}/stock/restore",
                new RestoreStockRequest { OperationId = order.CancellationId!.Value, Quantity = order.Quantity },
                timeout.Token);

            if (response.IsSuccessStatusCode) return AttemptOutcome.Completed;

            if (response.StatusCode == HttpStatusCode.NotFound && await IsProductNotFoundAsync(response, timeout.Token))
            {
                _logger.LogWarning(
                    "Product {ProductId} no longer exists in inventory; order {OrderId} is cancelled without returning stock.",
                    order.ProductId, order.Id);
                return AttemptOutcome.ProductGone;
            }

            _logger.LogWarning(
                "InventoryService answered {StatusCode} when returning stock for order {OrderId}; will retry.",
                (int)response.StatusCode, order.Id);

            return (int)response.StatusCode >= 500 ? AttemptOutcome.Unknown : AttemptOutcome.Refused;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the service is shutting down; CancellationInDoubt stays true in the database
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timed out returning stock for order {OrderId}; the outcome is unknown, will retry.", order.Id);
            return AttemptOutcome.Unknown;
        }
        catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.ConnectionError
                                                  or HttpRequestError.NameResolutionError
                                                  or HttpRequestError.SecureConnectionError)
        {
            // No connection was ever established, so Inventory cannot have seen the request.
            return AttemptOutcome.NotSent;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Connection problem while returning stock for order {OrderId}; the outcome is unknown, will retry.", order.Id);
            return AttemptOutcome.Unknown;
        }
    }

    private static async Task<bool> IsProductNotFoundAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Distinguishes "this product does not exist" from a 404 for any other reason
        // (wrong URL, older InventoryService without this endpoint, ...).
        try
        {
            var error = await response.Content.ReadFromJsonAsync<InventoryErrorDto>(cancellationToken: ct);
            return error?.Code == "product_not_found";
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
