using System.Text.Json;
using System.Text.Json.Nodes;
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
public sealed class GeometryCandidateApplicationTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    [Fact]
    public async Task ApplyRequiresConfirmationAndMatchingRevisionsThenPersistsGeometry()
    {
        var image = await fixture.CreateImageAsync();
        var sceneId = Guid.NewGuid();
        var sceneNode = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-scene.json")))!.AsObject();
        sceneNode["sceneId"] = sceneId;
        sceneNode["projectId"] = fixture.ProjectId;
        using (var seedDocument = JsonDocument.Parse(sceneNode.ToJsonString()))
        {
            await using var scope = fixture.CreateScope();
            var seeded = await scope.ServiceProvider.GetRequiredService<RoomForge.Application.Scenes.ScenePersistence>()
                .SaveAsync(sceneId, seedDocument.RootElement, 0, CancellationToken.None);
            Assert.Equal(RoomForge.Application.Scenes.SaveSceneStatus.Saved, seeded.Status);
        }

        Guid annotationId;
        Guid topologyId;
        await using (var scope = fixture.CreateScope())
        {
            var annotations = scope.ServiceProvider.GetRequiredService<AnnotationPersistence>();
            var annotation = await annotations.CreateAsync(fixture.ProjectId, image.Id, CancellationToken.None);
            annotationId = annotation!.AnnotationId;
            var region = new AnnotationRegion(Guid.NewGuid(), "floor", new ImagePolygon([new(10, 10), new(700, 10), new(700, 500), new(10, 500)]));
            var span = new AnnotationReferenceSpan(Guid.NewGuid(), new(10, 100), new(300, 100), 2.4);
            var updated = await annotations.UpdateAsync(fixture.ProjectId, annotationId, annotation.Revision,
                new UpdateAnnotationDocument([region], [span], annotation.CameraHint, []), CancellationToken.None);
            Assert.Equal(UpdateAnnotationStatus.Saved, updated.Status);
            topologyId = (await scope.ServiceProvider.GetRequiredService<TopologyPersistence>()
                .CreateAsync(fixture.ProjectId, annotationId, CancellationToken.None))!.Id;
        }

        var objectId = Guid.NewGuid();
        var geometry = new SceneGeometrySpec(1, annotationId, null, topologyId,
            [new GeometryInstance(objectId, "reviewed floor", "architecture", GeometryKind.Box, [0, 0, 0], [0, 0, 0, 1],
                [new(4, DimensionStatus.Estimated, "single-view estimate", .4), new(.12, DimensionStatus.Measured, "user_supplied", 1), new(3, DimensionStatus.Estimated, "single-view estimate", .2)], [], [], true)],
            new ReconstructionAssumptions("unknown", "user_supplied", null, []), []);
        var candidate = new ReconstructionCandidate(Guid.NewGuid(), fixture.ProjectId, sceneId, 1, annotationId, 2, topologyId, 1, geometry, DateTimeOffset.UtcNow);
        await using (var scope = fixture.CreateScope())
        {
            var candidateStore = scope.ServiceProvider.GetRequiredService<ReconstructionCandidatePersistence>();
            await candidateStore.CreateAsync(candidate, CancellationToken.None);
            var application = scope.ServiceProvider.GetRequiredService<ReconstructionCandidateApplication>();
            Assert.Equal(ApplyCandidateStatus.ConfirmationRequired,
                (await application.ApplyAsync(fixture.ProjectId, candidate.CandidateId, false, 1, CancellationToken.None)).Status);
            var applied = await application.ApplyAsync(fixture.ProjectId, candidate.CandidateId, true, 1, CancellationToken.None);
            Assert.Equal(ApplyCandidateStatus.Applied, applied.Status);
            Assert.Equal(2, applied.Revision);
            using var saved = JsonDocument.Parse(applied.Document!);
            var added = saved.RootElement.GetProperty("objects").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == objectId);
            Assert.Equal(.12, added.GetProperty("dimensions")[1].GetDouble());
            Assert.Equal("estimated", added.GetProperty("measurementStatus").GetString());
            Assert.False(added.GetProperty("locked").GetBoolean());
            var assignedSurface = added.GetProperty("surfaces").EnumerateArray().Single();
            Assert.Equal("floor", assignedSurface.GetProperty("semanticRole").GetString());
            Assert.Equal("top", assignedSurface.GetProperty("faceId").GetString());
            Assert.NotEqual(Guid.Empty, assignedSurface.GetProperty("materialId").GetGuid());
        }

        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<RoomForgeDbContext>();
            await db.ReconstructionCandidates.Where(item => item.CandidateId == candidate.CandidateId).ExecuteDeleteAsync();
            await db.Scenes.Where(item => item.Id == sceneId).ExecuteDeleteAsync();
        }
    }
}
