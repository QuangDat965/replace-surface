using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RoomForge.Application.Projects;
using RoomForge.Application.Scenes;

namespace RoomForge.Infrastructure.Persistence;

public static class RoomForgePersistenceExtensions
{
    public static IServiceCollection AddRoomForgePersistence(this IServiceCollection services, string connectionString)
    {
        ValidateConnectionString(connectionString);
        services.AddDbContext<RoomForgeDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ISceneStore, EfSceneStore>();
        services.AddScoped<IProjectStore, EfProjectStore>();
        services.AddScoped<ProjectPersistence>();
        services.AddScoped<ScenePersistence>();
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
