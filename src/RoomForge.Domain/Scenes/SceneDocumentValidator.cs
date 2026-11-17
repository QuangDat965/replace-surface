using System.Text.Json;

namespace RoomForge.Domain.Scenes;

public static class SceneDocumentValidator
{
    public static IReadOnlyList<string> Validate(JsonElement scene)
    {
        var errors = new List<string>();
        if (scene.ValueKind != JsonValueKind.Object) return ["Scene must be an object."];
        if (!scene.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schemaVersion) || schemaVersion != 1)
            errors.Add("Only schemaVersion 1 is supported.");
        ValidateId(scene, "sceneId", errors);
        ValidateId(scene, "projectId", errors);
        if (!scene.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.String || units.GetString() != "meters") errors.Add("Units must be meters.");
        if (!scene.TryGetProperty("world", out var world) || world.ValueKind != JsonValueKind.Object ||
            !HasStringValue(world, "up", "Y") || !HasStringValue(world, "floorPlane", "XZ"))
            errors.Add("World must be Y-up with an XZ floor.");

        var materialIds = new HashSet<Guid>();
        if (!scene.TryGetProperty("materials", out var materials) || materials.ValueKind != JsonValueKind.Array)
            errors.Add("Materials must be an array.");
        else
            foreach (var material in materials.EnumerateArray())
            {
                if (material.ValueKind != JsonValueKind.Object) { errors.Add("Every material must be an object."); continue; }
                if (!TryReadGuid(material, "id", out var id)) errors.Add("Every material must have a UUID id.");
                else if (!materialIds.Add(id)) errors.Add($"Duplicate material id {id}.");
                if (!HasRange(material, "roughness", 0, 1) || !HasRange(material, "metalness", 0, 1)) errors.Add("Material roughness and metalness must be between 0 and 1.");
                if (!HasPositiveVector(material, "tileSizeMeters", 2)) errors.Add("Material tileSizeMeters must contain two positive meter values.");
                if (material.TryGetProperty("maps", out var maps))
                {
                    if (maps.ValueKind != JsonValueKind.Object) errors.Add("Material maps must be an object.");
                    else foreach (var map in maps.EnumerateObject())
                    {
                        if (map.Name is not ("baseColor" or "roughness" or "metalness" or "normal") ||
                            map.Value.ValueKind != JsonValueKind.String || !IsTextureAssetUrl(map.Value.GetString() ?? string.Empty))
                            errors.Add("Material maps must reference uploaded texture assets using a supported PBR map key.");
                    }
                }
            }

