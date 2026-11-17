using System.Text.Json;
using System.Numerics;
using RoomForge.Infrastructure.Export;
using SharpGLTF.Schema2;
using ImageMagick;

namespace RoomForge.UnitTests;

public sealed class SceneGlbExportTests
{
    [Fact]
    public void ExportsBoxObjectsWithFlatFaceVerticesAndStableSemanticIds()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var expectedObjects = json.RootElement.GetProperty("objects").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedSurfaces = json.RootElement.GetProperty("objects").EnumerateArray()
            .SelectMany(item => item.GetProperty("surfaces").EnumerateArray()).Select(item => item.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hiddenId = expectedObjects.Last();
        var editable = System.Text.Json.Nodes.JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        var hidden = editable["objects"]!.AsArray().Single(item => string.Equals(item!["id"]!.GetValue<string>(), hiddenId, StringComparison.OrdinalIgnoreCase));
        hidden!["visible"] = false;
        using var editedDocument = JsonDocument.Parse(editable.ToJsonString());

        var bytes = new SharpGltfSceneExporter().Export(editedDocument.RootElement);
        using var stream = new MemoryStream(bytes);
        var model = ModelRoot.ReadGLB(stream);
        var nodes = model.DefaultScene!.VisualChildren.ToArray();

        Assert.Equal(expectedObjects, nodes.Select(node => node.Extras!["roomForgeObjectId"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase));
        var actualSurfaces = nodes.SelectMany(node => node.Extras!["roomForgeSurfaceIds"]!.AsArray())
            .Select(value => value!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expectedSurfaces, actualSurfaces);
        Assert.All(nodes, node => Assert.Equal(node.Name, node.Extras!["roomForgeObjectId"]!.GetValue<string>()));
        Assert.False(nodes.Single(node => node.Name == hiddenId).Extras!["roomForgeVisible"]!.GetValue<bool>());
        Assert.All(nodes.SelectMany(node => node.Mesh!.Primitives), primitive =>
        {
            var positions = primitive.GetVertexAccessor("POSITION");
            Assert.NotNull(positions);
            Assert.Equal(0, positions!.Count % 4);
            Assert.NotNull(primitive.Extras!["roomForgeSurfaceIds"]);
        });
        Assert.All(nodes, node => Assert.Equal(24, node.Mesh!.Primitives.Sum(primitive => primitive.GetVertexAccessor("POSITION")!.Count)));
        var countertop = nodes.Single(node => node.Name == "00000000-0000-4000-8000-000000000004");
        Assert.Equal(3, countertop.Mesh!.Primitives.Count);
        AssertWorldBoundsMatchSceneWithinOneMillimeter(editedDocument.RootElement, bytes);
    }

    [Fact]
    public void RejectsUnsupportedGeometryInsteadOfSubstitutingABox()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var root = System.Text.Json.Nodes.JsonNode.Parse(json.RootElement.GetRawText())!.AsObject();
        root["objects"]![0]!["geometry"]!["kind"] = "mesh";
        using var unsupported = JsonDocument.Parse(root.ToJsonString());

        var error = Assert.Throws<NotSupportedException>(() => new SharpGltfSceneExporter().Export(unsupported.RootElement));
        Assert.Contains("unsupported_geometry", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedsAllSupportedPbrMapsAndPacksSeparateMetalnessAndRoughnessChannels()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        const string storageId = "0123456789abcdef0123456789abcdef";
        root["materials"]![0]!["maps"] = new System.Text.Json.Nodes.JsonObject
        {
            ["baseColor"] = $"/api/assets/textures/{storageId}",
            ["normal"] = $"/api/assets/textures/{storageId}",
            ["roughness"] = $"/api/assets/textures/{storageId}",
            ["metalness"] = $"/api/assets/textures/{storageId}",
        };
        using var scene = JsonDocument.Parse(root.ToJsonString());
        using var sourceTexture = new MagickImage(MagickColors.White, 2, 2);
        var textureBytes = sourceTexture.ToByteArray(MagickFormat.Png);

        var glb = new SharpGltfSceneExporter().Export(scene.RootElement,
            id => string.Equals(id, storageId, StringComparison.Ordinal) ? textureBytes : null);
        using var glbJson = ReadGlbJson(glb);
        var exported = glbJson.RootElement;
        var material = exported.GetProperty("materials").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString()!.Contains("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", StringComparison.OrdinalIgnoreCase));
        Assert.True(material.GetProperty("pbrMetallicRoughness").TryGetProperty("baseColorTexture", out _));
        Assert.True(material.GetProperty("pbrMetallicRoughness").TryGetProperty("metallicRoughnessTexture", out _));
        Assert.True(material.TryGetProperty("normalTexture", out _));
        var images = exported.GetProperty("images").EnumerateArray().ToArray();
        Assert.Equal(2, images.Length); // base color and normal share the identical test image; metallic-roughness is packed separately.
        Assert.All(images, image =>
        {
            Assert.True(image.TryGetProperty("bufferView", out _));
            Assert.False(image.TryGetProperty("uri", out _));
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
        });

        using var stream = new MemoryStream(glb);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Equal(2, model.LogicalImages.Count);
        Assert.Equal(2, model.LogicalTextures.Count);
    }

