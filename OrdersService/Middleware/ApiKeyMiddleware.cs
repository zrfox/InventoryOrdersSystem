namespace OrdersService.Middleware;

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private const string HeaderName = "X-Api-Key";

    public ApiKeyMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        if (!context.Request.Headers.TryGetValue(HeaderName, out var extractedKey))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("API key missing.");
            return;
        }

        var validKey = configuration["InventoryApiKey"];

        if (!string.Equals(extractedKey, validKey, StringComparison.Ordinal))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("Invalid API key.");
            return;
        }

        await _next(context);
    }
}