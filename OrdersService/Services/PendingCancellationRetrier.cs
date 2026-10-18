using Microsoft.EntityFrameworkCore;
using OrdersService.Data;
using OrdersService.Models;

namespace OrdersService.Services;

public sealed record RetrySummary(int Pending, int Cancelled, int StillPending, bool InventoryUnavailable, bool Skipped = false);

public sealed class PendingCancellationRetrier
{
    private readonly OrdersDbContext _context;
    private readonly OrderCancellationProcessor _processor;
    private readonly RetryCoordinator _coordinator;
    private readonly ILogger<PendingCancellationRetrier> _logger;

    public PendingCancellationRetrier(OrdersDbContext context, OrderCancellationProcessor processor,
        RetryCoordinator coordinator, ILogger<PendingCancellationRetrier> logger)
    {
        _context = context;
        _processor = processor;
        _coordinator = coordinator;
        _logger = logger;
    }

    public async Task<RetrySummary> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_coordinator.TryEnter())
        {
            _logger.LogDebug("Retry run skipped: another run is in progress.");
            return new RetrySummary(0, 0, 0, false, Skipped: true);
        }

        try
        {
            var pending = await _context.Orders
                .Where(o => o.Status == OrderStatus.CancellationPending)
                .OrderBy(o => o.CancellationRequestedAt).ThenBy(o => o.Id)
                .ToListAsync(ct);

            if (pending.Count == 0)
                return new RetrySummary(0, 0, 0, false);

            _logger.LogInformation("Retrying {Count} pending cancellation(s).", pending.Count);

            var cancelled = 0;
            var inventoryUnavailable = false;

            foreach (var order in pending)
            {
                var attempt = await _processor.TryCompleteCancellationAsync(order, ct);

                if (attempt.Kind == CancellationAttemptKind.Completed)
                {
                    await _context.SaveChangesAsync(ct);
                    cancelled++;
                    continue;
                }

                if (attempt.Kind == CancellationAttemptKind.InventoryUnavailable)
                {
                    inventoryUnavailable = true;
                    _logger.LogInformation("InventoryService still unavailable; {Remaining} cancellation(s) remain pending and will be retried.",
                        pending.Count - cancelled);
                    break; // no point hammering a service that is down
                }

                _logger.LogWarning("Cancellation of order {OrderId} could not be completed: {Message}", order.Id, attempt.Message);
            }

            return new RetrySummary(pending.Count, cancelled, pending.Count - cancelled, inventoryUnavailable);
        }
        finally
        {
            _coordinator.Exit();
        }
    }
}
