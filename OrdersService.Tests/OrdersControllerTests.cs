using System.Net;
using Microsoft.AspNetCore.Mvc;
using OrdersService.Models;
using Xunit;

namespace OrdersService.Tests;

public class OrdersControllerTests
{
    // ------------------------------------------------------------------ create

    [Fact]
    public async Task CreateOrder_SufficientStock_ReturnsConfirmed_AndReservesStock()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 45, "k"));

        var result = await h.Controller.CreateOrder(new CreateOrderRequest { ProductId = 1, Quantity = 5 });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var order = Assert.IsType<Order>(created.Value);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.False(string.IsNullOrEmpty(order.ClientRequestId)); // server generated one

        var request = Assert.Single(h.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/api/stock-adjustments", request.Path);
        var body = TestHarness.ParseBody(request.Body);
        Assert.Equal(1, body.GetProperty("productId").GetInt32());
        Assert.Equal(-5, body.GetProperty("quantityChange").GetInt32());  // reservation = negative change
    }

    [Fact]
    public async Task CreateOrder_InsufficientStock_ReturnsRejected()
    {
        var h = new TestHarness((_, _) => Responses.InsufficientStock(2));

        var result = await h.Controller.CreateOrder(new CreateOrderRequest { ProductId = 1, Quantity = 10 });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var order = Assert.IsType<Order>(created.Value);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Single(h.Context.Orders); // rejected orders are still persisted
    }

    [Fact]
    public async Task CreateOrder_InvalidProductId_ProductNotFound()
    {
        var h = new TestHarness((_, _) => Responses.NotFound());

        var result = await h.Controller.CreateOrder(new CreateOrderRequest { ProductId = 1, Quantity = 1 });

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        var error = Assert.IsType<ErrorResponse>(notFound.Value);
        Assert.Equal("Product 1 not found in inventory.", error.Error);
        Assert.Empty(h.Context.Orders);
    }

    [Fact]
    public async Task CreateOrder_InventoryUnavailable_Returns503_AndSavesNothing()
    {
        var h = new TestHarness((_, _) => Responses.Down());

        var result = await h.Controller.CreateOrder(new CreateOrderRequest { ProductId = 1, Quantity = 1 });

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
        Assert.Empty(h.Context.Orders);
    }

    [Fact]
    public async Task CreateOrder_SameClientRequestIdTwice_ReturnsSameOrder_AndReservesOnce()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 45, "k"));
        var request = new CreateOrderRequest { ProductId = 1, Quantity = 5, ClientRequestId = "double-click-123" };

        var first = await h.Controller.CreateOrder(request);
        var second = await h.Controller.CreateOrder(request);

        var created = Assert.IsType<CreatedAtActionResult>(first.Result);
        var firstOrder = Assert.IsType<Order>(created.Value);

        var ok = Assert.IsType<OkObjectResult>(second.Result);      // 200, not 201
        var secondOrder = Assert.IsType<Order>(ok.Value);

        Assert.Equal(firstOrder.Id, secondOrder.Id);
        Assert.Single(h.Context.Orders);
        Assert.Single(h.Handler.Requests);                          // inventory was only asked once
    }

    [Fact]
    public async Task CreateOrder_ReservationAlreadyApplied_IsStillConfirmed()
    {
        // Reservation succeeded on an earlier attempt whose response was lost: the retry
        // must confirm the order without Inventory decrementing a second time.
        var h = new TestHarness((_, _) => Responses.AlreadyApplied(1, 45, "k"));

        var result = await h.Controller.CreateOrder(new CreateOrderRequest { ProductId = 1, Quantity = 5, ClientRequestId = "retry-1" });

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(OrderStatus.Confirmed, Assert.IsType<Order>(created.Value).Status);
    }

    // ------------------------------------------------------------------ cancel

    private static async Task<Order> SeedOrderAsync(TestHarness h, string status, int quantity = 2)
    {
        var order = new Order { ProductId = 1, Quantity = quantity, DateCreated = DateTime.UtcNow, Status = status, ClientRequestId = Guid.NewGuid().ToString("N") };
        if (status == OrderStatus.CancellationPending) order.CancellationRequestedAt = DateTime.UtcNow;
        h.Context.Orders.Add(order);
        await h.Context.SaveChangesAsync();
        return order;
    }

    [Fact]
    public async Task CancelOrder_Confirmed_RestoresStock_AndMarksCancelled()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 12, "order-1-cancel"));
        var order = await SeedOrderAsync(h, OrderStatus.Confirmed, quantity: 3);

        var result = await h.Controller.CancelOrder(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(OrderStatus.Cancelled, Assert.IsType<Order>(ok.Value).Status);

        var request = Assert.Single(h.Handler.Requests);
        var body = TestHarness.ParseBody(request.Body);
        Assert.Equal(+3, body.GetProperty("quantityChange").GetInt32());                 // stock returned
        Assert.Equal($"order-{order.Id}-cancel", body.GetProperty("referenceKey").GetString());
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.CancellationPending)]
    public async Task CancelOrder_NotConfirmed_ReturnsConflict_AndNeverCallsInventory(string status)
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 12, "k"));
        var order = await SeedOrderAsync(h, status);

        var result = await h.Controller.CancelOrder(order.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.IsType<ErrorResponse>(conflict.Value);
        Assert.Empty(h.Handler.Requests);
        Assert.Equal(status, (await h.Context.Orders.FindAsync(order.Id))!.Status);
    }

    [Fact]
    public async Task CancelOrder_UnknownOrder_ReturnsNotFound()
    {
        var h = new TestHarness((_, _) => Responses.Applied(1, 12, "k"));

        var result = await h.Controller.CancelOrder(999);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task CancelOrder_InventoryDown_BecomesCancellationPending_Returns202()
    {
        var h = new TestHarness((_, _) => Responses.Down());
        var order = await SeedOrderAsync(h, OrderStatus.Confirmed);

        var result = await h.Controller.CancelOrder(order.Id);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var pending = Assert.IsType<Order>(accepted.Value);
        Assert.Equal(OrderStatus.CancellationPending, pending.Status);
        Assert.NotNull(pending.CancellationRequestedAt);
    }

    [Fact]
    public async Task CancelOrder_InventoryDown_MaxPendingReached_Returns503_AndStaysConfirmed()
    {
        var h = new TestHarness((_, _) => Responses.Down(), maxPending: 5);
        for (var i = 0; i < 5; i++) await SeedOrderAsync(h, OrderStatus.CancellationPending);
        var order = await SeedOrderAsync(h, OrderStatus.Confirmed);

        var result = await h.Controller.CancelOrder(order.Id);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
        Assert.Contains("maximum of 5", Assert.IsType<ErrorResponse>(status.Value).Error);
        Assert.Equal(OrderStatus.Confirmed, (await h.Context.Orders.FindAsync(order.Id))!.Status);
    }

    // ------------------------------------------------------------------ undo

    [Fact]
    public async Task UndoCancellation_Pending_RevertsToConfirmed()
    {
        // Inventory is down: the lookup fails, which must not block the undo.
        var h = new TestHarness((_, _) => Responses.Down());
        var order = await SeedOrderAsync(h, OrderStatus.CancellationPending);

        var result = await h.Controller.UndoCancellation(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var restored = Assert.IsType<Order>(ok.Value);
        Assert.Equal(OrderStatus.Confirmed, restored.Status);
        Assert.Null(restored.CancellationRequestedAt);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Rejected)]
    public async Task UndoCancellation_NotPending_ReturnsConflict(string status)
    {
        var h = new TestHarness((_, _) => Responses.NotFound());
        var order = await SeedOrderAsync(h, status);

        var result = await h.Controller.UndoCancellation(order.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(status, (await h.Context.Orders.FindAsync(order.Id))!.Status);
    }

    [Fact]
    public async Task UndoCancellation_WhenInventoryAlreadyRestoredStock_FinalizesAsCancelled()
    {
        // GET /api/stock-adjustments/order-{id}-cancel -> 200 means the stock already went back.
        var h = new TestHarness((req, _) => req.Method == HttpMethod.Get
            ? Responses.Json200(new { referenceKey = "order-1-cancel" })
            : Responses.Down());
        var order = await SeedOrderAsync(h, OrderStatus.CancellationPending);

        var result = await h.Controller.UndoCancellation(order.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(OrderStatus.Cancelled, (await h.Context.Orders.FindAsync(order.Id))!.Status);
    }

    // ------------------------------------------------------------------ pending count

    [Fact]
    public async Task GetPendingCancellations_ReportsCountAndMax()
    {
        var h = new TestHarness((_, _) => Responses.Down(), maxPending: 5);
        await SeedOrderAsync(h, OrderStatus.CancellationPending);
        await SeedOrderAsync(h, OrderStatus.CancellationPending);
        await SeedOrderAsync(h, OrderStatus.Confirmed);

        var result = await h.Controller.GetPendingCancellations();

        Assert.Equal(new PendingCancellationsResponse(2, 5), result.Value);
    }
}
