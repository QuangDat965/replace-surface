using RoomForge.Domain.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class SceneAnalysisResultTests
{
    [Fact]
    public void Accepts_versioned_estimated_result_with_image_polygons_and_provenance()
    {
        var imageId = Guid.NewGuid();
        var result = new SceneAnalysisResult(1, Guid.NewGuid(), imageId, 800, 600, "roomforge-mock", "1.0", DateTimeOffset.UtcNow,
            new ReconstructionAssumptions("unknown", "unknown", null, ["measurement required"]),
            [new DetectedSurface(Guid.NewGuid(), "floor", new ImagePolygon([new(0, 0), new(800, 0), new(800, 600)]), null, null, .5, "mock/estimated")],
            [new DetectedObject(Guid.NewGuid(), "furniture", "Chair", new ImagePolygon([new(1, 1), new(20, 1), new(20, 40)]), [0, 0, 0], [.5, .8, .5], .6, "mock/estimated")]);

        Assert.Empty(SceneAnalysisResultValidator.Validate(result));
    }

    [Fact]
    public void Rejects_unknown_version_invalid_polygon_and_nonpositive_dimensions()
    {
        var result = new SceneAnalysisResult(99, Guid.NewGuid(), Guid.NewGuid(), 10, 10, "mock", "1", DateTimeOffset.UtcNow,
            new ReconstructionAssumptions("unknown", "unknown", null, []),
            [new DetectedSurface(Guid.NewGuid(), "wall", new ImagePolygon([new(-1, 1), new(2, 1), new(2, 2)]), null, null, .5, "mock")],
            [new DetectedObject(Guid.NewGuid(), "furniture", "Chair", new ImagePolygon([new(1, 1), new(2, 1), new(2, 2)]), [0, 0, 0], [0, .5, .5], .5, "mock")]);

        var errors = SceneAnalysisResultValidator.Validate(result);
        Assert.Contains(errors, error => error.Contains("version"));
        Assert.Contains(errors, error => error.Contains("polygons"));
        Assert.Contains(errors, error => error.Contains("positive meter"));
    }
}
