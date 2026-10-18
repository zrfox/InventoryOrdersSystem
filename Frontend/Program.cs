using Frontend;

// same .env discovery as the two services (solution root)
DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));

var builder = WebApplication.CreateBuilder(args);

var apiKey = builder.Configuration["InventoryApiKey"] ?? string.Empty;
var inventoryUrl = builder.Configuration["InventoryServiceUrl"] ?? "http://localhost:5209";
var ordersUrl = builder.Configuration["OrdersServiceUrl"] ?? "http://localhost:5210";

void RegisterUpstream(string name, string baseUrl)
{
    var clientBuilder = builder.Services.AddHttpClient(name, client =>
    {
        client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
    });

    if (builder.Environment.IsDevelopment())
    {
        // Local runs talk to the services over HTTPS with the ASP.NET Core dev certificate.
        clientBuilder.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });
    }
}

RegisterUpstream("inventory", inventoryUrl);
RegisterUpstream("orders", ordersUrl);

var app = builder.Build();
var logger = app.Logger;

app.UseDefaultFiles();
app.UseStaticFiles();

var proxiedMethods = new[] { "GET", "POST", "PUT", "DELETE" };

// /inventory-api/api/products  ->  {InventoryServiceUrl}/api/products
app.MapMethods("/inventory-api/{**path}", proxiedMethods,
    (HttpContext ctx, string path, IHttpClientFactory factory) =>
        UpstreamProxy.ForwardAsync(ctx, factory.CreateClient("inventory"), "InventoryService", path, logger));

// /orders-api/api/orders/5/cancel  ->  {OrdersServiceUrl}/api/orders/5/cancel
app.MapMethods("/orders-api/{**path}", proxiedMethods,
    (HttpContext ctx, string path, IHttpClientFactory factory) =>
        UpstreamProxy.ForwardAsync(ctx, factory.CreateClient("orders"), "OrdersService", path, logger));

// Service reachability for the status pills in the UI
app.MapGet("/api/status", async (IHttpClientFactory factory, CancellationToken ct) => Results.Json(new
{
    inventory = await UpstreamProxy.PingAsync(factory.CreateClient("inventory"), "api/products", ct),
    orders = await UpstreamProxy.PingAsync(factory.CreateClient("orders"), "api/orders/pending-cancellations", ct)
}));

logger.LogInformation("Frontend proxying InventoryService at {Inventory} and OrdersService at {Orders}", inventoryUrl, ordersUrl);

app.Run();
