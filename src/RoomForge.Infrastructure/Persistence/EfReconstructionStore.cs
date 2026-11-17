using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfReconstructionStore(RoomForgeDbContext db) : IReconstructionStore, IReconstructionImageStore, IReconstructionImageCatalog
{
    private static TimeSpan LeaseDuration => int.TryParse(Environment.GetEnvironmentVariable("ROOMFORGE_JOB_LEASE_SECONDS"), out var seconds) && seconds is > 0 and <= 3600
        ? TimeSpan.FromSeconds(seconds)
        : TimeSpan.FromMinutes(2);

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
                item.AttemptCount, item.MaxAttempts, item.CreatedAt, item.UpdatedAt, item.ErrorCode, item.ErrorMessage, item.AnalysisJson,
                item.SubmissionState, item.ProviderId, item.ProviderJobId, item.ProviderIdempotencySupported,
                item.ReconciliationAttempts, item.LastReconciledAt))
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
                  AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= now())
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
        item.LeaseUntil = now + LeaseDuration;
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
                .SetProperty(item => item.LeaseUntil, now + LeaseDuration)
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
            // Exponential backoff with deterministic per-job jitter so parallel retries do not
            // stampede the provider: 2s, 8s, 32s capped at 5 minutes.
            var exponent = Math.Max(0, item.AttemptCount - 1);
            var delaySeconds = Math.Min(300, Math.Pow(4, exponent) * 2);
            var jitter = (item.Id.GetHashCode() & 0xFF) / 255.0;
            item.NextAttemptAt = now.AddSeconds(delaySeconds + (delaySeconds * 0.25 * jitter));
            item.ErrorCode = errorCode;
            item.ErrorMessage = safeMessage;
        }
        else
        {
            item.Status = "failed";
            item.Stage = "failed";
            item.ErrorCode = errorCode;
            item.ErrorMessage = safeMessage;
            item.NextAttemptAt = null;
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
        var count = await db.ReconstructionJobs.Where(item => item.Id == id && item.Status == "failed" && item.AttemptCount < item.MaxAttempts
                && (item.SubmissionState == SubmissionStates.NotAttempted || item.SubmissionState == SubmissionStates.TerminalFailure))
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Status, "queued")
                .SetProperty(item => item.Stage, "retry_queued")
                .SetProperty(item => item.Progress, 0)
                .SetProperty(item => item.ErrorCode, (string?)null)
                .SetProperty(item => item.ErrorMessage, (string?)null)
                .SetProperty(item => item.SubmissionState, SubmissionStates.NotAttempted)
                .SetProperty(item => item.ProviderId, (string?)null)
                .SetProperty(item => item.ProviderJobId, (string?)null)
                .SetProperty(item => item.ProviderIdempotencySupported, false)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return count == 1;
    }

    public async Task<bool> SetSubmissionStateAsync(Guid id, string expectedSubmissionState, string submissionState, string? providerId, string? providerJobId,
        bool providerIdempotencySupported, CancellationToken cancellationToken)
    {
        ValidateSubmissionTransition(expectedSubmissionState, submissionState);
        if (expectedSubmissionState == SubmissionStates.OutcomeUnknown)
            throw new InvalidOperationException("Unknown outcomes must be resolved through provider reconciliation.");
        if ((submissionState is SubmissionStates.Accepted or SubmissionStates.Running or SubmissionStates.Completed) &&
            (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(providerJobId)))
            throw new ArgumentException("Accepted provider states require provider and external job IDs.", nameof(providerJobId));
        var query = db.ReconstructionJobs.Where(item => item.Id == id && item.SubmissionState == expectedSubmissionState);
        var count = await query
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SubmissionState, submissionState)
                .SetProperty(item => item.ProviderId, providerId)
                .SetProperty(item => item.ProviderJobId, providerJobId)
                .SetProperty(item => item.ProviderIdempotencySupported, providerIdempotencySupported)
                .SetProperty(item => item.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        return count == 1;
    }

    public async Task<bool> RecordReconciliationAsync(Guid id, string expectedSubmissionState, string resultingSubmissionState,
        string? providerJobId, CancellationToken cancellationToken)
    {
        ValidateSubmissionTransition(expectedSubmissionState, resultingSubmissionState);
        if (expectedSubmissionState != SubmissionStates.OutcomeUnknown && resultingSubmissionState == SubmissionStates.NotAttempted)
            throw new ArgumentException("Only reconciliation of an unknown outcome may clear submission uncertainty.", nameof(resultingSubmissionState));
        var now = DateTimeOffset.UtcNow;
        var query = db.ReconstructionJobs.Where(item => item.Id == id && item.SubmissionState == expectedSubmissionState);
        if (expectedSubmissionState == SubmissionStates.OutcomeUnknown && resultingSubmissionState == SubmissionStates.NotAttempted)
            query = query.Where(item => item.ProviderIdempotencySupported);
        if ((resultingSubmissionState is SubmissionStates.Accepted or SubmissionStates.Running or SubmissionStates.Completed) &&
            string.IsNullOrWhiteSpace(providerJobId))
            throw new ArgumentException("A reconciled remote job requires its provider job ID.", nameof(providerJobId));
        var count = providerJobId is null
            ? await query.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SubmissionState, resultingSubmissionState)
                .SetProperty(item => item.ReconciliationAttempts, item => item.ReconciliationAttempts + 1)
                .SetProperty(item => item.LastReconciledAt, (DateTimeOffset?)now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken)
            : await query.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.SubmissionState, resultingSubmissionState)
                .SetProperty(item => item.ProviderJobId, providerJobId)
                .SetProperty(item => item.ReconciliationAttempts, item => item.ReconciliationAttempts + 1)
                .SetProperty(item => item.LastReconciledAt, (DateTimeOffset?)now)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        return count == 1;
    }

    public async Task<bool> TryReserveProviderSpendAsync(Guid jobId, string providerId, decimal amountUsd, decimal totalCapUsd,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 120) throw new ArgumentException("A provider ID is required.", nameof(providerId));
        if (amountUsd <= 0 || amountUsd > 20m) throw new ArgumentOutOfRangeException(nameof(amountUsd));
        if (totalCapUsd <= 0 || totalCapUsd > 20m) throw new ArgumentOutOfRangeException(nameof(totalCapUsd), "The aggregate live smoke-test ceiling cannot exceed the user-approved 20 USD cap.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "SELECT pg_advisory_xact_lock(5638873362477427271)";
            await command.ExecuteScalarAsync(cancellationToken);
        }
        // Image-to-3D runs are launched from a completed analysis job after the user picks a scene object.
        // The durable owner must exist; requiring `processing` would bypass the shared cap for that later phase.
        if (!await db.ReconstructionJobs.AnyAsync(job => job.Id == jobId, cancellationToken))
            return false;
        var reserved = await db.ProviderSpendReservations.SumAsync(row => (decimal?)row.AmountUsd, cancellationToken) ?? 0m;
        if (reserved + amountUsd > totalCapUsd) return false;
        db.ProviderSpendReservations.Add(new ProviderSpendReservationRecord
        {
            Id = Guid.NewGuid(), JobId = jobId, ProviderId = providerId, AmountUsd = amountUsd, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static void ValidateSubmissionTransition(string current, string next)
    {
        if (!SubmissionStates.All.Contains(current))
            throw new ArgumentException($"Unsupported current submission state '{current}'.", nameof(current));
        if (!SubmissionStates.All.Contains(next))
            throw new ArgumentException($"Unsupported submission state '{next}'.", nameof(next));
        if (!SubmissionStates.CanTransition(current, next))
            throw new InvalidOperationException($"Submission transition '{current}' → '{next}' is not permitted.");
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
