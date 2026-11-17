using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomForge.Application.Projects;
using RoomForge.Application.Reconstruction;
using RoomForge.Infrastructure.Persistence;

namespace RoomForge.IntegrationTests;

/// <summary>
/// Builds a real dependency-injection scope against the local <c>roomforge</c> database.
/// Each fixture creates its own project row so tests never interfere with each other.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private ServiceProvider? provider;

    public IServiceProvider Services => provider ?? throw new InvalidOperationException("The fixture is not initialized.");
    public Guid ProjectId { get; private set; }
    public bool IsAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        var connectionString = IntegrationDatabase.Require();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRoomForgePersistence(connectionString);
        provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>().Database.MigrateAsync();

        var project = await scope.ServiceProvider.GetRequiredService<ProjectPersistence>()
            .CreateAsync($"integration-{Guid.NewGuid():N}", CancellationToken.None);
        ProjectId = project!.Id;
        IsAvailable = true;
    }

    public async Task DisposeAsync()
    {
        if (provider is null) return;
        try
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
            await db.ReconstructionCandidates.Where(item => item.ProjectId == ProjectId).ExecuteDeleteAsync();
            await db.RoomTopologies.Where(item => item.ProjectId == ProjectId).ExecuteDeleteAsync();
            await db.AnnotationDocuments.Where(item => item.ProjectId == ProjectId).ExecuteDeleteAsync();
            await db.ReconstructionJobs.Where(item => item.ProjectId == ProjectId).ExecuteDeleteAsync();
            await db.ImageAssets.Where(item => item.ProjectId == ProjectId).ExecuteDeleteAsync();
            await db.Projects.Where(item => item.Id == ProjectId).ExecuteDeleteAsync();
        }
        catch (Exception) { /* cleanup is best-effort; the project row is freshly generated per test run */ }
        await provider.DisposeAsync();
    }

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    /// <summary>Creates an image asset row so reconstruction jobs can reference it.</summary>
    public async Task<ReconstructionImage> CreateImageAsync(int width = 800, int height = 600)
    {
        await using var scope = CreateScope();
        var images = scope.ServiceProvider.GetRequiredService<IReconstructionImageCatalog>();
        var image = new ReconstructionImage(Guid.NewGuid(), ProjectId, width, height, "image/png", Guid.NewGuid().ToString("N"));
        return await images.AddAsync(image, CancellationToken.None);
    }
}
