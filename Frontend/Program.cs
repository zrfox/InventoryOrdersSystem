using System.Text;

// Same .env loading as the two services (ignored in Docker, where compose sets the variables).
DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));

var builder = WebApplication.CreateBuilder(args);

// The browser only ever talks to this app. It forwards to the services and adds the
// API key here, on the server, so the key never reaches the browser.
builder.Services.AddHttpClient(Backends.Inventory, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["InventoryServiceUrl"] ?? "http://localhost:5209");
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["InventoryApiKey"] ?? string.Empty);
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient(Backends.Orders, client =>
{
    client.BaseAddress = new Uri(builder.Configuration["OrdersServiceUrl"] ?? "http://localhost:5210");
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["InventoryApiKey"] ?? string.Empty);
    // A cancel can legitimately take a while when InventoryService is hanging.
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

if (string.IsNullOrEmpty(app.Configuration["InventoryApiKey"]))
{
    app.Logger.LogWarning("InventoryApiKey is not set; the services will reject the front-end's requests.");
}

app.UseDefaultFiles();   // "/" -> wwwroot/index.html
app.UseStaticFiles();

// Stock always comes straight from InventoryService.
app.MapGet("/api/products", (HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Inventory, "api/products"));

// Add a product.
app.MapPost("/api/products", (HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Inventory, "api/products"));

// Edit a product's stock (refused by InventoryService if the stock changed in the meantime).
app.MapPut("/api/products/{id:int}/stock", (int id, HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Inventory, $"api/products/{id}/stock"));

app.MapGet("/api/orders", (HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Orders, "api/orders"));

app.MapPost("/api/orders", (HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Orders, "api/orders"));

app.MapPost("/api/orders/{id:int}/cancel", (int id, HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Orders, $"api/orders/{id}/cancel"));

app.MapPost("/api/orders/{id:int}/cancel/undo", (int id, HttpContext context, IHttpClientFactory clients) =>
    Proxy.ForwardAsync(context, clients, Backends.Orders, $"api/orders/{id}/cancel/undo"));

app.Run();

static class Backends
{
    public const string Inventory = "InventoryService";
    public const string Orders = "OrdersService";
}

static class Proxy
{
    // Sends the browser's request to a back-end service and copies the answer back unchanged.
    // If the service can't be reached, the browser gets a 503 with a JSON error instead.
    public static async Task ForwardAsync(HttpContext context, IHttpClientFactory clients, string backend, string path)
    {
        var client = clients.CreateClient(backend);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), path);

        if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            if (body.Length > 0)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
        }

        if (context.Request.Headers.TryGetValue("Idempotency-Key", out var idempotencyKey))
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString());
        }

        try
        {
            using var response = await client.SendAsync(request, context.RequestAborted);

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser went away; nothing to send back.
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Frontend.Proxy");
            logger.LogWarning("{Backend} is not reachable: {Reason}", backend, ex.Message);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = $"{backend} is not reachable right now.",
                    service = backend
                });
            }
        }
    }
}