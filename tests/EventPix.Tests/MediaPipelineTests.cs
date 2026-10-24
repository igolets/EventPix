using System.Text;
using System.Text.Json;
using EventPix.Web.Media;
using SkiaSharp;
using Xunit;

namespace EventPix.Tests;

public sealed class MediaPipelineTests
{
    // ---- EXIF orientation ---------------------------------------------------------------------------------------

    [Fact]
    public void ImageVariants_WithoutExif_KeepsDimensions()
    {
        using var src = new MemoryStream(TestJpeg.Asymmetric());
        var result = ImageVariants.Create(src);
        Assert.NotNull(result);
        Assert.Equal((40, 20), (result.Value.Width, result.Value.Height));
    }

    [Theory]
    [InlineData(1, 40, 20, 10, 10, 30, 10)]   // TopLeft: identity
    [InlineData(2, 40, 20, 30, 10, 10, 10)]   // TopRight: mirror horizontally
    [InlineData(3, 40, 20, 30, 10, 10, 10)]   // BottomRight: rotate 180
    [InlineData(4, 40, 20, 10, 10, 30, 10)]   // BottomLeft: mirror vertically
    [InlineData(5, 20, 40, 10, 10, 10, 30)]   // LeftTop: transpose
    [InlineData(6, 20, 40, 10, 10, 10, 30)]   // RightTop: rotate 90 CW
    [InlineData(7, 20, 40, 10, 30, 10, 10)]   // RightBottom: transverse
    [InlineData(8, 20, 40, 10, 30, 10, 10)]   // LeftBottom: rotate 270 CW
    public void ImageVariants_Honours_Every_Exif_Origin(
        int origin, int w, int h, int redX, int redY, int blueX, int blueY)
    {
        using var src = new MemoryStream(TestJpeg.Asymmetric((ushort)origin));
        var result = ImageVariants.Create(src);
        Assert.NotNull(result);
        Assert.Equal((w, h), (result.Value.Width, result.Value.Height));
        AssertHalf(result.Value.Preview, redX, redY, red: true);
        AssertHalf(result.Value.Preview, blueX, blueY, red: false);
    }

    private static void AssertHalf(byte[] jpegBytes, int x, int y, bool red)
    {
        using var bmp = SKBitmap.Decode(jpegBytes);
        Assert.NotNull(bmp);
        var c = bmp.GetPixel(x, y);
        if (red)
        {
            Assert.True(c.Red > 150 && c.Blue < 100, $"Expected red at ({x},{y}), got {c}");
        }
        else
        {
            Assert.True(c.Blue > 150 && c.Red < 100, $"Expected blue at ({x},{y}), got {c}");
        }
    }

    // ---- EXIF capture time ----------------------------------------------------------------------------------------

    [Fact]
    public void ExifReader_AppliesOffsetTime()
    {
        using var src = new MemoryStream(TestJpeg.Asymmetric(taken: "2026:05:17 14:30:00", offset: "+02:00"));
        Assert.Equal(new DateTimeOffset(2026, 5, 17, 12, 30, 0, TimeSpan.Zero), ExifReader.ReadTakenUtc(src));
    }

    [Fact]
    public void ExifReader_WithoutOffset_TreatsWallTimeAsUtc()
    {
        using var src = new MemoryStream(TestJpeg.Asymmetric(taken: "2026:05:17 14:30:00"));
        Assert.Equal(new DateTimeOffset(2026, 5, 17, 14, 30, 0, TimeSpan.Zero), ExifReader.ReadTakenUtc(src));
    }

    [Fact]
    public void ExifReader_NoExif_ReturnsNull()
    {
        using var plain = new MemoryStream(TestJpeg.Asymmetric());
        Assert.Null(ExifReader.ReadTakenUtc(plain));

        using var notJpeg = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Assert.Null(ExifReader.ReadTakenUtc(notJpeg));
    }

