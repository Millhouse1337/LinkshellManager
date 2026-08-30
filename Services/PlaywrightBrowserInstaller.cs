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
            //
            // On Linux the DOWNLOAD is only half the job: Chromium also needs a set of
            // system shared libraries (libnss3, libatk, libgbm…) that a bare Ubuntu droplet
            // doesn't carry. Without them "install chromium" reports success and every
            // LAUNCH then fails, which is the silent path to "all my boards post as the
            // narrow text embed" — the download looks fine in the logs, so nobody goes
            // looking at the browser. "--with-deps" apt-installs those libraries.
            //
            // It needs root/apt, so a failure is expected on a locked-down or non-Debian
            // host and we retry the plain download, which is still better than nothing
            // (the libraries may already be present).
            var withDeps = OperatingSystem.IsLinux();
            var exit = Microsoft.Playwright.Program.Main(
                withDeps ? new[] { "install", "--with-deps", "chromium" } : new[] { "install", "chromium" });

            if (exit != 0 && withDeps)
            {
                _logger.LogWarning(
                    "Playwright \"install --with-deps chromium\" exited with code {Code} (it needs root + apt). " +
                    "Retrying without system dependencies — if boards still post as text, install them by hand: " +
                    "`sudo pwsh <app>/playwright.ps1 install --with-deps chromium`.", exit);
                exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
            }

            if (exit == 0)
            {
                _logger.LogInformation(
                    "Playwright Chromium is installed — event boards will render as images. A board that still " +
                    "posts as text means the LAUNCH failed, not the download; see the renderer's warning.");
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
