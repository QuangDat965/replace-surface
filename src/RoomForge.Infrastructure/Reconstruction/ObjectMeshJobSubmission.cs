using System.Security.Cryptography;
using System.Text.Json;
using RoomForge.Application.Reconstruction;
using RoomForge.Application.Scenes;
using RoomForge.Domain.Scenes;
using RoomForge.Infrastructure.Persistence;
using RoomForge.Infrastructure.Storage;

namespace RoomForge.Infrastructure.Reconstruction;

public enum ObjectMeshSubmissionStatus { Created, Reused, NotFound, Conflict, Invalid, ConfirmationRequired, ProviderUnavailable }
public sealed record ObjectMeshSubmissionResult(ObjectMeshSubmissionStatus Status, ObjectMeshJobSnapshot? Job = null, string? Error = null);

public sealed class ObjectMeshJobSubmission(IReconstructionStore analysisJobs, IReconstructionImageStore images,
    ScenePersistence scenes, IObjectMeshJobStore meshJobs, LocalAssetStore assets, FalHunyuan3DOptions providerOptions)
{
    public async Task<ObjectMeshSubmissionResult> CreateAsync(Guid analysisJobId, CreateObjectMeshJobRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Confirmed) return new(ObjectMeshSubmissionStatus.ConfirmationRequired, Error: "explicit_confirmation_required");
        if (!providerOptions.IsConfigured) return new(ObjectMeshSubmissionStatus.ProviderUnavailable, Error: "image_to_3d_provider_not_enabled_or_data_transfer_not_approved");
        if (request.Crop is null || request.SceneId == Guid.Empty || request.ObjectId == Guid.Empty || request.ExpectedSceneRevision < 1 ||
            string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length > 200 || request.IdempotencyKey.Any(char.IsControl))
            return new(ObjectMeshSubmissionStatus.Invalid, Error: "invalid_request");

        var analysisJob = await analysisJobs.GetAsync(analysisJobId, cancellationToken);
        if (analysisJob is null) return new(ObjectMeshSubmissionStatus.NotFound, Error: "analysis_job_not_found");
        if (analysisJob.Status != "succeeded" || string.IsNullOrWhiteSpace(analysisJob.AnalysisJson))
            return new(ObjectMeshSubmissionStatus.Conflict, Error: "analysis_must_complete_before_mesh_generation");
        try
        {
            using var analysis = JsonDocument.Parse(analysisJob.AnalysisJson);
            if (!analysis.RootElement.TryGetProperty("sourceImageId", out var sourceImage) || !sourceImage.TryGetGuid(out var sourceImageId) || sourceImageId != analysisJob.ImageId)
                return new(ObjectMeshSubmissionStatus.Conflict, Error: "analysis_source_image_mismatch");
        }
        catch (JsonException) { return new(ObjectMeshSubmissionStatus.Conflict, Error: "analysis_result_invalid"); }
        var image = await images.GetAsync(analysisJob.ImageId, cancellationToken);
        if (image is null || image.ProjectId != analysisJob.ProjectId) return new(ObjectMeshSubmissionStatus.NotFound, Error: "source_image_not_found");

        var storedScene = await scenes.GetAsync(request.SceneId, cancellationToken);
        if (storedScene is null) return new(ObjectMeshSubmissionStatus.NotFound, Error: "scene_not_found");
        if (storedScene.Revision != request.ExpectedSceneRevision) return new(ObjectMeshSubmissionStatus.Conflict, Error: "scene_revision_conflict");
        using var sceneDocument = JsonDocument.Parse(storedScene.Document);
        var root = sceneDocument.RootElement;
        if (root.GetProperty("projectId").GetGuid() != analysisJob.ProjectId) return new(ObjectMeshSubmissionStatus.Conflict, Error: "project_mismatch");
        var target = root.GetProperty("objects").EnumerateArray().SingleOrDefault(item => item.GetProperty("id").GetGuid() == request.ObjectId);
        if (target.ValueKind != JsonValueKind.Object) return new(ObjectMeshSubmissionStatus.NotFound, Error: "scene_object_not_found");
        if (SceneDocumentValidator.IsProtectedObject(target)) return new(ObjectMeshSubmissionStatus.Invalid, Error: "structural_objects_cannot_receive_generated_meshes");

        var imageAsset = assets.OpenRoomImage(image.StorageId);
        if (imageAsset is null) return new(ObjectMeshSubmissionStatus.NotFound, Error: "source_image_asset_not_found");
        byte[] sourceBytes;
        await using (imageAsset.Value.Content)
        {
            using var buffer = new MemoryStream();
            await imageAsset.Value.Content.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length > 10 * 1024 * 1024) return new(ObjectMeshSubmissionStatus.Invalid, Error: "source_image_too_large");
            sourceBytes = buffer.ToArray();
        }
        byte[] cropped;
        try { cropped = ImageCropper.CropToPng(sourceBytes, image.Width, image.Height, request.Crop); }
        catch (InvalidDataException exception) { return new(ObjectMeshSubmissionStatus.Invalid, Error: exception.Message); }
        var cropHash = Convert.ToHexString(SHA256.HashData(cropped)).ToLowerInvariant();

        var existing = await meshJobs.FindByIdempotencyKeyAsync(analysisJob.ProjectId, request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            var sameRequest = existing.AnalysisJobId == analysisJobId && existing.SourceImageId == image.Id &&
                existing.SceneId == request.SceneId && existing.ObjectId == request.ObjectId &&
                existing.ExpectedSceneRevision == request.ExpectedSceneRevision && existing.CropSha256 == cropHash;
            return sameRequest
                ? new(ObjectMeshSubmissionStatus.Reused, existing)
                : new(ObjectMeshSubmissionStatus.Conflict, Error: "idempotency_key_reused_with_different_mesh_request");
        }

        using var cropStream = new MemoryStream(cropped, writable: false);
        var cropStorageId = await assets.SaveAsync(cropStream, 10 * 1024 * 1024, cancellationToken);
        Guid jobId;
        try
        {
            jobId = await meshJobs.CreateOrGetAsync(new ObjectMeshJobRequest(analysisJob.ProjectId, analysisJobId,
                image.Id, request.SceneId, request.ObjectId, request.ExpectedSceneRevision, cropStorageId, "image/png", cropHash,
                request.IdempotencyKey), cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return new(ObjectMeshSubmissionStatus.Conflict, Error: "idempotency_key_reused_with_different_mesh_request");
        }
        var job = await meshJobs.GetAsync(jobId, cancellationToken);
        return job is null ? new(ObjectMeshSubmissionStatus.Conflict, Error: "mesh_job_creation_race") : new(ObjectMeshSubmissionStatus.Created, job);
    }
}
