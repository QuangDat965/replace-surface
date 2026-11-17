using Microsoft.EntityFrameworkCore;
using RoomForge.Domain.Annotations;

namespace RoomForge.Infrastructure.Persistence;

public sealed class RoomForgeDbContext(DbContextOptions<RoomForgeDbContext> options) : DbContext(options)

{
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SceneVersionRecord> SceneVersions => Set<SceneVersionRecord>();
    public DbSet<SceneRecord> Scenes => Set<SceneRecord>();
    public DbSet<ImageAssetRecord> ImageAssets => Set<ImageAssetRecord>();
    public DbSet<ReconstructionJobRecord> ReconstructionJobs => Set<ReconstructionJobRecord>();
    public DbSet<AnnotationDocumentRecord> AnnotationDocuments => Set<AnnotationDocumentRecord>();
    public DbSet<RoomTopologyRecord> RoomTopologies => Set<RoomTopologyRecord>();
    public DbSet<ReconstructionCandidateRecord> ReconstructionCandidates => Set<ReconstructionCandidateRecord>();
    public DbSet<ProviderSpendReservationRecord> ProviderSpendReservations => Set<ProviderSpendReservationRecord>();
    public DbSet<ObjectMeshJobRecord> ObjectMeshJobs => Set<ObjectMeshJobRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneVersionRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneVersionRecord>().Property(x => x.Document).HasColumnType("jsonb");
        modelBuilder.Entity<SceneVersionRecord>().HasIndex(x => new { x.SceneId, x.Revision }).IsUnique();
        modelBuilder.Entity<SceneRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<SceneRecord>().Property(x => x.Document).HasColumnType("jsonb");
        modelBuilder.Entity<SceneRecord>().Property(x => x.Revision).IsConcurrencyToken();
        modelBuilder.Entity<SceneRecord>().HasIndex(x => x.ProjectId);
        modelBuilder.Entity<SceneRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ImageAssetRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ImageAssetRecord>().HasIndex(x => new { x.ProjectId, x.CreatedAt });
        modelBuilder.Entity<ImageAssetRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReconstructionJobRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ReconstructionJobRecord>().HasIndex(x => new { x.ProjectId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<ReconstructionJobRecord>().HasIndex(x => new { x.Status, x.CreatedAt });
        modelBuilder.Entity<ReconstructionJobRecord>().HasIndex(x => new { x.Status, x.NextAttemptAt });
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.SubmissionState).HasMaxLength(40).HasDefaultValue("not_attempted");
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.ProviderId).HasMaxLength(120);
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.ProviderJobId).HasMaxLength(200);
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.AnalysisJson).HasColumnType("jsonb");
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.PreviewDocument).HasColumnType("jsonb");
        modelBuilder.Entity<ReconstructionJobRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReconstructionJobRecord>().HasOne<ImageAssetRecord>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AnnotationDocumentRecord>().HasKey(x => x.AnnotationId);
        modelBuilder.Entity<AnnotationDocumentRecord>().Property(x => x.Document).HasColumnType("jsonb");
        modelBuilder.Entity<AnnotationDocumentRecord>().HasIndex(x => new { x.ProjectId, x.SourceImageId });
        modelBuilder.Entity<AnnotationDocumentRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AnnotationDocumentRecord>().HasOne<ImageAssetRecord>().WithMany().HasForeignKey(x => x.SourceImageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<RoomTopologyRecord>().HasKey(x => x.TopologyId);
        modelBuilder.Entity<RoomTopologyRecord>().Property(x => x.Document).HasColumnType("jsonb");
        modelBuilder.Entity<RoomTopologyRecord>().HasIndex(x => new { x.ProjectId, x.AnnotationId, x.Revision });
        modelBuilder.Entity<RoomTopologyRecord>().HasOne<AnnotationDocumentRecord>().WithMany().HasForeignKey(x => x.AnnotationId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReconstructionCandidateRecord>().HasKey(x => x.CandidateId);
        modelBuilder.Entity<ReconstructionCandidateRecord>().Property(x => x.Document).HasColumnType("jsonb");
        modelBuilder.Entity<ReconstructionCandidateRecord>().HasIndex(x => new { x.ProjectId, x.SceneId, x.CreatedAt });
        modelBuilder.Entity<ReconstructionCandidateRecord>().HasOne<AnnotationDocumentRecord>().WithMany().HasForeignKey(x => x.AnnotationId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ReconstructionCandidateRecord>().HasOne<SceneRecord>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ProviderSpendReservationRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ProviderSpendReservationRecord>().Property(x => x.ProviderId).HasMaxLength(120);
        modelBuilder.Entity<ProviderSpendReservationRecord>().Property(x => x.AmountUsd).HasPrecision(10, 6);
        modelBuilder.Entity<ProviderSpendReservationRecord>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<ProviderSpendReservationRecord>().HasOne<ReconstructionJobRecord>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasKey(x => x.Id);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasIndex(x => new { x.ProjectId, x.IdempotencyKey }).IsUnique();
        modelBuilder.Entity<ObjectMeshJobRecord>().HasIndex(x => new { x.Status, x.CreatedAt });
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.Status).HasMaxLength(40);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.SubmissionState).HasMaxLength(40);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.ProviderId).HasMaxLength(120);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.ProviderJobId).HasMaxLength(200);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.IdempotencyKey).HasMaxLength(200);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.CropAssetStorageId).HasMaxLength(64);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.CropSha256).HasMaxLength(64);
        modelBuilder.Entity<ObjectMeshJobRecord>().Property(x => x.OutputAssetStorageId).HasMaxLength(64);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasOne<ReconstructionJobRecord>().WithMany().HasForeignKey(x => x.AnalysisJobId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasOne<ImageAssetRecord>().WithMany().HasForeignKey(x => x.SourceImageId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ObjectMeshJobRecord>().HasOne<SceneRecord>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ImageAssetRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string StorageId { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReconstructionJobRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ImageId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string Stage { get; set; } = "queued";
    public int Progress { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AnalysisJson { get; set; }
    public Guid? PreviewTargetSceneId { get; set; }
    public int PreviewBaseRevision { get; set; }
    public string? PreviewDocument { get; set; }

    // Seven-state remote submission tracking. Distinguishes local progress from
    // remote submission confidence so a lost response never causes a duplicate paid request.
    public string SubmissionState { get; set; } = "not_attempted";
    public string? ProviderId { get; set; }
    public string? ProviderJobId { get; set; }
    public bool ProviderIdempotencySupported { get; set; }
    public int ReconciliationAttempts { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
}

public sealed class AnnotationDocumentRecord
{
    public Guid AnnotationId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceImageId { get; set; }
    public int Revision { get; set; }
    public string Document { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class RoomTopologyRecord
{
    public Guid TopologyId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AnnotationId { get; set; }
    public int Revision { get; set; }
    public string Document { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ReconstructionCandidateRecord
{
    public Guid CandidateId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SceneId { get; set; }
    public Guid AnnotationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Document { get; set; } = "{}";
}

public sealed class ProviderSpendReservationRecord
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public decimal AmountUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ObjectMeshJobRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid AnalysisJobId { get; set; }
    public Guid SourceImageId { get; set; }
    public Guid SceneId { get; set; }
    public Guid ObjectId { get; set; }
    public int ExpectedSceneRevision { get; set; }
    public string CropAssetStorageId { get; set; } = string.Empty;
    public string CropSha256 { get; set; } = string.Empty;
    public string CropContentType { get; set; } = "image/png";
    public string IdempotencyKey { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public string SubmissionState { get; set; } = "not_attempted";
    public string? ProviderId { get; set; }
    public string? ProviderJobId { get; set; }
    public string? OutputAssetStorageId { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int Progress { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}

public sealed class ProjectRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class SceneVersionRecord
{
    public Guid Id { get; set; }
    public Guid SceneId { get; set; }
    public Guid ProjectId { get; set; }
    public int Revision { get; set; }
    public string Document { get; set; } = "{}";
}

public sealed class SceneRecord
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int Revision { get; set; }
    public string Document { get; set; } = "{}";
    public DateTimeOffset UpdatedAt { get; set; }
}