        var objectIds = new HashSet<Guid>();
        var surfaceIds = new HashSet<Guid>();
        var objectsToValidate = new List<(JsonElement Value, bool Archived)>();
        if (!scene.TryGetProperty("objects", out var objects) || objects.ValueKind != JsonValueKind.Array)
            errors.Add("Objects must be an array.");
        else
            objectsToValidate.AddRange(objects.EnumerateArray().Select(item => (item, false)));
        if (scene.TryGetProperty("deletedObjects", out var deletedObjects))
        {
            if (deletedObjects.ValueKind != JsonValueKind.Array) errors.Add("deletedObjects must be an array when present.");
            else objectsToValidate.AddRange(deletedObjects.EnumerateArray().Select(item => (item, true)));
        }
        foreach (var (item, archived) in objectsToValidate)
            {
                if (item.ValueKind != JsonValueKind.Object) { errors.Add("Every object must be an object."); continue; }
                if (archived && IsProtectedObject(item)) errors.Add("Structural or locked objects cannot be archived.");
                if (!TryReadGuid(item, "id", out var objectId)) errors.Add("Every object must have a UUID id.");
                else if (!objectIds.Add(objectId)) errors.Add($"Duplicate object id {objectId}.");
                if (!HasPositiveVector(item, "dimensions", 3)) errors.Add("Object dimensions must contain three positive meter values.");
                if (!HasFiniteVector(item, "position", 3, 100_000)) errors.Add("Object position must contain finite supported meter coordinates.");
                if (!IsValidQuaternion(item)) errors.Add("Object rotation must be a normalized quaternion [x,y,z,w].");
                if (!HasBoolean(item, "visible") || !HasBoolean(item, "locked")) errors.Add("Object visible and locked values must be booleans.");
                if (item.TryGetProperty("meshAsset", out var meshAsset) && !IsValidMeshAsset(meshAsset))
                    errors.Add("Object meshAsset must reference a server-owned object mesh job asset.");
                if (!item.TryGetProperty("surfaces", out var surfaces) || surfaces.ValueKind != JsonValueKind.Array)
                { errors.Add("Every object must have a surfaces array."); continue; }
                foreach (var surface in surfaces.EnumerateArray())
                {
                    if (surface.ValueKind != JsonValueKind.Object) { errors.Add("Every surface must be an object."); continue; }
                    if (!TryReadGuid(surface, "id", out var surfaceId)) errors.Add("Every surface must have a UUID id.");
                    else if (!surfaceIds.Add(surfaceId)) errors.Add($"Duplicate surface id {surfaceId}.");
                    if (!TryReadGuid(surface, "materialId", out var materialId) || !materialIds.Contains(materialId))
                        errors.Add("Every assigned surface materialId must reference a material in the scene.");
                    if (surface.TryGetProperty("uv", out var uv) && (uv.ValueKind != JsonValueKind.Object ||
                        !HasRange(uv, "rotationDegrees", -36000, 36000) || !HasRangeVector(uv, "offset", 2, 0, 1) ||
                        uv.TryGetProperty("repeat", out _) && !HasPositiveVector(uv, "repeat", 2)))
                        errors.Add("Surface UV rotation, offset, and repeat must contain finite supported values.");
                }
            }
        return errors;
    }

    public static IReadOnlyList<string> ValidateProtectedObjectsRetained(JsonElement current, JsonElement proposed)
    {
        if (!current.TryGetProperty("objects", out var currentObjects) || currentObjects.ValueKind != JsonValueKind.Array ||
            !proposed.TryGetProperty("objects", out var proposedObjects) || proposedObjects.ValueKind != JsonValueKind.Array) return [];

        var proposedById = proposedObjects.EnumerateArray()
            .Where(item => TryReadGuid(item, "id", out _))
            .ToDictionary(item => item.GetProperty("id").GetGuid());
        var errors = new List<string>();
        foreach (var original in currentObjects.EnumerateArray().Where(IsProtectedObject))
        {
            var objectId = original.GetProperty("id").GetGuid();
            if (!proposedById.TryGetValue(objectId, out var retained))
            {
                errors.Add($"Structural or locked object {objectId} must remain in the active scene.");
                continue;
            }
            var retainedSurfaceIds = retained.TryGetProperty("surfaces", out var surfaces) && surfaces.ValueKind == JsonValueKind.Array
                ? surfaces.EnumerateArray().Where(surface => TryReadGuid(surface, "id", out _)).Select(surface => surface.GetProperty("id").GetGuid()).ToHashSet()
                : [];
            if (original.TryGetProperty("surfaces", out var originalSurfaces) && originalSurfaces.ValueKind == JsonValueKind.Array &&
                originalSurfaces.EnumerateArray().Any(surface => TryReadGuid(surface, "id", out var surfaceId) && !retainedSurfaceIds.Contains(surfaceId)))
                errors.Add($"Structural or locked object {objectId} must retain all of its SurfaceIds.");
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateObjectArchiveIntegrity(JsonElement current, JsonElement proposed)
    {
        var currentActive = ReadObjects(current, "objects");
        var currentArchived = ReadObjects(current, "deletedObjects");
        var proposedActive = ReadObjects(proposed, "objects");
        var proposedArchived = ReadObjects(proposed, "deletedObjects");
        var proposedIds = proposedActive.Concat(proposedArchived).Select(GetObjectId).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        var errors = new List<string>();
        foreach (var item in currentActive.Where(item => !IsProtectedObject(item)))
        {
            var id = GetObjectId(item);
            if (id.HasValue && !proposedIds.Contains(id.Value)) errors.Add($"Optional object {id.Value} must be archived instead of discarded.");
        }
        foreach (var item in currentArchived)
        {
            var id = GetObjectId(item);
            if (id.HasValue && !proposedIds.Contains(id.Value)) errors.Add($"Archived object {id.Value} must remain available for restoration.");
        }
        return errors;
    }

    private static List<JsonElement> ReadObjects(JsonElement scene, string propertyName) =>
        scene.TryGetProperty(propertyName, out var objects) && objects.ValueKind == JsonValueKind.Array
            ? objects.EnumerateArray().ToList()
            : [];

    private static Guid? GetObjectId(JsonElement item) =>
        TryReadGuid(item, "id", out var id) ? id : null;

    public static bool IsProtectedObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return false;
        if (HasBoolean(item, "locked", true) || HasStringValue(item, "category", "architecture") || HasStringValue(item, "category", "structure")) return true;
        return item.TryGetProperty("surfaces", out var surfaces) && surfaces.ValueKind == JsonValueKind.Array &&
            surfaces.EnumerateArray().Any(surface => surface.ValueKind == JsonValueKind.Object &&
                (HasStringValue(surface, "semanticRole", "floor") || HasStringValue(surface, "semanticRole", "wall") ||
                 HasStringValue(surface, "semanticRole", "countertop_top") || HasStringValue(surface, "semanticRole", "countertop_edge")));
    }

    private static void ValidateId(JsonElement value, string name, ICollection<string> errors)
    {
        if (!TryReadGuid(value, name, out _)) errors.Add($"{name} must be a UUID.");
    }

    private static bool TryReadGuid(JsonElement value, string name, out Guid id)
    {
        id = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String && Guid.TryParse(property.GetString(), out id);
    }

    private static bool HasStringValue(JsonElement value, string name, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;

    private static bool HasBoolean(JsonElement value, string name, bool? expected = null) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        (property.ValueKind is JsonValueKind.True or JsonValueKind.False) &&
        (expected is null || property.GetBoolean() == expected.Value);

    private static bool IsValidQuaternion(JsonElement value)
    {
        if (!HasFiniteVector(value, "rotation", 4, 1)) return false;
        var rotation = value.GetProperty("rotation").EnumerateArray().Select(component => component.GetDouble()).ToArray();
        var lengthSquared = rotation.Sum(component => component * component);
        return lengthSquared is >= 0.99 and <= 1.01;
    }

    private static bool IsValidMeshAsset(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !TryReadGuid(value, "sourceJobId", out var jobId) ||
            !value.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String ||
            url.GetString() != $"/api/object-mesh-jobs/{jobId:D}/asset" ||
            !value.TryGetProperty("provider", out var provider) || provider.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(provider.GetString()) ||
            !value.TryGetProperty("generatedAt", out var generatedAt) || generatedAt.ValueKind != JsonValueKind.String ||
            !generatedAt.TryGetDateTimeOffset(out _)) return false;
        return true;
    }

    private static bool HasFiniteVector(JsonElement value, string name, int length, double absoluteMaximum)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != length) return false;
        return array.EnumerateArray().All(component => component.ValueKind == JsonValueKind.Number && component.TryGetDouble(out var number) &&
            double.IsFinite(number) && Math.Abs(number) <= absoluteMaximum);
    }

    private static bool HasPositiveVector(JsonElement value, string name, int length)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != length) return false;
        return array.EnumerateArray().All(component => component.ValueKind == JsonValueKind.Number && component.TryGetDouble(out var number) && double.IsFinite(number) && number > 0);
    }

    private static bool HasRange(JsonElement value, string name, double minimum, double maximum) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
        property.TryGetDouble(out var number) && double.IsFinite(number) && number >= minimum && number <= maximum;

    private static bool HasRangeVector(JsonElement value, string name, int length, double minimum, double maximum)
    {
        if (!value.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != length) return false;
        return array.EnumerateArray().All(component => component.ValueKind == JsonValueKind.Number && component.TryGetDouble(out var number) &&
            double.IsFinite(number) && number >= minimum && number <= maximum);
    }

    private static bool IsTextureAssetUrl(string value)
    {
        const string prefix = "/api/assets/textures/";
        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length == prefix.Length + 32 && value[prefix.Length..].All(Uri.IsHexDigit);
    }
}
