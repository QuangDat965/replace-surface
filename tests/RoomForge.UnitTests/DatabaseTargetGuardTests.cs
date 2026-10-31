using Microsoft.Extensions.DependencyInjection;
using RoomForge.Infrastructure.Persistence;

namespace RoomForge.UnitTests;

public sealed class DatabaseTargetGuardTests
{
    [Fact]
    public async Task Provisioner_rejects_banhang_before_opening_a_connection()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RoomForgeDatabaseProvisioner.EnsureCreatedAsync("Host=localhost;Port=5432;Database=banhang", CancellationToken.None));
        Assert.Contains("maintenance database 'postgres'", exception.Message);
    }

    [Fact]
    public void Api_registration_rejects_any_database_other_than_roomforge()
    {
        var services = new ServiceCollection();
        var exception = Assert.Throws<InvalidOperationException>(() =>
            services.AddRoomForgePersistence("Host=localhost;Port=5432;Database=banhang"));
        Assert.Contains("restricted to the 'roomforge' database", exception.Message);
    }
}
