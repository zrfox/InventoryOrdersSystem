using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Moq.Protected;
using OrdersService.Controllers;
using OrdersService.Data;
using OrdersService.Models;
using Xunit;

namespace OrdersService.Tests;

// unit tests
public class OrdersControllerTests
{
    // allows passing private member to both tests to set serializer
    // naming policy from default Pascal to camelCase
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    // creates a context that uses a database in RAM instead of an actual external database. 
    // builds options first and then returns proper context passing options
    // guid prevents collission, is the inMemory database name
    // new guid every time getinmemorycontexxt is called - keeps tests from colliding
    private OrdersDbContext GetInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<OrdersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new OrdersDbContext(options);
    }

    // keeps IHttpClientFactory initialization code in one location as a private member
    private IHttpClientFactory CreateMockHttpClientFactory(HttpStatusCode statusCode, object? responseBody)
    {
        var handlerMock = new Mock<HttpMessageHandler>();

        handlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
             {
                 StatusCode = statusCode,
                 Content = new StringContent(JsonSerializer.Serialize(responseBody, _jsonOptions))
             });

        var httpClient = new HttpClient(handlerMock.Object)
        {
            BaseAddress = new Uri("http://fake-inventory-service/")
        };

        var factoryMock = new Mock<IHttpClientFactory>();
        factoryMock.Setup(f => f.CreateClient("InventoryService")).Returns(httpClient);

        return factoryMock.Object;
    }

    [Fact]
    public async Task CreateOrder_SufficientStock_ReturnsConfirmed()
    {
        // create DbContext wired to an in-memory database
        var context = GetInMemoryContext();
        
        // create fakeProduct object for fake HTTP response body from InventoryService
        var fakeProduct = new ProductDto { Id = 1, Name = "Widget", StockQuantity = 50, Price = 9.99m };

        // create factory that will give OrdersController a fake HttpClient client (for faking InventoryService HTTP response)
        var factory = CreateMockHttpClientFactory(HttpStatusCode.OK, fakeProduct);

        // instance of OrdersController to call CreateOrder method
        var controller = new OrdersController(context, factory);

        // order request to send
        var request = new CreateOrderRequest { ProductId = 1, Quantity = 5 };

        // await since accessing database/using HTTP
        // in OrdersController.Create Order: 
        // SaveChangesAsync() is a real async DB call (though DB is in-memory in testing case)
        // client.GetAsync(...)/PutAsJsonAsync(...) are mocked HTTP calls-
        // mocked calls still return a Task, so await is still required even though no real network I/O in test.
        // await unwrwaps task, result is plain ActionResult<Order> object
        var result = await controller.CreateOrder(request);

        // synchronous now because await in last called unwrapped task into result
        // result.Result pulls IActionResult out of wrapper
        // (ActionResult<T> can hold either a raw T or an IActionResult [i.e. CreatedAtActionResult/NotFoundResult/etc.]
        // .Result gives you the IActionResult, as opposed to a T, if Result is an IActionResult.
        // CreatedAtActionResult is an IActionResult that DESCRIBES a 201 response (something created on the server) —
        // it's just a plain C# object right now, not an actual HTTP response, since this test calls the controller
        // directly and never runs it through the real ASP.NET Core pipeline that would turn it into real bytes on a wire.
        // IActionResult is an interface that means "I know how to become an HTTP response."
        // And ActionResult is the abstract base class implementing IActionResult.
        // so, CreatedAtActionResult describes an HTTP response that alerts us of a 201
        // (As opposed to an OkObjectResult [200], or NotFoundObjectResult [404], both of which are also IActionResults)
        // CreatedAtActionResult is different from OkObjectResult in that its constructor takes an action name (nameof(GetOrder))
        // and route values (new { id = order.Id }) and generates a Location header pointing at the URL of the new resource.
        // this is a REST convention - tell the client where to find the thing it just created.
        // OkObjectResult doesn't do this since 200 doesn't mean "a new thing exists, here's how to get it."
        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);

        // assert that Value of createdResult is type Order
        var order = Assert.IsType<Order>(createdResult.Value);

        // assert status is confirmed (means OrdersController succeeded creating order, implying stock exists)
        Assert.Equal("Confirmed", order.Status);
    }

    [Fact]
    public async Task CreateOrder_InsufficientStock_ReturnsRejected()
    {
        // new InMemoryContext prevents us from accessing fake orders in other inMemory databases
        var context = GetInMemoryContext();
        
        // fakeProduct serialized into JSON to fake HTTP response body from InventoryService,
        // ProductDto never touches InMemory db, only the Order would be stored there.
        var fakeProduct = new ProductDto { Id = 1, Name = "Widget", StockQuantity = 2, Price = 9.99m };
        var factory = CreateMockHttpClientFactory(HttpStatusCode.OK, fakeProduct);

        var controller = new OrdersController(context, factory);
        var request = new CreateOrderRequest { ProductId = 1, Quantity = 10 };

        var result = await controller.CreateOrder(request);

        var createdResult = Assert.IsType<CreatedAtActionResult>(result.Result);
        var order = Assert.IsType<Order>(createdResult.Value);
        Assert.Equal("Rejected", order.Status);
    }

    [Fact]
    public async Task CreateOrder_InvalidProductId_ProductNotFound()
    {
        // this test will never access the database 
        // OrdersController returns with Not Found message before touching db.
        // but we still need the context to create and call the controller.
        var context = GetInMemoryContext();

        // create factory for creating clients with controller object 
        // why status code OK? why isn't it not found?  
        // object null since we don't need to return an object in HTTP body for 404
        // the MockHttpClientFactory mimics InventoryService's response of no product found
        // allows us to test how OrdersController reacts to this response.
        var factory = CreateMockHttpClientFactory(HttpStatusCode.NotFound, null);

        // new OrdersController object takes the context for the InMemory db and factory
        // factory lets controller create HttpClients to make requests
        var controller = new OrdersController(context, factory);

        // request for non-existent object 
        var request = new CreateOrderRequest { ProductId = 1, Quantity = 1 };

        var result = await controller.CreateOrder(request);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal("Product 1 not found in inventory.", notFoundResult.Value);
    }
}

// Note on Descriptor Objects and ObjectResult derived classes like CreatedAtActionResult
// *Interface: IActionResult. ActionResult then implements this interface.
// *Chain of descendance: ActionResult -> ObjectResult -> CreatedAtActionResult

// "Descriptor object" = an object whose job is to hold the info needed to produce
// something else later, rather than being that something itself.
// CreatedAtActionResult isn't the HTTP response — it's a bundle of data
// (StatusCode=201, Value=order, action name, route values) that the real
// ASP.NET Core pipeline WOULD read to build the actual response, if this
// test ever ran through that pipeline (it doesn't — we call the controller directly).
// Like a shipping label filled out but not yet attached to a package: fully
// specifies where it'd go and what's inside, but nothing has shipped.

/*
 * 
 * so, i guess IEActionResult interface is defined by its (only?) function ExecuteResultAsync 
 * and everythign derived from this revolves around the ability to do that with the intention 
 * of producing an HTTP response



 * public interface IActionResult
{
    Task ExecuteResultAsync(ActionContext context);
}
 * That's the entire contract. Everything in that hierarchy — ActionResult, 
 * ObjectResult, CreatedAtActionResult, NotFoundObjectResult, OkObjectResult, all of them — 
 * exists purely to provide different implementations of that one method, each writing a 
 * different status code/headers/body combination to the actual HttpResponse when it's 
 * invoked for real by the framework
 * 
 * contract = interface. interface is just a promise that you have the same members
 * 
 * */