using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Scenes;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfSceneStore(RoomForgeDbContext db) : ISceneStore
{
    public async Task<StoredScene?> FindAsync(Guid sceneId, CancellationToken cancellationToken)
    {
        var row = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(scene => scene.Id == sceneId, cancellationToken);
        return row is null ? null : new StoredScene(row.Id, row.Revision, row.Document);
    }

    public async Task<bool> CreateAsync(Guid sceneId, Guid projectId, string document, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
            db.Projects.Add(new ProjectRecord { Id = projectId, Name = "RoomForge project" });
        db.Scenes.Add(new SceneRecord { Id = sceneId, ProjectId = projectId, Revision = 1, Document = document, UpdatedAt = DateTimeOffset.UtcNow });
        db.SceneVersions.Add(new SceneVersionRecord { Id = Guid.NewGuid(), SceneId = sceneId, ProjectId = projectId, Revision = 1, Document = document });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }

    public async Task<bool> ReplaceAsync(Guid sceneId, int expectedRevision, string document, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var current = await db.Scenes.AsNoTracking().SingleOrDefaultAsync(scene => scene.Id == sceneId && scene.Revision == expectedRevision, cancellationToken);
        if (current is null) return false;
        var rows = await db.Scenes.Where(scene => scene.Id == sceneId && scene.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(scene => scene.Revision, expectedRevision + 1)
                .SetProperty(scene => scene.Document, document)
                .SetProperty(scene => scene.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (rows != 1) return false;
        db.SceneVersions.Add(new SceneVersionRecord
        {
            Id = Guid.NewGuid(), SceneId = sceneId, ProjectId = current.ProjectId,
            Revision = expectedRevision + 1, Document = document,
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
