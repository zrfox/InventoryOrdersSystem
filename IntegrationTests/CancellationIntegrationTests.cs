extern alias InventoryApp;
extern alias OrdersApp;
using System.Net;
using System.Net.Http.Json;
using OrdersService.Models;
using OrdersService.Services;
using Xunit;

namespace Integration.Tests;

public class CancellationIntegrationTests
{
    [Fact]
    public async Task CancelConfirmedOrder_RestoresStock_AndMarksCancelled()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 2);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(3, await sys.GetStockAsync(product.Id));

        var response = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await response.Content.ReadFromJsonAsync<Order>(TestSystem.Json);
        Assert.Equal(OrderStatus.Cancelled, cancelled!.Status);
        Assert.Equal(5, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CancelOrder_Twice_SecondAttemptIsRefused_AndStockRestoredOnce()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 2);

        var first = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);
        var second = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var error = await second.Content.ReadFromJsonAsync<ErrorResponse>(TestSystem.Json);
        Assert.Contains("already cancelled", error!.Error);
        Assert.Equal(5, await sys.GetStockAsync(product.Id)); // not 7
    }

    [Fact]
    public async Task CancelRejectedOrder_IsRefused()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 1);
        var order = await sys.PlaceOrderAsync(product.Id, 5);
        Assert.Equal(OrderStatus.Rejected, order.Status);

        var response = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(TestSystem.Json);
        Assert.Contains("rejected", error!.Error);
        Assert.Equal(1, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CancelOrder_WhileInventoryDown_BecomesCancellationPending_ThenCompletesOnRetry()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 2);

        sys.Outage.InventoryDown = true;
        var response = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(OrderStatus.CancellationPending, (await sys.GetOrderAsync(order.Id)).Status);

        // still down: retry changes nothing
        var retryWhileDown = await sys.OrdersClient().PostAsync("/api/orders/retry-cancellations", null);
        var downSummary = await retryWhileDown.Content.ReadFromJsonAsync<RetrySummary>(TestSystem.Json);
        Assert.True(downSummary!.InventoryUnavailable);
        Assert.Equal(OrderStatus.CancellationPending, (await sys.GetOrderAsync(order.Id)).Status);

        // Inventory comes back
        sys.Outage.InventoryDown = false;
        var retry = await sys.OrdersClient().PostAsync("/api/orders/retry-cancellations", null);
        var summary = await retry.Content.ReadFromJsonAsync<RetrySummary>(TestSystem.Json);

        Assert.Equal(1, summary!.Cancelled);
        Assert.Equal(OrderStatus.Cancelled, (await sys.GetOrderAsync(order.Id)).Status);
        Assert.Equal(5, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CancellationPending_SurvivesOrdersServiceRestart_AndCompletesOnStartup()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 10);
        var order = await sys.PlaceOrderAsync(product.Id, 4);

        sys.Outage.InventoryDown = true;
        var response = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // "stop" OrdersService, bring Inventory back, "start" OrdersService with the worker on
        sys.Outage.InventoryDown = false;
        await sys.RestartOrdersServiceAsync(retryWorkerEnabled: true, retryIntervalSeconds: 1);

        var final = await sys.WaitForStatusAsync(order.Id, OrderStatus.Cancelled, TimeSpan.FromSeconds(20));
        Assert.Equal(OrderStatus.Cancelled, final.Status);
        Assert.Equal(10, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CancellationPending_CannotBeCancelledAgain()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 1);

        sys.Outage.InventoryDown = true;
        Assert.Equal(HttpStatusCode.Accepted, (await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null)).StatusCode);

        var again = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var error = await again.Content.ReadFromJsonAsync<ErrorResponse>(TestSystem.Json);
        Assert.Contains("pending", error!.Error);
    }

    [Fact]
    public async Task UndoCancellationPending_RevertsToConfirmed_WithoutTouchingStock()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 2);

        sys.Outage.InventoryDown = true;
        Assert.Equal(HttpStatusCode.Accepted, (await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null)).StatusCode);

        var undo = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/undo-cancellation", null);
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, (await sys.GetOrderAsync(order.Id)).Status);

        sys.Outage.InventoryDown = false;
        Assert.Equal(3, await sys.GetStockAsync(product.Id)); // still reserved

        // a retry run has nothing to do
        var retry = await sys.OrdersClient().PostAsync("/api/orders/retry-cancellations", null);
        var summary = await retry.Content.ReadFromJsonAsync<RetrySummary>(TestSystem.Json);
        Assert.Equal(0, summary!.Pending);

        // and the order can be cancelled for real later – stock restored exactly once
        var cancel = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(5, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task UndoCancellation_OnOrderThatIsNotPending_IsRefused()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 5);
        var order = await sys.PlaceOrderAsync(product.Id, 1);

        var undo = await sys.OrdersClient().PostAsync($"/api/orders/{order.Id}/undo-cancellation", null);

        Assert.Equal(HttpStatusCode.Conflict, undo.StatusCode);
    }

    [Fact]
    public async Task MaxFivePendingCancellations_SixthRequestIsRefused_AllFiveCompleteLater()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 10);
        var orders = new List<Order>();
        for (var i = 0; i < 6; i++) orders.Add(await sys.PlaceOrderAsync(product.Id, 1));
        Assert.Equal(4, await sys.GetStockAsync(product.Id));

        sys.Outage.InventoryDown = true;
        for (var i = 0; i < 5; i++)
        {
            var r = await sys.OrdersClient().PostAsync($"/api/orders/{orders[i].Id}/cancel", null);
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        }

        var sixth = await sys.OrdersClient().PostAsync($"/api/orders/{orders[5].Id}/cancel", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, sixth.StatusCode);
        var error = await sixth.Content.ReadFromJsonAsync<ErrorResponse>(TestSystem.Json);
        Assert.Contains("maximum of 5", error!.Error);
        Assert.Equal(OrderStatus.Confirmed, (await sys.GetOrderAsync(orders[5].Id)).Status);

        var pending = await sys.OrdersClient().GetFromJsonAsync<PendingCancellationsResponse>("/api/orders/pending-cancellations", TestSystem.Json);
        Assert.Equal(new PendingCancellationsResponse(5, 5), pending);

        sys.Outage.InventoryDown = false;
        var retry = await sys.OrdersClient().PostAsync("/api/orders/retry-cancellations", null);
        var summary = await retry.Content.ReadFromJsonAsync<RetrySummary>(TestSystem.Json);

        Assert.Equal(5, summary!.Cancelled);
        Assert.Equal(9, await sys.GetStockAsync(product.Id));
        Assert.Equal(OrderStatus.Confirmed, (await sys.GetOrderAsync(orders[5].Id)).Status);
    }

    [Fact]
    public async Task DuplicateClientRequestId_DoesNotCreateSecondOrder_OrDecrementTwice()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 10);
        var requestId = Guid.NewGuid().ToString();

        var first = await sys.PlaceOrderAsync(product.Id, 2, requestId);
        var second = await sys.PlaceOrderAsync(product.Id, 2, requestId);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(8, await sys.GetStockAsync(product.Id));

        var all = await sys.OrdersClient().GetFromJsonAsync<List<Order>>("/api/orders", TestSystem.Json);
        Assert.Single(all!);
    }

    [Fact]
    public async Task ConcurrentSubmissionsWithSameClientRequestId_DecrementStockOnlyOnce()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 10);
        var requestId = Guid.NewGuid().ToString();

        var tasks = Enumerable.Range(0, 5).Select(_ => sys.PlaceOrderAsync(product.Id, 2, requestId)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, o => Assert.Equal(OrderStatus.Confirmed, o.Status));
        Assert.Equal(8, await sys.GetStockAsync(product.Id)); // the reservation key is idempotent in Inventory
    }
}
