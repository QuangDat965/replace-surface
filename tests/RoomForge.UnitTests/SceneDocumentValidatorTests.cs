using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Domain.Scenes;

namespace RoomForge.UnitTests;

public class SceneDocumentValidatorTests
{
    [Fact]
    public void Sample_scene_obeys_bootstrap_contract()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Empty(SceneDocumentValidator.Validate(document.RootElement));
    }

    [Fact]
    public void Rejects_wrong_coordinate_frame()
    {
        using var document = JsonDocument.Parse("""{"schemaVersion":1,"sceneId":"11111111-1111-4111-8111-111111111111","projectId":"22222222-2222-4222-8222-222222222222","units":"meters","world":{"up":"Z","floorPlane":"XY"},"objects":[],"materials":[]}""");
        Assert.Contains(SceneDocumentValidator.Validate(document.RootElement), error => error.Contains("Y-up"));
    }

    [Fact]
    public void Rejects_unscoped_texture_paths_and_invalid_pbr_values()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        var scene = JsonNode.Parse(File.ReadAllText(path))!;
        scene["materials"]![0]!["maps"]!["baseColor"] = "C:/private/texture.png";
        scene["materials"]![0]!["roughness"] = 1.5;
        scene["objects"]![0]!["surfaces"]![0]!["uv"]!["repeat"] = new JsonArray(-1, 1);
        using var document = JsonDocument.Parse(scene.ToJsonString());
        var errors = SceneDocumentValidator.Validate(document.RootElement);
        Assert.Contains(errors, error => error.Contains("PBR map key"));
        Assert.Contains(errors, error => error.Contains("roughness and metalness"));
        Assert.Contains(errors, error => error.Contains("Surface UV"));
    }

    [Theory]
    [InlineData(-0.01, 0.5)]
    [InlineData(0.5, 1.01)]
    public void Rejects_offsets_outside_normalized_uv_range(double u, double v)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        var scene = JsonNode.Parse(File.ReadAllText(path))!;
        scene["objects"]![0]!["surfaces"]![0]!["uv"]!["offset"] = new JsonArray(u, v);
        using var document = JsonDocument.Parse(scene.ToJsonString());
        Assert.Contains(SceneDocumentValidator.Validate(document.RootElement), error => error.Contains("Surface UV"));
    }
}
