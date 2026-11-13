using System.Text.RegularExpressions;

namespace LinkshellManagerDiscordApp.Utils;

// How long a browser may keep each file of the Angular app (/discord-activity/...).
//
// The build names its scripts and styles with a content hash (main-BFI5SPP7.js), so a given name
// never changes content: those can be kept for a year. index.html is the one file that names them,
// so it must be checked with the server every time. Without that, a browser that cached an old
// index.html after a deploy asked for script files the new build no longer has, and the page broke.
public static partial class ActivitySpaCaching
{
    public const string NoCache = "no-cache";
    public const string Immutable = "public, max-age=31536000, immutable";

    // Angular's esbuild output: "<name>-<8 upper-case letters/digits>.<js|css>".
    [GeneratedRegex(@"-[A-Z0-9]{8}\.(js|mjs|css)$")]
    private static partial Regex HashedFileName();

    // The Cache-Control value for a file name, or null to leave the default (unhashed assets
    // such as fonts and images copied from public/).
    public static string? CacheControlFor(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return null;
        }
        if (fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            return NoCache;
        }
        return HashedFileName().IsMatch(fileName) ? Immutable : null;
    }
}
