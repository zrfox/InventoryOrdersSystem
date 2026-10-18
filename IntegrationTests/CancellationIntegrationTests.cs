using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using OrdersService.Models;
using Xunit;

namespace Integration.Tests;

public class CancellationIntegrationTests
{
    [Fact]
    public async Task Cancel_ConfirmedOrder_ReturnsStockToInventory_AndSecondCancelIsRefused()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 3);
        var order = await system.PlaceOrderAsync(product.Id, 2);
        Assert.Equal(1, await system.GetStockAsync(product.Id));

        var cancel = await system.CancelAsync(order.Id);
        var cancelAgain = await system.CancelAsync(order.Id);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.Content.ReadFromJsonAsync<Order>(TestSystem.JsonOptions);
        Assert.Equal(OrderStatus.Cancelled, cancelled!.Status);

        Assert.Equal(HttpStatusCode.Conflict, cancelAgain.StatusCode);
        Assert.Contains("already cancelled", await cancelAgain.Content.ReadAsStringAsync());
        Assert.Equal(3, await system.GetStockAsync(product.Id)); // back to 3, and not 5
    }

    [Fact]
    public async Task Cancel_RejectedOrder_IsRefused()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 1);
        var order = await system.PlaceOrderAsync(product.Id, 2);
        Assert.Equal(OrderStatus.Rejected, order.Status);

        var cancel = await system.CancelAsync(order.Id);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal(OrderStatus.Rejected, (await system.GetOrderAsync(order.Id)).Status);
        Assert.Equal(1, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task Cancel_WhileInventoryIsDown_BecomesPending_ThenCompletesWhenInventoryReturns()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);
        var order = await system.PlaceOrderAsync(product.Id, 2);

        system.Inventory.IsUp = false;
        var cancel = await system.CancelAsync(order.Id);

        Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        var pending = await cancel.Content.ReadFromJsonAsync<Order>(TestSystem.JsonOptions);
        Assert.Equal(OrderStatus.CancellationPending, pending!.Status);
        Assert.Equal(3, await system.GetStockAsync(product.Id)); // nothing returned yet

        // No double cancelling while it is pending.
        var cancelAgain = await system.CancelAsync(order.Id);
        Assert.Equal(HttpStatusCode.Conflict, cancelAgain.StatusCode);

        system.Inventory.IsUp = true; // the background worker should now finish the job on its own
        await system.WaitForStatusAsync(order.Id, OrderStatus.Cancelled);
        Assert.Equal(5, await system.GetStockAsync(product.Id));

        await Task.Delay(500); // several more worker passes: the stock must not be returned again
        Assert.Equal(5, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task UndoCancellation_ReturnsOrderToConfirmed_AndStockIsNeverReturned()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);
        var order = await system.PlaceOrderAsync(product.Id, 2);
        system.Inventory.IsUp = false;
        await system.CancelAsync(order.Id);

        var undo = await system.UndoAsync(order.Id);
        var undoAgain = await system.UndoAsync(order.Id);

        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, undoAgain.StatusCode); // nothing left to undo

        system.Inventory.IsUp = true;
        await Task.Delay(500); // give the worker time to (wrongly) act if it were going to

        Assert.Equal(OrderStatus.Confirmed, (await system.GetOrderAsync(order.Id)).Status);
        Assert.Equal(3, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task AtMostFiveOrdersCanBeCancellationPending()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 10);
        var orders = new List<Order>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await system.PlaceOrderAsync(product.Id, 1));
        }
        Assert.Equal(4, await system.GetStockAsync(product.Id));

        system.Inventory.IsUp = false;
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await system.CancelAsync(orders[i].Id)).StatusCode);
        }
        var sixth = await system.CancelAsync(orders[5].Id);

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, (await system.GetOrderAsync(orders[5].Id)).Status);

        // Once Inventory is back the five pending ones drain, and the sixth can be cancelled normally.
        system.Inventory.IsUp = true;
        for (var i = 0; i < 5; i++)
        {
            await system.WaitForStatusAsync(orders[i].Id, OrderStatus.Cancelled);
        }
        Assert.Equal(HttpStatusCode.OK, (await system.CancelAsync(orders[5].Id)).StatusCode);
        Assert.Equal(10, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CancellationPending_SurvivesOrdersServiceRestart_AndIsRetriedOnStartup()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);
        var order = await system.PlaceOrderAsync(product.Id, 2);

        system.Inventory.IsUp = false;
        Assert.Equal(HttpStatusCode.Accepted, (await system.CancelAsync(order.Id)).StatusCode);

        await system.StopOrdersServiceAsync();   // OrdersService goes down with the cancellation still pending
        system.Inventory.IsUp = true;            // Inventory comes back while Orders is not running
        Assert.Equal(3, await system.GetStockAsync(product.Id));

        system.StartOrdersService();             // brand-new host, same database

        await system.WaitForStatusAsync(order.Id, OrderStatus.Cancelled);
        Assert.Equal(5, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SubmittingTheSameOrderTwice_WithTheSameIdempotencyKey_CreatesOneOrder()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);
        var key = Guid.NewGuid().ToString();

        var first = await system.PostOrderAsync(product.Id, 2, key);
        var second = await system.PostOrderAsync(product.Id, 2, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var firstOrder = await first.Content.ReadFromJsonAsync<Order>(TestSystem.JsonOptions);
        var secondOrder = await second.Content.ReadFromJsonAsync<Order>(TestSystem.JsonOptions);
        Assert.Equal(firstOrder!.Id, secondOrder!.Id);

        var allOrders = await system.OrdersClient.GetFromJsonAsync<List<Order>>("/api/orders", TestSystem.JsonOptions);
        Assert.Single(allOrders!);
        Assert.Equal(3, await system.GetStockAsync(product.Id)); // deducted once, not twice
    }

    [Fact]
    public async Task Inventory_RestoreStock_WithTheSameOperationId_IsAppliedOnlyOnce()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);
        var request = new { operationId = Guid.NewGuid(), quantity = 2 };
        var url = $"/api/products/{product.Id}/stock/restore";

        var first = await system.InventoryClient.PostAsJsonAsync(url, request);
        var second = await system.InventoryClient.PostAsJsonAsync(url, request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        Assert.False((await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("applied").GetBoolean());
        Assert.Equal(7, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task Inventory_DeductStock_RefusesToGoBelowZero()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 1);
        var url = $"/api/products/{product.Id}/stock/deduct";

        var tooMany = await system.InventoryClient.PostAsJsonAsync(url, new { quantity = 2 });
        var exact = await system.InventoryClient.PostAsJsonAsync(url, new { quantity = 1 });

        Assert.Equal(HttpStatusCode.Conflict, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        Assert.Equal(0, await system.GetStockAsync(product.Id));
    }
}
