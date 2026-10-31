using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Application.Scenes;

namespace RoomForge.UnitTests;

public sealed class ScenePersistenceTests
{
    [Fact]
    public async Task Saves_fixture_with_new_revision_and_rejects_stale_revision()
    {
        var fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var id = fixture.RootElement.GetProperty("sceneId").GetGuid();
        var store = new MemorySceneStore();
        var service = new ScenePersistence(store);

        var first = await service.SaveAsync(id, fixture.RootElement, 0, CancellationToken.None);
        Assert.Equal(SaveSceneStatus.Saved, first.Status);
        Assert.Equal(1, first.Revision);
        using var saved = JsonDocument.Parse(first.Document!);
        Assert.Equal(id, saved.RootElement.GetProperty("sceneId").GetGuid());
        Assert.Equal(1, saved.RootElement.GetProperty("revision").GetInt32());

        var stale = await service.SaveAsync(id, fixture.RootElement, 0, CancellationToken.None);
        Assert.Equal(SaveSceneStatus.RevisionConflict, stale.Status);
        Assert.Equal(1, (await service.GetAsync(id, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task Rejects_scene_id_mismatch()
    {
        using var fixture = JsonDocument.Parse("""{"schemaVersion":1,"sceneId":"11111111-1111-4111-8111-111111111111","projectId":"22222222-2222-4222-8222-222222222222","units":"meters","world":{"up":"Y","floorPlane":"XZ"},"objects":[],"materials":[]}""");
        var service = new ScenePersistence(new MemorySceneStore());
        var result = await service.SaveAsync(Guid.NewGuid(), fixture.RootElement, 0, CancellationToken.None);
        Assert.Equal(SaveSceneStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Rejects_removing_a_structural_object_or_surface()
    {
        var fixture = await ReadFixture();
        var id = fixture["sceneId"]!.GetValue<Guid>();
        var store = new MemorySceneStore();
        var service = new ScenePersistence(store);
        using var initial = JsonDocument.Parse(fixture.ToJsonString());
        Assert.Equal(SaveSceneStatus.Saved, (await service.SaveAsync(id, initial.RootElement, 0, CancellationToken.None)).Status);

        fixture["objects"]!.AsArray().RemoveAt(0);
        using var proposed = JsonDocument.Parse(fixture.ToJsonString());
        var result = await service.SaveAsync(id, proposed.RootElement, 1, CancellationToken.None);

        Assert.Equal(SaveSceneStatus.Invalid, result.Status);
        Assert.Contains(result.Errors!, error => error.Contains("must remain in the active scene"));
        Assert.Equal(1, (await service.GetAsync(id, CancellationToken.None))!.Revision);

        var surfaceOnlyEdit = await ReadFixture();
        surfaceOnlyEdit["objects"]![0]!["surfaces"]!.AsArray().Clear();
        using var removedSurface = JsonDocument.Parse(surfaceOnlyEdit.ToJsonString());
        var surfaceResult = await service.SaveAsync(id, removedSurface.RootElement, 1, CancellationToken.None);
        Assert.Equal(SaveSceneStatus.Invalid, surfaceResult.Status);
        Assert.Contains(surfaceResult.Errors!, error => error.Contains("retain all of its SurfaceIds"));
    }

    [Fact]
    public async Task Archives_and_restores_optional_object_without_reassigning_its_identity()
    {
        var fixture = await ReadFixture();
        var id = fixture["sceneId"]!.GetValue<Guid>();
        var chair = fixture["objects"]!.AsArray().Single(item => item!["category"]!.GetValue<string>() == "furniture")!.DeepClone();
        var service = new ScenePersistence(new MemorySceneStore());
        using var initial = JsonDocument.Parse(fixture.ToJsonString());
        Assert.Equal(SaveSceneStatus.Saved, (await service.SaveAsync(id, initial.RootElement, 0, CancellationToken.None)).Status);

        var objects = fixture["objects"]!.AsArray();
        var objectIndex = Enumerable.Range(0, objects.Count).First(index => objects[index]?["id"]!.GetValue<string>() == chair["id"]!.GetValue<string>());
        Assert.NotEqual(-1, objectIndex);
        objects.RemoveAt(objectIndex);
        using (var discarded = JsonDocument.Parse(fixture.ToJsonString()))
        {
            var discardResult = await service.SaveAsync(id, discarded.RootElement, 1, CancellationToken.None);
            Assert.Equal(SaveSceneStatus.Invalid, discardResult.Status);
            Assert.Contains(discardResult.Errors!, error => error.Contains("archived instead of discarded"));
        }
        fixture["deletedObjects"] = new JsonArray(chair.DeepClone());
        using var archived = JsonDocument.Parse(fixture.ToJsonString());
        var archiveResult = await service.SaveAsync(id, archived.RootElement, 1, CancellationToken.None);
        Assert.Equal(SaveSceneStatus.Saved, archiveResult.Status);

        var restored = JsonNode.Parse(archiveResult.Document!)!;
        var archivedObject = restored["deletedObjects"]!.AsArray()[0]!.DeepClone();
        restored["deletedObjects"]!.AsArray().Clear();
        restored["objects"]!.AsArray().Add(archivedObject);
        using var restoredDocument = JsonDocument.Parse(restored.ToJsonString());
        var restoreResult = await service.SaveAsync(id, restoredDocument.RootElement, 2, CancellationToken.None);

        Assert.Equal(SaveSceneStatus.Saved, restoreResult.Status);
        using var saved = JsonDocument.Parse(restoreResult.Document!);
        Assert.Contains(saved.RootElement.GetProperty("objects").EnumerateArray(), item => item.GetProperty("id").GetString() == chair["id"]!.GetValue<string>());
        Assert.Empty(saved.RootElement.GetProperty("deletedObjects").EnumerateArray());
    }

    private static async Task<JsonObject> ReadFixture()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        return JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
    }

    private sealed class MemorySceneStore : ISceneStore
    {
        private readonly Dictionary<Guid, StoredScene> _scenes = [];
        public Task<StoredScene?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(_scenes.GetValueOrDefault(id));
        public Task<bool> CreateAsync(Guid id, Guid projectId, string document, CancellationToken ct)
        {
            if (_scenes.ContainsKey(id)) return Task.FromResult(false);
            _scenes.Add(id, new StoredScene(id, 1, document));
            return Task.FromResult(true);
        }
        public Task<bool> ReplaceAsync(Guid id, int expectedRevision, string document, CancellationToken ct)
        {
            if (!_scenes.TryGetValue(id, out var current) || current.Revision != expectedRevision) return Task.FromResult(false);
            _scenes[id] = new StoredScene(id, expectedRevision + 1, document);
            return Task.FromResult(true);
        }
    }
}
