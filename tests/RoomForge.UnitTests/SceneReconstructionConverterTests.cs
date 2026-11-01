using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Reconstruction;
using RoomForge.Domain.Scenes;

namespace RoomForge.UnitTests;

public sealed class SceneReconstructionConverterTests
{
    [Fact]
    public async Task Preview_preserves_linked_ids_and_surface_assignments_and_excludes_rejected_objects()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var baseJson = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var baseRoot = baseJson.RootElement;
        var targetObject = baseRoot.GetProperty("objects").EnumerateArray().First(item => item.GetProperty("surfaces").GetArrayLength() > 0);
        var targetObjectId = targetObject.GetProperty("id").GetGuid();
        var originalSurfaces = JsonNode.Parse(targetObject.GetProperty("surfaces").GetRawText());
        var targetSurfaceId = targetObject.GetProperty("surfaces")[0].GetProperty("id").GetGuid();
        var source = Guid.NewGuid();
        var analysis = new SceneAnalysisResult(1, Guid.NewGuid(), source, 100, 80, "roomforge-mock", "1.0", DateTimeOffset.UtcNow,
            new ReconstructionAssumptions("unknown", "unknown", null, []),
            [new DetectedSurface(Guid.NewGuid(), "floor", new ImagePolygon([new(0, 0), new(100, 0), new(100, 80)]), null, null, .8, "mock", targetSurfaceId)],
            [
                new DetectedObject(Guid.NewGuid(), "furniture", "Updated chair", new ImagePolygon([new(1, 1), new(10, 1), new(10, 10)]), [1, 0, 0], [1, 1, 1], .8, "mock", true, targetObjectId),
                new DetectedObject(Guid.NewGuid(), "furniture", "Excluded stool", new ImagePolygon([new(20, 1), new(30, 1), new(30, 10)]), [2, 0, 0], [.5, .5, .5], .7, "mock", false),
            ]);

        var preview = SceneReconstructionConverter.Convert(baseRoot.GetRawText(), analysis, baseRoot.GetProperty("sceneId").GetGuid(), preserveSceneIdentity: true);
        using var result = JsonDocument.Parse(preview);
        var errors = SceneDocumentValidator.Validate(result.RootElement);

        Assert.Empty(errors);
        var retained = result.RootElement.GetProperty("objects").EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == targetObjectId);
        Assert.Equal("Updated chair", retained.GetProperty("name").GetString());
        Assert.True(JsonNode.DeepEquals(originalSurfaces, JsonNode.Parse(retained.GetProperty("surfaces").GetRawText())));
        Assert.DoesNotContain(result.RootElement.GetProperty("objects").EnumerateArray(), item => item.GetProperty("name").GetString() == "Excluded stool");
        Assert.Contains(result.RootElement.GetProperty("objects").EnumerateArray(), item => item.GetProperty("id").GetGuid() == targetObjectId);
    }
}
