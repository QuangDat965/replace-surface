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

        var diagnostics = new JsonArray();
        foreach (var detectedSurface in analysis.Surfaces)
        {
            if (!string.IsNullOrWhiteSpace(detectedSurface.SemanticRole) && detectedSurface.LinkedSurfaceId is { } linkedSurfaceId)
            {
                var linked = objects.SelectMany(item => item?["surfaces"]?.AsArray() ?? [])
                    .OfType<JsonObject>().FirstOrDefault(surface => surface["id"]?.GetValue<Guid>() == linkedSurfaceId);
                if (linked is not null) continue; // Keep the original surface and its material/UV assignment intact.
            }
            diagnostics.Add($"Surface {detectedSurface.Id} remains a 2D candidate; metric thickness, 3D plane and placement are unknown.");
        }

        foreach (var detected in analysis.Objects.Where(item => item.Included))
        {
            var stableObjectId = detected.LinkedObjectId ?? detected.Id;
            var existing = objects.OfType<JsonObject>().FirstOrDefault(item => item["id"]?.GetValue<Guid>() == stableObjectId);
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
                ["id"] = stableObjectId, ["name"] = detected.Name, ["category"] = detected.Type,
                ["geometry"] = new JsonObject { ["kind"] = "box" },
                ["position"] = JsonSerializer.SerializeToNode(detected.PositionMeters),
                ["rotation"] = new JsonArray(0, 0, 0, 1),
                ["dimensions"] = JsonSerializer.SerializeToNode(detected.DimensionsMeters),
                ["visible"] = true, ["locked"] = false, ["measurementStatus"] = "estimated",
                ["confidence"] = detected.Confidence, ["provenance"] = detected.Provenance,
                ["surfaces"] = new JsonArray(),
            });
        }
        if (diagnostics.Count > 0) scene["reconstructionDiagnostics"] = diagnostics;
        return scene.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
