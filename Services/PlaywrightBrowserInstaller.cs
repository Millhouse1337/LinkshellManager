namespace LinkshellManagerDiscordApp.Services;

// On boot, ensures the Playwright Chromium binary is downloaded so the event-board
// image renderer works without a manual `playwright install`. Best-effort and
// off the startup path: the download runs on a background task so a slow/blocked
// CDN can't delay the app coming up, and any failure just logs (boards fall back
// to the text embed until Chromium is available). Opt out with
// LSM_PLAYWRIGHT_AUTOINSTALL=0 (e.g. when the binary is baked into the image).
public sealed class PlaywrightBrowserInstaller : IHostedService
{
    private readonly ILogger<PlaywrightBrowserInstaller> _logger;

    public PlaywrightBrowserInstaller(ILogger<PlaywrightBrowserInstaller> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("LSM_PLAYWRIGHT_AUTOINSTALL"), "0", StringComparison.Ordinal))
        {
            _logger.LogInformation("Playwright Chromium auto-install disabled (LSM_PLAYWRIGHT_AUTOINSTALL=0).");
            return Task.CompletedTask;
        }

        _ = Task.Run(Install, CancellationToken.None);
        return Task.CompletedTask;
    }

    private void Install()
    {
        try
        {
            // Microsoft.Playwright bundles its CLI; this downloads the Chromium
            // build matching the package version into the Playwright browsers path.
            var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            if (exit == 0)
            {
                _logger.LogInformation("Playwright Chromium is installed — event boards will render as images.");
            }
            else
            {
                _logger.LogWarning(
                    "Playwright Chromium install exited with code {Code}; event boards will post as the text " +
                    "embed until Chromium is available.", exit);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Playwright Chromium install failed; event boards will post as the text embed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
