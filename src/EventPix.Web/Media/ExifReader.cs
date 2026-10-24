using System.Globalization;
using System.Text;

namespace EventPix.Web.Media;

/// <summary>
/// Minimal read-only EXIF parser for JPEG (APP1/TIFF). Extracts only the capture time —
/// orientation comes from SKCodec. Never follows unbounded offsets or counts.
/// </summary>
internal static class ExifReader
{
    internal sealed record ExifData(int? Orientation, string? DateTimeOriginal, string? OffsetTimeOriginal, string? DateTimeDigitized);

    private readonly record struct ExifEntry(ushort Tag, ushort Type, uint Count, int ValuePos);

    private const int MaxHeadBytes = 512 * 1024;   // EXIF must live in the leading segments

    /// <summary>DateTimeOriginal (falling back to DateTimeDigitized) converted to UTC. Null when absent/unreadable.</summary>
    public static DateTimeOffset? ReadTakenUtc(Stream jpeg)
    {
        var head = new byte[MaxHeadBytes];   // EXIF must live in the leading segments; photos are far larger anyway
        var total = 0;
        while (total < head.Length)
        {
            var read = jpeg.Read(head.AsSpan(total));
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return ReadTakenUtc(head.AsSpan(0, total));
    }

    internal static DateTimeOffset? ReadTakenUtc(ReadOnlySpan<byte> head) => Read(head) is { } exif
        ? ParseTakenUtc(exif.DateTimeOriginal ?? exif.DateTimeDigitized, exif.OffsetTimeOriginal)
        : null;

    internal static ExifData? Read(ReadOnlySpan<byte> head)
    {
        if (head.Length < 4 || head[0] != 0xFF || head[1] != 0xD8)
        {
            return null;   // not a JPEG
        }

        var app1 = FindExifApp1(head);
        return app1 < 0 ? null : ParseTiff(head[(app1 + 6)..]);
    }

    /// <summary>Returns the offset of the APP1 payload ("Exif\0\0" included), or -1.</summary>
    private static int FindExifApp1(ReadOnlySpan<byte> b)
    {
        var pos = 2;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF)
            {
                return -1;
            }

            var marker = b[pos + 1];
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                pos += 2;                                            // standalone marker, no length field
                continue;
            }

            if (marker is 0xDA or 0xD9)
            {
                return -1;                                           // start of scan / end of image: no EXIF ahead
            }

            var len = (b[pos + 2] << 8) | b[pos + 3];
            if (len < 2 || pos + 2 + len > b.Length)
            {
                return -1;                                           // truncated or bogus segment
            }

            if (marker == 0xE1 && len >= 8 && HasExifHeader(b.Slice(pos + 4, len - 2)))
            {
                return pos + 4;
            }

