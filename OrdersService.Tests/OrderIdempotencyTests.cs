using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrdersService.Models;
using OrdersService.Services;
using Xunit;

namespace OrdersService.Tests;

public class OrderIdempotencyTests
{
    private static readonly CreateOrderRequest TwoWidgets = new() { ProductId = FakeInventory.ProductId, Quantity = 2 };

    [Fact]
    public async Task CreateOrder_SameIdempotencyKeyTwice_CreatesOneOrderAndDeductsStockOnce()
    {
        var inventory = new FakeInventory { Stock = 10 };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);

        var first = await controller.CreateOrder(TwoWidgets, "submit-1");
        var second = await controller.CreateOrder(TwoWidgets, "submit-1");

        var created = Assert.IsType<Order>(Assert.IsType<CreatedAtActionResult>(first.Result).Value);
        var replayed = Assert.IsType<Order>(Assert.IsType<OkObjectResult>(second.Result).Value);
        Assert.Equal(created.Id, replayed.Id);
        Assert.Equal(1, await context.Orders.CountAsync());
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task CreateOrder_SameKeySubmittedConcurrently_CreatesOneOrder()
    {
        // Two requests arriving at the same time = two controllers with their own DbContext,
        // sharing the database and the (singleton) idempotency locks.
        var dbName = Guid.NewGuid().ToString();
        var inventory = new FakeInventory { Stock = 10 };
        var locks = new IdempotencyLocks();
        using var contextA = TestSupport.NewContext(dbName);
        using var contextB = TestSupport.NewContext(dbName);
        var controllerA = TestSupport.CreateController(contextA, inventory, idempotencyLocks: locks);
        var controllerB = TestSupport.CreateController(contextB, inventory, idempotencyLocks: locks);

        await Task.WhenAll(
            controllerA.CreateOrder(TwoWidgets, "double-click"),
            controllerB.CreateOrder(TwoWidgets, "double-click"));

        using var verify = TestSupport.NewContext(dbName);
        Assert.Equal(1, await verify.Orders.CountAsync());
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task CreateOrder_SameKeyWithDifferentOrder_Returns409()
    {
        var inventory = new FakeInventory { Stock = 10 };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);
        await controller.CreateOrder(TwoWidgets, "submit-1");

        var different = new CreateOrderRequest { ProductId = FakeInventory.ProductId, Quantity = 3 };
        var result = await controller.CreateOrder(different, "submit-1");

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(1, await context.Orders.CountAsync());
        Assert.Equal(8, inventory.Stock);
    }

    [Fact]
    public async Task CreateOrder_WithoutKey_EachCallIsANewOrder()
    {
        var inventory = new FakeInventory { Stock = 10 };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);

        await controller.CreateOrder(TwoWidgets);
        await controller.CreateOrder(TwoWidgets);

        Assert.Equal(2, await context.Orders.CountAsync());
        Assert.Equal(6, inventory.Stock);
    }

    [Fact]
    public async Task CreateOrder_StockTakenBetweenCheckAndDeduct_IsRejected()
    {
        // GET says 10 in stock, but the fake only has... exactly what it says; ask for more
        // than is there to exercise the deduct-side 409 path via a second controller call.
        var inventory = new FakeInventory { Stock = 2 };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);

        await controller.CreateOrder(TwoWidgets);              // takes the last 2
        var result = await controller.CreateOrder(TwoWidgets); // nothing left

        var order = Assert.IsType<Order>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal(OrderStatus.Rejected, order.Status);
        Assert.Equal(0, inventory.Stock);
    }

    [Fact]
    public async Task CreateOrder_InventoryDown_Returns503_AndSavesNoOrder()
    {
        var inventory = new FakeInventory { Stock = 10, Mode = FakeInventoryMode.Down };
        using var context = TestSupport.NewContext();
        var controller = TestSupport.CreateController(context, inventory);

        var result = await controller.CreateOrder(TwoWidgets, "submit-1");

        var unavailable = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, unavailable.StatusCode);
        Assert.Equal(0, await context.Orders.CountAsync());
    }
}
