using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using InventoryService.Data;


// load env vars to use locally. Docker will override with its own config
// goes up from explicit base directory (where the compiled app runs). 
// means you don't have to worry about what your working dir is to run
DotNetEnv.Env.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));
//Console.WriteLine("Raw env var: " + Environment.GetEnvironmentVariable("InventoryApiKey"));
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
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
    // applies to every endpoint. Without this, you'd get Authorize button
    // BUT Swagger wouldn't send the header
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





builder.Services.AddDbContext<InventoryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("InventoryDb")));


var app = builder.Build();

app.UseHttpsRedirection();


app.UseSwagger();
app.UseSwaggerUI();

// Configure the HTTP request pipeline.
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



app.UseMiddleware<InventoryService.Middleware.ApiKeyMiddleware>();

app.UseAuthorization();

app.MapControllers();

app.Run();

// this partial class is created so that Program.cs is accessibly my IntegrationTests
// since Program.cs does not include a class declaration and defaults to internal,
// this must be added to expose to another project. 
public partial class Program { }