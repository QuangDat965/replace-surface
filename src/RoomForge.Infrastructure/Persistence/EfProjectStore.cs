using Microsoft.EntityFrameworkCore;
using RoomForge.Application.Projects;

namespace RoomForge.Infrastructure.Persistence;

public sealed class EfProjectStore(RoomForgeDbContext db) : IProjectStore
{
    public async Task<ProjectSummary?> FindAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == projectId, cancellationToken);
        return project is null ? null : new ProjectSummary(project.Id, project.Name);
    }

    public async Task<bool> CreateAsync(ProjectSummary project, CancellationToken cancellationToken)
    {
        db.Projects.Add(new ProjectRecord { Id = project.Id, Name = project.Name });
        try { await db.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); return false; }
    }
}
