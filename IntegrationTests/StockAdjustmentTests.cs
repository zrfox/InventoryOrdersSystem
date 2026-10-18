extern alias InventoryApp;
extern alias OrdersApp;
using System.Net;
using System.Net.Http.Json;
using InventoryService.Models;
using Xunit;

namespace Integration.Tests;

public class StockAdjustmentTests
{
    [Fact]
    public async Task SameReferenceKey_IsAppliedOnlyOnce()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 10);
        var client = sys.InventoryClient();
        var request = new { productId = product.Id, quantityChange = -3, referenceKey = "key-1" };

        var first = await client.PostAsJsonAsync("/api/stock-adjustments", request);
        var second = await client.PostAsJsonAsync("/api/stock-adjustments", request);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<StockAdjustmentResponse>(TestSystem.Json);
        var b = await second.Content.ReadFromJsonAsync<StockAdjustmentResponse>(TestSystem.Json);
        Assert.False(a!.AlreadyApplied);
        Assert.True(b!.AlreadyApplied);
        Assert.Equal(7, a.StockQuantity);
        Assert.Equal(7, b.StockQuantity);
        Assert.Equal(7, await sys.GetStockAsync(product.Id));

        var lookup = await client.GetAsync("/api/stock-adjustments/key-1");
        Assert.Equal(HttpStatusCode.OK, lookup.StatusCode);
    }

    [Fact]
    public async Task RestoreWithSameKeyTwice_RestoresOnce()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 2);
        var client = sys.InventoryClient();
        var request = new { productId = product.Id, quantityChange = 5, referenceKey = "order-1-cancel" };

        await client.PostAsJsonAsync("/api/stock-adjustments", request);
        await client.PostAsJsonAsync("/api/stock-adjustments", request);

        Assert.Equal(7, await sys.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task InsufficientStock_ReturnsConflict_AndRecordsNothing()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 2);
        var client = sys.InventoryClient();

        var response = await client.PostAsJsonAsync("/api/stock-adjustments",
            new { productId = product.Id, quantityChange = -5, referenceKey = "key-too-many" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(TestSystem.Json);
        Assert.Equal(2, error!.StockQuantity);
        Assert.Equal(2, await sys.GetStockAsync(product.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/stock-adjustments/key-too-many")).StatusCode);
    }

    [Fact]
    public async Task UnknownProduct_ReturnsNotFound()
    {
        await using var sys = new TestSystem();
        var response = await sys.InventoryClient().PostAsJsonAsync("/api/stock-adjustments",
            new { productId = 999, quantityChange = -1, referenceKey = "key-404" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ZeroQuantityChange_ReturnsBadRequest()
    {
        await using var sys = new TestSystem();
        var product = await sys.SeedProductAsync("Widget", 2);

        var response = await sys.InventoryClient().PostAsJsonAsync("/api/stock-adjustments",
            new { productId = product.Id, quantityChange = 0, referenceKey = "key-zero" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
