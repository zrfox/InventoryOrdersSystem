using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrdersService.Data;
using OrdersService.Services;

DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "API key needed to access the endpoints."
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));

// Retry worker settings ("CancellationRetry" section in appsettings / env vars)
builder.Services.Configure<CancellationRetryOptions>(
    builder.Configuration.GetSection(CancellationRetryOptions.SectionName));

// Typed HttpClient for InventoryService. Still registered under the name "InventoryService"
// so the integration tests can swap in the in-memory TestServer handler.
builder.Services.AddHttpClient<IInventoryClient, InventoryClient>("InventoryService", client =>
{
    var baseUrl = builder.Configuration["InventoryServiceUrl"] ?? "http://localhost:5209";
    client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");

    var apiKey = builder.Configuration["InventoryApiKey"];
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);

    // A down Inventory must fail fast so cancellations can fall back to CancellationPending.
    client.Timeout = TimeSpan.FromSeconds(5);
});

builder.Services.AddScoped<OrderCancellationProcessor>();
builder.Services.AddScoped<PendingCancellationRetrier>();
builder.Services.AddSingleton<RetryCoordinator>();
builder.Services.AddHostedService<CancellationRetryWorker>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    if (db.Database.IsSqlServer())
    {
        db.Database.Migrate();
    }
}

app.UseHttpsRedirection();

app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"An unexpected error occurred. \"}");
        });
    });
}

app.UseMiddleware<OrdersService.Middleware.ApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }
