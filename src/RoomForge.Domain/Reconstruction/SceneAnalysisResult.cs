namespace RoomForge.Domain.Reconstruction;

public sealed record ImagePoint(double X, double Y);
public sealed record ImagePolygon(IReadOnlyList<ImagePoint> Points);

public sealed record ReconstructionAssumptions(
    string CameraModel,
    string ScaleStatus,
    double? MetersPerPixel,
    IReadOnlyList<string> Notes,
    double? ReferencePixelLength = null,
    double? ReferenceLengthMeters = null);

public sealed record DetectedSurface(
    Guid Id,
    string SemanticRole,
    ImagePolygon Polygon,
    double? EstimatedWidthMeters,
    double? EstimatedHeightMeters,
    double Confidence,
    string Provenance,
    Guid? LinkedSurfaceId = null);

public sealed record DetectedObject(
    Guid Id,
    string Type,
    string Name,
    ImagePolygon Polygon,
    double[] PositionMeters,
    double[] DimensionsMeters,
    double Confidence,
    string Provenance,
    bool Included = true,
    Guid? LinkedObjectId = null);

public sealed record SceneAnalysisResult(
    int ContractVersion,
    Guid AnalysisId,
    Guid SourceImageId,
    int ImageWidth,
    int ImageHeight,
    string ProviderId,
    string ProviderVersion,
    DateTimeOffset CreatedAt,
    ReconstructionAssumptions Assumptions,
    IReadOnlyList<DetectedSurface> Surfaces,
    IReadOnlyList<DetectedObject> Objects);

public static class SceneAnalysisResultValidator
{
    public static IReadOnlyList<string> Validate(SceneAnalysisResult result)
    {
        var errors = new List<string>();
        if (result.ContractVersion != 1) errors.Add("Only SceneAnalysisResult contract version 1 is supported.");
        if (result.AnalysisId == Guid.Empty || result.SourceImageId == Guid.Empty) errors.Add("Analysis and source image IDs must be UUIDs.");
        if (result.ImageWidth is < 1 or > 8192 || result.ImageHeight is < 1 or > 8192 || (long)result.ImageWidth * result.ImageHeight > 40_000_000)
            errors.Add("Analysis image dimensions exceed supported limits.");
        if (string.IsNullOrWhiteSpace(result.ProviderId) || string.IsNullOrWhiteSpace(result.ProviderVersion)) errors.Add("Provider provenance is required.");
        if (result.Assumptions is null || string.IsNullOrWhiteSpace(result.Assumptions.CameraModel) || string.IsNullOrWhiteSpace(result.Assumptions.ScaleStatus) || result.Assumptions.Notes is null)
            errors.Add("Camera and scale assumptions are required.");
        else if (result.Assumptions.MetersPerPixel is { } scale && (!double.IsFinite(scale) || scale <= 0)) errors.Add("Meters per pixel must be finite and positive.");
        if ((result.Assumptions?.ReferencePixelLength is null) != (result.Assumptions?.ReferenceLengthMeters is null) ||
            result.Assumptions?.ReferencePixelLength is { } pixels && (!double.IsFinite(pixels) || pixels <= 0) ||
            result.Assumptions?.ReferenceLengthMeters is { } length && (!double.IsFinite(length) || length <= 0))
            errors.Add("A calibration reference requires positive pixel and meter lengths together.");
        if (result.Assumptions?.ReferencePixelLength is { } referencePixels && result.Assumptions.ReferenceLengthMeters is { } referenceMeters &&
            (result.Assumptions.MetersPerPixel is not { } metersPerPixel || Math.Abs(metersPerPixel - referenceMeters / referencePixels) > Math.Max(1e-8, referenceMeters / referencePixels * 1e-6)))
            errors.Add("Meters per pixel must match the supplied reference span.");

        if (result.Surfaces is { Count: > 100 } || result.Objects is { Count: > 1000 }) errors.Add("Analysis contains too many detected regions.");

        var ids = new HashSet<Guid>();
        foreach (var surface in result.Surfaces ?? [])
        {
            if (surface.Id == Guid.Empty || !ids.Add(surface.Id)) errors.Add("Surface candidate IDs must be unique UUIDs.");
            if (string.IsNullOrWhiteSpace(surface.SemanticRole) || surface.SemanticRole.Length > 80) errors.Add("Detected surfaces need a supported semantic role.");
            ValidatePolygon(surface.Polygon, result.ImageWidth, result.ImageHeight, errors);
            ValidateConfidence(surface.Confidence, surface.Provenance, errors);
            ValidateOptionalLength(surface.EstimatedWidthMeters, errors);
            ValidateOptionalLength(surface.EstimatedHeightMeters, errors);
        }
        foreach (var item in result.Objects ?? [])
        {
            if (item.Id == Guid.Empty || !ids.Add(item.Id)) errors.Add("Detected candidate IDs must be unique UUIDs.");
            if (string.IsNullOrWhiteSpace(item.Type) || item.Type.Length > 60 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120) errors.Add("Detected objects need a supported type and name.");
            ValidatePolygon(item.Polygon, result.ImageWidth, result.ImageHeight, errors);
            ValidateConfidence(item.Confidence, item.Provenance, errors);
            if (item.PositionMeters is not { Length: 3 } || item.PositionMeters.Any(value => !double.IsFinite(value) || Math.Abs(value) > 100_000))
                errors.Add("Detected object position must contain three finite meter coordinates.");
            if (item.DimensionsMeters is not { Length: 3 } || item.DimensionsMeters.Any(value => !double.IsFinite(value) || value <= 0 || value > 1000))
                errors.Add("Detected object dimensions must contain three positive meter values.");
        }
        return errors;
    }

    private static void ValidatePolygon(ImagePolygon? polygon, int width, int height, ICollection<string> errors)
    {
        if (polygon?.Points is not { Count: >= 3 and <= 1000 } points || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > width || point.Y > height))
            errors.Add("Image-space polygons must have at least three finite points within the source image.");
    }

    private static void ValidateConfidence(double confidence, string provenance, ICollection<string> errors)
    {
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1) errors.Add("Confidence must be between 0 and 1.");
        if (string.IsNullOrWhiteSpace(provenance)) errors.Add("Every candidate needs provenance.");
    }

    private static void ValidateOptionalLength(double? value, ICollection<string> errors)
    {
        if (value is { } length && (!double.IsFinite(length) || length <= 0)) errors.Add("Estimated dimensions must be positive finite meters or unknown.");
    }
}
