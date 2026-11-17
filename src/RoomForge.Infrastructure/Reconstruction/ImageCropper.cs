using ImageMagick;
using RoomForge.Application.Reconstruction;

namespace RoomForge.Infrastructure.Reconstruction;

public static class ImageCropper
{
    private const int MaxCropBytes = 10 * 1024 * 1024;
    private const long MaxCropPixels = 20_000_000;

    public static byte[] CropToPng(ReadOnlySpan<byte> sourceBytes, int sourceWidth, int sourceHeight, PixelCrop crop)
    {
        if (sourceWidth is < 1 or > 8192 || sourceHeight is < 1 or > 8192 ||
            crop.X < 0 || crop.Y < 0 || crop.Width < 16 || crop.Height < 16 ||
            crop.X + (long)crop.Width > sourceWidth || crop.Y + (long)crop.Height > sourceHeight ||
            (long)crop.Width * crop.Height > MaxCropPixels)
            throw new InvalidDataException("Crop must be at least 16×16 pixels and stay inside the source image.");
        try
        {
            using var image = new MagickImage(sourceBytes.ToArray());
            if (image.Width != sourceWidth || image.Height != sourceHeight)
                throw new InvalidDataException("Source image dimensions did not match its saved metadata.");
            image.Crop(new MagickGeometry(crop.X, crop.Y, (uint)crop.Width, (uint)crop.Height));
            image.Format = MagickFormat.Png;
            var result = image.ToByteArray();
            if (result.Length > MaxCropBytes) throw new InvalidDataException("Cropped PNG exceeds the 10 MB provider input limit.");
            return result;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception exception)
        {
            throw new InvalidDataException("Source image could not be cropped safely.", exception);
        }
    }
}
