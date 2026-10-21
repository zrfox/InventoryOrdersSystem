using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using InventoryService.Models;
using Xunit;

namespace Integration.Tests;

public class StockEditIntegrationTests
{
    [Fact]
    public async Task CreateProduct_ThroughTheApi_IsListedWithItsStock()
    {
        await using var system = new TestSystem();

        var response = await system.InventoryClient.PostAsJsonAsync("/api/products",
            new { name = "Gadget", price = 4.50m, stockQuantity = 7 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<Product>(TestSystem.JsonOptions);
        Assert.Equal("Gadget", created!.Name);
        Assert.Equal(7, await system.GetStockAsync(created.Id));
    }

    [Fact]
    public async Task SetStock_WhenExpectedMatchesCurrent_Updates()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        var response = await system.InventoryClient.PutAsJsonAsync($"/api/products/{product.Id}/stock",
            new { expectedStockQuantity = 5, stockQuantity = 12 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(12, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_WhenStockChangedSinceItWasRead_IsRefusedAndNothingChanges()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        // The user was shown 5, then an order takes 2 before they click Save.
        await system.PlaceOrderAsync(product.Id, 2);
        Assert.Equal(3, await system.GetStockAsync(product.Id));

        var stale = await system.InventoryClient.PutAsJsonAsync($"/api/products/{product.Id}/stock",
            new { expectedStockQuantity = 5, stockQuantity = 10 });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var error = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_changed", error.GetProperty("code").GetString());
        Assert.Equal(3, error.GetProperty("stockQuantity").GetInt32());
        Assert.Equal(3, await system.GetStockAsync(product.Id)); // the stale edit did not win

        // Editing again from the fresh value works.
        var fresh = await system.InventoryClient.PutAsJsonAsync($"/api/products/{product.Id}/stock",
            new { expectedStockQuantity = 3, stockQuantity = 10 });

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(10, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_UnknownProduct_Returns404()
    {
        await using var system = new TestSystem();

        var response = await system.InventoryClient.PutAsJsonAsync("/api/products/9999/stock",
            new { expectedStockQuantity = 0, stockQuantity = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}