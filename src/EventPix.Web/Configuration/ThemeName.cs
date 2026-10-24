using System.Text.RegularExpressions;

namespace EventPix.Web.Configuration;

/// <summary>Compile-time guard for the configured theme name; the strict charset rules out path injection.</summary>
internal static partial class ThemeName
{
    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.None, 250)]
    public static partial Regex Valid();
}
