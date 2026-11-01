using Microsoft.EntityFrameworkCore;

namespace RoomForge.Infrastructure.Persistence;

public sealed class RoomForgeDbContext(DbContextOptions<RoomForgeDbContext> options) : DbContext(options)
{
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SceneVersionRecord> SceneVersions => Set<SceneVersionRecord>();
    public DbSet<SceneRecord> Scenes => Set<SceneRecord>();
    public DbSet<ImageAssetRecord> ImageAssets => Set<ImageAssetRecord>();
    public DbSet<ReconstructionJobRecord> ReconstructionJobs => Set<ReconstructionJobRecord>();

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
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.AnalysisJson).HasColumnType("jsonb");
        modelBuilder.Entity<ReconstructionJobRecord>().Property(x => x.PreviewDocument).HasColumnType("jsonb");
        modelBuilder.Entity<ReconstructionJobRecord>().HasOne<ProjectRecord>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ReconstructionJobRecord>().HasOne<ImageAssetRecord>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
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
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public string? AnalysisJson { get; set; }
    public Guid? PreviewTargetSceneId { get; set; }
    public int PreviewBaseRevision { get; set; }
    public string? PreviewDocument { get; set; }
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
