using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RoomForge.Domain.Scenes;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using ImageMagick;

namespace RoomForge.Infrastructure.Export;

/// <summary>Exports supported RoomForge box scenes to a self-contained binary glTF.</summary>
public sealed class SharpGltfSceneExporter
{
    private static readonly (string Id, Vector3 Normal, Vector3[] Corners)[] Faces =
    [
        ("right", Vector3.UnitX, [new(1,-1,1),new(1,-1,-1),new(1,1,-1),new(1,1,1)]),
        ("left", -Vector3.UnitX, [new(-1,-1,-1),new(-1,-1,1),new(-1,1,1),new(-1,1,-1)]),
        ("top", Vector3.UnitY, [new(-1,1,1),new(1,1,1),new(1,1,-1),new(-1,1,-1)]),
        ("bottom", -Vector3.UnitY, [new(-1,-1,-1),new(1,-1,-1),new(1,-1,1),new(-1,-1,1)]),
        ("front", Vector3.UnitZ, [new(-1,-1,1),new(1,-1,1),new(1,1,1),new(-1,1,1)]),
        ("back", -Vector3.UnitZ, [new(1,-1,-1),new(-1,-1,-1),new(-1,1,-1),new(1,1,-1)]),
    ];

    public byte[] Export(JsonElement document, Func<string, byte[]?>? textureResolver = null)
    {
        var errors = SceneDocumentValidator.Validate(document);
        if (errors.Count > 0) throw new InvalidDataException($"Scene is not exportable: {string.Join("; ", errors)}");
        var model = ModelRoot.CreateModel();
        var gltfScene = model.UseScene("RoomForge");
        var materials = ReadMaterials(document, textureResolver);
        var objects = document.GetProperty("objects");
        foreach (var item in objects.EnumerateArray())
        {
            var kind = item.GetProperty("geometry").GetProperty("kind").GetString();
            if (kind != "box") throw new NotSupportedException($"unsupported_geometry: object {item.GetProperty("id").GetString()} has kind '{kind}'.");
            var objectId = item.GetProperty("id").GetGuid();
            var dimensions = Vector(item.GetProperty("dimensions"));
            var position = Vector(item.GetProperty("position"));
            var rotation = QuaternionValue(item.GetProperty("rotation"));
            var surfaces = item.GetProperty("surfaces").EnumerateArray().ToDictionary(
                value => value.GetProperty("faceId").GetString() ?? string.Empty, value => value);
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(objectId.ToString("D"));
            var primitiveSurfaces = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
            var builders = materials.ToDictionary(pair => pair.Key.ToString("D"), pair => pair.Value.Builder, StringComparer.Ordinal);
            const string defaultKey = "roomforge-default";
            builders[defaultKey] = DefaultMaterial();

            foreach (var face in Faces)
            {
                surfaces.TryGetValue(face.Id, out var surface);
                var materialId = surface.ValueKind == JsonValueKind.Object ? surface.GetProperty("materialId").GetGuid() : (Guid?)null;
                var materialKey = materialId?.ToString("D") ?? defaultKey;
                var material = builders[materialKey];
                var primitive = mesh.UsePrimitive(material);
                var faceSize = FaceDimensions(dimensions, face.Id);
                var tile = materialId is { } id ? materials[id].TileSize : Vector2.One;
                var repeat = ReadUv(surface, "repeat", new Vector2(1,1));
                var offset = ReadUv(surface, "offset", Vector2.Zero);
                var rotationUv = surface.ValueKind == JsonValueKind.Object && surface.TryGetProperty("uv", out var uv) && uv.TryGetProperty("rotationDegrees", out var degrees)
                    ? degrees.GetSingle() * MathF.PI / 180f : 0;
                var uvCorners = new[] { Vector2.Zero, new Vector2(faceSize.X / tile.X * repeat.X, 0),
                    new Vector2(faceSize.X / tile.X * repeat.X, faceSize.Y / tile.Y * repeat.Y),
                    new Vector2(0, faceSize.Y / tile.Y * repeat.Y) };
                var verts = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>[4];
                for (var i = 0; i < 4; i++)
                {
                    var local = face.Corners[i] * dimensions / 2;
                    var tex = uvCorners[i];
                    var rotated = new Vector2(MathF.Cos(rotationUv) * tex.X - MathF.Sin(rotationUv) * tex.Y,
                        MathF.Sin(rotationUv) * tex.X + MathF.Cos(rotationUv) * tex.Y) + offset;
                    verts[i] = new VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(
                        new VertexPositionNormal(local, face.Normal), new VertexTexture1(rotated), new VertexEmpty());
                }
                primitive.AddQuadrangle(verts[0], verts[1], verts[2], verts[3]);
                if (surface.ValueKind == JsonValueKind.Object)
                {
                    if (!primitiveSurfaces.TryGetValue(materialKey, out var ids)) primitiveSurfaces[materialKey] = ids = [];
                    ids.Add(surface.GetProperty("id").GetGuid());
                }
            }

            var gltfMesh = model.CreateMesh(mesh);
            foreach (var primitive in gltfMesh.Primitives)
            {
                var materialName = primitive.Material?.Name;
                var key = builders.FirstOrDefault(pair => pair.Value.Name == materialName).Key;
                var ids = key is not null && primitiveSurfaces.TryGetValue(key, out var values) ? values : [];
                primitive.Extras = new JsonObject { ["roomForgeSurfaceIds"] = new JsonArray(ids.Select(value => (JsonNode?)JsonValue.Create(value.ToString("D"))).ToArray()) };
            }
            var node = gltfScene.CreateNode(objectId.ToString("D"));
            node.Mesh = gltfMesh;
            node.LocalMatrix = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
            node.Extras = new JsonObject
            {
                ["roomForgeObjectId"] = objectId.ToString("D"),
                ["roomForgeSurfaceIds"] = new JsonArray(item.GetProperty("surfaces").EnumerateArray().Select(value => (JsonNode?)JsonValue.Create(value.GetProperty("id").GetGuid().ToString("D"))).ToArray()),
                ["roomForgeVisible"] = item.GetProperty("visible").GetBoolean(),
                ["roomForgeMeasurementStatus"] = item.TryGetProperty("measurementStatus", out var status) ? status.GetString() : "unknown",
            };
        }
        using var stream = new MemoryStream();
        model.WriteGLB(stream);
        return stream.ToArray();
    }

