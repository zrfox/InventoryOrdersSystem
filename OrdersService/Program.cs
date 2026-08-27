using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using OrdersService.Data;

DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));

// WebApplication is the real, running app itself - the object Program.cs builds and runs
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// builder.Services IS the container. Or precisely,
// IServiceCollection (which during the builder
// phase is the mutable list we are adding registrations to. 
// every builder.Services.AddX(...) call is adding an entry to this list saying:
// "here's a type, here's how to construct it."
// after .Build(), builder.Services goes from container being assembled to
// container the app runs on. 
// when the controller's constructor asks for OrdersDbContext context, it's the
// built container that looks up "what did we register for this type?"
// then constructs/hands one over.

// adds controllers registers MVC framework. Fframework then discovers controllers marked with [ApiController]
builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle

// registers metadat generation that describes endpoints (routes, parameters, response types)
// in machine-readable form. 
builder.Services.AddEndpointsApiExplorer();
// consumes meta data created below to build Swagger UI. 
builder.Services.AddSwaggerGen(options =>
{
    // decalres the scheme (name, where it goes, what type)
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Name = "X-Api-Key",
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Description = "API key needed to access the endpoints."
    });
    // applies to every endpoint. Without this, you'd get Authoroize button,
    // but Swagger wouldn't send the header
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "ApiKey"
                }
            },
            Array.Empty<string>()
        }
    });
});
// tells DI container "whenever something asks for an OrdersDbContext, construct one wired to a 
// eral SQL Server connection string.Note: OrdersController constructor takes a context as a param
// it never makes one itself. 
// the DI container looks at this registration, builds a real OrdersDbContext, hands it to controller 
// automatically every time a request needs one. <- that's what dep injection is -
// controller declares what it needs, continaer decides how to build it, all based on what's registered here.
builder.Services.AddDbContext<OrdersDbContext>(options =>
options.UseSqlServer(builder.Configuration.GetConnectionString("OrdersDb")));

// Registers named HttpClient config when code calls _httpClientFactory.CreateClient("InventoryService")
// - which is exactly what OrdersController does. 
// Factory hands back client pre-configured with this base URL and this API key header.
// for integration test, must route into InventoryService's TestServer
builder.Services.AddHttpClient("InventoryService", client =>
{
    // Looks for "InventoryServiceUrl" in appsettings or Environment Variables
    var baseUrl = builder.Configuration["InventoryServiceUrl"] ?? "http://localhost:5209";
    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Add("X-Api-Key", builder.Configuration["InventoryApiKey"]);
});


// app phase - middleware pipeline
// every incoming request flows through each of these
// before hitting controller.

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

// do i need this or not? 
// read that the issue was usehttpsredirection before middleware meant redirect on every request which
// means the test would end up pointing to nothing due to redirection.
// idk if this is actually an issue
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

// wires ApiKeyMiddleware eclass into request pipeline. ASP.NET Core
// framework calls InvokeAync automatically for every incoming
// HTTP request once reaching that point in the pipeline
// UseMiddleware expenct constructor taking RequestDelegate next and a method
// named exactly InvookeAsync (or Invoke). 
app.UseMiddleware<OrdersService.Middleware.ApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program { }