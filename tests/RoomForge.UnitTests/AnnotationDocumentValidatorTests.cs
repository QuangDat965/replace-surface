using RoomForge.Domain.Annotations;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class AnnotationDocumentValidatorTests
{
    [Fact]
    public void AcceptsManualPolygonAndMeasuredReference()
    {
        var document = CreateDocument(
            [new AnnotationRegion(Guid.NewGuid(), "floor", Polygon((10, 10), (90, 10), (90, 90), (10, 90)))],
            [new AnnotationReferenceSpan(Guid.NewGuid(), new ImagePoint(15, 50), new ImagePoint(85, 50), 2.4)]);

        Assert.Empty(AnnotationDocumentValidator.Validate(document));
    }

    [Fact]
    public void RejectsSelfIntersectingPolygonAndOutOfBoundsReference()
    {
        var document = CreateDocument(
            [new AnnotationRegion(Guid.NewGuid(), "wall", Polygon((10, 10), (90, 90), (10, 90), (90, 10)))],
            [new AnnotationReferenceSpan(Guid.NewGuid(), new ImagePoint(-1, 20), new ImagePoint(90, 20), 1)]);

        var errors = AnnotationDocumentValidator.Validate(document);
        Assert.Contains(errors, error => error.Contains("polygons", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("endpoints", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsFabricatedConfidenceOnUserAnnotations()
    {
        var document = CreateDocument(
            [new AnnotationRegion(Guid.NewGuid(), "object", Polygon((10, 10), (90, 10), (90, 90)), "mock", .7)], []);

        Assert.Contains(AnnotationDocumentValidator.Validate(document), error => error.Contains("provenance", StringComparison.Ordinal));
    }

    private static AnnotationDocument CreateDocument(IReadOnlyList<AnnotationRegion> regions, IReadOnlyList<AnnotationReferenceSpan> spans) =>
        new(1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 100, 100, regions, spans, new AnnotationCameraHint(null, null), []);

    private static ImagePolygon Polygon(params (double X, double Y)[] points) =>
        new(points.Select(point => new ImagePoint(point.X, point.Y)).ToArray());
}
