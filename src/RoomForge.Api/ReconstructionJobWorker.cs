using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoomForge.Application.Reconstruction;

public sealed class ReconstructionJobWorker(IServiceScopeFactory scopes, ILogger<ReconstructionJobWorker> logger) : BackgroundService
{
    /// <summary>
    /// Poll interval when no work is available. Configurable so integration tests are not forced to
    /// wait a full second per idle poll.
    /// </summary>
    public static readonly TimeSpan DefaultIdleDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.TryParse(Environment.GetEnvironmentVariable("ROOMFORGE_JOB_POLL_INTERVAL"), out var configured) && configured > TimeSpan.Zero
            ? configured
            : DefaultIdleDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IReconstructionStore>();
                var work = await jobs.TryClaimAsync(stoppingToken);
                if (work is null)
                {
                    await Task.Delay(idleDelay, stoppingToken);
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<ReconstructionJobProcessor>().ProcessAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Reconstruction worker iteration failed; it will retry shortly.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
