using System.Text.Json;
using RoomForge.Application.Projects;
using RoomForge.Application.Scenes;
using RoomForge.Domain.Scenes;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Storage;
using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Reconstruction;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 11 * 1024 * 1024);
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
{
    builder.Services.AddRoomForgePersistence(roomForgeConnection);
    builder.Services.AddHostedService<ReconstructionJobWorker>();
}

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
app.MapGet("/api/assets/images/{id}", (string id) =>
{
    var asset = assetStore.OpenRoomImage(id);
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

    app.MapPost("/api/projects/{projectId:guid}/images", async (Guid projectId, HttpRequest request, LocalAssetStore assets,
        IReconstructionImageCatalog images, CancellationToken ct) =>
    {
        if (request.ContentLength is > 11 * 1024 * 1024) return Results.Problem("Room image exceeds the 10 MB file limit.", statusCode: StatusCodes.Status413PayloadTooLarge);
        if (!request.HasFormContentType) return Results.Problem("Upload a PNG, JPEG or WebP room image as multipart form data.", statusCode: StatusCodes.Status415UnsupportedMediaType);
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("image") ?? form.Files.SingleOrDefault();
        if (file is null) return Results.BadRequest(new { error = "image_required" });
        if (file.Length is <= 0 or > 10 * 1024 * 1024) return Results.Problem("Room image must be between 1 byte and 10 MB.", statusCode: StatusCodes.Status413PayloadTooLarge);
        (string Id, string ContentType, int Width, int Height) saved;
        try
        {
            await using var stream = file.OpenReadStream();
            saved = await assets.SaveRoomImageAsync(stream, file.ContentType, ct);
        }
        catch (InvalidDataException exception) { return Results.BadRequest(new { error = exception.Message }); }
        var image = new ReconstructionImage(Guid.NewGuid(), projectId, saved.Width, saved.Height, saved.ContentType, saved.Id);
        try { await images.AddAsync(image, ct); }
        catch (InvalidOperationException) { assets.DeleteRoomImage(saved.Id); return Results.NotFound(new { error = "project_not_found" }); }
        catch { assets.DeleteRoomImage(saved.Id); throw; }
        return Results.Created($"/api/assets/images/{saved.Id}", new { imageId = image.Id, url = $"/api/assets/images/{saved.Id}", image.Width, image.Height, image.ContentType, provenance = "user-upload" });
    });

    app.MapPost("/api/projects/{projectId:guid}/reconstruction-jobs", async (Guid projectId, CreateReconstructionJobRequest request,
        IReconstructionStore jobs, CancellationToken ct) =>
    {
        if (request.ImageId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["A valid imageId and 1–200 character idempotencyKey are required."] });
        try
        {
            var id = await jobs.CreateOrGetAsync(new ReconstructionJobRequest(projectId, request.ImageId, request.IdempotencyKey.Trim()), ct);
            var job = await jobs.GetAsync(id, ct);
            return Results.Accepted($"/api/reconstruction/jobs/{id}", job);
        }
        catch (InvalidOperationException) { return Results.NotFound(new { error = "image_not_found_in_project" }); }
    });

    app.MapGet("/api/reconstruction/capabilities", (IReconstructionCapabilityRegistry capabilities) => Results.Ok(capabilities.GetCapabilities()));

    app.MapGet("/api/reconstruction/jobs/{jobId:guid}", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    });
    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/cancel", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
        await jobs.CancelAsync(jobId, ct) ? Results.Ok(new { status = "cancelled" }) : Results.Conflict(new { error = "job_not_cancellable" }));
    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/retry", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
        await jobs.RetryAsync(jobId, ct) ? Results.Accepted() : Results.Conflict(new { error = "job_not_retryable" }));

    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/preview", async (Guid jobId, ReconstructionPreviewRequest request,
        IReconstructionStore jobs, ScenePersistence scenes, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Results.NotFound();
        if (job.Status != "succeeded" || job.AnalysisJson is null) return Results.Conflict(new { error = "analysis_not_ready" });
        if (request.Analysis is null) return Results.BadRequest(new { error = "reviewed_analysis_required" });
        var analysisErrors = SceneAnalysisResultValidator.Validate(request.Analysis);
        if (analysisErrors.Count > 0) return Results.ValidationProblem(analysisErrors.ToDictionary(error => error, error => new[] { error }));
        var original = JsonSerializer.Deserialize<SceneAnalysisResult>(job.AnalysisJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (original is null) return Results.BadRequest(new { error = "invalid_reviewed_analysis" });
        var reviewErrors = GetReviewedAnalysisErrors(original, request.Analysis);
        if (reviewErrors.Count > 0) return Results.BadRequest(new { error = "invalid_reviewed_analysis", details = reviewErrors });

        string baseSceneJson;
        bool preserveIdentity;
        Guid sceneId;
        int baseRevision;
        if (request.TargetSceneId is { } targetSceneId)
        {
            var current = await scenes.GetAsync(targetSceneId, ct);
            if (current is null) return Results.NotFound(new { error = "target_scene_not_found" });
            if (request.ExpectedRevision is null || request.ExpectedRevision != current.Revision) return Results.Conflict(new { error = "revision_conflict", currentRevision = current.Revision });
            using var sceneDocument = JsonDocument.Parse(current.Document);
            if (sceneDocument.RootElement.GetProperty("projectId").GetGuid() != job.ProjectId) return Results.BadRequest(new { error = "target_scene_project_mismatch" });
            var currentObjectIds = sceneDocument.RootElement.GetProperty("objects").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
            if (request.Analysis.Objects.Any(item => item.LinkedObjectId is { } linkedId && !currentObjectIds.Contains(linkedId)))
                return Results.BadRequest(new { error = "linked_object_not_in_target_scene" });
            var currentSurfaceIds = sceneDocument.RootElement.GetProperty("objects").EnumerateArray()
                .SelectMany(item => item.GetProperty("surfaces").EnumerateArray()).Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
            if (request.Analysis.Surfaces.Any(item => item.LinkedSurfaceId is { } linkedId && !currentSurfaceIds.Contains(linkedId)))
                return Results.BadRequest(new { error = "linked_surface_not_in_target_scene" });
            if (request.Analysis.Objects.Where(item => item.LinkedObjectId is not null).Select(item => item.LinkedObjectId).Distinct().Count() != request.Analysis.Objects.Count(item => item.LinkedObjectId is not null) ||
                request.Analysis.Surfaces.Where(item => item.LinkedSurfaceId is not null).Select(item => item.LinkedSurfaceId).Distinct().Count() != request.Analysis.Surfaces.Count(item => item.LinkedSurfaceId is not null))
                return Results.BadRequest(new { error = "duplicate_entity_links" });
            baseSceneJson = current.Document;
            preserveIdentity = true;
            sceneId = targetSceneId;
            baseRevision = current.Revision;
        }
        else
        {
            if (request.Analysis.Objects.Any(item => item.LinkedObjectId is not null) || request.Analysis.Surfaces.Any(item => item.LinkedSurfaceId is not null))
                return Results.BadRequest(new { error = "entity_links_require_a_target_scene" });
            baseSceneJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json"));
            preserveIdentity = false;
            sceneId = Guid.NewGuid();
            baseRevision = 0;
        }
        var candidate = SceneReconstructionConverter.Convert(baseSceneJson, request.Analysis, sceneId, preserveIdentity, job.ProjectId);
        using var candidateDocument = JsonDocument.Parse(candidate);
        var candidateErrors = SceneDocumentValidator.Validate(candidateDocument.RootElement);
        if (candidateErrors.Count > 0) return Results.ValidationProblem(candidateErrors.ToDictionary(error => error, error => new[] { error }));
        await jobs.SavePreviewAsync(jobId, request.TargetSceneId, baseRevision, candidate, ct);
        return Results.Ok(new ReconstructionPreview(sceneId, baseRevision, candidate, request.TargetSceneId is null));
    });

    app.MapGet("/api/reconstruction/jobs/{jobId:guid}/preview", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
    {
        var preview = await jobs.GetPreviewAsync(jobId, ct);
        return preview is null ? Results.NotFound() : Results.Ok(preview);
    });

    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/apply", async (Guid jobId, ConfirmReconstructionRequest request,
        IReconstructionStore jobs, ScenePersistence scenes, CancellationToken ct) =>
    {
        if (!request.Confirmed) return Results.BadRequest(new { error = "explicit_confirmation_required" });
        var preview = await jobs.GetPreviewAsync(jobId, ct);
        if (preview is null) return Results.Conflict(new { error = "preview_required" });
        if (request.ExpectedRevision != preview.BaseRevision) return Results.Conflict(new { error = "preview_revision_mismatch", expectedRevision = preview.BaseRevision });
        using var document = JsonDocument.Parse(preview.Document);
        var result = await scenes.SaveAsync(preview.SceneId, document.RootElement, preview.BaseRevision, ct);
        if (result.Status == SaveSceneStatus.RevisionConflict) return Results.Conflict(new { error = "revision_conflict", currentRevision = result.Revision });
        if (result.Status != SaveSceneStatus.Saved) return Results.ValidationProblem(result.Errors!.ToDictionary(error => error, error => new[] { error }));
        return Results.Ok(new { sceneId = preview.SceneId, revision = result.Revision, created = preview.CreatesNewScene, document = result.Document });
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
    app.MapPost("/api/projects/{projectId:guid}/images", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/reconstruction-jobs", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
}

app.Run();

static int? ParseExpectedRevision(string header)
{
    var value = header.Trim().Trim('"');
    return int.TryParse(value, out var revision) && revision >= 0 ? revision : null;
}

static IReadOnlyList<string> GetReviewedAnalysisErrors(SceneAnalysisResult original, SceneAnalysisResult reviewed)
{
    var errors = new List<string>();
    if (original.ContractVersion != reviewed.ContractVersion || original.AnalysisId != reviewed.AnalysisId || original.SourceImageId != reviewed.SourceImageId ||
        original.ProviderId != reviewed.ProviderId || original.ProviderVersion != reviewed.ProviderVersion || original.ImageWidth != reviewed.ImageWidth || original.ImageHeight != reviewed.ImageHeight ||
        !original.Surfaces.Select(item => item.Id).Order().SequenceEqual(reviewed.Surfaces.Select(item => item.Id).Order()) ||
        !original.Objects.Select(item => item.Id).Order().SequenceEqual(reviewed.Objects.Select(item => item.Id).Order()))
        errors.Add("Analysis identity, provider metadata, or candidate ID set changed.");
    if (errors.Count > 0) return errors;
    var reviewedSurfaces = reviewed.Surfaces.ToDictionary(item => item.Id);
    foreach (var source in original.Surfaces)
    {
        var current = reviewedSurfaces[source.Id];
        if (source.SemanticRole != current.SemanticRole || source.Confidence != current.Confidence || source.Provenance != current.Provenance ||
            source.EstimatedWidthMeters != current.EstimatedWidthMeters || source.EstimatedHeightMeters != current.EstimatedHeightMeters ||
            !source.Polygon.Points.SequenceEqual(current.Polygon.Points)) errors.Add($"Surface candidate {source.Id} changed outside supported review fields.");
    }
    var reviewedObjects = reviewed.Objects.ToDictionary(item => item.Id);
    foreach (var source in original.Objects)
    {
        var current = reviewedObjects[source.Id];
        if (source.Confidence != current.Confidence || source.Provenance != current.Provenance || !source.Polygon.Points.SequenceEqual(current.Polygon.Points))
            errors.Add($"Object candidate {source.Id} changed outside supported review fields.");
    }
    return errors;
}

public partial class Program { }

public sealed record CreateProjectRequest(string? Name);
public sealed record CreateReconstructionJobRequest(Guid ImageId, string IdempotencyKey);
public sealed record ConfirmReconstructionRequest(bool Confirmed, int ExpectedRevision);
