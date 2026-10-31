using Npgsql;

namespace RoomForge.Infrastructure.Persistence;

public static class RoomForgeDatabaseProvisioner
{
    public static async Task EnsureCreatedAsync(string adminConnectionString, CancellationToken cancellationToken)
    {
        var builder = new NpgsqlConnectionStringBuilder(adminConnectionString);
        if (!string.Equals(builder.Database, "postgres", StringComparison.Ordinal))
            throw new InvalidOperationException("The admin connection must target the PostgreSQL maintenance database 'postgres'.");
        if (builder.Port != 5432 || !(builder.Host ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .All(host => host is "localhost" or "127.0.0.1" or "::1" or "[::1]"))
            throw new InvalidOperationException("Database provisioning is restricted to local PostgreSQL on port 5432.");

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", connection);
        exists.Parameters.AddWithValue("name", "roomforge");
        if ((bool)(await exists.ExecuteScalarAsync(cancellationToken) ?? false)) return;

        await using var create = new NpgsqlCommand("CREATE DATABASE roomforge", connection);
        try { await create.ExecuteNonQueryAsync(cancellationToken); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.DuplicateDatabase) { }
    }
}
