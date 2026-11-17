using Microsoft.Extensions.DependencyInjection;
using RoomForge.Application.Annotations;
using RoomForge.Domain.Annotations;
using RoomForge.Domain.Geometry;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.IntegrationTests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class TopologyPersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task TopologyIsStoredReloadedAndCorrectedWithRevisionCheck()
    {
        var image = await fixture.CreateImageAsync();
        Guid annotationId;
        await using (var scope = fixture.CreateScope())
        {
            var annotations = scope.ServiceProvider.GetRequiredService<AnnotationPersistence>();
            var annotation = await annotations.CreateAsync(fixture.ProjectId, image.Id, CancellationToken.None);
            annotationId = annotation!.AnnotationId;
            var region = new AnnotationRegion(Guid.NewGuid(), "floor",
                new ImagePolygon([new(20, 20), new(400, 20), new(400, 300), new(20, 300)]));
            var saved = await annotations.UpdateAsync(fixture.ProjectId, annotationId, annotation.Revision,
                new UpdateAnnotationDocument([region], [], annotation.CameraHint, []), CancellationToken.None);
            Assert.Equal(UpdateAnnotationStatus.Saved, saved.Status);
        }

        Guid topologyId;
        Guid edgeId;
        await using (var scope = fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TopologyPersistence>();
            var topology = await service.CreateAsync(fixture.ProjectId, annotationId, CancellationToken.None);
            topologyId = topology!.Id;
            edgeId = topology.Edges[0].Id;
            Assert.All(topology.Edges, edge => Assert.Equal(TopologyEdgeRole.UnknownCandidate, edge.Role));
        }

        await using (var scope = fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TopologyPersistence>();
            var loaded = await service.GetAsync(fixture.ProjectId, topologyId, CancellationToken.None);
            Assert.Equal(TopologyEdgeRole.UnknownCandidate, loaded!.Edges[0].Role);
            var update = await service.UpdateAsync(fixture.ProjectId, topologyId, 1,
                [(edgeId, TopologyEdgeRole.FloorWall)], CancellationToken.None);
            Assert.True(update.Saved);
            Assert.Equal("user_supplied", update.Topology!.Edges[0].Provenance);
            Assert.Equal(2, update.Topology.Revision);
        }

        await using (var scope = fixture.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TopologyPersistence>();
            var persisted = await service.GetAsync(fixture.ProjectId, topologyId, CancellationToken.None);
            Assert.Equal(TopologyEdgeRole.FloorWall, persisted!.Edges[0].Role);
            var stale = await service.UpdateAsync(fixture.ProjectId, topologyId, 1,
                [(edgeId, TopologyEdgeRole.Occluded)], CancellationToken.None);
            Assert.False(stale.Saved);
            Assert.Equal(2, stale.Topology!.Revision);
        }
    }
}
