namespace RoomForge.Application.Reconstruction;

public sealed record PixelCrop(int X, int Y, int Width, int Height);
public sealed record CreateObjectMeshJobRequest(Guid SceneId, Guid ObjectId, int ExpectedSceneRevision, PixelCrop Crop,
    string IdempotencyKey, bool Confirmed);
public sealed record ObjectMeshJobRequest(Guid ProjectId, Guid AnalysisJobId, Guid SourceImageId, Guid SceneId, Guid ObjectId,
    int ExpectedSceneRevision, string CropAssetStorageId, string CropContentType, string CropSha256, string IdempotencyKey);
public sealed record ObjectMeshJobSnapshot(Guid Id, Guid ProjectId, Guid AnalysisJobId, Guid SourceImageId, Guid SceneId,
    Guid ObjectId, int ExpectedSceneRevision, string CropSha256, string Status, string SubmissionState, string? ProviderJobId,
    string? OutputAssetStorageId, string? ErrorCode, string? ErrorMessage, int Progress, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ObjectMeshWorkItem(Guid Id, Guid AnalysisJobId, string SubmissionState, string? ProviderJobId,
    string CropAssetStorageId, string CropContentType, int AttemptCount);

public interface IObjectMeshJobStore
{
    Task<Guid> CreateOrGetAsync(ObjectMeshJobRequest request, CancellationToken cancellationToken);
    Task<ObjectMeshJobSnapshot?> FindByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ObjectMeshJobSnapshot?> GetAsync(Guid jobId, CancellationToken cancellationToken);
    Task<ObjectMeshWorkItem?> TryClaimAsync(CancellationToken cancellationToken);
    Task<bool> SetSubmissionAsync(Guid jobId, string expectedState, string nextState, string? providerJobId, CancellationToken cancellationToken);
    Task<bool> SetProgressAsync(Guid jobId, int progress, CancellationToken cancellationToken);
    Task<bool> CompleteAsync(Guid jobId, string outputAssetStorageId, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid jobId, string errorCode, string safeMessage, CancellationToken cancellationToken);
}

public static class ObjectMeshSubmissionStates
{
    public const string NotAttempted = "not_attempted";
    public const string InProgress = "in_progress";
    public const string OutcomeUnknown = "outcome_unknown";
    public const string Accepted = "accepted";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string TerminalFailure = "terminal_failure";
}
