using RoomForge.Domain.Annotations;
using RoomForge.Domain.Geometry;

namespace RoomForge.Application.Annotations;

public sealed record UpdateTopologyResult(bool Saved, RoomTopology? Topology);

public interface ITopologyStore
{
    Task<RoomTopology> CreateAsync(Guid projectId, RoomTopology topology, CancellationToken cancellationToken);
    Task<RoomTopology?> GetAsync(Guid projectId, Guid topologyId, CancellationToken cancellationToken);
    Task<UpdateTopologyResult> UpdateAsync(Guid projectId, RoomTopology topology, int expectedRevision, CancellationToken cancellationToken);
}

public sealed class TopologyPersistence(IAnnotationStore annotations, ITopologyStore store)
{
    public async Task<RoomTopology?> CreateAsync(Guid projectId, Guid annotationId, CancellationToken cancellationToken)
    {
        var annotation = await annotations.GetAsync(projectId, annotationId, cancellationToken);
        if (annotation is null) return null;
        var result = RoomTopologyBuilder.Build(annotation);
        if (result.Value is null) throw new ArgumentException(string.Join(" ", result.Diagnostics));
        return await store.CreateAsync(projectId, result.Value, cancellationToken);
    }

    public Task<RoomTopology?> GetAsync(Guid projectId, Guid topologyId, CancellationToken cancellationToken) =>
        store.GetAsync(projectId, topologyId, cancellationToken);

    public async Task<UpdateTopologyResult> UpdateAsync(Guid projectId, Guid topologyId, int expectedRevision,
        IReadOnlyList<(Guid EdgeId, TopologyEdgeRole Role)> corrections, CancellationToken cancellationToken)
    {
        var current = await store.GetAsync(projectId, topologyId, cancellationToken);
        if (current is null) return new UpdateTopologyResult(false, null);
        if (current.Revision != expectedRevision) return new UpdateTopologyResult(false, current);
        var annotation = await annotations.GetAsync(projectId, current.AnnotationId, cancellationToken);
        if (annotation is null) return new UpdateTopologyResult(false, null);
        if (annotation.Revision != current.AnnotationRevision)
            throw new ArgumentException("Topology is based on an older annotation revision; rebuild it before applying corrections.");
        var errors = RoomTopologyBuilder.ValidateCorrections(current, corrections);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        var correctionsById = corrections.ToDictionary(item => item.EdgeId, item => item.Role);
        var corrected = current with
        {
            Revision = checked(current.Revision + 1),
            Edges = current.Edges.Select(edge => correctionsById.TryGetValue(edge.Id, out var role)
                ? edge with { Role = role, Provenance = "user_supplied" }
                : edge).ToArray(),
        };
        return await store.UpdateAsync(projectId, corrected, expectedRevision, cancellationToken);
    }
}
