using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public sealed record ReconstructionImage(Guid Id, Guid ProjectId, int Width, int Height, string ContentType, string StorageId);
public sealed record SceneUnderstandingOutput(IReadOnlyList<DetectedSurface> Surfaces, IReadOnlyList<DetectedObject> Objects);
public sealed record SegmentationOutput(IReadOnlyDictionary<Guid, ImagePolygon> Polygons, string Provenance);
public sealed record DepthEstimationOutput(string ScaleStatus, double? MetersPerPixel, IReadOnlyList<string> Notes);
public sealed record ImageTo3DOutput(bool Supported, string? Reason, string? AssetId, string? Format);
public sealed record ReconstructionCapability(string Name, bool Available, string ProviderId, string Description);

public interface ISceneUnderstandingProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    Task<SceneUnderstandingOutput> UnderstandAsync(ReconstructionImage image, CancellationToken cancellationToken);
}

public interface IObjectSegmentationProvider
{
    string ProviderId { get; }
    Task<SegmentationOutput> SegmentAsync(ReconstructionImage image, CancellationToken cancellationToken);
}

public interface IDepthEstimationProvider
{
    string ProviderId { get; }
    Task<DepthEstimationOutput> EstimateAsync(ReconstructionImage image, CancellationToken cancellationToken);
}

public interface IImageTo3DProvider
{
    string ProviderId { get; }
    Task<ImageTo3DOutput> GenerateAsync(ReconstructionImage image, DetectedObject item, CancellationToken cancellationToken);
}

public interface IReconstructionCapabilityRegistry
{
    IReadOnlyList<ReconstructionCapability> GetCapabilities();
}

public sealed record ReconstructionJobRequest(Guid ProjectId, Guid ImageId, string IdempotencyKey);
public sealed record ReconstructionJobSnapshot(
    Guid Id, Guid ProjectId, Guid ImageId, string Status, string Stage, int Progress,
    int AttemptCount, int MaxAttempts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ErrorCode, string? ErrorMessage, string? AnalysisJson);
public sealed record ReconstructionJobWorkItem(Guid Id, Guid ProjectId, Guid ImageId, int AttemptCount, int MaxAttempts);
public sealed record ReconstructionPreviewRequest(Guid? TargetSceneId, int? ExpectedRevision, SceneAnalysisResult Analysis);
public sealed record ReconstructionPreview(Guid SceneId, int BaseRevision, string Document, bool CreatesNewScene);

public interface IReconstructionStore
{
    Task<Guid> CreateOrGetAsync(ReconstructionJobRequest request, CancellationToken cancellationToken);
    Task<ReconstructionJobSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<ReconstructionJobWorkItem?> TryClaimAsync(CancellationToken cancellationToken);
    Task SetProgressAsync(Guid id, string stage, int progress, CancellationToken cancellationToken);
    Task CompleteAsync(Guid id, string resultJson, CancellationToken cancellationToken);
    Task FailAsync(Guid id, string errorCode, string safeMessage, bool retryable, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);
    Task SavePreviewAsync(Guid id, Guid? targetSceneId, int baseRevision, string document, CancellationToken cancellationToken);
    Task<ReconstructionPreview?> GetPreviewAsync(Guid id, CancellationToken cancellationToken);
}

public interface IReconstructionImageStore
{
    Task<ReconstructionImage?> GetAsync(Guid id, CancellationToken cancellationToken);
}

public interface IReconstructionImageCatalog
{
    Task<ReconstructionImage> AddAsync(ReconstructionImage image, CancellationToken cancellationToken);
}
