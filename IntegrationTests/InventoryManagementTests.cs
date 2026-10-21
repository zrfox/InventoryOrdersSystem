using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using InventoryService.Models;
using OrdersService.Models;
using Xunit;

namespace Integration.Tests;

// Covers what the front-end's "Add a product" form and "Edit stock" dialog rely on.
public class InventoryManagementTests
{
    private static string StockUrl(int productId) => $"/api/products/{productId}/stock";

    [Fact]
    public async Task SetStock_WhenStockIsStillWhatTheEditorSaw_UpdatesIt()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        var response = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 12, expectedStockQuantity = 5 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<Product>(TestSystem.JsonOptions);
        Assert.Equal(12, updated!.StockQuantity);
        Assert.Equal(12, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_WithStaleExpectedValue_Returns409WithCurrentStock_AndChangesNothing()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        var response = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 12, expectedStockQuantity = 4 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stock_changed", error.GetProperty("code").GetString());
        Assert.Equal(5, error.GetProperty("stockQuantity").GetInt32());
        Assert.Equal(5, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_AfterAnOrderTookStock_IsRefused_AndTheOrdersDeductionIsNotOverwritten()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        // The editor opens the dialog and sees 5. Before they save, an order takes 2.
        var order = await system.PlaceOrderAsync(product.Id, 2);
        Assert.Equal(OrderStatus.Confirmed, order.Status);

        var stale = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 15, expectedStockQuantity = 5 });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var error = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, error.GetProperty("stockQuantity").GetInt32());
        Assert.Equal(3, await system.GetStockAsync(product.Id));

        // With the up-to-date value the edit goes through.
        var fresh = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 13, expectedStockQuantity = 3 });

        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(13, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_TwoEditorsStartingFromTheSameValue_OnlyTheFirstIsApplied()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        var first = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 8, expectedStockQuantity = 5 });
        var second = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 9, expectedStockQuantity = 5 });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(8, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task SetStock_UnknownProduct_Returns404()
    {
        await using var system = new TestSystem();

        var response = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(12345), new { stockQuantity = 1, expectedStockQuantity = 0 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SetStock_NegativeOrMissingValues_Returns400_AndChangesNothing()
    {
        await using var system = new TestSystem();
        var product = await system.SeedProductAsync(stock: 5);

        var negative = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = -1, expectedStockQuantity = 5 });
        var missingExpected = await system.InventoryClient.PutAsJsonAsync(
            StockUrl(product.Id), new { stockQuantity = 3 });

        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingExpected.StatusCode);
        Assert.Equal(5, await system.GetStockAsync(product.Id));
    }

    [Fact]
    public async Task CreateProduct_ThenItIsListedAndCanBeOrdered()
    {
        await using var system = new TestSystem();

        // The same JSON shape the front-end's "Add a product" form sends.
        var create = await system.InventoryClient.PostAsJsonAsync(
            "/api/products", new { name = "Gadget", price = 19.99, stockQuantity = 4 });

        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<Product>(TestSystem.JsonOptions);
        Assert.True(created!.Id > 0);

        var all = await system.InventoryClient.GetFromJsonAsync<List<Product>>("/api/products", TestSystem.JsonOptions);
        Assert.Contains(all!, p => p.Id == created.Id && p.Name == "Gadget" && p.StockQuantity == 4);

        var order = await system.PlaceOrderAsync(created.Id, 3);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(1, await system.GetStockAsync(created.Id));
    }

    [Fact]
    public async Task CreateProduct_WithInvalidData_Returns400_AndAddsNothing()
    {
        await using var system = new TestSystem();

        var noName = await system.InventoryClient.PostAsJsonAsync(
            "/api/products", new { name = "", price = 19.99, stockQuantity = 4 });
        var negativeStock = await system.InventoryClient.PostAsJsonAsync(
            "/api/products", new { name = "Gadget", price = 19.99, stockQuantity = -1 });

        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, negativeStock.StatusCode);
        var all = await system.InventoryClient.GetFromJsonAsync<List<Product>>("/api/products", TestSystem.JsonOptions);
        Assert.Empty(all!);
    }
}