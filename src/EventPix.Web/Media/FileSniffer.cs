using EventPix.Web.Domain;

namespace EventPix.Web.Media;

internal readonly record struct SniffResult(MediaKind Kind, string ContentType);

/// <summary>
/// Identifies the real file type from magic bytes. The client-supplied Content-Type and extension are ignored.
/// Deliberately NOT supported: SVG/HTML (script risk). TODO: HEIC/HEIF (needs conversion for browsers).
/// </summary>
internal static class FileSniffer
{
    public static SniffResult? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length < 12)
        {
            return null;
        }

        if (h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return new(MediaKind.Photo, "image/jpeg");
        }

        // Byte array, not a "\x89PNG..."u8 literal: u8 holds UTF-8, so U+0089 would encode as C2 89 and never match.
        if (h[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return new(MediaKind.Photo, "image/png");
        }

        if (h[..4].SequenceEqual("GIF8"u8))
        {
            return new(MediaKind.Photo, "image/gif");
        }

        if (h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))
        {
            return new(MediaKind.Photo, "image/webp");
        }

        if (h[..4].SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }))
        {
            return new(MediaKind.Video, "video/webm");
        }

        if (h[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = h[8..12];
            if (brand.SequenceEqual("qt  "u8))
            {
                return new(MediaKind.Video, "video/quicktime");
            }

            if (brand.SequenceEqual("isom"u8) || brand.SequenceEqual("iso2"u8) || brand.SequenceEqual("mp41"u8) ||
                brand.SequenceEqual("mp42"u8) || brand.SequenceEqual("avc1"u8) || brand.SequenceEqual("M4V "u8))
            {
                return new(MediaKind.Video, "video/mp4");
            }
        }
        return null;
    }
}
