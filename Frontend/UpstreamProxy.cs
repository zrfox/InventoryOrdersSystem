using System.Net;

namespace Frontend;

public sealed record ServiceStatus(bool Online, string Detail);

internal static class UpstreamProxy
{
    public static async Task ForwardAsync(HttpContext context, HttpClient client, string serviceName, string path, ILogger logger)
    {
        var relative = path + context.Request.QueryString.Value;
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), relative);

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        if (buffer.Length > 0)
        {
            upstreamRequest.Content = new ByteArrayContent(buffer.ToArray());
            if (!string.IsNullOrEmpty(context.Request.ContentType))
                upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
        }

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{Service} unreachable for {Method} {Path}: {Message}", serviceName, context.Request.Method, relative, ex.Message);
            await WriteErrorAsync(context, 503, $"{serviceName} is not reachable. Is it running?");
            return;
        }
        catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("{Service} timed out for {Method} {Path}", serviceName, context.Request.Method, relative);
            await WriteErrorAsync(context, 504, $"{serviceName} did not respond in time.");
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            var contentType = upstreamResponse.Content.Headers.ContentType?.ToString();
            if (contentType is not null) context.Response.ContentType = contentType;
            await upstreamResponse.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    public static async Task<ServiceStatus> PingAsync(HttpClient client, string path, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new ServiceStatus(true, "Online, but the API key was rejected. Check InventoryApiKey in .env.");

            return new ServiceStatus((int)response.StatusCode < 500, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ServiceStatus(false, "Unreachable");
        }
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        return context.Response.WriteAsJsonAsync(new { error = message });
    }
}
