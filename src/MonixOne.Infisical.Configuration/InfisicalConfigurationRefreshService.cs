using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MonixOne.Infisical.Configuration;

internal sealed class InfisicalConfigurationRefreshService(
    InfisicalConfigurationProvider provider,
    InfisicalConfigurationOptions options,
    ILogger<InfisicalConfigurationRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.RefreshInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await provider.RefreshAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    // Keep the last known configuration on transient failures and
                    // retry at the next configured interval.
                    logger.LogError(exception, "Failed to refresh configuration from Infisical.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }
}
