using System.Text.Json;
using RoomForge.Domain.Scenes;

namespace RoomForge.Application.Scenes;

public interface ISceneStore
{
    Task<StoredScene?> FindAsync(Guid sceneId, CancellationToken cancellationToken);
    Task<bool> CreateAsync(Guid sceneId, Guid projectId, string document, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(Guid sceneId, int expectedRevision, string document, CancellationToken cancellationToken);
}

public sealed record StoredScene(Guid SceneId, int Revision, string Document);
public enum SaveSceneStatus { Saved, RevisionConflict, Invalid }
public sealed record SaveSceneResult(SaveSceneStatus Status, int? Revision, string? Document, IReadOnlyList<string>? Errors = null);

public sealed class ScenePersistence(ISceneStore store)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<StoredScene?> GetAsync(Guid id, CancellationToken ct) => store.FindAsync(id, ct);

    public async Task<SaveSceneResult> SaveAsync(Guid id, JsonElement body, int? expectedRevision, CancellationToken ct)
    {
        if (body.ValueKind != JsonValueKind.Object ||
            !body.TryGetProperty("sceneId", out var sceneIdElement) ||
            sceneIdElement.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(sceneIdElement.GetString(), out var bodyId) || bodyId != id)
            return new(SaveSceneStatus.Invalid, null, null, ["Scene ID must match the request path."]);

        var errors = ValidateScene(body);
        if (errors.Count != 0) return new(SaveSceneStatus.Invalid, null, null, errors);
        var projectId = body.GetProperty("projectId").GetGuid();
        var current = await store.FindAsync(id, ct);
        if (current is null)
        {
            if (expectedRevision != 0) return new(SaveSceneStatus.RevisionConflict, null, null);
            var created = SetRevision(body, 1);
            return await store.CreateAsync(id, projectId, created, ct)
                ? new(SaveSceneStatus.Saved, 1, created)
                : new(SaveSceneStatus.RevisionConflict, null, null);
        }

        if (expectedRevision is null || current.Revision != expectedRevision)
            return new(SaveSceneStatus.RevisionConflict, current.Revision, current.Document);
        using (var currentDocument = JsonDocument.Parse(current.Document))
        {
            var protectedObjectErrors = SceneDocumentValidator.ValidateProtectedObjectsRetained(currentDocument.RootElement, body);
            if (protectedObjectErrors.Count != 0) return new(SaveSceneStatus.Invalid, null, null, protectedObjectErrors);
            var archiveErrors = SceneDocumentValidator.ValidateObjectArchiveIntegrity(currentDocument.RootElement, body);
            if (archiveErrors.Count != 0) return new(SaveSceneStatus.Invalid, null, null, archiveErrors);
        }
        var updated = SetRevision(body, current.Revision + 1);
        return await store.ReplaceAsync(id, current.Revision, updated, ct)
            ? new(SaveSceneStatus.Saved, current.Revision + 1, updated)
            : new(SaveSceneStatus.RevisionConflict, current.Revision, current.Document);
    }

    private static string SetRevision(JsonElement body, int revision)
    {
        using var parsed = JsonDocument.Parse(body.GetRawText());
        var fields = parsed.RootElement.EnumerateObject().Where(property => property.Name != "revision")
            .ToDictionary(property => property.Name, property => JsonSerializer.Deserialize<JsonElement>(property.Value.GetRawText(), JsonOptions));
        fields["revision"] = JsonSerializer.SerializeToElement(revision, JsonOptions);
        return JsonSerializer.Serialize(fields, JsonOptions);
    }

    private static List<string> ValidateScene(JsonElement body) => SceneDocumentValidator.Validate(body).ToList();
}
