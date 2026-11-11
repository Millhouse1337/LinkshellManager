namespace LinkshellManagerDiscordApp.Utils;

// Which build is running. deploy.ps1 writes the git commit (e.g. "3f2a9c1") into
// version.txt next to the app, and /healthz shows it, so after a deploy you can
// check the droplet is running the commit you meant to ship.
public static class AppVersion
{
    public const string FileName = "version.txt";
    public const string Unknown = "unknown";

    // Reads version.txt from the content root (the app folder). A local run has
    // no version.txt, so it reports "unknown" instead of failing.
    public static string Read(string contentRootPath)
    {
        try
        {
            var path = Path.Combine(contentRootPath, FileName);
            if (!File.Exists(path)) return Unknown;
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0 ? Unknown : text;
        }
        catch (IOException)
        {
            return Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return Unknown;
        }
    }
}
