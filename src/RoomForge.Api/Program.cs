using System.Text.Json;
using System.Text.Json.Nodes;
using System.Numerics;
using RoomForge.Application.Projects;
using RoomForge.Application.Scenes;
using RoomForge.Domain.Scenes;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Storage;
using RoomForge.Application.Reconstruction;
using RoomForge.Application.Annotations;
using RoomForge.Domain.Reconstruction;
using RoomForge.Domain.Geometry;
using RoomForge.Infrastructure.Export;
using RoomForge.Infrastructure.Reconstruction;

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
builder.Services.AddSingleton<SharpGltfSceneExporter>();

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
    builder.Services.AddRoomForgePersistence(roomForgeConnection, builder.Configuration["FAL_KEY"],
        builder.Configuration["Reconstruction:SegmentationProvider"], builder.Configuration["Reconstruction:ImageTo3DProvider"],
        builder.Configuration.GetValue<bool>("Reconstruction:FalHunyuanDataTransferApproved"));
    builder.Services.AddHostedService<ReconstructionJobWorker>();
    builder.Services.AddHostedService<ObjectMeshJobWorker>();
}

var app = builder.Build();
if (app.Environment.IsDevelopment()) { app.MapOpenApi(); app.UseCors("dev-web"); }
app.MapGet("/health", () => Results.Ok(new { status = "ok", phase = "04" }));
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

    app.MapPost("/api/projects/{projectId:guid}/annotations", async (Guid projectId, CreateAnnotationRequest request,
        AnnotationPersistence annotations, HttpContext context, CancellationToken ct) =>
    {
        if (request.SourceImageId == Guid.Empty) return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceImageId"] = ["A valid sourceImageId is required."] });
        var document = await annotations.CreateAsync(projectId, request.SourceImageId, ct);
        if (document is null) return Results.NotFound(new { error = "source_image_not_found_in_project" });
        context.Response.Headers.ETag = $"\"{document.Revision}\"";
        return Results.Created($"/api/projects/{projectId}/annotations/{document.AnnotationId}", document);
    });

    app.MapGet("/api/projects/{projectId:guid}/annotations/{annotationId:guid}", async (Guid projectId, Guid annotationId,
        AnnotationPersistence annotations, HttpContext context, CancellationToken ct) =>
    {
        var document = await annotations.GetAsync(projectId, annotationId, ct);
        if (document is null) return Results.NotFound();
        context.Response.Headers.ETag = $"\"{document.Revision}\"";
        return Results.Ok(document);
    });

    app.MapPut("/api/projects/{projectId:guid}/annotations/{annotationId:guid}", async (Guid projectId, Guid annotationId,
        UpdateAnnotationDocument request, HttpRequest httpRequest, HttpContext context, AnnotationPersistence annotations, CancellationToken ct) =>
    {
        if (!TryReadRevision(httpRequest.Headers.IfMatch, out var expectedRevision))
            return Results.Problem("A numeric If-Match revision is required.", statusCode: StatusCodes.Status428PreconditionRequired);
        try
        {
            var result = await annotations.UpdateAsync(projectId, annotationId, expectedRevision, request, ct);
            if (result.Status == UpdateAnnotationStatus.NotFound) return Results.NotFound();
            if (result.Status == UpdateAnnotationStatus.RevisionConflict)
            {
                context.Response.Headers.ETag = $"\"{result.Document!.Revision}\"";
                return Results.Conflict(new { error = "revision_conflict", currentRevision = result.Document.Revision });
            }
            context.Response.Headers.ETag = $"\"{result.Document!.Revision}\"";
            return Results.Ok(result.Document);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["annotation"] = [exception.Message] });
        }
    });

    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/topology", async (Guid projectId, Guid annotationId,
        TopologyPersistence topologies, HttpContext context, CancellationToken ct) =>
    {
        try
        {
            var topology = await topologies.CreateAsync(projectId, annotationId, ct);
            if (topology is null) return Results.NotFound();
            context.Response.Headers.ETag = $"\"{topology.Revision}\"";
            return Results.Created($"/api/projects/{projectId}/topologies/{topology.Id}", topology);
        }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["topology"] = [exception.Message] }); }
    });

    app.MapGet("/api/projects/{projectId:guid}/topologies/{topologyId:guid}", async (Guid projectId, Guid topologyId,
        TopologyPersistence topologies, HttpContext context, CancellationToken ct) =>
    {
        var topology = await topologies.GetAsync(projectId, topologyId, ct);
        if (topology is null) return Results.NotFound();
        context.Response.Headers.ETag = $"\"{topology.Revision}\"";
        return Results.Ok(topology);
    });

    app.MapPatch("/api/projects/{projectId:guid}/topologies/{topologyId:guid}", async (Guid projectId, Guid topologyId,
        UpdateTopologyRequest request, TopologyPersistence topologies, HttpContext context, CancellationToken ct) =>
    {
        if (request.ExpectedRevision < 1 || request.Edges is null || request.Edges.Count > 1000)
            return Results.Problem("A positive expectedRevision and at most 1000 edge corrections are required.", statusCode: StatusCodes.Status428PreconditionRequired);
        try
        {
            var result = await topologies.UpdateAsync(projectId, topologyId, request.ExpectedRevision,
                request.Edges.Select(edge => (edge.EdgeId, edge.Role)).ToArray(), ct);
            if (result.Topology is null) return Results.NotFound();
            if (!result.Saved)
            {
                context.Response.Headers.ETag = $"\"{result.Topology.Revision}\"";
                return Results.Conflict(new { error = "revision_conflict", currentRevision = result.Topology.Revision });
            }
            context.Response.Headers.ETag = $"\"{result.Topology.Revision}\"";
            return Results.Ok(result.Topology);
        }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["topology"] = [exception.Message] }); }
    });

    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/topologies/{topologyId:guid}/alignment",
        async (Guid projectId, Guid annotationId, Guid topologyId, PhotoAlignmentRequest request,
            AnnotationPersistence annotations, TopologyPersistence topologies, CancellationToken ct) =>
    {
        var annotation = await annotations.GetAsync(projectId, annotationId, ct);
        var topology = await topologies.GetAsync(projectId, topologyId, ct);
        if (annotation is null || topology is null || topology.AnnotationId != annotationId) return Results.NotFound();
        if (topology.AnnotationRevision != annotation.Revision) return Results.Conflict(new { error = "topology_annotation_revision_mismatch" });
        if (request.Camera is null || request.Camera.Id == Guid.Empty || request.Camera.PositionMeters is not { Length: 3 } position ||
            request.Camera.RotationCameraToWorld is not { Length: 4 } rotation || request.WorldEdges is null || request.WorldEdges.Count > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["alignment"] = ["A camera pose and at most 1000 world edges are required."] });
        if (request.WorldEdges.Any(edge => edge.StartMeters is not { Length: 3 } || edge.EndMeters is not { Length: 3 } ||
            edge.StartMeters.Concat(edge.EndMeters).Any(value => !double.IsFinite(value) || Math.Abs(value) > 100_000)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["worldEdges"] = ["Every world edge must contain finite meter coordinates."] });
        var camera = new CameraModel(request.Camera.Id, request.Camera.Fx, request.Camera.Fy, request.Camera.Cx, request.Camera.Cy,
            new Vector3((float)position[0], (float)position[1], (float)position[2]),
            new Quaternion((float)rotation[0], (float)rotation[1], (float)rotation[2], (float)rotation[3]),
            request.Camera.IntrinsicsStatus, request.Camera.Provenance);
        var calibration = CameraCalibrationBuilder.Build(annotation, camera);
        if (calibration.Status != StageStatus.Ok)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["camera"] = calibration.Diagnostics.ToArray() });
        var duplicateIds = request.WorldEdges.GroupBy(edge => edge.EdgeId).Any(group => group.Count() > 1);
        var knownEdgeIds = topology.Edges.Select(edge => edge.Id).ToHashSet();
        if (duplicateIds || request.WorldEdges.Any(edge => !knownEdgeIds.Contains(edge.EdgeId)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["worldEdges"] = ["World edge IDs must be unique topology edge IDs."] });
        var worldEdges = request.WorldEdges.ToDictionary(edge => edge.EdgeId, edge => (
            new Vector3((float)edge.StartMeters[0], (float)edge.StartMeters[1], (float)edge.StartMeters[2]),
            new Vector3((float)edge.EndMeters[0], (float)edge.EndMeters[1], (float)edge.EndMeters[2])));
        var alignment = PhotoAlignmentVerifier.Verify(annotation, topology, camera, worldEdges);
        return alignment.Value is null
            ? Results.Problem(string.Join(" ", alignment.Diagnostics), statusCode: StatusCodes.Status422UnprocessableEntity)
            : Results.Ok(new { status = "ok", alignment = alignment.Value, diagnostics = alignment.Diagnostics });
    });

    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/geometry",
        async (Guid projectId, Guid annotationId, GeometryCandidateRequest request,
            AnnotationPersistence annotations, TopologyPersistence topologies, CancellationToken ct) =>
    {
        var annotation = await annotations.GetAsync(projectId, annotationId, ct);
        var topology = await topologies.GetAsync(projectId, request.TopologyId, ct);
        if (annotation is null || topology is null || topology.AnnotationId != annotationId) return Results.NotFound();
        var plane = PlaneConstraintBuilder.Build(annotation);
        var camera = CameraCalibrationBuilder.Build(annotation);
        var geometry = ParametricGeometryBuilder.Build(annotation, topology, request.FloorRegionId, request.CalibrationSpanId);
        static string StageName(StageStatus status) => status switch
        {
            StageStatus.InsufficientData => "insufficient_data",
            StageStatus.Unsupported => "unsupported",
            _ => "ok",
        };
        return Results.Ok(new
        {
            geometrySpec = geometry.Value,
            stageResults = new[]
            {
                new { stage = "plane_constraints", status = StageName(plane.Status), diagnostics = plane.Diagnostics, assumptions = plane.Assumptions },
                new { stage = "camera_calibration", status = StageName(camera.Status), diagnostics = camera.Diagnostics, assumptions = camera.Assumptions },
                new { stage = "parametric_geometry", status = StageName(geometry.Status), diagnostics = geometry.Diagnostics, assumptions = geometry.Assumptions },
            },
        });
    });

    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/geometry-candidates",
        async (Guid projectId, Guid annotationId, CreateGeometryCandidateRequest request,
            AnnotationPersistence annotations, TopologyPersistence topologies, ScenePersistence scenes,
            ReconstructionCandidatePersistence candidates, CancellationToken ct) =>
    {
        var annotation = await annotations.GetAsync(projectId, annotationId, ct);
        var topology = await topologies.GetAsync(projectId, request.TopologyId, ct);
        var scene = await scenes.GetAsync(request.SceneId, ct);
        if (annotation is null || topology is null || scene is null || topology.AnnotationId != annotationId) return Results.NotFound();
        using var currentScene = JsonDocument.Parse(scene.Document);
        if (currentScene.RootElement.GetProperty("projectId").GetGuid() != projectId) return Results.BadRequest(new { error = "scene_project_mismatch" });
        if (scene.Revision != request.ExpectedSceneRevision) return Results.Conflict(new { error = "revision_conflict", currentRevision = scene.Revision });
        var result = ParametricGeometryBuilder.Build(annotation, topology, request.FloorRegionId, request.CalibrationSpanId);
        if (result.Value is null) return Results.UnprocessableEntity(new { status = "insufficient_data", diagnostics = result.Diagnostics });
        var instances = result.Value.Instances.ToDictionary(instance => instance.StableId);
        if (request.DimensionOverrides is null || request.DimensionOverrides.Count > 1000)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["dimensionOverrides"] = ["At most 1000 explicit meter dimension overrides are allowed."] });
        var overridden = new HashSet<(Guid InstanceId, int Index)>();
        foreach (var dimension in request.DimensionOverrides)
        {
            if (!instances.TryGetValue(dimension.InstanceId, out var instance) || dimension.DimensionIndex < 0 || dimension.DimensionIndex >= instance.Dimensions.Length ||
                !double.IsFinite(dimension.Meters) || dimension.Meters <= 0 || dimension.Meters > 1000 || !overridden.Add((dimension.InstanceId, dimension.DimensionIndex)))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["dimensionOverrides"] = ["Each override must target one dimension once and contain a positive finite length in meters."] });
            if (instance.Dimensions[dimension.DimensionIndex].Status != DimensionStatus.Unknown)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["dimensionOverrides"] = ["Only unknown dimensions may be filled by user overrides."] });
            var dimensions = instance.Dimensions.ToArray();
            dimensions[dimension.DimensionIndex] = new Measurement(dimension.Meters, DimensionStatus.Measured, "user_supplied", 1);
            instances[dimension.InstanceId] = instance with { Dimensions = dimensions };
        }
        var geometry = result.Value with { Instances = result.Value.Instances.Select(instance => instances[instance.StableId]).ToArray() };
        var candidate = new ReconstructionCandidate(Guid.NewGuid(), projectId, request.SceneId, scene.Revision,
            annotationId, annotation.Revision, topology.Id, topology.Revision, geometry, DateTimeOffset.UtcNow);
        try
        {
            var created = await candidates.CreateAsync(candidate, ct);
            return Results.Created($"/api/projects/{projectId}/geometry-candidates/{created.CandidateId}", created);
        }
        catch (InvalidOperationException) { return Results.Conflict(new { error = "candidate_source_revision_changed" }); }
    });

    app.MapGet("/api/projects/{projectId:guid}/geometry-candidates/{candidateId:guid}", async (Guid projectId, Guid candidateId,
        ReconstructionCandidatePersistence candidates, CancellationToken ct) =>
    {
        var candidate = await candidates.GetAsync(projectId, candidateId, ct);
        return candidate is null ? Results.NotFound() : Results.Ok(candidate);
    });

    app.MapPost("/api/projects/{projectId:guid}/geometry-candidates/{candidateId:guid}/apply", async (Guid projectId, Guid candidateId,
        ApplyGeometryCandidateRequest request, ReconstructionCandidateApplication application, CancellationToken ct) =>
    {
        var result = await application.ApplyAsync(projectId, candidateId, request.Confirmed, request.ExpectedRevision, ct);
        return result.Status switch
        {
            ApplyCandidateStatus.Applied => Results.Ok(new { candidateId, sceneId = result.SceneId, revision = result.Revision, document = result.Document }),
            ApplyCandidateStatus.NotFound => Results.NotFound(),
            ApplyCandidateStatus.ConfirmationRequired => Results.BadRequest(new { error = "explicit_confirmation_required" }),
            ApplyCandidateStatus.RevisionConflict => Results.Conflict(new { error = "revision_conflict", currentRevision = result.Revision }),
            ApplyCandidateStatus.SourceConflict => Results.Conflict(new { error = "candidate_source_revision_changed" }),
            ApplyCandidateStatus.InsufficientDimensions => Results.UnprocessableEntity(new { error = "insufficient_dimensions" }),
            ApplyCandidateStatus.ObjectsAlreadyExist => Results.Conflict(new { error = "candidate_objects_already_exist" }),
            _ => Results.ValidationProblem((result.Errors ?? ["Candidate could not be applied."]).ToDictionary(error => error, error => new[] { error })),
        };
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

    app.MapPost("/api/reconstruction/jobs/{analysisJobId:guid}/objects/{objectId:guid}/mesh", async (Guid analysisJobId, Guid objectId,
        CreateObjectMeshHttpRequest request, ObjectMeshJobSubmission submission, CancellationToken ct) =>
    {
        if (request.ObjectId != objectId) return Results.BadRequest(new { error = "object_id_path_mismatch" });
        var result = await submission.CreateAsync(analysisJobId,
            new RoomForge.Application.Reconstruction.CreateObjectMeshJobRequest(request.SceneId, objectId, request.ExpectedSceneRevision,
                request.Crop, request.IdempotencyKey, request.Confirmed), ct);
        return result.Status switch
        {
            ObjectMeshSubmissionStatus.Created => Results.Accepted($"/api/object-mesh-jobs/{result.Job!.Id}", result.Job),
            ObjectMeshSubmissionStatus.Reused => Results.Accepted($"/api/object-mesh-jobs/{result.Job!.Id}", result.Job),
            ObjectMeshSubmissionStatus.NotFound => Results.NotFound(new { error = result.Error }),
            ObjectMeshSubmissionStatus.Conflict => Results.Conflict(new { error = result.Error }),
            ObjectMeshSubmissionStatus.ConfirmationRequired => Results.BadRequest(new { error = result.Error }),
            ObjectMeshSubmissionStatus.ProviderUnavailable => Results.Problem(title: "Image-to-3D provider is unavailable", detail: result.Error, statusCode: StatusCodes.Status503ServiceUnavailable),
            _ => Results.BadRequest(new { error = result.Error }),
        };
    });

    app.MapGet("/api/object-mesh-jobs/{jobId:guid}", async (Guid jobId, IObjectMeshJobStore jobs, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    });
    app.MapGet("/api/object-mesh-jobs/{jobId:guid}/asset", async (Guid jobId, IObjectMeshJobStore jobs, LocalAssetStore assets, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job?.OutputAssetStorageId is not { } assetId) return Results.NotFound();
        var stored = assets.OpenStoredAsset(assetId, "model/gltf-binary");
        return stored is null ? Results.NotFound() : Results.File(stored.Value.Content, stored.Value.ContentType, $"roomforge-object-{job.ObjectId:N}.glb");
    });
    app.MapPost("/api/object-mesh-jobs/{jobId:guid}/apply", async (Guid jobId, ConfirmObjectMeshApplyRequest request,
        IObjectMeshJobStore jobs, ScenePersistence scenes, CancellationToken ct) =>
    {
        if (!request.Confirmed) return Results.BadRequest(new { error = "explicit_confirmation_required" });
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Results.NotFound();
        if (job.Status != "completed" || job.OutputAssetStorageId is null) return Results.Conflict(new { error = "mesh_job_not_completed" });
        if (request.ExpectedRevision != job.ExpectedSceneRevision) return Results.Conflict(new { error = "mesh_scene_revision_mismatch", expectedRevision = job.ExpectedSceneRevision });
        var stored = await scenes.GetAsync(job.SceneId, ct);
        if (stored is null) return Results.NotFound();
        if (stored.Revision != request.ExpectedRevision) return Results.Conflict(new { error = "revision_conflict", currentRevision = stored.Revision });
        var document = JsonNode.Parse(stored.Document)!.AsObject();
        var objects = document["objects"]?.AsArray();
        var target = objects?.SingleOrDefault(item => item?["id"]?.GetValue<Guid>() == job.ObjectId)?.AsObject();
        if (target is null) return Results.Conflict(new { error = "scene_object_missing" });
        target["meshAsset"] = new JsonObject
        {
            ["url"] = $"/api/object-mesh-jobs/{jobId:D}/asset",
            ["sourceJobId"] = jobId.ToString("D"),
            ["provider"] = "fal-ai/hunyuan3d-v3/image-to-3d",
            ["generatedAt"] = job.UpdatedAt,
        };
        using var proposed = JsonDocument.Parse(document.ToJsonString());
        var save = await scenes.SaveAsync(job.SceneId, proposed.RootElement, request.ExpectedRevision, ct);
        if (save.Status == SaveSceneStatus.RevisionConflict) return Results.Conflict(new { error = "revision_conflict", currentRevision = save.Revision });
        if (save.Status != SaveSceneStatus.Saved) return Results.ValidationProblem(save.Errors!.ToDictionary(message => message, message => new[] { message }));
        return Results.Ok(new { jobId, sceneId = job.SceneId, revision = save.Revision, document = save.Document });
    });

    app.MapGet("/api/reconstruction/jobs/{jobId:guid}", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? Results.NotFound() : Results.Ok(job);
    });
    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/cancel", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
        await jobs.CancelAsync(jobId, ct) ? Results.Ok(new { status = "cancelled" }) : Results.Conflict(new { error = "job_not_cancellable" }));
    app.MapPost("/api/reconstruction/jobs/{jobId:guid}/retry", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
        await jobs.RetryAsync(jobId, ct) ? Results.Accepted() : Results.Conflict(new { error = "job_not_retryable" }));

    app.MapGet("/api/reconstruction/jobs/{jobId:guid}/reconciliation", async (Guid jobId, IReconstructionStore jobs, CancellationToken ct) =>
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is null) return Results.NotFound();
        return Results.Ok(new
        {
            submissionState = job.SubmissionState,
            providerJobId = job.ProviderJobId,
            providerId = job.ProviderId,
            idempotencySupported = job.ProviderIdempotencySupported,
            reconciliationAttempts = job.ReconciliationAttempts,
            lastCheckedAt = job.LastReconciledAt,
            requiresReconciliationBeforeSubmit = SubmissionStates.RequiresReconciliationBeforeSubmit(job.SubmissionState),
        });
    });

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

    app.MapGet("/api/scenes/{sceneId:guid}/export.json", async (Guid sceneId, ScenePersistence scenes, CancellationToken ct) =>
    {
        var stored = await scenes.GetAsync(sceneId, ct);
        if (stored is null) return Results.NotFound();
        using var scene = JsonDocument.Parse(stored.Document);
        var export = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "roomforge-scene-export",
            formatVersion = 1,
            revision = stored.Revision,
            exportedAt = DateTimeOffset.UtcNow,
            scene = scene.RootElement,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.File(export, "application/json", $"roomforge-{sceneId:N}-r{stored.Revision}.json");
    });

    app.MapGet("/api/scenes/{sceneId:guid}/export.glb", async (Guid sceneId, ScenePersistence scenes, SharpGltfSceneExporter exporter,
        LocalAssetStore assets, CancellationToken ct) =>
    {
        var stored = await scenes.GetAsync(sceneId, ct);
        if (stored is null) return Results.NotFound();
        try
        {
            using var scene = JsonDocument.Parse(stored.Document);
            var glb = exporter.Export(scene.RootElement, textureId =>
            {
                var storedTexture = assets.OpenTexture(textureId);
                if (storedTexture is null) return null;
                using var content = storedTexture.Value.Content;
                using var buffer = new MemoryStream();
                content.CopyTo(buffer);
                return buffer.ToArray();
            });
            return Results.File(glb, "model/gltf-binary", $"roomforge-{sceneId:N}-r{stored.Revision}.glb");
        }
        catch (NotSupportedException exception)
        {
            return Results.Problem(title: "Scene geometry cannot be exported", detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
        catch (InvalidDataException exception)
        {
            return Results.Problem(title: "Scene is invalid for export", detail: exception.Message, statusCode: StatusCodes.Status422UnprocessableEntity);
        }
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
    app.MapGet("/api/scenes/{sceneId:guid}/export.json", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/scenes/{sceneId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPut("/api/scenes/{sceneId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/health/database", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/images", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/annotations", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/projects/{projectId:guid}/annotations/{annotationId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPut("/api/projects/{projectId:guid}/annotations/{annotationId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/topology", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/projects/{projectId:guid}/topologies/{topologyId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPatch("/api/projects/{projectId:guid}/topologies/{topologyId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/topologies/{topologyId:guid}/alignment", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/geometry", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/annotations/{annotationId:guid}/geometry-candidates", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapGet("/api/projects/{projectId:guid}/geometry-candidates/{candidateId:guid}", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
    app.MapPost("/api/projects/{projectId:guid}/geometry-candidates/{candidateId:guid}/apply", () => Results.Problem("Configure ConnectionStrings:RoomForge in .NET User Secrets or environment variables.", statusCode: StatusCodes.Status503ServiceUnavailable));
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
        var geometryChanged = !source.Polygon.Points.SequenceEqual(current.Polygon.Points);
        var expectedProvenance = geometryChanged ? $"{source.Provenance}; user-corrected" : source.Provenance;
        if (source.SemanticRole != current.SemanticRole || source.Confidence != current.Confidence || current.Provenance != expectedProvenance ||
            source.EstimatedWidthMeters != current.EstimatedWidthMeters || source.EstimatedHeightMeters != current.EstimatedHeightMeters)
            errors.Add($"Surface candidate {source.Id} changed outside supported review fields.");
    }
    var reviewedObjects = reviewed.Objects.ToDictionary(item => item.Id);
    foreach (var source in original.Objects)
    {
        var current = reviewedObjects[source.Id];
        var geometryChanged = !source.Polygon.Points.SequenceEqual(current.Polygon.Points);
        var expectedProvenance = geometryChanged ? $"{source.Provenance}; user-corrected" : source.Provenance;
        if (source.Confidence != current.Confidence || current.Provenance != expectedProvenance)
            errors.Add($"Object candidate {source.Id} changed outside supported review fields.");
    }
    return errors;
}

static bool TryReadRevision(string? ifMatch, out int revision)
{
    revision = 0;
    if (string.IsNullOrWhiteSpace(ifMatch) || ifMatch.Trim() == "*") return false;
    var token = ifMatch.Trim();
    if (token.StartsWith("W/", StringComparison.OrdinalIgnoreCase)) return false;
    token = token.Trim('"');
    return int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out revision) && revision > 0;
}

public partial class Program { }

public sealed record CreateProjectRequest(string? Name);
public sealed record CreateReconstructionJobRequest(Guid ImageId, string IdempotencyKey);
public sealed record CreateObjectMeshHttpRequest(Guid SceneId, Guid ObjectId, int ExpectedSceneRevision,
    RoomForge.Application.Reconstruction.PixelCrop Crop, string IdempotencyKey, bool Confirmed);
public sealed record CreateAnnotationRequest(Guid SourceImageId);
public sealed record TopologyEdgeCorrectionRequest(Guid EdgeId, RoomForge.Domain.Geometry.TopologyEdgeRole Role);
public sealed record UpdateTopologyRequest(int ExpectedRevision, IReadOnlyList<TopologyEdgeCorrectionRequest> Edges);
public sealed record CameraModelRequest(Guid Id, double Fx, double Fy, double Cx, double Cy, double[] PositionMeters,
    double[] RotationCameraToWorld, string IntrinsicsStatus, string Provenance);
public sealed record WorldEdgeRequest(Guid EdgeId, double[] StartMeters, double[] EndMeters);
public sealed record PhotoAlignmentRequest(CameraModelRequest Camera, IReadOnlyList<WorldEdgeRequest> WorldEdges);
public sealed record GeometryCandidateRequest(Guid TopologyId, Guid FloorRegionId, Guid CalibrationSpanId);
public sealed record DimensionOverrideRequest(Guid InstanceId, int DimensionIndex, double Meters);
public sealed record CreateGeometryCandidateRequest(Guid TopologyId, Guid SceneId, int ExpectedSceneRevision,
    Guid FloorRegionId, Guid CalibrationSpanId, IReadOnlyList<DimensionOverrideRequest> DimensionOverrides);
public sealed record ApplyGeometryCandidateRequest(bool Confirmed, int ExpectedRevision);
public sealed record ConfirmReconstructionRequest(bool Confirmed, int ExpectedRevision);
public sealed record ConfirmObjectMeshApplyRequest(bool Confirmed, int ExpectedRevision);
