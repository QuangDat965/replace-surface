using RoomForge.Application.Projects;

namespace RoomForge.UnitTests;

public sealed class ProjectPersistenceTests
{
    [Fact]
    public async Task Creates_trimmed_project_and_rejects_blank_name()
    {
        var store = new MemoryProjectStore();
        var service = new ProjectPersistence(store);
        var created = await service.CreateAsync("  Sample room  ", CancellationToken.None);
        Assert.NotNull(created);
        Assert.Equal("Sample room", created.Name);
        Assert.Equal(created, await service.GetAsync(created.Id, CancellationToken.None));
        Assert.Null(await service.CreateAsync("  ", CancellationToken.None));
    }

    private sealed class MemoryProjectStore : IProjectStore
    {
        private readonly Dictionary<Guid, ProjectSummary> _projects = [];
        public Task<ProjectSummary?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(_projects.GetValueOrDefault(id));
        public Task<bool> CreateAsync(ProjectSummary project, CancellationToken ct)
        {
            if (!_projects.TryAdd(project.Id, project)) return Task.FromResult(false);
            return Task.FromResult(true);
        }
    }
}
