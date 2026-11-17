using Microsoft.Extensions.Configuration;
using RoomForge.Infrastructure.Persistence;

namespace RoomForge.IntegrationTests;

/// <summary>
/// Resolves the local PostgreSQL connection string from configuration only.
/// Credentials are never hardcoded, printed or committed.
/// </summary>
public static class IntegrationDatabase
{
    // The API project owns this User Secrets store. Its UserSecretsId is declared in
    // src/RoomForge.Api/RoomForge.Api.csproj; no secret value is duplicated here.
    private const string ApiUserSecretsId = "4354d028-6a8a-4226-bf22-d44618c561c3";

    public const string SkipReason =
        "The local roomforge connection is not configured. Set ConnectionStrings:RoomForge (or ConnectionStrings:RoomForgeDatabase) " +
        "in the API project's .NET User Secrets, or set ROOMFORGE_TEST_CONNECTION in the environment, then re-run. " +
        "Integration gates must not be reported as passing while unconfigured.";

    public static string? ConnectionString { get; } = Resolve();

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);

    private static string? Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("ROOMFORGE_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment;

        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // The existing local configuration stores the value under the RoomForgeDatabase alias.
        return configuration.GetConnectionString("RoomForge")
            ?? configuration.GetConnectionString("RoomForgeDatabase");
    }

    /// <summary>
    /// Fails loudly rather than silently skipping when a gate is executed without configuration.
    /// </summary>
    public static string Require()
    {
        if (!IsConfigured) throw new InvalidOperationException(SkipReason);
        try
        {
            RoomForgePersistenceExtensions.ValidateConnectionString(ConnectionString!);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "The configured test connection string is not the permitted local 'roomforge' target. " +
                "No other database may be used for tests.", exception);
        }
        return ConnectionString!;
    }
}
