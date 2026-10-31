namespace RoomForge.Application.Projects;

public sealed record ProjectSummary(Guid Id, string Name);
public interface IProjectStore
{
    Task<ProjectSummary?> FindAsync(Guid projectId, CancellationToken cancellationToken);
    Task<bool> CreateAsync(ProjectSummary project, CancellationToken cancellationToken);
}

public sealed class ProjectPersistence(IProjectStore store)
{
    public Task<ProjectSummary?> GetAsync(Guid id, CancellationToken ct) => store.FindAsync(id, ct);

    public async Task<ProjectSummary?> CreateAsync(string name, CancellationToken ct)
    {
        var normalized = name.Trim();
        if (normalized.Length is 0 or > 120) return null;
        var project = new ProjectSummary(Guid.NewGuid(), normalized);
        return await store.CreateAsync(project, ct) ? project : null;
    }
}
