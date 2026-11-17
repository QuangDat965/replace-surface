using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomForge.Application.Annotations;
using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Annotations;
using RoomForge.Domain.Geometry;
using RoomForge.Domain.Reconstruction;
using RoomForge.Infrastructure.Persistence;

namespace RoomForge.IntegrationTests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class GeometryCandidatePersistenceTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task CandidatePersistsAsAppendOnlyRecordWithSourceRevisions()
    {
        var image = await fixture.CreateImageAsync();
        Guid annotationId;
        Guid topologyId;
        await using (var scope = fixture.CreateScope())
        {
            var annotations = scope.ServiceProvider.GetRequiredService<AnnotationPersistence>();
            var annotation = await annotations.CreateAsync(fixture.ProjectId, image.Id, CancellationToken.None);
            annotationId = annotation!.AnnotationId;
            var region = new AnnotationRegion(Guid.NewGuid(), "floor", new ImagePolygon([new(10, 10), new(700, 10), new(700, 500), new(10, 500)]));
            var span = new AnnotationReferenceSpan(Guid.NewGuid(), new(10, 100), new(300, 100), 2.4);
            var update = await annotations.UpdateAsync(fixture.ProjectId, annotationId, annotation.Revision,
                new UpdateAnnotationDocument([region], [span], annotation.CameraHint, []), CancellationToken.None);
            Assert.Equal(UpdateAnnotationStatus.Saved, update.Status);
            var topology = await scope.ServiceProvider.GetRequiredService<TopologyPersistence>()
                .CreateAsync(fixture.ProjectId, annotationId, CancellationToken.None);
            topologyId = topology!.Id;
        }

        var sceneId = Guid.NewGuid();
        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
            db.Scenes.Add(new SceneRecord { Id = sceneId, ProjectId = fixture.ProjectId, Revision = 7, Document = "{}", UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var instanceId = Guid.NewGuid();
        var geometry = new SceneGeometrySpec(1, annotationId, null, topologyId,
            [new GeometryInstance(instanceId, "floor candidate", "architecture", GeometryKind.Box,
                [0, 0, 0], [0, 0, 0, 1],
                [new(4, DimensionStatus.Estimated, "single-view estimate", .3), new(.1, DimensionStatus.Measured, "user_supplied", 1), new(3, DimensionStatus.Estimated, "single-view estimate", .2)],
                [], [], true)], new ReconstructionAssumptions("unknown", "user_supplied", null, []), []);
        var candidate = new ReconstructionCandidate(Guid.NewGuid(), fixture.ProjectId, sceneId, 7, annotationId, 2, topologyId, 1, geometry, DateTimeOffset.UtcNow);
        await using (var scope = fixture.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReconstructionCandidatePersistence>().CreateAsync(candidate, CancellationToken.None);
        }
        await using (var scope = fixture.CreateScope())
        {
            var loaded = await scope.ServiceProvider.GetRequiredService<ReconstructionCandidatePersistence>()
                .GetAsync(fixture.ProjectId, candidate.CandidateId, CancellationToken.None);
            Assert.NotNull(loaded);
            Assert.Equal(7, loaded.BaseSceneRevision);
            Assert.Equal(topologyId, loaded.TopologyId);
            Assert.Equal(instanceId, loaded.GeometrySpec.Instances.Single().StableId);
            Assert.Equal("measured", loaded.GeometrySpec.Instances.Single().Dimensions[1].Status.ToString().ToLowerInvariant());
        }
    }
}
