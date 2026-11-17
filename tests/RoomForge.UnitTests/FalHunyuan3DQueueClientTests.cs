using System.Net;
using System.Text;
using System.Text.Json;
using RoomForge.Infrastructure.Export;
using RoomForge.Infrastructure.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class FalHunyuan3DQueueClientTests
{
    private static readonly Guid RequestId = Guid.Parse("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b");

    [Fact]
    public async Task SubmitUsesDocumentedGeometryOnlyQueueContractAndServerKey()
    {
        string? body = null;
        string? authorization = null;
        var handler = new StubHandler((request, _) =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(Json(HttpStatusCode.OK, $$"""{"request_id":"{{RequestId:D}}"}"""));
        });
        var client = Create(handler);

        var result = await client.SubmitGeometryAsync(new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

        Assert.Equal(RequestId, result.RequestId);
        Assert.Equal("https://queue.fal.run/fal-ai/hunyuan3d-v3/image-to-3d", handler.Requests.Single());
        Assert.Equal("Key fixture-secret", authorization);
        using var payload = JsonDocument.Parse(body!);
        var input = payload.RootElement;
        Assert.Equal("data:image/png;base64,AQID", input.GetProperty("input_image_url").GetString());
        Assert.Equal("Geometry", input.GetProperty("generate_type").GetString());
        Assert.False(input.GetProperty("enable_pbr").GetBoolean());
        Assert.True(input.GetProperty("enable_safety_checker").GetBoolean());
        Assert.False(input.TryGetProperty("face_count", out _));
    }

    [Fact]
    public async Task PollsAndValidatesModelGlbFromTrustedHost()
    {
        var glb = SampleGlb();
        var replies = new Queue<HttpResponseMessage>([
            Json(HttpStatusCode.OK, """{"status":"COMPLETED"}"""),
            Json(HttpStatusCode.OK, """{"model_glb":{"url":"https://v3b.fal.media/files/generated.glb","content_type":"model/gltf-binary","file_size":FILESIZE}}""".Replace("FILESIZE", glb.Length.ToString(), StringComparison.Ordinal)),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(glb),
            },
        ]);
        replies.Last().Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("model/gltf-binary");
        var handler = new StubHandler((_, _) => Task.FromResult(replies.Dequeue()));
        var client = Create(handler);

        Assert.Equal("COMPLETED", (await client.GetStatusAsync(RequestId, CancellationToken.None)).Status);
        var asset = await client.GetResultAsync(RequestId, CancellationToken.None);
        var downloaded = await client.DownloadAndValidateGlbAsync(asset, CancellationToken.None);

        Assert.Equal(glb, downloaded);
        Assert.Contains($"/requests/{RequestId:D}/status", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains($"/requests/{RequestId:D}", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsUnexpectedAssetHostAndInvalidInputBeforeNetwork()
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Network should not be called."));
        var client = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SubmitGeometryAsync(new byte[] { 1 }, "image/gif", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAndValidateGlbAsync(
            new FalHunyuanAsset(new Uri("https://attacker.example/asset.glb"), 100), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    private static byte[] SampleGlb()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../examples/sample-scene.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new SharpGltfSceneExporter().Export(document.RootElement);
    }

    private static FalHunyuan3DQueueClient Create(StubHandler handler) =>
        new(new HttpClient(handler), new FalHunyuan3DOptions("fixture-secret", Enabled: true, DataTransferApproved: true));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> factory) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return await factory(request, cancellationToken);
        }
    }
}
