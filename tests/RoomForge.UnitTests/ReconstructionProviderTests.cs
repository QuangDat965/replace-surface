using RoomForge.Application.Reconstruction;
using RoomForge.Domain.Reconstruction;
using RoomForge.Infrastructure.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class ReconstructionProviderTests
{
    [Fact]
    public async Task Mock_providers_are_deterministic_and_explicit_about_unknown_scale_and_mesh_support()
    {
        var image = new ReconstructionImage(Guid.NewGuid(), Guid.NewGuid(), 1024, 768, "image/png", "asset-id");
        var understanding = new MockSceneUnderstandingProvider();
        var first = await understanding.UnderstandAsync(image, CancellationToken.None);
        var second = await understanding.UnderstandAsync(image, CancellationToken.None);
        Assert.Equal(first.Surfaces.Select(item => item.Id), second.Surfaces.Select(item => item.Id));
        Assert.Equal(first.Objects.Select(item => item.Id), second.Objects.Select(item => item.Id));

        var depth = await new MockDepthEstimationProvider().EstimateAsync(image, CancellationToken.None);
        Assert.Equal("unknown", depth.ScaleStatus);
        Assert.Null(depth.MetersPerPixel);
        var mesh = await new MockImageTo3DProvider().GenerateAsync(image, first.Objects[0], CancellationToken.None);
        Assert.False(mesh.Supported);
        Assert.Contains("primitive", mesh.Reason);
        var polygons = await new MockObjectSegmentationProvider().SegmentAsync(image, CancellationToken.None);
        Assert.Contains(first.Surfaces[0].Id, polygons.Polygons.Keys);
    }
}
