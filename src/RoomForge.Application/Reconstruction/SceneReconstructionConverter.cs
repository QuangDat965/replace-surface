using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Application.Reconstruction;

public static class SceneReconstructionConverter
{
    public static string Convert(string baseSceneJson, SceneAnalysisResult analysis, Guid sceneId, bool preserveSceneIdentity, Guid? projectId = null)
    {
        var scene = JsonNode.Parse(baseSceneJson)?.AsObject() ?? throw new InvalidDataException("Base scene JSON must be an object.");
        var objects = scene["objects"]?.AsArray() ?? throw new InvalidDataException("Base scene objects must be an array.");
        var materials = scene["materials"]?.AsArray() ?? throw new InvalidDataException("Base scene materials must be an array.");
        if (materials.Count == 0) throw new InvalidDataException("A reconstruction scene needs at least one material.");
        scene["sceneId"] = sceneId;
        if (projectId is { } ownerProjectId) scene["projectId"] = ownerProjectId;
        scene["sourceImageId"] = analysis.SourceImageId;
        if (!preserveSceneIdentity || analysis.Assumptions.ScaleStatus != "unknown")
        {
            scene["calibration"] = new JsonObject
            {
                ["status"] = analysis.Assumptions.ScaleStatus,
                ["anchors"] = analysis.Assumptions.ReferencePixelLength is { } pixels && analysis.Assumptions.ReferenceLengthMeters is { } meters
                    ? new JsonArray(new JsonObject
                    {
                        ["referencePixelLength"] = pixels, ["lengthMeters"] = meters,
                        ["metersPerPixel"] = analysis.Assumptions.MetersPerPixel, ["provenance"] = "user_supplied",
                    })
                    : new JsonArray(),
                ["notes"] = string.Join(" ", analysis.Assumptions.Notes),
            };
        }
        if (!preserveSceneIdentity)
        {
            scene["revision"] = 0;
            scene["sourceImageId"] = analysis.SourceImageId;
            foreach (var item in objects)
            {
                if (item is not JsonObject obj) continue;
                obj["id"] = Guid.NewGuid();
                if (obj["surfaces"] is JsonArray surfaces)
                    foreach (var surface in surfaces.OfType<JsonObject>()) surface["id"] = Guid.NewGuid();
            }
        }

        var materialId = materials[0]?["id"]?.GetValue<Guid>() ?? throw new InvalidDataException("Base material ID is missing.");
        foreach (var detectedSurface in analysis.Surfaces)
        {
            if (!string.IsNullOrWhiteSpace(detectedSurface.SemanticRole) && detectedSurface.LinkedSurfaceId is { } linkedSurfaceId)
            {
                var linked = objects.SelectMany(item => item?["surfaces"]?.AsArray() ?? [])
                    .OfType<JsonObject>().FirstOrDefault(surface => surface["id"]?.GetValue<Guid>() == linkedSurfaceId);
                if (linked is not null) continue; // Keep the original surface and its material/UV assignment intact.
            }
            var width = detectedSurface.EstimatedWidthMeters ?? 3.6;
            var height = detectedSurface.EstimatedHeightMeters ?? (detectedSurface.SemanticRole == "floor" ? 3.6 : 2.7);
            var isFloor = detectedSurface.SemanticRole == "floor";
            var objectId = Guid.NewGuid();
            var surfaceId = Guid.NewGuid();
            objects.Add(new JsonObject
            {
                ["id"] = objectId, ["name"] = detectedSurface.SemanticRole, ["category"] = "architecture",
                ["geometry"] = new JsonObject { ["kind"] = "box" },
                ["position"] = new JsonArray(0, isFloor ? -0.025 : height / 2, isFloor ? 0 : -1.8),
                ["rotation"] = new JsonArray(0, 0, 0, 1),
                ["dimensions"] = isFloor ? new JsonArray(width, 0.05, height) : new JsonArray(width, height, 0.05),
                ["visible"] = true, ["locked"] = true, ["measurementStatus"] = "estimated",
                ["confidence"] = detectedSurface.Confidence, ["provenance"] = detectedSurface.Provenance,
                ["surfaces"] = new JsonArray(new JsonObject
                {
                    ["id"] = surfaceId, ["semanticRole"] = detectedSurface.SemanticRole,
                    ["faceId"] = isFloor ? "top" : "front", ["materialId"] = materialId,
                }),
            });
        }

        foreach (var detected in analysis.Objects.Where(item => item.Included))
        {
            JsonObject? existing = null;
            if (detected.LinkedObjectId is { } objectId)
                existing = objects.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<Guid>() == objectId);
            if (existing is not null)
            {
                existing["name"] = detected.Name;
                existing["category"] = detected.Type;
                existing["position"] = JsonSerializer.SerializeToNode(detected.PositionMeters);
                existing["dimensions"] = JsonSerializer.SerializeToNode(detected.DimensionsMeters);
                existing["measurementStatus"] = "estimated";
                existing["confidence"] = detected.Confidence;
                existing["provenance"] = detected.Provenance;
                continue;
            }

            objects.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid(), ["name"] = detected.Name, ["category"] = detected.Type,
                ["geometry"] = new JsonObject { ["kind"] = "box" },
                ["position"] = JsonSerializer.SerializeToNode(detected.PositionMeters),
                ["rotation"] = new JsonArray(0, 0, 0, 1),
                ["dimensions"] = JsonSerializer.SerializeToNode(detected.DimensionsMeters),
                ["visible"] = true, ["locked"] = false, ["measurementStatus"] = "estimated",
                ["confidence"] = detected.Confidence, ["provenance"] = detected.Provenance,
                ["surfaces"] = new JsonArray(),
            });
        }
        return scene.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