    private static void AssertWorldBoundsMatchSceneWithinOneMillimeter(JsonElement sourceScene, byte[] glb)
    {
        using var glbJson = ReadGlbJson(glb);
        var root = glbJson.RootElement;
        var accessors = root.GetProperty("accessors");
        var bufferViews = root.GetProperty("bufferViews");
        var binaryOffset = GetGlbBinaryDataOffset(glb);
        var nodes = root.GetProperty("nodes");
        var exportedBounds = new Dictionary<string, (Vector3 Min, Vector3 Max)>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.EnumerateArray())
        {
            var id = node.GetProperty("extras").GetProperty("roomForgeObjectId").GetString()!;
            var mesh = root.GetProperty("meshes")[node.GetProperty("mesh").GetInt32()];
            var matrixValues = node.GetProperty("matrix").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            Assert.Equal(16, matrixValues.Length);
            // glTF stores matrices column-major; System.Numerics transforms row vectors.
            var world = new Matrix4x4(
                matrixValues[0], matrixValues[1], matrixValues[2], matrixValues[3],
                matrixValues[4], matrixValues[5], matrixValues[6], matrixValues[7],
                matrixValues[8], matrixValues[9], matrixValues[10], matrixValues[11],
                matrixValues[12], matrixValues[13], matrixValues[14], matrixValues[15]);
            var worldPositions = new List<Vector3>();
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var accessor = accessors[primitive.GetProperty("attributes").GetProperty("POSITION").GetInt32()];
                Assert.Equal(5126, accessor.GetProperty("componentType").GetInt32()); // FLOAT
                var bufferView = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
                var stride = bufferView.TryGetProperty("byteStride", out var strideValue) ? strideValue.GetInt32() : 12;
                var start = binaryOffset + (bufferView.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0) +
                    (accessor.TryGetProperty("byteOffset", out var accessorOffset) ? accessorOffset.GetInt32() : 0);
                for (var index = 0; index < accessor.GetProperty("count").GetInt32(); index++)
                {
                    var offset = start + index * stride;
                    var local = new Vector3(BitConverter.ToSingle(glb, offset), BitConverter.ToSingle(glb, offset + 4), BitConverter.ToSingle(glb, offset + 8));
                    worldPositions.Add(Vector3.Transform(local, world));
                }
            }
            Assert.NotEmpty(worldPositions);
            exportedBounds[id] = (worldPositions.Aggregate(Vector3.Min), worldPositions.Aggregate(Vector3.Max));
        }

        foreach (var item in sourceScene.GetProperty("objects").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var dimensions = ReadVector3(item.GetProperty("dimensions"));
            var position = ReadVector3(item.GetProperty("position"));
            var rotationValues = item.GetProperty("rotation").EnumerateArray().Select(value => value.GetSingle()).ToArray();
            var rotation = Quaternion.Normalize(new Quaternion(rotationValues[0], rotationValues[1], rotationValues[2], rotationValues[3]));
            var expectedCorners = Corners(-dimensions / 2, dimensions / 2)
                .Select(point => Vector3.Transform(point, rotation) + position).ToArray();
            var expectedMin = expectedCorners.Aggregate(Vector3.Min);
            var expectedMax = expectedCorners.Aggregate(Vector3.Max);
            var actual = exportedBounds[id];
            AssertVectorWithin(expectedMin, actual.Min, .001f, $"{id} world minimum");
            AssertVectorWithin(expectedMax, actual.Max, .001f, $"{id} world maximum");
        }
    }

    private static JsonDocument ReadGlbJson(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BitConverter.ToUInt32(glb, 0));
        Assert.Equal(2u, BitConverter.ToUInt32(glb, 4));
        var chunkLength = checked((int)BitConverter.ToUInt32(glb, 12));
        Assert.Equal(0x4E4F534Au, BitConverter.ToUInt32(glb, 16));
        return JsonDocument.Parse(glb.AsMemory(20, chunkLength));
    }

    private static int GetGlbBinaryDataOffset(byte[] glb)
    {
        var jsonChunkLength = checked((int)BitConverter.ToUInt32(glb, 12));
        var binaryChunkHeader = 20 + jsonChunkLength;
        Assert.Equal(0x004E4942u, BitConverter.ToUInt32(glb, binaryChunkHeader + 4));
        return binaryChunkHeader + 8;
    }

    private static Vector3 ReadVector3(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());

    private static IEnumerable<Vector3> Corners(Vector3 min, Vector3 max)
    {
        for (var x = 0; x < 2; x++)
        for (var y = 0; y < 2; y++)
        for (var z = 0; z < 2; z++)
            yield return new Vector3(x == 0 ? min.X : max.X, y == 0 ? min.Y : max.Y, z == 0 ? min.Z : max.Z);
    }

    private static void AssertVectorWithin(Vector3 expected, Vector3 actual, float tolerance, string label)
    {
        Assert.True(MathF.Abs(expected.X - actual.X) <= tolerance && MathF.Abs(expected.Y - actual.Y) <= tolerance &&
            MathF.Abs(expected.Z - actual.Z) <= tolerance,
            $"{label} differs by more than {tolerance} m: expected {expected}, actual {actual}.");
    }
}
