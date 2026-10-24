using SkiaSharp;

namespace EventPix.Web.Media;

internal static class ImageVariants
{
    private const long MaxPixels = 100_000_000;   // decompression-bomb guard

    internal readonly record struct Result(int Width, int Height, byte[] Thumb, byte[] Preview);

    public static Result? Create(Stream input)
    {
        using var codec = SKCodec.Create(input);
        if ((long)codec.Info.Width * codec.Info.Height > MaxPixels)
        {
            return null;
        }

        using var bmp = SKBitmap.Decode(codec);
        if (bmp is null)
        {
            return null;
        }

        // EXIF orientation is honoured before resizing; JPEG re-encoding strips it, so previews
        // are stored upright and the dimensions reported to clients are the DISPLAYED ones.
        if (codec.EncodedOrigin == SKEncodedOrigin.TopLeft)
        {
            return Render(bmp);
        }

        using var oriented = Orient(bmp, codec.EncodedOrigin);
        return Render(oriented);
    }

    private static Result Render(SKBitmap bmp) =>
        new(bmp.Width, bmp.Height, Encode(bmp, 480, 78), Encode(bmp, 1920, 85));

    /// <summary>Draws the source through the EXIF transform into an upright bitmap (swapped size for quarter turns).</summary>
    private static SKBitmap Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        var swapped = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swapped ? src.Height : src.Width, swapped ? src.Width : src.Height);
        using var canvas = new SKCanvas(dst);
        canvas.SetMatrix(MatrixFor(origin, src.Width, src.Height));
        canvas.DrawBitmap(src, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));   // exact integer transform, no resampling
        return dst;
    }

    /// <summary>
    /// Maps source pixel (x, y) onto an upright canvas; (w, h) are the SOURCE dimensions.
    /// SkiaSharp's 9-argument constructor takes (scaleX, skewX, transX, skewY, scaleY, transY, persp*).
    /// </summary>
    internal static SKMatrix MatrixFor(SKEncodedOrigin origin, int w, int h) => origin switch
    {
        SKEncodedOrigin.TopRight => new(-1, 0, w, 0, 1, 0, 0, 0, 1),      // mirror horizontally
        SKEncodedOrigin.BottomRight => new(-1, 0, w, 0, -1, h, 0, 0, 1),  // rotate 180
        SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, h, 0, 0, 1),    // mirror vertically
        SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),        // transpose
        SKEncodedOrigin.RightTop => new(0, -1, h, 1, 0, 0, 0, 0, 1),      // rotate 90 CW
        SKEncodedOrigin.RightBottom => new(0, -1, h, -1, 0, w, 0, 0, 1),  // transverse
        SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, w, 0, 0, 1),    // rotate 270 CW
        _ => SKMatrix.CreateIdentity(),
    };

    private static byte[] Encode(SKBitmap src, int maxEdge, int quality)
    {
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(src.Width, src.Height));
        var w = Math.Max(1, (int)(src.Width * scale));
        var h = Math.Max(1, (int)(src.Height * scale));
        using var resized = scale < 1.0
            ? src.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : src.Copy();
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);   // re-encoding strips EXIF/GPS
        return data.ToArray();
    }
}
