using System.Text.Json;
using RoomForge.Application.Projects;
using RoomForge.Application.Scenes;
using RoomForge.Domain.Scenes;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddCors(options => options.AddPolicy("dev-web", policy => policy
    .WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
var assetRoot = Environment.GetEnvironmentVariable("ROOMFORGE_ASSET_ROOT")
    ?? Path.Combine(builder.Environment.ContentRootPath, "data", "assets");
var legacyAssetRoots = new[]
{
    Path.Combine(builder.Environment.ContentRootPath, "data", "assets"),
    Path.Combine(builder.Environment.ContentRootPath, "src", "RoomForge.Api", "data", "assets"),
};
var assetStore = new LocalAssetStore(assetRoot, legacyAssetRoots);
builder.Services.AddSingleton(assetStore);

var roomForgeConnection = builder.Configuration.GetConnectionString("RoomForge")
    ?? builder.Configuration.GetConnectionString("RoomForgeDatabase");
var adminConnection = builder.Configuration.GetConnectionString("RoomForgeAdmin")
    ?? Environment.GetEnvironmentVariable("ROOMFORGE_ADMIN_CONNECTION");
if (adminConnection is null && !string.IsNullOrWhiteSpace(roomForgeConnection))
{
    var adminBuilder = new Npgsql.NpgsqlConnectionStringBuilder(roomForgeConnection);
    adminBuilder.Database = "postgres";
    adminConnection = adminBuilder.ConnectionString;
}
if (args.Contains("--ensure-roomforge-database", StringComparer.Ordinal))
{
    if (string.IsNullOrWhiteSpace(adminConnection))
        throw new InvalidOperationException("Set ConnectionStrings:RoomForgeAdmin in .NET User Secrets or ROOMFORGE_ADMIN_CONNECTION in the environment.");
    await RoomForgeDatabaseProvisioner.EnsureCreatedAsync(adminConnection, CancellationToken.None);
    Console.WriteLine("Database 'roomforge' is ready. No other database was accessed.");
    return;
}

if (!string.IsNullOrWhiteSpace(roomForgeConnection))
    builder.Services.AddRoomForgePersistence(roomForgeConnection);

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.MapOpenApi(); app.UseCors("dev-web"); }
app.MapGet("/health", () => Results.Ok(new { status = "ok", phase = "01" }));
app.MapGet("/api/sample-scene", () =>
{
    var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var errors = SceneDocumentValidator.Validate(document.RootElement);
    return errors.Count == 0 ? Results.Json(document.RootElement.Clone()) : Results.Problem(string.Join("; ", errors));
});
app.MapPost("/api/assets/textures", async (HttpRequest request, CancellationToken ct) =>
{
    if (request.ContentLength is > 10 * 1024 * 1024) return Results.Problem("Texture exceeds the 10 MB limit.", statusCode: StatusCodes.Status413PayloadTooLarge);
    try
    {
        var saved = await assetStore.SaveTextureAsync(request.Body, request.ContentType ?? string.Empty, ct);
        return Results.Created($"/api/assets/textures/{saved.Id}", new { url = $"/api/assets/textures/{saved.Id}", contentType = saved.ContentType });
    }
    catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
});
app.MapGet("/api/assets/textures/{id}", (string id) =>
{
    var asset = assetStore.OpenTexture(id);
    return asset is null ? Results.NotFound() : Results.File(asset.Value.Content, asset.Value.ContentType);
});

if (!string.IsNullOrWhiteSpace(roomForgeConnection))
{
    await app.Services.MigrateRoomForgeDatabaseAsync();

    app.MapGet("/api/projects/{projectId:guid}", async (Guid projectId, ProjectPersistence projects, CancellationToken ct) =>
    {
        var project = await projects.GetAsync(projectId, ct);
        return project is null ? Results.NotFound() : Results.Ok(project);
    });
    app.MapPost("/api/projects", async (CreateProjectRequest request, ProjectPersistence projects, CancellationToken ct) =>
    {
        var project = await projects.CreateAsync(request.Name ?? string.Empty, ct);
        return project is null ? Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Project name must contain 1–120 characters."] })
            : Results.Created($"/api/projects/{project.Id}", project);
    });

    app.MapGet("/api/scenes/{sceneId:guid}", async (Guid sceneId, ScenePersistence scenes, HttpContext context, CancellationToken ct) =>
    {
        var stored = await scenes.GetAsync(sceneId, ct);
        if (stored is null) return Results.NotFound();
        context.Response.Headers.ETag = $"\"{stored.Revision}\"";
        return Results.Content(stored.Document, "application/json");
    });

    app.MapPut("/api/scenes/{sceneId:guid}", async (Guid sceneId, JsonElement body, ScenePersistence scenes, HttpContext context, ILogger<Program> logger, CancellationToken ct) =>
    {
        var expected = ParseExpectedRevision(context.Request.Headers.IfMatch.ToString());
        logger.LogInformation("Saving scene {SceneId} with expected revision {ExpectedRevision}.", sceneId, expected);
        var result = await scenes.SaveAsync(sceneId, body, expected, ct);
        logger.LogInformation("Scene save for {SceneId} finished with status {SaveStatus} and revision {Revision}.", sceneId, result.Status, result.Revision);
        if (result.Status == SaveSceneStatus.Invalid) return Results.ValidationProblem(result.Errors!.ToDictionary(message => message, message => new[] { message }));
        if (result.Status == SaveSceneStatus.RevisionConflict) return Results.Conflict(new { error = "revision_conflict", currentRevision = result.Revision });
        context.Response.Headers.ETag = $"\"{result.Revision}\"";
        return Results.Content(result.Document, "application/json");
    });
    app.MapGet("/health/database", async (RoomForgeDbContext db, CancellationToken ct) =>
    {
        var name = await RoomForgeDatabaseHealth.GetCurrentDatabaseAsync(db, ct);
        return string.Equals(name, "roomforge", StringComparison.Ordinal)
            ? Results.Ok(new { status = "ok", database = name })
            : Results.Problem("API is connected to an unexpected database.", statusCode: StatusCodes.Status500InternalServerError);
    });
}
else
{
    app.MapGet("/api/projects/{projectId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/scenes/{sceneId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPut("/api/scenes/{sceneId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/health/database", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
}

app.Run();

static int? ParseExpectedRevision(string header)
{
    var value = header.Trim().Trim('"');
    return int.TryParse(value, out var revision) && revision >= 0 ? revision : null;
}

public partial class Program { }

public sealed record CreateProjectRequest(string? Name);
