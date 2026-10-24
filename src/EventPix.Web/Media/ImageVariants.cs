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
        // TODO(orientation): honour codec.EncodedOrigin (EXIF rotation) before resizing.
        using var bmp = SKBitmap.Decode(codec);
        if (bmp is null)
        {
            return null;
        }

        return new Result(bmp.Width, bmp.Height, Encode(bmp, 480, 78), Encode(bmp, 1920, 85));
    }

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
