using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Reconstruction;
using RoomForge.Infrastructure.Reconstruction;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfObjectMeshJobStore(RoomForgeDbContext db) : IObjectMeshJobStore
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 3;

    public async Task<Guid> CreateOrGetAsync(ObjectMeshJobRequest request, CancellationToken cancellationToken)
    {
        var existing = await db.ObjectMeshJobs.AsNoTracking().SingleOrDefaultAsync(
            item => item.ProjectId == request.ProjectId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!Matches(existing, request)) throw new InvalidOperationException("Idempotency key was reused with different object mesh input.");
            return existing.Id;
        }
        var now = DateTimeOffset.UtcNow;
        var record = new ObjectMeshJobRecord
        {
            Id = Guid.NewGuid(), ProjectId = request.ProjectId, AnalysisJobId = request.AnalysisJobId,
            SourceImageId = request.SourceImageId, SceneId = request.SceneId, ObjectId = request.ObjectId,
            ExpectedSceneRevision = request.ExpectedSceneRevision, CropAssetStorageId = request.CropAssetStorageId,
            CropContentType = request.CropContentType, CropSha256 = request.CropSha256, IdempotencyKey = request.IdempotencyKey,
            Status = "queued", SubmissionState = ObjectMeshSubmissionStates.NotAttempted,
            CreatedAt = now, UpdatedAt = now,
        };
        db.ObjectMeshJobs.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); return record.Id; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var raced = await db.ObjectMeshJobs.AsNoTracking().SingleOrDefaultAsync(
                item => item.ProjectId == request.ProjectId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            if (raced is not null)
            {
                if (!Matches(raced, request)) throw new InvalidOperationException("Idempotency key was reused with different object mesh input.");
                return raced.Id;
            }
            throw;
        }
    }

    private static bool Matches(ObjectMeshJobRecord existing, ObjectMeshJobRequest request) =>
        existing.AnalysisJobId == request.AnalysisJobId && existing.SourceImageId == request.SourceImageId &&
        existing.SceneId == request.SceneId && existing.ObjectId == request.ObjectId &&
        existing.ExpectedSceneRevision == request.ExpectedSceneRevision && existing.CropSha256 == request.CropSha256;

    public async Task<ObjectMeshJobSnapshot?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        await db.ObjectMeshJobs.AsNoTracking().Where(item => item.Id == jobId)
            .Select(item => new ObjectMeshJobSnapshot(item.Id, item.ProjectId, item.AnalysisJobId, item.SourceImageId,
                item.SceneId, item.ObjectId, item.ExpectedSceneRevision, item.CropSha256, item.Status, item.SubmissionState, item.ProviderJobId,
                item.OutputAssetStorageId, item.ErrorCode, item.ErrorMessage, item.Progress, item.CreatedAt, item.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ObjectMeshJobSnapshot?> FindByIdempotencyKeyAsync(Guid projectId, string idempotencyKey, CancellationToken cancellationToken) =>
        await db.ObjectMeshJobs.AsNoTracking().Where(item => item.ProjectId == projectId && item.IdempotencyKey == idempotencyKey)
            .Select(item => new ObjectMeshJobSnapshot(item.Id, item.ProjectId, item.AnalysisJobId, item.SourceImageId,
                item.SceneId, item.ObjectId, item.ExpectedSceneRevision, item.CropSha256, item.Status, item.SubmissionState, item.ProviderJobId,
                item.OutputAssetStorageId, item.ErrorCode, item.ErrorMessage, item.Progress, item.CreatedAt, item.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<ObjectMeshWorkItem?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var item = await db.ObjectMeshJobs.FromSqlRaw("""
                SELECT * FROM "ObjectMeshJobs"
                WHERE "Status" = 'queued' OR ("Status" = 'processing' AND "LeaseUntil" < now())
                ORDER BY "CreatedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """).FirstOrDefaultAsync(cancellationToken);
        if (item is null) { await transaction.CommitAsync(cancellationToken); return null; }
        item.Status = "processing";
        item.Progress = Math.Max(item.Progress, 5);
        item.AttemptCount++;
        item.LeaseUntil = now + LeaseDuration;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ObjectMeshWorkItem(item.Id, item.AnalysisJobId, item.SubmissionState, item.ProviderJobId,
            item.CropAssetStorageId, item.CropContentType, item.AttemptCount);
    }

    public async Task<bool> SetSubmissionAsync(Guid jobId, string expectedState, string nextState, string? providerJobId,
        CancellationToken cancellationToken)
    {
        if (!CanTransition(expectedState, nextState)) return false;
        var now = DateTimeOffset.UtcNow;
        var query = db.ObjectMeshJobs.Where(item => item.Id == jobId && item.Status == "processing" && item.SubmissionState == expectedState);
        var rows = await query.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SubmissionState, nextState)
            .SetProperty(item => item.ProviderId, FalHunyuan3DQueueClient.ModelId)
            .SetProperty(item => item.ProviderJobId, providerJobId)
            .SetProperty(item => item.LeaseUntil, now + LeaseDuration)
            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> SetProgressAsync(Guid jobId, int progress, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ObjectMeshJobs.Where(item => item.Id == jobId && item.Status == "processing")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Progress, Math.Clamp(progress, 0, 99))
                .SetProperty(item => item.LeaseUntil, now + LeaseDuration).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> CompleteAsync(Guid jobId, string outputAssetStorageId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ObjectMeshJobs.Where(item => item.Id == jobId && item.Status == "processing" &&
                (item.SubmissionState == ObjectMeshSubmissionStates.Accepted || item.SubmissionState == ObjectMeshSubmissionStates.Running))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "completed")
                .SetProperty(item => item.SubmissionState, ObjectMeshSubmissionStates.Completed)
                .SetProperty(item => item.OutputAssetStorageId, outputAssetStorageId)
                .SetProperty(item => item.Progress, 100).SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return rows == 1;
    }

    public async Task<bool> FailAsync(Guid jobId, string errorCode, string safeMessage, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await db.ObjectMeshJobs.Where(item => item.Id == jobId && item.Status == "processing")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.ErrorCode, errorCode).SetProperty(item => item.ErrorMessage, safeMessage)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null).SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return rows == 1;
    }

    private static bool CanTransition(string current, string next) => (current, next) switch
    {
        (ObjectMeshSubmissionStates.NotAttempted, ObjectMeshSubmissionStates.InProgress) => true,
        (ObjectMeshSubmissionStates.InProgress, ObjectMeshSubmissionStates.Accepted or ObjectMeshSubmissionStates.OutcomeUnknown or ObjectMeshSubmissionStates.TerminalFailure) => true,
        (ObjectMeshSubmissionStates.Accepted, ObjectMeshSubmissionStates.Running or ObjectMeshSubmissionStates.Completed or ObjectMeshSubmissionStates.TerminalFailure) => true,
        (ObjectMeshSubmissionStates.Running, ObjectMeshSubmissionStates.Running or ObjectMeshSubmissionStates.Completed or ObjectMeshSubmissionStates.TerminalFailure) => true,
        _ => false,
    };
}
