using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RoomForge.Application.Reconstruction;
using RoomForge.Infrastructure.Reconstruction;

public sealed class ObjectMeshJobWorker(IServiceScopeFactory scopes, ILogger<ObjectMeshJobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var jobs = scope.ServiceProvider.GetRequiredService<IObjectMeshJobStore>();
                var work = await jobs.TryClaimAsync(stoppingToken);
                if (work is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<ObjectMeshJobProcessor>().ProcessAsync(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Object mesh worker iteration failed; persisted work will be recovered from its lease.");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