    [Fact]
    public void ExifReader_RejectsOutOfBoundsExifPointers()
    {
        // Orientation is set, but the Exif IFD pointer targets beyond the segment: parser must not throw or follow it.
        using var src = new MemoryStream(TestJpeg.Asymmetric(bogusExifPointer: true));
        Assert.Null(ExifReader.ReadTakenUtc(src));
    }

    [Fact]
    public void ExifReader_Reads_BigEndian_Tiff()
    {
        using var src = new MemoryStream(TestJpeg.Asymmetric(6, "2026:05:17 14:30:00", "+02:00", bigEndian: true));
        Assert.Equal(new DateTimeOffset(2026, 5, 17, 12, 30, 0, TimeSpan.Zero), ExifReader.ReadTakenUtc(src));
    }

    [Fact]
    public void ExifReader_ParseOffset_Handles_Both_Signs_And_Garbage()
    {
        Assert.Equal(new TimeSpan(2, 0, 0), ExifReader.ParseOffset("+02:00"));
        Assert.Equal(new TimeSpan(-7, -30, 0), ExifReader.ParseOffset("-07:30"));
        Assert.Equal(TimeSpan.Zero, ExifReader.ParseOffset(null));
        Assert.Equal(TimeSpan.Zero, ExifReader.ParseOffset("junk"));
        Assert.Equal(TimeSpan.Zero, ExifReader.ParseOffset("+99:99"));
    }

    // ---- ffprobe JSON ---------------------------------------------------------------------------------------------

