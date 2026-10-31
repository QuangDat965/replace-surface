namespace RoomForge.Infrastructure.Storage;

using ImageMagick;

public sealed class LocalAssetStore
{
    private readonly string root;
    private readonly string[] fallbackRoots;
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxImageDimension = 8192;
    private const long MaxImagePixels = 40_000_000;
    private static readonly (string ContentType, string Extension)[] ImageTypes =
    [
        ("image/png", ".png"), ("image/jpeg", ".jpg"), ("image/webp", ".webp"),
    ];

    public LocalAssetStore(string root, params string[] fallbackRoots)
    {
        this.root = root;
        this.fallbackRoots = fallbackRoots;
    }

    public async Task<string> SaveAsync(Stream source, long maxBytes, CancellationToken cancellationToken)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        Directory.CreateDirectory(root);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(root, id);
        try
        {
            await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes) throw new InvalidDataException("Asset exceeds the configured size limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            return id;
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    public async Task<(string Id, string ContentType)> SaveTextureAsync(Stream source, string declaredContentType, CancellationToken cancellationToken)
    {
        var type = ImageTypes.FirstOrDefault(item => item.ContentType.Equals(declaredContentType, StringComparison.OrdinalIgnoreCase));
        if (type == default) throw new InvalidDataException("Texture must be PNG, JPEG, or WebP.");
        await using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes) throw new InvalidDataException("Texture exceeds the 10 MB limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        buffer.Position = 0;
        ImageInfo? info;
        try
        {
            var bytes = buffer.ToArray();
            if (!HasImageSignature(bytes, type.ContentType))
                throw new InvalidDataException("Texture content does not match its PNG, JPEG, or WebP content type.");
            var imageInfo = new MagickImageInfo(bytes);
            if (imageInfo.Format != ContentTypeFormat(type.ContentType))
                throw new InvalidDataException("Texture content does not match its declared image type.");
            info = new ImageInfo(imageInfo.Width, imageInfo.Height);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("Texture is not a valid supported image.", exception);
        }

        var dimensions = info.Value;
        if (dimensions.Width == 0 || dimensions.Height == 0 || dimensions.Width > MaxImageDimension || dimensions.Height > MaxImageDimension ||
            (long)dimensions.Width * dimensions.Height > MaxImagePixels)
            throw new InvalidDataException("Texture dimensions exceed the supported limit of 8192 pixels per side and 40 megapixels.");

        buffer.Position = 0;
        try
        {
            using var decoded = new MagickImage(buffer.ToArray());
            if (decoded.Width != dimensions.Width || decoded.Height != dimensions.Height)
                throw new InvalidDataException("Texture dimensions changed during decoding.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("Texture image data is malformed or incomplete.", exception);
        }

        buffer.Position = 0;
        var id = await SaveAsync(buffer, MaxImageBytes, cancellationToken);
        var oldPath = Path.Combine(root, id);
        File.Move(oldPath, oldPath + type.Extension);
        return (id, type.ContentType);
    }

    public (Stream Content, string ContentType)? OpenTexture(string id)
    {
        if (id.Length != 32 || id.Any(character => !Uri.IsHexDigit(character))) return null;
        foreach (var directory in new[] { root }.Concat(fallbackRoots))
        foreach (var type in ImageTypes)
        {
            var path = Path.Combine(directory, id + type.Extension);
            if (File.Exists(path)) return (File.OpenRead(path), type.ContentType);
        }
        return null;
    }

    private static MagickFormat ContentTypeFormat(string contentType) => contentType switch
    {
        "image/png" => MagickFormat.Png,
        "image/jpeg" => MagickFormat.Jpeg,
        "image/webp" => MagickFormat.WebP,
        _ => MagickFormat.Unknown,
    };

    private static bool HasImageSignature(ReadOnlySpan<byte> bytes, string contentType) => contentType switch
    {
        "image/png" => bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff,
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };
    private readonly record struct ImageInfo(uint Width, uint Height);
}
