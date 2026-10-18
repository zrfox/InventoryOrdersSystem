using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using OrdersService.Controllers;
using OrdersService.Models;
using Xunit;

namespace OrdersService.Tests;

// In these tests a Confirmed order of quantity 2 is seeded directly, and FakeInventory.Stock
// starts at 8: "there were 10, this order is holding 2". A completed cancellation brings it back to 10.
public class OrderCancellationTests
{
    [Fact]
    public async Task Cancel_ConfirmedOrder_ReturnsStockAndMarksCancelled()
    {
        var inventory = new FakeInventory { Stock = 8 };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);

        var result = await controller.CancelOrder(order.Id);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var cancelled = Assert.IsType<Order>(ok.Value);
        Assert.Equal(OrderStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        Assert.Equal(10, inventory.Stock);
    }

    [Fact]
    public async Task Cancel_AlreadyCancelledOrder_Returns409_LogsError_AndDoesNotReturnStockAgain()
    {
        var inventory = new FakeInventory { Stock = 8 };
        var logger = new RecordingLogger<OrdersController>();
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory, logger: logger);
        await controller.CancelOrder(order.Id);

        var second = await controller.CancelOrder(order.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Contains("already cancelled", Assert.IsType<string>(conflict.Value));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("already cancelled"));
        Assert.Equal(10, inventory.Stock);
        Assert.Equal(1, inventory.RestoreRequests);
    }

    [Fact]
    public async Task Cancel_RejectedOrder_Returns409_AndLogsError()
    {
        var inventory = new FakeInventory { Stock = 8 };
        var logger = new RecordingLogger<OrdersController>();
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Rejected);
        var controller = TestSupport.CreateController(context, inventory, logger: logger);

        var result = await controller.CancelOrder(order.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("rejected"));
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(8, inventory.Stock);
        Assert.Equal(0, inventory.RestoreRequests);
    }

    [Fact]
    public async Task Cancel_UnknownOrder_Returns404()
    {
        var inventory = new FakeInventory { Stock = 8 };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);

        var result = await controller.CancelOrder(12345);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Cancel_InventoryDown_Returns202_AndOrderBecomesCancellationPending()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);

        var result = await controller.CancelOrder(order.Id);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var pending = Assert.IsType<Order>(accepted.Value);
        Assert.Equal(OrderStatus.CancellationPending, pending.Status);
        Assert.NotNull(pending.CancellationId);
        Assert.False(pending.CancellationInDoubt); // connection refused: nothing can have reached Inventory
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task Cancel_OrderAlreadyCancellationPending_Returns409()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        await controller.CancelOrder(order.Id);

        var second = await controller.CancelOrder(order.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(second.Result);
        Assert.Contains("already has a cancellation pending", Assert.IsType<string>(conflict.Value));
        Assert.Equal(OrderStatus.CancellationPending, order.Status);
    }

    [Fact]
    public async Task Cancel_WhenFiveOrdersArePending_SixthIsRefusedAndStaysConfirmed()
    {
        var inventory = new FakeInventory { Stock = 0, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);
        var orders = new List<Order>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed));
        }

        foreach (var order in orders.Take(5))
        {
            Assert.IsType<AcceptedResult>((await controller.CancelOrder(order.Id)).Result);
        }
        var sixth = await controller.CancelOrder(orders[5].Id);

        var refused = Assert.IsType<ObjectResult>(sixth.Result);
        Assert.Equal(429, refused.StatusCode);
        Assert.Equal(OrderStatus.Confirmed, orders[5].Status);
    }

    [Fact]
    public async Task Undo_FreesASlot_SoAnotherOrderCanBecomePending()
    {
        var inventory = new FakeInventory { Stock = 0, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);
        var orders = new List<Order>();
        for (var i = 0; i < 6; i++)
        {
            orders.Add(await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed));
        }
        foreach (var order in orders.Take(5))
        {
            await controller.CancelOrder(order.Id);
        }

        Assert.IsType<OkObjectResult>((await controller.UndoCancellation(orders[0].Id)).Result);
        var sixth = await controller.CancelOrder(orders[5].Id);

        Assert.IsType<AcceptedResult>(sixth.Result);
        Assert.Equal(OrderStatus.CancellationPending, orders[5].Status);
    }

    [Fact]
    public async Task Retry_WhenInventoryComesBack_CancelsAndReturnsStockExactlyOnce()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        var service = TestSupport.CreateCancellationService(context, inventory);
        await controller.CancelOrder(order.Id);

        inventory.Mode = FakeInventoryMode.Up;
        var completedFirstPass = await service.RetryPendingAsync();
        var completedSecondPass = await service.RetryPendingAsync();

        Assert.Equal(1, completedFirstPass);
        Assert.Equal(0, completedSecondPass);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(10, inventory.Stock);
    }

    [Fact]
    public async Task Retry_WhileInventoryStaysDown_KeepsRetryingAndStaysPending()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        var service = TestSupport.CreateCancellationService(context, inventory);
        await controller.CancelOrder(order.Id);

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0, await service.RetryPendingAsync());
        }

        Assert.Equal(OrderStatus.CancellationPending, order.Status);
        Assert.Equal(4, order.CancellationAttempts); // the original attempt + 3 retries
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task Retry_AfterRestart_PicksUpPendingCancellationFromDatabase()
    {
        var dbName = Guid.NewGuid().ToString();
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        int orderId;

        // First run of OrdersService: the cancel is requested while Inventory is down.
        using (var context = TestSupport.NewContext(dbName))
        {
            var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
            orderId = order.Id;
            await TestSupport.CreateController(context, inventory).CancelOrder(orderId);
        }

        // "Restart": nothing is carried over in memory, only the database.
        inventory.Mode = FakeInventoryMode.Up;
        using (var context = TestSupport.NewContext(dbName))
        {
            var service = TestSupport.CreateCancellationService(context, inventory);

            var completed = await service.RetryPendingAsync();

            Assert.Equal(1, completed);
            var reloaded = await context.Orders.FindAsync(orderId);
            Assert.Equal(OrderStatus.Cancelled, reloaded!.Status);
            Assert.Equal(10, inventory.Stock);
        }
    }

    [Fact]
    public async Task Retry_WhenFirstAttemptWasAppliedButReplyWasLost_DoesNotReturnStockTwice()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.AppliesThenLosesReply };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        var service = TestSupport.CreateCancellationService(context, inventory);

        var first = await controller.CancelOrder(order.Id);

        Assert.IsType<AcceptedResult>(first.Result);
        Assert.Equal(OrderStatus.CancellationPending, order.Status);
        Assert.True(order.CancellationInDoubt);
        Assert.Equal(10, inventory.Stock); // Inventory did apply it; Orders just doesn't know yet

        inventory.Mode = FakeInventoryMode.Up;
        var completed = await service.RetryPendingAsync();

        Assert.Equal(1, completed);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(2, inventory.RestoreRequests);
        Assert.Equal(10, inventory.Stock); // still 10: the second request was recognised as a repeat
    }

    [Fact]
    public async Task Undo_PendingCancellation_RestoresConfirmed_AndIsNotRetried()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        var service = TestSupport.CreateCancellationService(context, inventory);
        await controller.CancelOrder(order.Id);

        var undo = await controller.UndoCancellation(order.Id);
        inventory.Mode = FakeInventoryMode.Up;
        var completed = await service.RetryPendingAsync();

        var ok = Assert.IsType<OkObjectResult>(undo.Result);
        Assert.Equal(OrderStatus.Confirmed, Assert.IsType<Order>(ok.Value).Status);
        Assert.Equal(0, completed);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task Undo_ThenCancelAgain_ReturnsStockOnce()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        await controller.CancelOrder(order.Id);
        var firstCancellationId = order.CancellationId;
        await controller.UndoCancellation(order.Id);

        inventory.Mode = FakeInventoryMode.Up;
        var again = await controller.CancelOrder(order.Id);

        Assert.IsType<OkObjectResult>(again.Result);
        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(firstCancellationId, order.CancellationId);
        Assert.Equal(10, inventory.Stock);
        Assert.Single(inventory.AppliedOperations);
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Rejected)]
    [InlineData(OrderStatus.Cancelled)]
    public async Task Undo_WhenOrderIsNotCancellationPending_Returns409(string status)
    {
        var inventory = new FakeInventory { Stock = 8 };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, status);
        var controller = TestSupport.CreateController(context, inventory);

        var result = await controller.UndoCancellation(order.Id);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(status, order.Status);
    }

    [Fact]
    public async Task Undo_WhenAnAttemptMayHaveReachedInventory_Returns409_AndStaysPending()
    {
        var inventory = new FakeInventory { Stock = 8, Mode = FakeInventoryMode.Hanging };
        using var context = TestSupport.NewContext();
        var order = await TestSupport.SeedOrderAsync(context, OrderStatus.Confirmed);
        var controller = TestSupport.CreateController(context, inventory);
        await controller.CancelOrder(order.Id);

        var undo = await controller.UndoCancellation(order.Id);

        var conflict = Assert.IsType<ConflictObjectResult>(undo.Result);
        Assert.Contains("cannot be undone", Assert.IsType<string>(conflict.Value));
        Assert.Equal(OrderStatus.CancellationPending, order.Status);
    }
}
