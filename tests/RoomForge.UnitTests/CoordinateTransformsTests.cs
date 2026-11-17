using System.Numerics;
using RoomForge.Domain.Geometry;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class CoordinateTransformsTests
{
    private static readonly CameraModel Camera = new(Guid.NewGuid(), 800, 800, 320, 240,
        new Vector3(1.2f, 1.6f, 2f), Quaternion.CreateFromYawPitchRoll(.2f, .1f, -.15f), "estimated", "user_supplied");

    [Fact]
    public void CameraAndWorldTransformsRoundTrip()
    {
        var cameraPoint = new Vector3(.3f, -.2f, -2.5f);
        var worldPoint = CoordinateTransforms.CameraToWorld(cameraPoint, Camera);
        var roundTrip = CoordinateTransforms.WorldToCamera(worldPoint, Camera);
        AssertVectorNear(cameraPoint, roundTrip, 1e-6f);
    }

    [Fact]
    public void ObjectLocalTransformsRoundTrip()
    {
        var localPoint = new Vector3(.4f, .7f, -.2f);
        var position = new Vector3(2, 1, -3);
        var rotation = Quaternion.CreateFromYawPitchRoll(.4f, -.1f, .2f);
        var world = CoordinateTransforms.ObjectLocalToWorld(localPoint, position, rotation);
        AssertVectorNear(localPoint, CoordinateTransforms.WorldToObjectLocal(world, position, rotation), 1e-6f);
    }

    [Fact]
    public void ImageAndProjectionRoundTripFlipsImageYExactlyOnce()
    {
        var pixel = new ImagePoint(120, 80);
        var ray = CoordinateTransforms.ImageToCamera(pixel, Camera);
        Assert.True(ray.Y > 0); // Image Y-up flip is applied once.
        var worldPoint = CoordinateTransforms.CameraToWorld(ray, Camera);
        var projected = CoordinateTransforms.ProjectToImage(worldPoint, Camera, 640, 480);
        Assert.Equal(ImageProjectionStatus.Projected, projected.Status);
        Assert.NotNull(projected.Point);
        Assert.InRange(Math.Abs(projected.Point!.X - pixel.X), 0, 1e-4);
        Assert.InRange(Math.Abs(projected.Point.Y - pixel.Y), 0, 1e-4);
    }

    [Fact]
    public void ProjectionRejectsBehindCameraAndOffImagePoints()
    {
        var behind = CoordinateTransforms.CameraToWorld(new Vector3(0, 0, 1), Camera);
        Assert.Equal(ImageProjectionStatus.BehindCamera, CoordinateTransforms.ProjectToImage(behind, Camera, 640, 480).Status);

        var outside = CoordinateTransforms.CameraToWorld(new Vector3(-2, 0, -1), Camera);
        Assert.Equal(ImageProjectionStatus.OutsideImage, CoordinateTransforms.ProjectToImage(outside, Camera, 640, 480).Status);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float tolerance)
    {
        Assert.InRange(Math.Abs(expected.X - actual.X), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, tolerance);
        Assert.InRange(Math.Abs(expected.Z - actual.Z), 0, tolerance);
    }
}
