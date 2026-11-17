using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Annotations;
using RoomForge.Domain.Geometry;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfTopologyStore(RoomForgeDbContext db) : ITopologyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RoomTopology> CreateAsync(Guid projectId, RoomTopology topology, CancellationToken cancellationToken)
    {
        if (!await db.AnnotationDocuments.AnyAsync(item => item.ProjectId == projectId && item.AnnotationId == topology.AnnotationId, cancellationToken))
            throw new InvalidOperationException("Topology annotation does not belong to the project.");
        var now = DateTimeOffset.UtcNow;
        db.RoomTopologies.Add(new RoomTopologyRecord
        {
            TopologyId = topology.Id, ProjectId = projectId, AnnotationId = topology.AnnotationId,
            Revision = topology.Revision, Document = JsonSerializer.Serialize(topology, JsonOptions), CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return topology;
    }

    public async Task<RoomTopology?> GetAsync(Guid projectId, Guid topologyId, CancellationToken cancellationToken)
    {
        var json = await db.RoomTopologies.AsNoTracking().Where(item => item.ProjectId == projectId && item.TopologyId == topologyId)
            .Select(item => item.Document).SingleOrDefaultAsync(cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<RoomTopology>(json, JsonOptions);
    }

    public async Task<UpdateTopologyResult> UpdateAsync(Guid projectId, RoomTopology topology, int expectedRevision, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await db.RoomTopologies.Where(item => item.ProjectId == projectId && item.TopologyId == topology.Id && item.Revision == expectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Revision, topology.Revision)
                .SetProperty(item => item.Document, JsonSerializer.Serialize(topology, JsonOptions))
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (count == 1) return new UpdateTopologyResult(true, topology);
        var current = await GetAsync(projectId, topology.Id, cancellationToken);
        return new UpdateTopologyResult(false, current);
    }
}
