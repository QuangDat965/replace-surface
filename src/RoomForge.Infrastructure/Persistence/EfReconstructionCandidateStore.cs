using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfReconstructionCandidateStore(RoomForgeDbContext db) : IReconstructionCandidateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ReconstructionCandidate> CreateAsync(ReconstructionCandidate candidate, CancellationToken cancellationToken)
    {
        var sceneMatches = await db.Scenes.AnyAsync(scene => scene.Id == candidate.SceneId &&
            scene.ProjectId == candidate.ProjectId && scene.Revision == candidate.BaseSceneRevision, cancellationToken);
        var annotationMatches = await db.AnnotationDocuments.AnyAsync(annotation => annotation.AnnotationId == candidate.AnnotationId &&
            annotation.ProjectId == candidate.ProjectId && annotation.Revision == candidate.AnnotationRevision, cancellationToken);
        var topologyMatches = await db.RoomTopologies.AnyAsync(topology => topology.TopologyId == candidate.TopologyId &&
            topology.ProjectId == candidate.ProjectId && topology.Revision == candidate.TopologyRevision, cancellationToken);
        if (!sceneMatches || !annotationMatches || !topologyMatches)
            throw new InvalidOperationException("Candidate source revisions must belong to the same project and match current persisted records.");
        db.ReconstructionCandidates.Add(new ReconstructionCandidateRecord
        {
            CandidateId = candidate.CandidateId, ProjectId = candidate.ProjectId, SceneId = candidate.SceneId,
            AnnotationId = candidate.AnnotationId, CreatedAt = candidate.CreatedAt,
            Document = JsonSerializer.Serialize(candidate, JsonOptions),
        });
        await db.SaveChangesAsync(cancellationToken);
        return candidate;
    }

    public async Task<ReconstructionCandidate?> GetAsync(Guid projectId, Guid candidateId, CancellationToken cancellationToken)
    {
        var json = await db.ReconstructionCandidates.AsNoTracking()
            .Where(item => item.ProjectId == projectId && item.CandidateId == candidateId)
            .Select(item => item.Document).SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<ReconstructionCandidate>(json, JsonOptions);
    }
}
