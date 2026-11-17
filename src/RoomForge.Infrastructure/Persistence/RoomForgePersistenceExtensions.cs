using Microsoft.EntityFrameworkCore;
using Npgsql;
using RoomForge.Application.Projects;
using RoomForge.Application.Scenes;
using RoomForge.Application.Reconstruction;
using RoomForge.Application.Annotations;
using RoomForge.Infrastructure.Reconstruction;
using Microsoft.Extensions.DependencyInjection;

namespace RoomForge.Infrastructure.Persistence;

public static class RoomForgePersistenceExtensions
{
    public static IServiceCollection AddRoomForgePersistence(this IServiceCollection services, string connectionString, string? falApiKey = null,
        string? segmentationProvider = null, string? imageTo3DProvider = null, bool falHunyuanDataTransferApproved = false)
    {
        ValidateConnectionString(connectionString);
        services.AddDbContext<RoomForgeDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ISceneStore, EfSceneStore>();
        services.AddScoped<IProjectStore, EfProjectStore>();
        services.AddScoped<ProjectPersistence>();
        services.AddScoped<ScenePersistence>();
        services.AddScoped<EfReconstructionStore>();
        services.AddScoped<IReconstructionStore>(provider => provider.GetRequiredService<EfReconstructionStore>());
        services.AddScoped<IReconstructionImageStore>(provider => provider.GetRequiredService<EfReconstructionStore>());
        services.AddScoped<IReconstructionImageCatalog>(provider => provider.GetRequiredService<EfReconstructionStore>());
        services.AddScoped<IAnnotationStore, EfAnnotationStore>();
        services.AddScoped<AnnotationPersistence>();
        services.AddScoped<ITopologyStore, EfTopologyStore>();
        services.AddScoped<TopologyPersistence>();
        services.AddScoped<IReconstructionCandidateStore, EfReconstructionCandidateStore>();
        services.AddScoped<ReconstructionCandidatePersistence>();
        services.AddScoped<ReconstructionCandidateApplication>();
        services.AddScoped<IObjectMeshJobStore, EfObjectMeshJobStore>();
        services.AddScoped<ObjectMeshJobSubmission>();
        services.AddScoped<ObjectMeshJobProcessor>();
        services.AddSingleton<ISceneUnderstandingProvider, MockSceneUnderstandingProvider>();
        services.AddSingleton<IDepthEstimationProvider, MockDepthEstimationProvider>();
        services.AddSingleton<IImageTo3DProvider, MockImageTo3DProvider>();
        var falOptions = new FalSam3Options(falApiKey ?? Environment.GetEnvironmentVariable("FAL_KEY"));
        services.AddSingleton(falOptions);
        var hunyuanEnabled = string.Equals(imageTo3DProvider ?? Environment.GetEnvironmentVariable("ROOMFORGE_IMAGE_TO_3D_PROVIDER"),
            "fal-hunyuan3d", StringComparison.OrdinalIgnoreCase);
        var hunyuanOptions = new FalHunyuan3DOptions(falApiKey ?? Environment.GetEnvironmentVariable("FAL_KEY"), hunyuanEnabled, falHunyuanDataTransferApproved);
        services.AddSingleton(hunyuanOptions);
        services.AddHttpClient<FalSam3QueueClient>(client => client.Timeout = TimeSpan.FromMinutes(3))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddHttpClient<FalHunyuan3DQueueClient>(client => client.Timeout = TimeSpan.FromMinutes(3))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        var falRequested = string.Equals(segmentationProvider ?? Environment.GetEnvironmentVariable("ROOMFORGE_SEGMENTATION_PROVIDER"), "fal-sam3", StringComparison.OrdinalIgnoreCase);
        var falEnabled = falRequested && falOptions.IsConfigured;
        var hunyuanAvailable = hunyuanOptions.IsConfigured;
        if (falEnabled)
        {
            services.AddScoped<FalSam3SegmentationProvider>();
            services.AddScoped<IObjectSegmentationProvider>(provider => provider.GetRequiredService<FalSam3SegmentationProvider>());
        }
        else services.AddSingleton<IObjectSegmentationProvider, MockObjectSegmentationProvider>();
        services.AddSingleton<IReconstructionCapabilityRegistry>(new MockReconstructionCapabilityRegistry(
            falEnabled ? "fal-ai/sam-3/image" : "roomforge-mock", true,
            falEnabled ? "Prompted floor-region candidate; result is a provider bounding box requiring human correction." : "Deterministic image-space polygons.",
            hunyuanAvailable ? "fal-ai/hunyuan3d-v3/image-to-3d" : "roomforge-mock", hunyuanAvailable,
            hunyuanAvailable ? "Paid Geometry generation enabled after explicit provider and data-transfer configuration; each request reserves $0.50 from the shared $20 cap." : "No image-to-3D provider enabled; editable primitive fallback."));
        services.AddScoped<ReconstructionAnalysisService>();
        services.AddScoped<ReconstructionJobProcessor>();
        return services;
    }

    public static void ValidateConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.Equals(builder.Database, "roomforge", StringComparison.Ordinal))
            throw new InvalidOperationException("RoomForge API is restricted to the 'roomforge' database.");
        if (builder.Port != 5432 || !IsLocalHost(builder.Host ?? string.Empty))
            throw new InvalidOperationException("RoomForge API is restricted to the local PostgreSQL server on port 5432.");
    }

    public static async Task MigrateRoomForgeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>().Database.MigrateAsync(cancellationToken);
    }

    private static bool IsLocalHost(string host) => host.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .All(value => value is "localhost" or "127.0.0.1" or "::1" or "[::1]");
}

public static class RoomForgeDatabaseHealth
{
    public static async Task<string?> GetCurrentDatabaseAsync(RoomForgeDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT current_database()";
        return (string?)await command.ExecuteScalarAsync(cancellationToken);
    }
}
