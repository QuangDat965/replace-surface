using System.Numerics;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Domain.Geometry;

public enum ImageProjectionStatus { Projected, BehindCamera, OutsideImage }

public sealed record CameraModel(
    Guid Id,
    double Fx,
    double Fy,
    double Cx,
    double Cy,
    Vector3 PositionMeters,
    Quaternion RotationCameraToWorld,
    string IntrinsicsStatus,
    string Provenance);

public sealed record ImageProjection(ImageProjectionStatus Status, ImagePoint? Point);

/// <summary>Single implementation of image, camera, world and object-local coordinate transforms.</summary>
public static class CoordinateTransforms
{
    public static Vector3 ImageToCamera(ImagePoint pixel, CameraModel camera)
    {
        ValidateIntrinsics(camera);
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y)) throw new ArgumentOutOfRangeException(nameof(pixel));
        return new Vector3((float)((pixel.X - camera.Cx) / camera.Fx), (float)(-(pixel.Y - camera.Cy) / camera.Fy), -1f);
    }

    public static Vector3 CameraToWorld(Vector3 cameraPoint, CameraModel camera) =>
        Vector3.Transform(cameraPoint, Normalize(camera.RotationCameraToWorld)) + camera.PositionMeters;

    public static Vector3 WorldToCamera(Vector3 worldPoint, CameraModel camera) =>
        Vector3.Transform(worldPoint - camera.PositionMeters, Quaternion.Inverse(Normalize(camera.RotationCameraToWorld)));

    public static Vector3 ObjectLocalToWorld(Vector3 localPoint, Vector3 positionMeters, Quaternion rotation) =>
        Vector3.Transform(localPoint, Normalize(rotation)) + positionMeters;

    public static Vector3 WorldToObjectLocal(Vector3 worldPoint, Vector3 positionMeters, Quaternion rotation) =>
        Vector3.Transform(worldPoint - positionMeters, Quaternion.Inverse(Normalize(rotation)));

    public static ImageProjection ProjectToImage(Vector3 worldPoint, CameraModel camera, int imageWidth, int imageHeight)
    {
        ValidateIntrinsics(camera);
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        var cameraPoint = WorldToCamera(worldPoint, camera);
        if (cameraPoint.Z >= 0) return new ImageProjection(ImageProjectionStatus.BehindCamera, null);
        var depth = -(double)cameraPoint.Z;
        var x = camera.Cx + camera.Fx * (cameraPoint.X / depth);
        var y = camera.Cy - camera.Fy * (cameraPoint.Y / depth);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > imageWidth || y > imageHeight)
            return new ImageProjection(ImageProjectionStatus.OutsideImage, null);
        return new ImageProjection(ImageProjectionStatus.Projected, new ImagePoint(x, y));
    }

    private static Quaternion Normalize(Quaternion rotation)
    {
        if (!float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y) || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W) || rotation.LengthSquared() < 1e-12f)
            throw new ArgumentException("Rotation quaternion must be finite and nonzero.", nameof(rotation));
        return Quaternion.Normalize(rotation);
    }

    private static void ValidateIntrinsics(CameraModel camera)
    {
        if (!double.IsFinite(camera.Fx) || !double.IsFinite(camera.Fy) || camera.Fx <= 0 || camera.Fy <= 0 ||
            !double.IsFinite(camera.Cx) || !double.IsFinite(camera.Cy))
            throw new ArgumentException("Camera intrinsics must be finite with positive focal lengths.", nameof(camera));
    }
}
