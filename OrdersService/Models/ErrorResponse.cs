namespace OrdersService.Models;

// Every error the API returns has this shape: { "error": "..." }
public sealed record ErrorResponse(string Error);
