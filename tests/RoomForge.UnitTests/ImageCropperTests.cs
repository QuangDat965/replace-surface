using ImageMagick;
using RoomForge.Application.Reconstruction;
using RoomForge.Infrastructure.Reconstruction;

namespace RoomForge.UnitTests;

public sealed class ImageCropperTests
{
    [Fact]
    public void CropsOnlyRequestedBoundsAndEmitsPng()
    {
        using var source = new MagickImage(MagickColors.Red, 64, 48);
        var bytes = source.ToByteArray(MagickFormat.Jpeg);

        var cropped = ImageCropper.CropToPng(bytes, 64, 48, new PixelCrop(12, 9, 32, 24));
        using var result = new MagickImage(cropped);

        Assert.Equal(MagickFormat.Png, result.Format);
        Assert.Equal(32u, result.Width);
        Assert.Equal(24u, result.Height);
    }

    [Theory]
    [InlineData(-1, 0, 20, 20)]
    [InlineData(0, 0, 15, 20)]
    [InlineData(50, 30, 20, 20)]
    public void RejectsCropOutsideImageOrBelowMinimum(int x, int y, int width, int height) =>
        Assert.Throws<InvalidDataException>(() => ImageCropper.CropToPng(new byte[] { 0 }, 64, 48, new PixelCrop(x, y, width, height)));
}
