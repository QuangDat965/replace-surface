using System.Net;
using System.Text;
using RoomForge.Infrastructure.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class FalSam3QueueClientTests
{
    [Fact]
    public async Task SubmitUsesDocumentedFalQueueContractAndServerSideKey()
    {
        HttpMethod? capturedMethod = null;
        string? capturedUrl = null;
        string? capturedAuthorization = null;
        string? capturedBody = null;
        var handler = new StubHandler((request, _) =>
        {
            capturedMethod = request.Method;
            capturedUrl = request.RequestUri!.AbsoluteUri;
            capturedAuthorization = $"{request.Headers.Authorization!.Scheme} {request.Headers.Authorization.Parameter}";
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"}"""));
        });
        var client = Create(handler);

        var result = await client.SubmitAsync(new byte[] { 1, 2, 3 }, "image/png", "chair", CancellationToken.None);

        Assert.Equal(Guid.Parse("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"), result.RequestId);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("https://queue.fal.run/fal-ai/sam-3/image", capturedUrl);
        Assert.Equal("Key fixture-secret", capturedAuthorization);
        var json = capturedBody!;
        Assert.Contains("data:image/png;base64,AQID", json);
        Assert.Contains("\"prompt\":\"chair\"", json);
        Assert.Contains("\"apply_mask\":false", json);
    }

    [Fact]
    public async Task PollAndResultParseOnlyDocumentedMetadata()
    {
        var replies = new Queue<string>([
            """{"status":"COMPLETED","request_id":"019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b"}""",
            """{"masks":[{"url":"https://v3b.fal.media/mask.png","content_type":"image/png","file_size":120,"width":20,"height":10}],"scores":[0.93],"boxes":[[0.5,0.5,0.4,0.6]]}""",
        ]);
        var handler = new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, replies.Dequeue())));
        var client = Create(handler);
        var id = Guid.Parse("019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b");

        var status = await client.GetStatusAsync(id, CancellationToken.None);
        var result = await client.GetResultAsync(id, CancellationToken.None);

        Assert.Equal("COMPLETED", status.Status);
        Assert.Equal("https://queue.fal.run/fal-ai/sam-3/image/requests/019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b/status", handler.Requests[0]);
        Assert.Equal("https://queue.fal.run/fal-ai/sam-3/image/requests/019a1b2c-3d4e-7f80-9a1b-2c3d4e5f6a7b", handler.Requests[1]);
        Assert.Equal(120, result.Masks[0].FileSize);
        Assert.Equal(0.93, result.Scores[0]);
        Assert.Equal(4, result.Boxes[0]!.Length);
    }

    [Theory]
    [InlineData("image/gif", "chair")]
    [InlineData("image/png", " ")]
    public async Task RejectsInvalidInputBeforeNetwork(string declaredType, string prompt)
    {
        var handler = new StubHandler((_, _) => throw new InvalidOperationException("Network must not be called."));
        var client = Create(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => client.SubmitAsync(new byte[] { 1 }, declaredType, prompt, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task MissingKeyAndMalformedProviderContractsFailClosed()
    {
        var unconfigured = new FalSam3QueueClient(new HttpClient(new StubHandler((_, _) => throw new InvalidOperationException())), new FalSam3Options(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => unconfigured.SubmitAsync(new byte[] { 1 }, "image/png", "chair", CancellationToken.None));

        var malformed = Create(new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, """{"request_id":"not-a-guid"}"""))));
        await Assert.ThrowsAsync<InvalidDataException>(() => malformed.SubmitAsync(new byte[] { 1 }, "image/png", "chair", CancellationToken.None));
    }

    [Fact]
    public async Task RateLimitIsMarkedTransientWithoutLeakingResponseBody()
    {
        var client = Create(new StubHandler((_, _) => Task.FromResult(Json(HttpStatusCode.TooManyRequests, "secret prompt material"))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.SubmitAsync(new byte[] { 1 }, "image/png", "chair", CancellationToken.None));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        Assert.DoesNotContain("secret", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static FalSam3QueueClient Create(StubHandler handler) => new(new HttpClient(handler), new FalSam3Options("fixture-secret"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsoluteUri);
            return await responseFactory(request, cancellationToken);
        }
    }
}
