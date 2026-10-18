using OrdersService.Models;
using Xunit;

namespace OrdersService.Tests;

public class PendingCancellationRetrierTests
{
    private static async Task<Order> SeedPendingAsync(TestHarness h, int quantity)
    {
        var order = new Order
        {
            ProductId = 1, Quantity = quantity, DateCreated = DateTime.UtcNow,
            Status = OrderStatus.CancellationPending, CancellationRequestedAt = DateTime.UtcNow,
            ClientRequestId = Guid.NewGuid().ToString("N")
        };
        h.Context.Orders.Add(order);
        await h.Context.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task RunOnce_InventoryBackOnline_CancelsAllPendingOrders()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 10, "k"));
        var a = await SeedPendingAsync(h, 2);
        var b = await SeedPendingAsync(h, 3);

        var summary = await h.Retrier.RunOnceAsync();

        Assert.Equal(2, summary.Pending);
        Assert.Equal(2, summary.Cancelled);
        Assert.Equal(0, summary.StillPending);
        Assert.False(summary.InventoryUnavailable);
        Assert.Equal(OrderStatus.Cancelled, (await h.Context.Orders.FindAsync(a.Id))!.Status);
        Assert.Equal(OrderStatus.Cancelled, (await h.Context.Orders.FindAsync(b.Id))!.Status);

        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.All(h.Handler.Requests, r => Assert.True(TestHarness.ParseBody(r.Body).GetProperty("quantityChange").GetInt32() > 0));
    }

    [Fact]
    public async Task RunOnce_InventoryStillDown_KeepsOrdersPending_AndStopsAfterFirstFailure()
    {
        var h = new TestHarness((_, _) => Responses.Down());
        await SeedPendingAsync(h, 1);
        await SeedPendingAsync(h, 1);

        var summary = await h.Retrier.RunOnceAsync();

        Assert.True(summary.InventoryUnavailable);
        Assert.Equal(2, summary.StillPending);
        Assert.All(h.Context.Orders, o => Assert.Equal(OrderStatus.CancellationPending, o.Status));
        Assert.Single(h.Handler.Requests); // doesn't hammer a service that is down
    }

    [Fact]
    public async Task RunOnce_StockAlreadyRestoredEarlier_MarksCancelledWithoutRestoringTwice()
    {
        // e.g. OrdersService crashed after Inventory applied the restore but before saving "Cancelled".
        var h = new TestHarness((_, _) => Responses.AlreadyApplied(1, 10, "k"));
        var order = await SeedPendingAsync(h, 2);

        var summary = await h.Retrier.RunOnceAsync();

        Assert.Equal(1, summary.Cancelled);
        Assert.Equal(OrderStatus.Cancelled, (await h.Context.Orders.FindAsync(order.Id))!.Status);
    }

    [Fact]
    public async Task RunOnce_NothingPending_DoesNotCallInventory()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 10, "k"));

        var summary = await h.Retrier.RunOnceAsync();

        Assert.Equal(0, summary.Pending);
        Assert.Empty(h.Handler.Requests);
    }

    [Fact]
    public async Task RunOnce_UsesDeterministicReferenceKeyPerOrder()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 10, "k"));
        var order = await SeedPendingAsync(h, 2);

        await h.Retrier.RunOnceAsync();

        var body = TestHarness.ParseBody(Assert.Single(h.Handler.Requests).Body);
        Assert.Equal($"order-{order.Id}-cancel", body.GetProperty("referenceKey").GetString());
    }
}
