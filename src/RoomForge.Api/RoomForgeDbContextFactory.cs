using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using RoomForge.Infrastructure.Persistence;

public sealed class RoomForgeDbContextFactory : IDesignTimeDbContextFactory<RoomForgeDbContext>
{
    public RoomForgeDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("RoomForge")
            ?? configuration.GetConnectionString("RoomForgeDatabase")
            ?? throw new InvalidOperationException("Set ConnectionStrings:RoomForge in .NET User Secrets or environment variables before running EF tooling.");
        RoomForgePersistenceExtensions.ValidateConnectionString(connectionString);
        var options = new DbContextOptionsBuilder<RoomForgeDbContext>().UseNpgsql(connectionString).Options;
        return new RoomForgeDbContext(options);
    }
}
