using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfReconstructionStore(RoomForgeDbContext db) : IReconstructionStore, IReconstructionImageStore, IReconstructionImageCatalog
{
    public async Task<ReconstructionImage> AddAsync(ReconstructionImage image, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(project => project.Id == image.ProjectId, cancellationToken))
            throw new InvalidOperationException("The project does not exist.");
        var record = new ImageAssetRecord
        {
            Id = image.Id, ProjectId = image.ProjectId, StorageId = image.StorageId,
            ContentType = image.ContentType, Width = image.Width, Height = image.Height, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ImageAssets.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return image;
    }

    public async Task<ReconstructionImage?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.ImageAssets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == id, cancellationToken);
        return item is null ? null : new ReconstructionImage(item.Id, item.ProjectId, item.Width, item.Height, item.ContentType, item.StorageId);
    }

    async Task<ReconstructionJobSnapshot?> IReconstructionStore.GetAsync(Guid id, CancellationToken cancellationToken) =>
        await db.ReconstructionJobs.AsNoTracking().Where(item => item.Id == id)
            .Select(item => new ReconstructionJobSnapshot(item.Id, item.ProjectId, item.ImageId, item.Status, item.Stage, item.Progress,
                item.AttemptCount, item.MaxAttempts, item.CreatedAt, item.UpdatedAt, item.ErrorCode, item.ErrorMessage, item.AnalysisJson))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<Guid> CreateOrGetAsync(ReconstructionJobRequest request, CancellationToken cancellationToken)
    {
        var existing = await db.ReconstructionJobs.AsNoTracking().SingleOrDefaultAsync(item =>
            item.ProjectId == request.ProjectId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
        if (existing is not null) return existing.Id;
        var imageExists = await db.ImageAssets.AnyAsync(item => item.Id == request.ImageId && item.ProjectId == request.ProjectId, cancellationToken);
        if (!imageExists) throw new InvalidOperationException("Image asset was not found in this project.");
        var now = DateTimeOffset.UtcNow;
        var record = new ReconstructionJobRecord
        {
            Id = Guid.NewGuid(), ProjectId = request.ProjectId, ImageId = request.ImageId,
            IdempotencyKey = request.IdempotencyKey, Status = "queued", Stage = "queued", Progress = 0,
            AttemptCount = 0, MaxAttempts = 3, CreatedAt = now, UpdatedAt = now,
        };
        db.ReconstructionJobs.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); return record.Id; }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var raced = await db.ReconstructionJobs.AsNoTracking().SingleOrDefaultAsync(item =>
                item.ProjectId == request.ProjectId && item.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            if (raced is not null) return raced.Id;
            throw;
        }
    }

    public async Task<ReconstructionJobWorkItem?> TryClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await db.ReconstructionJobs.Where(item => item.Status == "processing" && item.LeaseUntil < now && item.AttemptCount >= item.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "failed")
                .SetProperty(item => item.Stage, "failed")
                .SetProperty(item => item.ErrorCode, "worker_lease_expired")
                .SetProperty(item => item.ErrorMessage, "The worker stopped before completing the final allowed attempt.")
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        var item = await db.ReconstructionJobs.FromSqlRaw("""
                SELECT * FROM "ReconstructionJobs"
                WHERE "AttemptCount" < "MaxAttempts"
                  AND ("Status" = 'queued' OR ("Status" = 'processing' AND "LeaseUntil" < now()))
                ORDER BY "CreatedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """).OrderBy(item => item.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (item is null) { await transaction.CommitAsync(cancellationToken); return null; }
        item.Status = "processing";
        item.Stage = "starting";
        item.Progress = 5;
        item.AttemptCount++;
        item.LeaseUntil = now.AddMinutes(2);
        item.ErrorCode = null;
        item.ErrorMessage = null;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ReconstructionJobWorkItem(item.Id, item.ProjectId, item.ImageId, item.AttemptCount, item.MaxAttempts);
    }

    public async Task SetProgressAsync(Guid id, string stage, int progress, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ReconstructionJobs.Where(item => item.Id == id && item.Status == "processing")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Stage, stage)
                .SetProperty(item => item.Progress, Math.Clamp(progress, 0, 99))
                .SetProperty(item => item.LeaseUntil, now.AddMinutes(2))
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task CompleteAsync(Guid id, string resultJson, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        await db.ReconstructionJobs.Where(item => item.Id == id && item.Status == "processing")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "succeeded")
                .SetProperty(item => item.Stage, "completed")
                .SetProperty(item => item.Progress, 100)
                .SetProperty(item => item.AnalysisJson, resultJson)
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    public async Task FailAsync(Guid id, string errorCode, string safeMessage, bool retryable, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var item = await db.ReconstructionJobs.SingleOrDefaultAsync(job => job.Id == id && job.Status == "processing", cancellationToken);
        if (item is null) return;
        if (retryable && item.AttemptCount < item.MaxAttempts)
        {
            item.Status = "queued";
            item.Stage = "retry_queued";
        }
        else
        {
            item.Status = "failed";
            item.Stage = "failed";
            item.ErrorCode = errorCode;
            item.ErrorMessage = safeMessage;
        }
        item.LeaseUntil = null;
        item.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await db.ReconstructionJobs.Where(item => item.Id == id && (item.Status == "queued" || item.Status == "processing"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "cancelled")
                .SetProperty(item => item.Stage, "cancelled")
                .SetProperty(item => item.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return count == 1;
    }

    public async Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var count = await db.ReconstructionJobs.Where(item => item.Id == id && item.Status == "failed" && item.AttemptCount < item.MaxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "queued")
                .SetProperty(item => item.Stage, "retry_queued")
                .SetProperty(item => item.Progress, 0)
                .SetProperty(item => item.ErrorCode, (string?)null)
                .SetProperty(item => item.ErrorMessage, (string?)null)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return count == 1;
    }

    public async Task SavePreviewAsync(Guid id, Guid? targetSceneId, int baseRevision, string document, CancellationToken cancellationToken)
    {
        await db.ReconstructionJobs.Where(item => item.Id == id && item.Status == "succeeded")
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.PreviewTargetSceneId, targetSceneId)
                .SetProperty(item => item.PreviewBaseRevision, baseRevision)
                .SetProperty(item => item.PreviewDocument, document)
                .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<ReconstructionPreview?> GetPreviewAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.ReconstructionJobs.AsNoTracking().SingleOrDefaultAsync(job => job.Id == id && job.PreviewDocument != null, cancellationToken);
        if (item is null) return null;
        using var json = System.Text.Json.JsonDocument.Parse(item.PreviewDocument!);
        return new ReconstructionPreview(json.RootElement.GetProperty("sceneId").GetGuid(), item.PreviewBaseRevision,
            item.PreviewDocument!, item.PreviewTargetSceneId is null);
    }
}
