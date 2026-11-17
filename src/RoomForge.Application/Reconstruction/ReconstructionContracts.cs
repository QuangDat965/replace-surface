using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public sealed record ReconstructionImage(Guid Id, Guid ProjectId, int Width, int Height, string ContentType, string StorageId, Guid? JobId = null);
public sealed record SceneUnderstandingOutput(IReadOnlyList<DetectedSurface> Surfaces, IReadOnlyList<DetectedObject> Objects);
public sealed record SegmentationOutput(IReadOnlyDictionary<Guid, ImagePolygon> Polygons, string Provenance, IReadOnlyDictionary<Guid, double>? Confidences = null);
public sealed record DepthEstimationOutput(string ScaleStatus, double? MetersPerPixel, IReadOnlyList<string> Notes);
public sealed record ImageTo3DOutput(bool Supported, string? Reason, string? AssetId, string? Format);
public sealed record ReconstructionCapability(string Name, bool Available, string ProviderId, string Description);

public sealed class ProviderSpendLimitExceededException(string message) : Exception(message);

public interface ISceneUnderstandingProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    Task<SceneUnderstandingOutput> UnderstandAsync(ReconstructionImage image, CancellationToken cancellationToken);
}

public interface IObjectSegmentationProvider
{
    string ProviderId { get; }
    Task<SegmentationOutput> SegmentAsync(ReconstructionImage image, Guid jobId, IReconstructionStore jobs, CancellationToken cancellationToken);
}

public static class ReconstructionCandidateIds
{
    public static Guid ForImage(Guid imageId, string key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(imageId.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(key)).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }
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

/// <summary>
/// Seven-state remote submission tracking. Local job progress is tracked separately in
/// <see cref="ReconstructionJobSnapshot.Status"/>; this value records only how confident we are
/// that a provider accepted the work, so an ambiguous failure never causes a duplicate paid submit.
/// </summary>
public static class SubmissionStates
{
    public const string NotAttempted = "not_attempted";
    public const string InProgress = "in_progress";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string TerminalFailure = "terminal_failure";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        NotAttempted, InProgress, OutcomeUnknown, Accepted, Running, Completed, TerminalFailure,
    };

    /// <summary>True when the state permits a submission attempt without reconciliation first.</summary>
    public static bool CanSubmitWithoutReconciliation(string state) =>
        state is NotAttempted or TerminalFailure;

    /// <summary>True when a resubmission could duplicate paid work and must be reconciled first.</summary>
    public static bool RequiresReconciliationBeforeSubmit(string state) =>
        state is InProgress or OutcomeUnknown or Accepted or Running or Completed;

    public static bool CanTransition(string current, string next) => (current, next) switch
    {
        (NotAttempted, InProgress) => true,
        (InProgress, OutcomeUnknown or Accepted or TerminalFailure) => true,
        (OutcomeUnknown, NotAttempted or Accepted or Running or Completed or TerminalFailure) => true,
        (Accepted, Running or Completed or TerminalFailure) => true,
        (Running, Running or Completed or TerminalFailure) => true,
        (TerminalFailure, NotAttempted) => true,
        _ => false,
    };
}

public sealed record ReconstructionJobSnapshot(
    Guid Id, Guid ProjectId, Guid ImageId, string Status, string Stage, int Progress,
    int AttemptCount, int MaxAttempts, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? ErrorCode, string? ErrorMessage, string? AnalysisJson,
    string SubmissionState = SubmissionStates.NotAttempted, string? ProviderId = null,
    string? ProviderJobId = null, bool ProviderIdempotencySupported = false,
    int ReconciliationAttempts = 0, DateTimeOffset? LastReconciledAt = null);

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
    Task<bool> SetSubmissionStateAsync(Guid id, string expectedSubmissionState, string submissionState, string? providerId, string? providerJobId,
        bool providerIdempotencySupported, CancellationToken cancellationToken);
    Task<bool> RecordReconciliationAsync(Guid id, string expectedSubmissionState, string resultingSubmissionState,
        string? providerJobId, CancellationToken cancellationToken);
    Task<bool> TryReserveProviderSpendAsync(Guid jobId, string providerId, decimal amountUsd, decimal totalCapUsd, CancellationToken cancellationToken);
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
