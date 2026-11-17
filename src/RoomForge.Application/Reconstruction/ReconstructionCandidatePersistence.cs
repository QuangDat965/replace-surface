using RoomForge.Domain.Geometry;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Application.Annotations;
using RoomForge.Application.Scenes;

namespace RoomForge.Application.Reconstruction;

public sealed record ReconstructionCandidate(Guid CandidateId, Guid ProjectId, Guid SceneId, int BaseSceneRevision,
    Guid AnnotationId, int AnnotationRevision, Guid TopologyId, int TopologyRevision, SceneGeometrySpec GeometrySpec,
    DateTimeOffset CreatedAt);

public interface IReconstructionCandidateStore
{
    Task<ReconstructionCandidate> CreateAsync(ReconstructionCandidate candidate, CancellationToken cancellationToken);
    Task<ReconstructionCandidate?> GetAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken);
}

public sealed class ReconstructionCandidatePersistence(IReconstructionCandidateStore store)
{
    public Task<ReconstructionCandidate> CreateAsync(ReconstructionCandidate candidate, CancellationToken cancellationToken) =>
        store.CreateAsync(candidate, cancellationToken);

    public Task<ReconstructionCandidate?> GetAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken) =>
        store.GetAsync(projectId, candidateId, cancellationToken);
}

public enum ApplyCandidateStatus { Applied, NotFound, ConfirmationRequired, RevisionConflict, SourceConflict, InsufficientDimensions, ObjectsAlreadyExist, InvalidScene }
public sealed record ApplyCandidateResult(ApplyCandidateStatus Status, int? Revision = null, string? Document = null,
    IReadOnlyList<string>? Errors = null, Guid? SceneId = null);

public sealed class ReconstructionCandidateApplication(
    ReconstructionCandidatePersistence candidates,
    AnnotationPersistence annotations,
    TopologyPersistence topologies,
    ScenePersistence scenes)
{
    public async Task<ApplyCandidateResult> ApplyAsync(Guid projectId, Guid candidateId, bool confirmed, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!confirmed) return new(ApplyCandidateStatus.ConfirmationRequired);
        var candidate = await candidates.GetAsync(projectId, candidateId, cancellationToken);
        if (candidate is null) return new(ApplyCandidateStatus.NotFound);
        if (expectedRevision != candidate.BaseSceneRevision) return new(ApplyCandidateStatus.RevisionConflict, candidate.BaseSceneRevision);
        var annotation = await annotations.GetAsync(projectId, candidate.AnnotationId, cancellationToken);
        var topology = await topologies.GetAsync(projectId, candidate.TopologyId, cancellationToken);
        if (annotation is null || topology is null || annotation.Revision != candidate.AnnotationRevision || topology.Revision != candidate.TopologyRevision)
            return new(ApplyCandidateStatus.SourceConflict);
        var scene = await scenes.GetAsync(candidate.SceneId, cancellationToken);
        if (scene is null) return new(ApplyCandidateStatus.NotFound);
        if (scene.Revision != candidate.BaseSceneRevision) return new(ApplyCandidateStatus.RevisionConflict, scene.Revision);
        if (candidate.GeometrySpec.Instances.Any(instance => instance.Kind != GeometryKind.Box || instance.Dimensions.Length != 3 ||
            instance.Dimensions.Any(dimension => dimension.Meters is not { } meters || !double.IsFinite(meters) || meters <= 0)))
            return new(ApplyCandidateStatus.InsufficientDimensions);

        var root = JsonNode.Parse(scene.Document)?.AsObject();
        var objects = root?["objects"] as JsonArray;
        if (root is null || objects is null) return new(ApplyCandidateStatus.InvalidScene, Errors: ["Persisted scene must contain an objects array."]);
        var materialId = root["materials"]?.AsArray().OfType<JsonObject>()
            .Select(item => item["id"]?.GetValue<Guid>()).FirstOrDefault(id => id.HasValue);
        if (materialId is null) return new(ApplyCandidateStatus.InvalidScene, Errors: ["Scene must contain a material for generated architecture surfaces."]);
        var existingIds = objects.OfType<JsonObject>().Select(item => item["id"]?.GetValue<Guid>()).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        if (candidate.GeometrySpec.Instances.Any(instance => existingIds.Contains(instance.StableId))) return new(ApplyCandidateStatus.ObjectsAlreadyExist);
        foreach (var instance in candidate.GeometrySpec.Instances)
        {
            var semanticRole = instance.Name.Contains("floor", StringComparison.OrdinalIgnoreCase) ? "floor" : "wall";
            var faceId = semanticRole == "floor" ? "top" : "front";
            var surfaceIdBytes = System.Security.Cryptography.SHA256.HashData(instance.StableId.ToByteArray()
                .Concat(System.Text.Encoding.UTF8.GetBytes("surface-" + faceId)).ToArray());
            var surfaceId = new Guid(surfaceIdBytes.AsSpan(0, 16));
            objects.Add(new JsonObject
            {
                ["id"] = instance.StableId, ["name"] = instance.Name, ["category"] = instance.Category,
                ["geometry"] = new JsonObject { ["kind"] = "box" },
                ["position"] = JsonSerializer.SerializeToNode(instance.PositionMeters),
                ["rotation"] = JsonSerializer.SerializeToNode(instance.RotationQuaternion),
                ["dimensions"] = JsonSerializer.SerializeToNode(instance.Dimensions.Select(dimension => dimension.Meters).ToArray()),
                ["visible"] = instance.Visible, ["locked"] = false,
                ["measurementStatus"] = instance.Dimensions.Any(dimension => dimension.Status == DimensionStatus.Estimated) ? "estimated" : "measured",
                ["provenance"] = string.Join("; ", instance.Dimensions.Select(dimension => dimension.Provenance).Distinct()),
                ["surfaces"] = new JsonArray(new JsonObject
                {
                    ["id"] = surfaceId, ["semanticRole"] = semanticRole, ["faceId"] = faceId, ["materialId"] = materialId.Value,
                    ["uv"] = new JsonObject { ["rotationDegrees"] = 0, ["offset"] = new JsonArray(0, 0) },
                }),
            });
        }
        using var document = JsonDocument.Parse(root.ToJsonString());
        var saved = await scenes.SaveAsync(candidate.SceneId, document.RootElement, candidate.BaseSceneRevision, cancellationToken);
        return saved.Status switch
        {
            SaveSceneStatus.Saved => new(ApplyCandidateStatus.Applied, saved.Revision, saved.Document, SceneId: candidate.SceneId),
            SaveSceneStatus.RevisionConflict => new(ApplyCandidateStatus.RevisionConflict, saved.Revision),
            _ => new(ApplyCandidateStatus.InvalidScene, Errors: saved.Errors),
        };
    }
}
