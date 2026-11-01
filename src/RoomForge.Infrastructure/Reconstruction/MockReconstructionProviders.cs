using System.Security.Cryptography;
using System.Text;
using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed class MockSceneUnderstandingProvider : ISceneUnderstandingProvider
{
    public string ProviderId => "roomforge-mock";
    public string ProviderVersion => "1.0";

    public Task<SceneUnderstandingOutput> UnderstandAsync(ReconstructionImage image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var floorId = MockIds.From(image.Id, "floor");
        var wallId = MockIds.From(image.Id, "wall-back");
        var chairId = MockIds.From(image.Id, "chair-1");
        var width = image.Width;
        var height = image.Height;
        var surfaces = new[]
        {
            new DetectedSurface(floorId, "floor", new ImagePolygon([new(width * .04, height * .65), new(width * .96, height * .65), new(width * .99, height * .97), new(width * .01, height * .97)]), 3.6, 3.6, .82, "mock/estimated"),
            new DetectedSurface(wallId, "wall", new ImagePolygon([new(width * .05, height * .05), new(width * .95, height * .05), new(width * .96, height * .65), new(width * .04, height * .65)]), 3.6, 2.7, .76, "mock/estimated"),
        };
        var objects = new[]
        {
            new DetectedObject(chairId, "furniture", "Detected chair", new ImagePolygon([new(width * .62, height * .58), new(width * .76, height * .58), new(width * .78, height * .9), new(width * .6, height * .9)]), [0.9, 0, 0.2], [0.55, 0.9, 0.55], .68, "mock/estimated"),
        };
        return Task.FromResult(new SceneUnderstandingOutput(surfaces, objects));
    }
}

public sealed class MockObjectSegmentationProvider : IObjectSegmentationProvider
{
    public string ProviderId => "roomforge-mock";

    public Task<SegmentationOutput> SegmentAsync(ReconstructionImage image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var width = image.Width;
        var height = image.Height;
        IReadOnlyDictionary<Guid, ImagePolygon> polygons = new Dictionary<Guid, ImagePolygon>
        {
            [MockIds.From(image.Id, "floor")] = new([new(width * .04, height * .65), new(width * .96, height * .65), new(width * .99, height * .97), new(width * .01, height * .97)]),
            [MockIds.From(image.Id, "wall-back")] = new([new(width * .05, height * .05), new(width * .95, height * .05), new(width * .96, height * .65), new(width * .04, height * .65)]),
            [MockIds.From(image.Id, "chair-1")] = new([new(width * .62, height * .58), new(width * .76, height * .58), new(width * .78, height * .9), new(width * .6, height * .9)]),
        };
        return Task.FromResult(new SegmentationOutput(polygons, "mock/deterministic-polygons"));
    }
}

public sealed class MockDepthEstimationProvider : IDepthEstimationProvider
{
    public string ProviderId => "roomforge-mock";
    public Task<DepthEstimationOutput> EstimateAsync(ReconstructionImage image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DepthEstimationOutput("unknown", null,
            ["Mock depth is relative only; absolute dimensions require a user-entered measurement.", "Camera intrinsics and lens distortion are unknown."]));
    }
}

public sealed class MockImageTo3DProvider : IImageTo3DProvider
{
    public string ProviderId => "roomforge-mock";
    public Task<ImageTo3DOutput> GenerateAsync(ReconstructionImage image, DetectedObject item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ImageTo3DOutput(false, "No mesh generator is configured; the editable primitive fallback is used.", null, null));
    }
}

internal static class MockIds
{
    public static Guid From(Guid imageId, string key)
    {
        var hash = SHA256.HashData(imageId.ToByteArray().Concat(Encoding.UTF8.GetBytes(key)).ToArray());
        return new Guid(hash.AsSpan(0, 16));
    }
}
