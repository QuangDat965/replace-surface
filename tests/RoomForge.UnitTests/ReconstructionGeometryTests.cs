using RoomForge.Domain.Annotations;
using RoomForge.Domain.Geometry;
using RoomForge.Domain.Reconstruction;
using System.Numerics;

namespace RoomForge.UnitTests;

public sealed class ReconstructionGeometryTests
{
    private static AnnotationDocument Annotation(bool withReference = true) => new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
        640, 480,
        [new AnnotationRegion(Guid.NewGuid(), "floor", new ImagePolygon([new(100, 100), new(500, 100), new(500, 400), new(100, 400)]))],
        withReference ? [new AnnotationReferenceSpan(Guid.NewGuid(), new(100, 200), new(300, 200), 2.5)] : [],
        new AnnotationCameraHint(null, null), []);

    [Fact]
    public void TopologyStartsWithUnknownEdgesAndCalibrationReportsInsufficientData()
    {
        var annotation = Annotation();
        var topology = RoomTopologyBuilder.Build(annotation);
        Assert.Equal(StageStatus.Ok, topology.Status);
        Assert.All(topology.Value!.Edges, edge => Assert.Equal(TopologyEdgeRole.UnknownCandidate, edge.Role));

        var calibration = CameraCalibrationBuilder.Build(annotation);
        Assert.Equal(StageStatus.InsufficientData, calibration.Status);
        Assert.Null(calibration.Value);
    }

    [Fact]
    public void GeometryRequiresMeasuredReferenceAndMarksUnobservedDimensionsUnknown()
    {
        var annotation = Annotation(withReference: false);
        var topology = RoomTopologyBuilder.Build(annotation).Value!;
        var absent = ParametricGeometryBuilder.Build(annotation, topology, annotation.Regions[0].Id, Guid.NewGuid());
        Assert.Equal(StageStatus.InsufficientData, absent.Status);

        annotation = Annotation();
        topology = RoomTopologyBuilder.Build(annotation).Value!;
        var candidate = ParametricGeometryBuilder.Build(annotation, topology, annotation.Regions[0].Id, annotation.ReferenceSpans[0].Id);
        Assert.Equal(StageStatus.Ok, candidate.Status);
        Assert.Contains(candidate.Value!.Instances[0].Dimensions, dimension => dimension.Status == DimensionStatus.Unknown && dimension.Meters is null);
        Assert.Contains(candidate.Value.Diagnostics, diagnostic => diagnostic.Contains("perspective", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CameraCalibrationRejectsInvalidSuppliedIntrinsics()
    {
        var annotation = Annotation();
        var invalid = new CameraModel(Guid.NewGuid(), 0, 800, 320, 240, System.Numerics.Vector3.Zero,
            System.Numerics.Quaternion.Identity, "measured", "user_supplied");
        var result = CameraCalibrationBuilder.Build(annotation, invalid);
        Assert.Equal(StageStatus.Unsupported, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public void AlignmentProjectsWorldEdgesAndReportsPixelErrorWithoutClaimingMetricAccuracy()
    {
        var annotation = Annotation();
        var topology = RoomTopologyBuilder.Build(annotation).Value!;
        var camera = new CameraModel(Guid.NewGuid(), 100, 100, 320, 240, Vector3.Zero, Quaternion.Identity,
            "user_supplied", "fixture_camera");
        var edge = topology.Edges[0];
        var result = PhotoAlignmentVerifier.Verify(annotation, topology, camera,
            new Dictionary<Guid, (Vector3 Start, Vector3 End)>
            {
                [edge.Id] = (new Vector3(-2.2f, 1.4f, -1), new Vector3(1.8f, 1.4f, -1)),
            });

        Assert.Equal(StageStatus.Ok, result.Status);
        Assert.Equal(0, result.Value!.RmsErrorPixels, 3);
        Assert.Contains(result.Value.UncertaintyNotes, note => note.Contains("not real-world accuracy", StringComparison.Ordinal));
        Assert.Equal(2, result.Value.VisibleSampleCount);
    }
}
