using CortexPlexus.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CortexPlexus.Memory;

/// <summary>
/// Background service that periodically calls <see cref="IAgentMemoryStore.ReapAsync"/>
/// to delete memories whose decay-weighted score has fallen below the forget threshold
/// (see ADR-012). Does nothing when <see cref="MemoryOptions.Enabled"/> is false —
/// the service is always registered but short-circuits when the feature is off.
/// </summary>
public sealed class MemoryReaper(
    IServiceScopeFactory scopeFactory,
    IOptions<MemoryOptions> options,
    ILogger<MemoryReaper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("MemoryReaper: memory disabled — reaper idle");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, opts.ReapIntervalHours));
        logger.LogInformation("MemoryReaper: scanning every {Interval}", interval);

        // First pass on startup (after a short delay so schema init can finish).
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IAgentMemoryStore>();
                var removed = await store.ReapAsync(stoppingToken);
                if (removed > 0)
                    logger.LogInformation("MemoryReaper: reaped {Count} memories", removed);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "MemoryReaper scan failed; will retry on next interval");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
