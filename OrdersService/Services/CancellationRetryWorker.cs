using Microsoft.Extensions.Options;

namespace OrdersService.Services;

// Runs at startup (so pending cancellations survive an OrdersService restart)
// and then periodically, forever, until every CancellationPending order is done.
public sealed class CancellationRetryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CancellationRetryOptions _options;
    private readonly ILogger<CancellationRetryWorker> _logger;

    public CancellationRetryWorker(IServiceScopeFactory scopeFactory, IOptions<CancellationRetryOptions> options,
        ILogger<CancellationRetryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Cancellation retry worker is disabled.");
            return;
        }

        _logger.LogInformation("Cancellation retry worker started; pending cancellations are retried every {Interval}s.", _options.IntervalSeconds);

        if (!await DelayAsync(TimeSpan.FromSeconds(Math.Max(0, _options.InitialDelaySeconds)), stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var retrier = scope.ServiceProvider.GetRequiredService<PendingCancellationRetrier>();
                await retrier.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancellation retry run failed; it will run again.");
            }

            if (!await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, _options.IntervalSeconds)), stoppingToken)) break;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
