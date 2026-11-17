using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RoomForge.Infrastructure.Reconstruction;

/// <summary>
/// Thin, provider-specific SAM 3 queue client. This is deliberately not selected by the
/// reconstruction worker until the durable job submission/reconciliation workflow is wired.
/// </summary>
public sealed class FalSam3Options(string? apiKey)
{
    public string? ApiKey { get; } = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
    public bool IsConfigured => ApiKey is not null;
}

public sealed record FalQueueSubmission(Guid RequestId);
public sealed record FalQueueStatus(string Status, string? Error);
public sealed record FalImageResult(string Url, string? ContentType, long? FileSize, int? Width, int? Height);
public sealed record FalSam3Result(IReadOnlyList<FalImageResult> Masks, IReadOnlyList<double?> Scores, IReadOnlyList<double[]?> Boxes);

public sealed class FalSam3QueueClient(HttpClient httpClient, FalSam3Options options)
{
    private const string Endpoint = "https://queue.fal.run/fal-ai/sam-3/image";
    private const int MaxResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsConfigured => options.IsConfigured;

    public async Task<FalQueueSubmission> SubmitAsync(ReadOnlyMemory<byte> image, string contentType, string prompt, CancellationToken cancellationToken)
    {
        var key = options.ApiKey ?? throw new InvalidOperationException("fal SAM 3 is not configured. Set FAL_KEY in the server environment or User Secrets.");
        if (image.IsEmpty || image.Length > 10 * 1024 * 1024) throw new InvalidDataException("SAM 3 image input must be between 1 byte and 10 MB.");
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp")) throw new InvalidDataException("SAM 3 supports only validated PNG, JPEG, or WebP assets.");
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 300) throw new InvalidDataException("SAM 3 prompt must contain 1 to 300 characters.");

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
        var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(image.Span)}";
        request.Content = JsonContent.Create(new
        {
            image_url = dataUri,
            prompt,
            apply_mask = false,
            include_scores = true,
            include_boxes = true,
            return_multiple_masks = true,
            max_masks = 3,
        }, options: JsonOptions);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await ReadJsonAsync(body, cancellationToken);
        if (!json.RootElement.TryGetProperty("request_id", out var idToken) || !Guid.TryParse(idToken.GetString(), out var id))
            throw new InvalidDataException("fal queue submission response did not contain a valid request_id.");
        return new FalQueueSubmission(id);
    }

    public async Task<FalQueueStatus> GetStatusAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A valid fal request ID is required.", nameof(requestId));
        using var request = CreateAuthorizedRequest(HttpMethod.Get, $"{Endpoint}/requests/{requestId:D}/status");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await ReadJsonAsync(body, cancellationToken);
        var status = json.RootElement.TryGetProperty("status", out var token) ? token.GetString() : null;
        if (status is not ("IN_QUEUE" or "IN_PROGRESS" or "COMPLETED"))
            throw new InvalidDataException("fal queue status response contained an unsupported state.");
        var error = json.RootElement.TryGetProperty("error", out var errorToken) ? errorToken.GetString() : null;
        return new FalQueueStatus(status, error);
    }

    public async Task<FalSam3Result> GetResultAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A valid fal request ID is required.", nameof(requestId));
        using var request = CreateAuthorizedRequest(HttpMethod.Get, $"{Endpoint}/requests/{requestId:D}");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await ReadJsonAsync(body, cancellationToken);
        if (!json.RootElement.TryGetProperty("masks", out var masksToken) || masksToken.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("fal SAM 3 result did not contain a masks array.");
        var masks = masksToken.EnumerateArray().Select(mask =>
        {
            var url = mask.TryGetProperty("url", out var urlToken) ? urlToken.GetString() : null;
            var type = mask.TryGetProperty("content_type", out var typeToken) ? typeToken.GetString() : null;
            var size = mask.TryGetProperty("file_size", out var sizeToken) && sizeToken.TryGetInt64(out var parsedSize) ? parsedSize : (long?)null;
            var width = mask.TryGetProperty("width", out var widthToken) && widthToken.TryGetInt32(out var parsedWidth) ? parsedWidth : (int?)null;
            var height = mask.TryGetProperty("height", out var heightToken) && heightToken.TryGetInt32(out var parsedHeight) ? parsedHeight : (int?)null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) || parsedUrl.Scheme != Uri.UriSchemeHttps ||
                !(parsedUrl.Host.Equals("fal.media", StringComparison.OrdinalIgnoreCase) || parsedUrl.Host.EndsWith(".fal.media", StringComparison.OrdinalIgnoreCase)) ||
                type != "image/png" || size is <= 0 or > 10 * 1024 * 1024 || width is <= 0 or > 8192 || height is <= 0 or > 8192)
                throw new InvalidDataException("fal SAM 3 result contained a mask outside the supported host, format, size, or dimensions.");
            return new FalImageResult(parsedUrl.AbsoluteUri, type, size, width, height);
        }).ToArray();
        var scores = ReadNumbers(json.RootElement, "scores");
        var boxes = ReadBoxes(json.RootElement, "boxes");
        if (masks.Length is < 1 or > 3 || scores.Length > masks.Length || boxes.Length > masks.Length)
            throw new InvalidDataException("fal SAM 3 returned an unsupported number of masks, scores, or boxes.");
        return new FalSam3Result(masks, scores, boxes);
    }

    public async Task<byte[]> DownloadMaskAsync(FalImageResult mask, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(mask.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("fal.media", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".fal.media", StringComparison.OrdinalIgnoreCase)) ||
            mask.ContentType != "image/png" || mask.FileSize is <= 0 or > 10 * 1024 * 1024)
            throw new InvalidDataException("fal mask download is outside the supported host, format, or size.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is >= HttpStatusCode.MovedPermanently and <= HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            throw new InvalidDataException("fal mask downloads may not redirect to another host.");
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("fal mask download failed.", null, response.StatusCode);
        if (response.Content.Headers.ContentType?.MediaType != "image/png") throw new InvalidDataException("fal mask response was not a PNG image.");
        if (response.Content.Headers.ContentLength is > 10 * 1024 * 1024) throw new InvalidDataException("fal mask exceeds the 10 MB limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > 10 * 1024 * 1024) throw new InvalidDataException("fal mask exceeds the 10 MB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        return buffer.ToArray();
    }

    private HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string url)
    {
        var key = options.ApiKey ?? throw new InvalidOperationException("fal SAM 3 is not configured.");
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Key", key);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests || (int)status >= 500)
            throw new HttpRequestException("fal SAM 3 returned a transient error.", null, status);
        // Do not retain response details which may contain user prompts or signed URLs.
        throw new InvalidDataException($"fal SAM 3 rejected the request (HTTP {(int)status}).");
    }

    private static async Task<JsonDocument> ReadJsonAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new InvalidDataException("fal SAM 3 response exceeds the 1 MB metadata limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        try { return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken); }
        catch (JsonException exception) { throw new InvalidDataException("fal SAM 3 returned malformed JSON.", exception); }
    }

    private static double?[] ReadNumbers(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray().Select(item =>
        {
            if (!item.TryGetDouble(out var value) || !double.IsFinite(value) || value is < 0 or > 1)
                throw new InvalidDataException($"fal SAM 3 returned an invalid {name} value.");
            return (double?)value;
        }).ToArray()
        : [];

    private static double[]?[] ReadBoxes(JsonElement root, string name) => root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 4) return null;
            var values = item.EnumerateArray().Select(value => value.TryGetDouble(out var number) ? number : double.NaN).ToArray();
            return values.All(value => double.IsFinite(value) && value is >= 0 and <= 1) ? values : null;
        }).ToArray()
        : [];
}
