using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SharpGLTF.Schema2;

namespace RoomForge.Infrastructure.Reconstruction;

public sealed record FalHunyuan3DOptions(string? ApiKey, bool Enabled = false, bool DataTransferApproved = false)
{
    public bool IsConfigured => Enabled && DataTransferApproved && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed record FalHunyuanSubmission(Guid RequestId);
public sealed record FalHunyuanStatus(string Status, string? Error);
public sealed record FalHunyuanAsset(Uri Url, long FileSize);

/// <summary>Typed fal Hunyuan3D v3 queue client. It performs transport and asset validation only.</summary>
public sealed class FalHunyuan3DQueueClient(HttpClient httpClient, FalHunyuan3DOptions options)
{
    public const string ModelId = "fal-ai/hunyuan3d-v3/image-to-3d";
    public bool IsConfigured => options.IsConfigured;
    private const string QueueRoot = "https://queue.fal.run/" + ModelId;
    private const int MaxInputBytes = 10 * 1024 * 1024;
    private const int MaxOutputBytes = 80 * 1024 * 1024;

    public async Task<FalHunyuanSubmission> SubmitGeometryAsync(ReadOnlyMemory<byte> croppedImage, string contentType,
        CancellationToken cancellationToken)
    {
        if (!options.IsConfigured) throw new InvalidOperationException("fal Hunyuan3D is not configured.");
        if (croppedImage.IsEmpty || croppedImage.Length > MaxInputBytes || contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new InvalidDataException("Hunyuan3D crop must be a supported image no larger than 10 MB.");
        var base64 = Convert.ToBase64String(croppedImage.Span);
        var body = JsonSerializer.Serialize(new
        {
            input_image_url = $"data:{contentType};base64,{base64}",
            enable_safety_checker = true,
            enable_pbr = false,
            generate_type = "Geometry",
        });
        using var request = Authorized(HttpMethod.Post, QueueRoot);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await ReadJsonAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
        if (!json.RootElement.TryGetProperty("request_id", out var token) || !Guid.TryParse(token.GetString(), out var id))
            throw new InvalidDataException("fal Hunyuan3D did not return a valid request ID.");
        return new FalHunyuanSubmission(id);
    }

    public async Task<FalHunyuanStatus> GetStatusAsync(Guid requestId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, $"{QueueRoot}/requests/{requestId:D}/status");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await ReadJsonAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
        var status = json.RootElement.TryGetProperty("status", out var token) ? token.GetString() : null;
        if (status is not ("IN_QUEUE" or "IN_PROGRESS" or "COMPLETED")) throw new InvalidDataException("fal Hunyuan3D returned an unknown queue state.");
        var error = json.RootElement.TryGetProperty("error", out var errorToken) ? errorToken.GetString() : null;
        return new FalHunyuanStatus(status, error);
    }

    public async Task<FalHunyuanAsset> GetResultAsync(Guid requestId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, $"{QueueRoot}/requests/{requestId:D}");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = await ReadJsonAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken);
        if (!json.RootElement.TryGetProperty("model_glb", out var asset) || asset.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("fal Hunyuan3D result did not contain model_glb.");
        var url = asset.TryGetProperty("url", out var urlToken) ? urlToken.GetString() : null;
        var contentType = asset.TryGetProperty("content_type", out var typeToken) ? typeToken.GetString() : null;
        var size = asset.TryGetProperty("file_size", out var sizeToken) && sizeToken.TryGetInt64(out var parsed) ? parsed : 0;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("fal.media", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".fal.media", StringComparison.OrdinalIgnoreCase)) ||
            contentType != "model/gltf-binary" || size is <= 0 or > MaxOutputBytes)
            throw new InvalidDataException("fal Hunyuan3D result asset has an unsupported host, format or size.");
        return new FalHunyuanAsset(uri, size);
    }

    public async Task<byte[]> DownloadAndValidateGlbAsync(FalHunyuanAsset asset, CancellationToken cancellationToken)
    {
        if (asset.Url.Scheme != Uri.UriSchemeHttps ||
            !(asset.Url.Host.Equals("fal.media", StringComparison.OrdinalIgnoreCase) || asset.Url.Host.EndsWith(".fal.media", StringComparison.OrdinalIgnoreCase)) ||
            asset.FileSize is <= 0 or > MaxOutputBytes)
            throw new InvalidDataException("Hunyuan3D asset URL or size is not allowed.");
        using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("Hunyuan3D asset downloads may not redirect to another host.");
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "model/gltf-binary" ||
            response.Content.Headers.ContentLength is > MaxOutputBytes)
            throw new InvalidDataException("Hunyuan3D asset response is not a supported bounded GLB.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxOutputBytes) throw new InvalidDataException("Hunyuan3D GLB exceeds the 80 MB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length != asset.FileSize || bytes.Length < 12 || !bytes.AsSpan(0, 4).SequenceEqual("glTF"u8) ||
            BitConverter.ToUInt32(bytes, 4) != 2 || BitConverter.ToUInt32(bytes, 8) != bytes.Length)
            throw new InvalidDataException("Hunyuan3D returned a malformed or truncated GLB container.");
        try
        {
            using var glbStream = new MemoryStream(bytes, writable: false);
            _ = ModelRoot.ReadGLB(glbStream);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("Hunyuan3D returned a GLB that cannot be parsed.", exception);
        }
        return bytes;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Key", options.ApiKey ?? throw new InvalidOperationException("fal Hunyuan3D is not configured."));
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            throw new HttpRequestException("fal Hunyuan3D returned a transient error.", null, response.StatusCode);
        throw new InvalidDataException($"fal Hunyuan3D rejected the request (HTTP {(int)response.StatusCode}).");
    }

    private static async Task<JsonDocument> ReadJsonAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 1024 * 1024) throw new InvalidDataException("fal Hunyuan3D metadata exceeded 1 MB.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken); }
        catch (JsonException exception) { throw new InvalidDataException("fal Hunyuan3D returned malformed JSON.", exception); }
    }
}