    private static Dictionary<Guid, ExportMaterial> ReadMaterials(JsonElement document, Func<string, byte[]?>? textureResolver) => document.GetProperty("materials").EnumerateArray().ToDictionary(
        item => item.GetProperty("id").GetGuid(), item =>
        {
            var id = item.GetProperty("id").GetGuid();
            var color = item.GetProperty("baseColor").GetString() ?? "#c8ccc7";
            if (color.Length != 7 || color[0] != '#') throw new InvalidDataException($"unsupported_geometry: invalid baseColor on material {id}.");
            var rgb = new Vector3(Convert.ToInt32(color[1..3],16), Convert.ToInt32(color[3..5],16), Convert.ToInt32(color[5..7],16)) / 255f;
            rgb = new Vector3(ToLinear(rgb.X), ToLinear(rgb.Y), ToLinear(rgb.Z));
            var builder = new MaterialBuilder($"{item.GetProperty("name").GetString() ?? "material"}:{id:D}")
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(rgb, 1))
                .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.MetallicFactor, item.GetProperty("metalness").GetSingle())
                .WithChannelParam(KnownChannel.MetallicRoughness, KnownProperty.RoughnessFactor, item.GetProperty("roughness").GetSingle());
            if (item.TryGetProperty("maps", out var maps) && maps.ValueKind == JsonValueKind.Object)
                AddTextureMaps(builder, maps, id, textureResolver);
            var tileValues = item.GetProperty("tileSizeMeters");
            var tile = new Vector2(tileValues[0].GetSingle(), tileValues[1].GetSingle());
            return new ExportMaterial(id, builder.Name, tile, builder);
        });

    private static void AddTextureMaps(MaterialBuilder material, JsonElement maps, Guid materialId, Func<string, byte[]?>? resolver)
    {
        var baseColor = ReadTexture("baseColor");
        var normal = ReadTexture("normal");
        var roughness = ReadTexture("roughness");
        var metalness = ReadTexture("metalness");
        if (baseColor is not null) material.UseChannel(KnownChannel.BaseColor).UseTexture().WithPrimaryImage(baseColor);
        if (normal is not null) material.UseChannel(KnownChannel.Normal).UseTexture().WithPrimaryImage(normal);
        if (roughness is not null || metalness is not null)
        {
            var packed = PackMetallicRoughness(roughness, metalness);
            material.UseChannel(KnownChannel.MetallicRoughness).UseTexture().WithPrimaryImage(packed);
        }

        byte[]? ReadTexture(string name)
        {
            if (!maps.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
            var path = value.GetString()!;
            const string prefix = "/api/assets/textures/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length != prefix.Length + 32)
                throw new InvalidDataException($"Texture map {name} on material {materialId} has an invalid asset reference.");
            if (resolver is null) throw new NotSupportedException($"texture_asset_unavailable: material {materialId} requires server-owned texture resolution.");
            var bytes = resolver(path[prefix.Length..]);
            if (bytes is null || bytes.Length is 0 or > 10 * 1024 * 1024)
                throw new NotSupportedException($"texture_asset_unavailable: texture {path[prefix.Length..]} for material {materialId} is unavailable or oversized.");
            try
            {
                using var decoded = new MagickImage(bytes);
                if (decoded.Width == 0 || decoded.Height == 0 || decoded.Width > 8192 || decoded.Height > 8192 ||
                    decoded.Width * decoded.Height > 40_000_000)
                    throw new InvalidDataException("Texture dimensions exceed the supported export limits.");
                decoded.Format = MagickFormat.Png;
                return decoded.ToByteArray();
            }
            catch (InvalidDataException) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidDataException($"Texture map {name} on material {materialId} could not be decoded.", exception);
            }
        }
    }

    private static byte[] PackMetallicRoughness(byte[]? roughnessBytes, byte[]? metalnessBytes)
    {
        using var roughness = DecodeGray(roughnessBytes);
        using var metalness = DecodeGray(metalnessBytes);
        var width = Math.Max(roughness?.Width ?? 1, metalness?.Width ?? 1);
        var height = Math.Max(roughness?.Height ?? 1, metalness?.Height ?? 1);
        if (roughness is not null && (roughness.Width != width || roughness.Height != height)) roughness.Resize(width, height);
        if (metalness is not null && (metalness.Width != width || metalness.Height != height)) metalness.Resize(width, height);
        var roughValues = GrayValues(roughness, width, height);
        var metalValues = GrayValues(metalness, width, height);
        var packed = new byte[checked((int)(width * height * 3))];
        for (var pixel = 0; pixel < checked((int)(width * height)); pixel++)
        {
            packed[pixel * 3] = 0; // Red is unused by glTF metallic-roughness.
            packed[pixel * 3 + 1] = roughValues?[pixel] ?? byte.MaxValue;
            packed[pixel * 3 + 2] = metalValues?[pixel] ?? byte.MaxValue;
        }
        using var image = new MagickImage(MagickColors.Black, (uint)width, (uint)height) { ColorSpace = ColorSpace.RGB };
        image.GetPixels().SetBytePixels(packed);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static MagickImage? DecodeGray(byte[]? bytes)
    {
        if (bytes is null) return null;
        var image = new MagickImage(bytes) { ColorSpace = ColorSpace.Gray };
        return image;
    }

    private static byte[]? GrayValues(MagickImage? image, uint width, uint height)
    {
        if (image is null) return null;
        if (image.Width != width || image.Height != height) throw new InvalidOperationException("PBR maps must be resized to a common resolution before channel packing.");
        var rgb = image.GetPixels()!.ToByteArray(PixelMapping.RGB)
            ?? throw new InvalidDataException("A grayscale PBR map could not be read.");
        var gray = new byte[checked((int)(width * height))];
        for (var index = 0; index < gray.Length; index++) gray[index] = rgb[index * 3];
        return gray;
    }

    private static MaterialBuilder DefaultMaterial() => new MaterialBuilder("RoomForge default").WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(.577f,.604f,.56f,1));
    private static Vector2 FaceDimensions(Vector3 d, string face) => face switch
    {
        "left" or "right" => new(d.Z, d.Y), "top" or "bottom" => new(d.X, d.Z), _ => new(d.X, d.Y),
    };
    private static Vector3 Vector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value.GetArrayLength() > 2 ? value[2].GetSingle() : 0);
    private static Quaternion QuaternionValue(JsonElement value) => Quaternion.Normalize(new(value[0].GetSingle(),value[1].GetSingle(),value[2].GetSingle(),value[3].GetSingle()));
    private static Vector2 ReadUv(JsonElement surface, string name, Vector2 fallback) => surface.ValueKind == JsonValueKind.Object && surface.TryGetProperty("uv", out var uv) && uv.TryGetProperty(name, out var values)
        ? new(values[0].GetSingle(), values[1].GetSingle()) : fallback;
    private static float ToLinear(float value) => value <= .04045f ? value / 12.92f : MathF.Pow((value + .055f) / 1.055f, 2.4f);
    private sealed record ExportMaterial(Guid Id, string Name, Vector2 TileSize, MaterialBuilder Builder);
}
