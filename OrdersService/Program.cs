using Microsoft.EntityFrameworkCore;
using OrdersService.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<OrdersDbContext>(options =>
options.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));

builder.Services.AddHttpClient("InventoryService", client =>
{
    // Looks for "InventoryServiceUrl" in appsettings or Environment Variables
    var baseUrl = builder.Configuration["InventoryServiceUrl"] ?? "http://localhost:5209";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["InventoryApiKey"]);
});

var app = builder.Build();

// need to keep Swagger in dev mode when implementing security. 

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
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

app.UseHttpsRedirection();

app.UseMiddleware<OrdersService.Middleware.ApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();
