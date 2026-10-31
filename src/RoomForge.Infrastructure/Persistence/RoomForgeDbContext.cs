using Microsoft.EntityFrameworkCore;

namespace RoomForge.Infrastructure.Persistence;

public sealed class RoomForgeDbContext(DbContextOptions<RoomForgeDbContext> options) : DbContext(options)
{
    public DbSet<ProjectRecord> Projects => Set<ProjectRecord>();
    public DbSet<SceneVersionRecord> SceneVersions => Set<SceneVersionRecord>();
    public DbSet<SceneRecord> Scenes => Set<SceneRecord>();

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
    }
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
