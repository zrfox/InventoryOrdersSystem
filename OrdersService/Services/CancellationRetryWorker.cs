using Microsoft.Extensions.Options;

namespace OrdersService.Services;

// Runs for the lifetime of OrdersService. First pass happens right at startup, which is
// how CancellationPending orders from before a restart get picked up again.
public class CancellationRetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationOptions _options;
    private readonly ILogger<CancellationRetryWorker> _logger;

    public CancellationRetryWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<CancellationOptions> options,
        ILogger<CancellationRetryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // don't hold up application startup

        var interval = TimeSpan.FromSeconds(Math.Max(0.05, _options.RetryIntervalSeconds));
        var firstPass = true;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // DbContext is scoped, the worker is a singleton: new scope per pass.
                using var scope = _scopeFactory.CreateScope();
                var cancellations = scope.ServiceProvider.GetRequiredService<OrderCancellationService>();

                if (firstPass)
                {
                    firstPass = false;
                    var pending = await cancellations.CountPendingAsync(stoppingToken);
                    if (pending > 0)
                    {
                        _logger.LogInformation(
                            "{Count} order(s) were left CancellationPending before startup; retrying them now.", pending);
                    }
                }

                await cancellations.RetryPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let an exception escape: that would stop the whole host and end the retries.
                _logger.LogError(ex, "Retrying pending cancellations failed; will try again.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