            pos += 2 + len;
        }

        return -1;
    }

    private static bool HasExifHeader(ReadOnlySpan<byte> segment) =>
        segment.Length >= 6 && segment[0] is (byte)'E' && segment[1] is (byte)'x' && segment[2] is (byte)'i'
        && segment[3] is (byte)'f' && segment[4] == 0 && segment[5] == 0;

    private static ExifData? ParseTiff(ReadOnlySpan<byte> t)
    {
        if (t.Length < 8)
        {
            return null;
        }

        var littleEndian = t[0] is (byte)'I' && t[1] is (byte)'I';
        if (!littleEndian && !(t[0] is (byte)'M' && t[1] is (byte)'M'))
        {
            return null;
        }

        if (U16(t, 2, littleEndian) != 42)
        {
            return null;
        }

        var ifd0 = U32(t, 4, littleEndian);
        if (ifd0 < 8 || ifd0 >= t.Length)
        {
            return null;
        }

        var ifd0Entries = new List<ExifEntry>();
        if (!WalkIfd(t, ifd0, littleEndian, ifd0Entries))
        {
            return null;
        }

        int? orientation = null;
        var exifIfd = -1;
        foreach (var e in ifd0Entries)
        {
            switch (e.Tag, e.Type)
            {
                case (0x0112, 3) when e.Count == 1: orientation = U16(t, e.ValuePos, littleEndian); break;
                case (0x8769, 4) when e.Count == 1: exifIfd = U32(t, e.ValuePos, littleEndian); break;
            }
        }

        string? original = null, offset = null, digitized = null;
        if (exifIfd >= 0)
        {
            if (exifIfd >= t.Length)
            {
                return null;
            }

            var exifEntries = new List<ExifEntry>();
            if (!WalkIfd(t, exifIfd, littleEndian, exifEntries))
            {
                return null;
            }

            foreach (var e in exifEntries)
            {
                switch (e.Tag, e.Type)
                {
                    case (0x9003, 2): original = ReadAscii(t, e.ValuePos, e.Count); break;
                    case (0x9004, 2): digitized = ReadAscii(t, e.ValuePos, e.Count); break;
                    case (0x9010, 2): offset = ReadAscii(t, e.ValuePos, e.Count); break;
                }
            }
        }

        return original is null && digitized is null && orientation is null
            ? null
            : new ExifData(orientation, original, offset, digitized);
    }

    private static bool WalkIfd(ReadOnlySpan<byte> t, int ifdPos, bool le, List<ExifEntry> into)
    {
        if (ifdPos + 2 > t.Length)
        {
            return false;
        }

        var count = U16(t, ifdPos, le);
        if (count > 256 || ifdPos + 2 + (long)count * 12 + 4 > t.Length)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            var e = ifdPos + 2 + (i * 12);
            var tag = (ushort)U16(t, e, le);
            var type = (ushort)U16(t, e + 2, le);
            var cnt = U32(t, e + 4, le);
            if (cnt > 4096)
            {
                continue;                                       // absurd count: skip, never trust
            }

            var size = TypeSize(type) * cnt;
            var valuePos = size <= 4 ? e + 8 : U32(t, e + 8, le);
            if (size > 4 && (valuePos < 8 || valuePos + size > t.Length))
            {
                continue;                                       // out-of-bounds pointer: skip
            }

            into.Add(new ExifEntry(tag, type, (uint)cnt, valuePos));
        }

        return true;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> t, int pos, uint count)
    {
        if (count == 0 || pos + count > t.Length || count > 64)
        {
            return null;
        }

        var text = t.Slice(pos, (int)count);
        var nul = text.IndexOf((byte)0);
        if (nul >= 0)
        {
            text = text[..nul];
        }

        return Encoding.ASCII.GetString(text);
    }

    private static uint TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,      // byte / ascii / sbyte / undefined
        3 or 8 => 2,                // short
        4 or 9 or 11 => 4,          // long / slong / float
        5 or 10 or 12 => 8,         // rational / srational / double
        _ => 0,                     // unknown: treated as inline (size 0), value never dereferenced
    };

    public static DateTimeOffset? ParseTakenUtc(string? exifTime, string? offsetTime)
    {
        if (string.IsNullOrWhiteSpace(exifTime) ||
            !DateTime.TryParseExact(exifTime.Trim(), "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = ParseOffset(offsetTime);                   // null/invalid: treat wall time as UTC
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset).ToUniversalTime();
    }

    /// <summary>Parses OffsetTimeOriginal like "+02:00" / "-07:30". Returns zero when absent or malformed.</summary>
    internal static TimeSpan ParseOffset(string? offsetTime)
    {
        var s = offsetTime?.Trim();
        if (s is null || s.Length != 6 || (s[0] != '+' && s[0] != '-') ||
            !int.TryParse(s.AsSpan(1, 2), CultureInfo.InvariantCulture, out var hours) || hours > 14 ||
            !int.TryParse(s.AsSpan(4, 2), CultureInfo.InvariantCulture, out var minutes) || minutes > 59)
        {
            return TimeSpan.Zero;
        }

        var offset = new TimeSpan(hours, minutes, 0);
        return s[0] == '-' ? -offset : offset;
    }

    private static int U16(ReadOnlySpan<byte> b, int pos, bool le) =>
        le ? b[pos] | (b[pos + 1] << 8) : (b[pos] << 8) | b[pos + 1];

    private static int U32(ReadOnlySpan<byte> b, int pos, bool le) =>
        le ? b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24)
           : (b[pos] << 24) | (b[pos + 1] << 16) | (b[pos + 2] << 8) | b[pos + 3];
}
