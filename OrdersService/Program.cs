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
});

var app = builder.Build();

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
    app.UseSwagger();
    app.UseSwaggerUI();
//}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