    [Fact]
    public void VideoProbe_Parse_ReadsDurationAndCreationTime()
    {
        var info = VideoProbe.Parse(
            """{"format":{"duration":"91.512000","tags":{"creation_time":"2024-05-01T10:20:30.000000Z"}},"streams":[]}""");
        Assert.Equal(TimeSpan.FromSeconds(91.512), info.Duration);
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 20, 30, TimeSpan.Zero), info.CreatedUtc);
    }

    [Fact]
    public void VideoProbe_Parse_ConvertsOffsetTimestampsToUtc()
    {
        var info = VideoProbe.Parse(
            """{"format":{"tags":{"creation_time":"2024-05-01T12:20:30+02:00"}}}""");
        Assert.Equal(new DateTimeOffset(2024, 5, 1, 10, 20, 30, TimeSpan.Zero), info.CreatedUtc);
    }

    [Fact]
    public void VideoProbe_Parse_ToleratesGarbageAndAbsence()
    {
        Assert.Equal(default, VideoProbe.Parse("{}"));
        Assert.Equal(default, VideoProbe.Parse("not json at all"));
        Assert.Equal(default, VideoProbe.Parse("""{"format":{"duration":"abc"}}"""));
    }

    [Fact]
    public async Task VideoProbe_ProbeAsync_Disabled_ReturnsNull()
    {
        var probe = new VideoProbe(
            Microsoft.Extensions.Options.Options.Create(new EventPix.Web.Configuration.EventPixOptions { FfprobePath = "" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoProbe>.Instance);
        Assert.Null(await probe.ProbeAsync("clip.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task VideoProbe_ProbeAsync_Missing_Tool_ReturnsNull()
    {
        var probe = new VideoProbe(
            Microsoft.Extensions.Options.Options.Create(
                new EventPix.Web.Configuration.EventPixOptions { FfprobePath = "definitely-not-a-real-tool-xyz" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoProbe>.Instance);
        Assert.Null(await probe.ProbeAsync("clip.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task VideoFrameExtractor_Disabled_ReturnsNull()
    {
        var extractor = new VideoFrameExtractor(
            Microsoft.Extensions.Options.Options.Create(new EventPix.Web.Configuration.EventPixOptions { FfmpegPath = "" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoFrameExtractor>.Instance);
        Assert.Null(await extractor.ExtractAsync("clip.mp4", CancellationToken.None));
    }

    [Fact]
    public async Task VideoFrameExtractor_Missing_Tool_ReturnsNull()
    {
        var extractor = new VideoFrameExtractor(
            Microsoft.Extensions.Options.Options.Create(
                new EventPix.Web.Configuration.EventPixOptions { FfmpegPath = "definitely-not-a-real-tool-xyz" }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<VideoFrameExtractor>.Instance);
        Assert.Null(await extractor.ExtractAsync("clip.mp4", CancellationToken.None));
    }
}

/// <summary>Builds small test JPEGs (40x20, red left / blue right) with a hand-crafted EXIF APP1 segment.</summary>
internal static class TestJpeg
{
    public static byte[] Asymmetric(ushort orientation = 1, string? taken = null, string? offset = null,
        bool bogusExifPointer = false, bool bigEndian = false)
    {
        using var bmp = new SKBitmap(40, 20);
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(SKColors.Blue);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, 20, 20, paint);
        }

        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        return WithExif(data.ToArray(), orientation, taken, offset, bogusExifPointer, bigEndian);
    }

    public static byte[] WithExif(byte[] jpeg, ushort orientation, string? taken, string? offset,
        bool bogusExifPointer = false, bool bigEndian = false)
    {
        var tiff = BuildTiff(orientation, taken, offset, bogusExifPointer, bigEndian);
        var app1 = new List<byte> { 0xFF, 0xE1 };
        var len = 2 + 6 + tiff.Count;   // segment length counts the length bytes themselves
        app1.Add((byte)(len >> 8));
        app1.Add((byte)len);
        app1.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
        app1.AddRange(tiff);

        var result = new byte[2 + app1.Count + jpeg.Length - 2];
        result[0] = jpeg[0];
        result[1] = jpeg[1];
        app1.CopyTo(result, 2);
        Array.Copy(jpeg, 2, result, 2 + app1.Count, jpeg.Length - 2);
        return result;
    }

    private static List<byte> BuildTiff(ushort orientation, string? taken, string? offset, bool bogusExifPointer,
        bool bigEndian)
    {
        var tiff = new List<byte>();
        void U16(int v)
        {
            if (bigEndian) { tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); }
            else { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); }
        }

        void U32(int v)
        {
            if (bigEndian) { U16(v >> 16); U16(v & 0xFFFF); }
            else { U16(v & 0xFFFF); U16(v >> 16); }
        }

        void Entry(int tag, int type, int count, int value)
        {
            U16(tag);
            U16(type);
            U32(count);
            // A SHORT value sits in the first two bytes of the value field in big-endian files.
            U32(bigEndian && type == 3 ? value << 16 : value);
        }

        var hasTimes = taken is not null;
        var ifd0Size = 2 + (hasTimes ? 2 : 1) * 12 + 4;
        var dataPos = 8 + ifd0Size;
        var takenBytes = hasTimes ? Encoding.ASCII.GetBytes(taken + "\0") : Array.Empty<byte>();
        var offsetBytes = hasTimes ? Encoding.ASCII.GetBytes(offset + "\0") : Array.Empty<byte>();
        var exifIfdPos = dataPos + takenBytes.Length + offsetBytes.Length;

        tiff.AddRange(bigEndian ? [(byte)'M', (byte)'M'] : [(byte)'I', (byte)'I']);
        U16(42);
        U32(8);                                                      // IFD0 offset

        U16(hasTimes ? 2 : 1);
        Entry(0x0112, 3, 1, orientation);                            // orientation, SHORT, fits inline
        if (hasTimes)
        {
            Entry(0x8769, 4, 1, bogusExifPointer ? 0x7FFFFFFF : exifIfdPos);   // pointer to the Exif IFD
        }

        U32(0);                                                      // no next IFD

        if (hasTimes)
        {
            var takenPos = dataPos;
            var offsetPos = dataPos + takenBytes.Length;
            tiff.AddRange(takenBytes);
            tiff.AddRange(offsetBytes);

            U16(2);
            Entry(0x9003, 2, takenBytes.Length, takenPos);           // DateTimeOriginal, ASCII
            Entry(0x9010, 2, offsetBytes.Length, offsetPos);         // OffsetTimeOriginal, ASCII
            U32(0);
        }

        return tiff;
    }
}
