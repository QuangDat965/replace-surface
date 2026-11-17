using RoomForge.Domain.Reconstruction;

namespace RoomForge.Domain.Annotations;

public sealed record AnnotationRegion(Guid Id, string SemanticRole, ImagePolygon Polygon, string Provenance = "user_supplied", double? Confidence = null);
public sealed record AnnotationReferenceSpan(Guid Id, ImagePoint Start, ImagePoint End, double KnownMeters, string Provenance = "user_supplied");
public sealed record AnnotationCameraHint(double? FocalLengthPixels, ImagePoint? PrincipalPoint);

public sealed record AnnotationDocument(
    int ContractVersion,
    Guid AnnotationId,
    Guid ProjectId,
    Guid SourceImageId,
    int Revision,
    int ImageWidth,
    int ImageHeight,
    IReadOnlyList<AnnotationRegion> Regions,
    IReadOnlyList<AnnotationReferenceSpan> ReferenceSpans,
    AnnotationCameraHint CameraHint,
    IReadOnlyList<string> Notes);

public static class AnnotationDocumentValidator
{
    private static readonly HashSet<string> Roles = new(StringComparer.Ordinal)
        { "floor", "wall", "object", "ceiling", "opening", "other" };

    public static IReadOnlyList<string> Validate(AnnotationDocument document)
    {
        var errors = new List<string>();
        if (document.ContractVersion != 1) errors.Add("Only AnnotationDocument contract version 1 is supported.");
        if (document.AnnotationId == Guid.Empty || document.ProjectId == Guid.Empty || document.SourceImageId == Guid.Empty)
            errors.Add("Annotation, project and source image IDs must be UUIDs.");
        if (document.Revision < 1) errors.Add("Annotation revision must be positive.");
        if (document.ImageWidth is < 1 or > 8192 || document.ImageHeight is < 1 or > 8192 || (long)document.ImageWidth * document.ImageHeight > 40_000_000)
            errors.Add("Annotation image dimensions exceed supported limits.");
        if (document.Regions is null || document.Regions.Count > 1000) errors.Add("Annotation must contain at most 1000 regions.");
        if (document.ReferenceSpans is null || document.ReferenceSpans.Count > 100) errors.Add("Annotation must contain at most 100 reference spans.");
        if (document.Notes is null || document.Notes.Count > 100 || document.Notes.Any(note => note is null || note.Length > 1000))
            errors.Add("Annotation notes must contain at most 100 entries of 1000 characters each.");

        var ids = new HashSet<Guid>();
        foreach (var region in document.Regions ?? [])
        {
            if (region.Id == Guid.Empty || !ids.Add(region.Id)) errors.Add("Region IDs must be unique UUIDs.");
            if (!Roles.Contains(region.SemanticRole)) errors.Add($"Unsupported annotation role '{region.SemanticRole}'.");
            if (!string.Equals(region.Provenance, "user_supplied", StringComparison.Ordinal) || region.Confidence is not null)
                errors.Add("Manual annotations must have user_supplied provenance and no confidence value.");
            ValidatePolygon(region.Polygon, document.ImageWidth, document.ImageHeight, errors);
        }

        foreach (var span in document.ReferenceSpans ?? [])
        {
            if (span.Id == Guid.Empty || !ids.Add(span.Id)) errors.Add("Annotation entity IDs must be unique UUIDs.");
            if (!string.Equals(span.Provenance, "user_supplied", StringComparison.Ordinal)) errors.Add("Reference spans must have user_supplied provenance.");
            if (!ValidPoint(span.Start, document.ImageWidth, document.ImageHeight) || !ValidPoint(span.End, document.ImageWidth, document.ImageHeight))
                errors.Add("Reference span endpoints must lie within the source image.");
            if (!double.IsFinite(span.KnownMeters) || span.KnownMeters <= 0) errors.Add("Reference length must be a positive finite meter value.");
            if (span.Start is not null && span.End is not null && DistanceSquared(span.Start, span.End) < 1)
                errors.Add("Reference span endpoints must be at least one pixel apart.");
        }

        if (document.CameraHint is null) errors.Add("Camera hint is required.");
        else
        {
            if (document.CameraHint.FocalLengthPixels is { } focal && (!double.IsFinite(focal) || focal <= 0))
                errors.Add("Camera focal length must be positive finite pixels or unknown.");
            if (document.CameraHint.PrincipalPoint is { } principal && !ValidPoint(principal, document.ImageWidth, document.ImageHeight))
                errors.Add("Camera principal point must lie within the source image.");
        }
        return errors;
    }

    private static void ValidatePolygon(ImagePolygon? polygon, int width, int height, ICollection<string> errors)
    {
        if (polygon?.Points is not { Count: >= 3 and <= 1000 } points ||
            points.Any(point => !ValidPoint(point, width, height)) ||
            Math.Abs(SignedDoubleArea(points)) < 1 || HasDuplicateAdjacentPoints(points) || HasSelfIntersections(points))
            errors.Add("Region polygons must have at least three distinct finite points within the source image.");
    }

    private static bool ValidPoint(ImagePoint? point, int width, int height) => point is not null &&
        double.IsFinite(point.X) && double.IsFinite(point.Y) && point.X >= 0 && point.Y >= 0 && point.X <= width && point.Y <= height;

    private static double SignedDoubleArea(IReadOnlyList<ImagePoint> points)
    {
        var area = 0d;
        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return area;
    }

    private static bool HasDuplicateAdjacentPoints(IReadOnlyList<ImagePoint> points) =>
        Enumerable.Range(0, points.Count).Any(index => DistanceSquared(points[index], points[(index + 1) % points.Count]) < 1);

    private static bool HasSelfIntersections(IReadOnlyList<ImagePoint> points)
    {
        for (var first = 0; first < points.Count; first++)
        {
            var firstNext = (first + 1) % points.Count;
            for (var second = first + 1; second < points.Count; second++)
            {
                var secondNext = (second + 1) % points.Count;
                if (first == second || firstNext == second || secondNext == first) continue;
                if (SegmentsIntersect(points[first], points[firstNext], points[second], points[secondNext])) return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(ImagePoint a, ImagePoint b, ImagePoint c, ImagePoint d)
    {
        var abC = Orientation(a, b, c);
        var abD = Orientation(a, b, d);
        var cdA = Orientation(c, d, a);
        var cdB = Orientation(c, d, b);
        if (((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) &&
            ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))) return true;
        return Math.Abs(abC) < 1e-9 && OnSegment(a, b, c) || Math.Abs(abD) < 1e-9 && OnSegment(a, b, d) ||
               Math.Abs(cdA) < 1e-9 && OnSegment(c, d, a) || Math.Abs(cdB) < 1e-9 && OnSegment(c, d, b);
    }

    private static double Orientation(ImagePoint a, ImagePoint b, ImagePoint c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool OnSegment(ImagePoint a, ImagePoint b, ImagePoint point) =>
        point.X >= Math.Min(a.X, b.X) && point.X <= Math.Max(a.X, b.X) &&
        point.Y >= Math.Min(a.Y, b.Y) && point.Y <= Math.Max(a.Y, b.Y);

    private static double DistanceSquared(ImagePoint first, ImagePoint second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return dx * dx + dy * dy;
    }
}
