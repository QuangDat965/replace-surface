using System.Text.Json;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public sealed class ReconstructionAnalysisService(
    ISceneUnderstandingProvider sceneUnderstanding,
    IObjectSegmentationProvider segmentation,
    IDepthEstimationProvider depth,
    IImageTo3DProvider imageTo3D,
    IReconstructionStore jobs)
{
    public async Task<SceneAnalysisResult> AnalyzeAsync(ReconstructionImage image, Guid jobId, CancellationToken cancellationToken)
    {
        var scene = await sceneUnderstanding.UnderstandAsync(image, cancellationToken);
        var masks = await segmentation.SegmentAsync(image, jobId, jobs, cancellationToken);
        var scale = await depth.EstimateAsync(image, cancellationToken);
        var meshCapabilities = await Task.WhenAll(scene.Objects.Select(item => imageTo3D.GenerateAsync(image, item, cancellationToken)));
        var surfaces = scene.Surfaces.Select(item => masks.Polygons.TryGetValue(item.Id, out var polygon)
            ? item with { Polygon = polygon, Confidence = masks.Confidences?.GetValueOrDefault(item.Id) ?? item.Confidence, Provenance = $"{item.Provenance}; segmentation={masks.Provenance}" }
            : item).ToArray();
        var objects = scene.Objects.Select(item => masks.Polygons.TryGetValue(item.Id, out var polygon)
            ? item with { Polygon = polygon, Confidence = masks.Confidences?.GetValueOrDefault(item.Id) ?? item.Confidence, Provenance = $"{item.Provenance}; segmentation={masks.Provenance}" }
            : item).ToArray();
        return new SceneAnalysisResult(1, StableGuid(image.Id, "analysis-v1"), image.Id, image.Width, image.Height,
            sceneUnderstanding.ProviderId, sceneUnderstanding.ProviderVersion, DateTimeOffset.UtcNow,
            new ReconstructionAssumptions("single-image-perspective-unknown", scale.ScaleStatus, scale.MetersPerPixel,
                scale.Notes.Append($"Segmentation provenance: {masks.Provenance}")
                    .Append(meshCapabilities.Any(item => item.Supported)
                        ? "Image-to-3D mock capability returned an asset candidate."
                        : $"Image-to-3D unavailable: {meshCapabilities.FirstOrDefault()?.Reason ?? "no object candidates"} Use editable primitive geometry.")
                    .ToArray()), surfaces, objects);
    }

    public static string Serialize(SceneAnalysisResult result) => JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static Guid StableGuid(Guid seed, string name)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(seed.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(name)).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }
}
