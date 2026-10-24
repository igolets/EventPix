using EventPix.Web.Configuration;
using EventPix.Web.Domain;
using EventPix.Web.Features;
using EventPix.Web.Media;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace EventPix.Tests;

public class SecurityTests
{
    [Fact]
    public void UrlTokens_AreLongAndUnique()
    {
        var a = Tokens.NewUrlToken(); var b = Tokens.NewUrlToken();
        Assert.Equal(32, a.Length);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("  Bob@Example.COM ", "bob@example.com")]
    [InlineData("not-an-email", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void Email_IsNormalised(string? input, string? expected)
    {
        Assert.Equal(expected, Tokens.NormalizeEmail(input));
    }

    [Fact]
    public void Email_LongerThan254_IsRejected()
    {
        Assert.Null(Tokens.NormalizeEmail("a@b." + new string('c', 260)));
    }

    [Fact]
    public void Storage_RejectsPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "eventpix-test-" + Guid.NewGuid().ToString("N"));
        var storage = new FileSystemMediaStorage(Options.Create(new EventPixOptions { StoragePath = root }));
        Assert.Throws<ArgumentException>(() => storage.Resolve("../evil"));
        Assert.Throws<ArgumentException>(() => storage.Resolve("a/../../evil"));
        Assert.Throws<ArgumentException>(() => storage.Resolve("/etc/passwd"));
        Assert.StartsWith(Path.GetFullPath(root), storage.Resolve("ev/media/original"), StringComparison.Ordinal);
    }

    [Fact]
    public void Sniffer_TrustsBytesNotNames()
    {
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(MediaKind.Photo, FileSniffer.Detect(jpeg)!.Value.Kind);
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        Assert.Equal(MediaKind.Photo, FileSniffer.Detect(png)!.Value.Kind);
        Assert.Null(FileSniffer.Detect("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
    }

    [Fact]
    public void ZipNames_AreSafeAndUnique()
    {
        Assert.Equal("a_b", DownloadEndpoints.Safe("a/b"));
        var used = new HashSet<string>();
        Assert.Equal("t/x.jpg", DownloadEndpoints.Unique(used, "t/x.jpg"));
        Assert.Equal("t/x (2).jpg", DownloadEndpoints.Unique(used, "t/x.jpg"));
    }
}
