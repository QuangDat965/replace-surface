using ImageMagick;
using RoomForge.Domain.Reconstruction;

namespace RoomForge.Infrastructure.Reconstruction;

/// <summary>Converts a binary SAM mask into a bounded image-space silhouette polygon.</summary>
public static class FalMaskPolygonExtractor
{
    private const long MaxPixels = 40_000_000;

    public static ImagePolygon? Extract(ReadOnlySpan<byte> pngBytes, int imageWidth, int imageHeight, double[] normalizedBox)
    {
        if (normalizedBox.Length != 4 || normalizedBox.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new InvalidDataException("A valid normalized SAM mask box is required.");
        try
        {
            var bytes = pngBytes.ToArray();
            var info = new MagickImageInfo(bytes);
            if (info.Format != MagickFormat.Png || info.Width is 0 or > 8192 || info.Height is 0 or > 8192 || (long)info.Width * info.Height > MaxPixels)
                throw new InvalidDataException("SAM mask dimensions or format are unsupported.");
            using var mask = new MagickImage(bytes);
            var width = checked((int)info.Width);
            var height = checked((int)info.Height);
            var pixels = mask.GetPixels() ?? throw new InvalidDataException("SAM mask pixels could not be read.");
            var rgb = pixels.ToByteArray(0, 0, (uint)width, (uint)height, "RGB");
            if (rgb is null || rgb.Length != (long)width * height * 3) throw new InvalidDataException("SAM mask pixel extraction returned an unexpected buffer size.");

            var centerX = normalizedBox[0] * width;
            var centerY = normalizedBox[1] * height;
            var boxWidth = normalizedBox[2] * width;
            var boxHeight = normalizedBox[3] * height;
            var x0 = Math.Clamp((int)Math.Floor(centerX - boxWidth / 2), 0, width - 1);
            var x1 = Math.Clamp((int)Math.Ceiling(centerX + boxWidth / 2), x0 + 1, width);
            var y0 = Math.Clamp((int)Math.Floor(centerY - boxHeight / 2), 0, height - 1);
            var y1 = Math.Clamp((int)Math.Ceiling(centerY + boxHeight / 2), y0 + 1, height);

            long light = 0;
            long dark = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                if (IsLight(rgb, y * width + x)) light++; else dark++;
            }
            var cropArea = (long)(x1 - x0) * (y1 - y0);
            var targetIsLight = light <= dark;
            var targetCount = targetIsLight ? light : dark;
            if (targetCount < 4 || targetCount > cropArea * .995) return null;

            var left = new List<(int Y, int X)>();
            var right = new List<(int Y, int X)>();
            for (var y = y0; y < y1; y++)
            {
                var first = -1;
                var last = -1;
                var row = y * width;
                for (var x = x0; x < x1; x++)
                {
                    var isTarget = IsLight(rgb, row + x) == targetIsLight;
                    if (!isTarget) continue;
                    if (first < 0) first = x;
                    last = x;
                }
                if (first >= 0) { left.Add((y, first)); right.Add((y, last)); }
            }
            if (left.Count < 2) return null;

            // Sample the two silhouette edges; cap output well below the canonical 1,000-vertex limit.
            var stride = Math.Max(1, (int)Math.Ceiling(left.Count / 450d));
            var points = new List<ImagePoint>(Math.Min(900, left.Count * 2));
            for (var index = 0; index < left.Count; index += stride)
                points.Add(ToImagePoint(left[index], width, height, imageWidth, imageHeight));
            if ((left.Count - 1) % stride != 0) points.Add(ToImagePoint(left[^1], width, height, imageWidth, imageHeight));
            for (var index = right.Count - 1; index >= 0; index -= stride)
                points.Add(ToImagePoint(right[index], width, height, imageWidth, imageHeight));
            if (points.Count < 4) return null;
            return new ImagePolygon(RemoveAdjacentDuplicates(points));
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidDataException("SAM mask image was malformed or could not be decoded.", exception);
        }
    }

    private static ImagePoint ToImagePoint((int Y, int X) pixel, int maskWidth, int maskHeight, int imageWidth, int imageHeight) =>
        new(Math.Clamp((pixel.X + .5) * imageWidth / maskWidth, 0, imageWidth),
            Math.Clamp((pixel.Y + .5) * imageHeight / maskHeight, 0, imageHeight));

    private static bool IsLight(byte[] rgb, int pixelIndex)
    {
        var offset = pixelIndex * 3;
        return (rgb[offset] + rgb[offset + 1] + rgb[offset + 2]) >= 384;
    }

    private static IReadOnlyList<ImagePoint> RemoveAdjacentDuplicates(IReadOnlyList<ImagePoint> points)
    {
        var result = new List<ImagePoint>(points.Count);
        foreach (var point in points)
            if (result.Count == 0 || result[^1] != point) result.Add(point);
        if (result.Count > 1 && result[0] == result[^1]) result.RemoveAt(result.Count - 1);
        return result;
    }
}
