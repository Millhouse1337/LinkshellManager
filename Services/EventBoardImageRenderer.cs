using Microsoft.Playwright;

namespace LinkshellManagerDiscordApp.Services;

// Renders an event-board HTML card (EventBoardHtmlBuilder) to a PNG with headless
// Chromium via Playwright. Singleton: the browser is launched once and reused
// across renders (relaunching per call would cost ~hundreds of ms). Renders are
// serialised by a gate (they're infrequent — event posts/edits — so one-at-a-time
// is fine and keeps the shared browser simple).
//
// Best-effort by design: ANY failure (Chromium not installed, launch error, render
// timeout) logs and returns null, and the caller falls back to the text board so
// events always post. Self-healing — a failed launch tears down the browser so the
// next call retries (e.g. after the installer finishes downloading Chromium).
public sealed class EventBoardImageRenderer : IAsyncDisposable
{
    private readonly ILogger<EventBoardImageRenderer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public EventBoardImageRenderer(ILogger<EventBoardImageRenderer> logger)
    {
        _logger = logger;
    }

    // Returns the PNG bytes, or null if rendering isn't available (caller falls
    // back to the text board).
    public async Task<byte[]?> RenderAsync(string html, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var browser = await EnsureBrowserAsync();
            if (browser is null)
            {
                return null;
            }

            await using var page = await browser.NewPageAsync(new BrowserNewPageOptions
            {
                ViewportSize = new ViewportSize { Width = 1000, Height = 1400 },
                DeviceScaleFactor = 2, // retina-crisp text
            });
            await page.SetContentAsync(html, new PageSetContentOptions
            {
                // Wait for the Google Fonts to load so the card isn't screenshotted
                // mid-swap; cap it so a slow/blocked font CDN can't hang the render.
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 8000,
            });

            var card = await page.QuerySelectorAsync(".embed");
            var bytes = card is not null
                ? await card.ScreenshotAsync(new ElementHandleScreenshotOptions { Type = ScreenshotType.Png })
                : await page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Png, FullPage = true });
            return bytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Event board image render failed — falling back to the text board. If this persists, install " +
                "Chromium: `pwsh bin/Debug/net8.0/playwright.ps1 install chromium` (or set LSM_PLAYWRIGHT_AUTOINSTALL=1).");
            // Tear down a possibly-broken browser so the next render relaunches.
            await DisposeBrowserAsync();
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IBrowser?> EnsureBrowserAsync()
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        _playwright ??= await Playwright.CreateAsync();
        // --no-sandbox: required when the app runs as root in a container/droplet,
        // where Chromium's sandbox can't initialise. Harmless elsewhere.
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = new[] { "--no-sandbox", "--disable-dev-shm-usage" },
        });
        return _browser;
    }

    private async Task DisposeBrowserAsync()
    {
        try
        {
            if (_browser is not null)
            {
                await _browser.CloseAsync();
            }
        }
        catch
        {
            // best-effort teardown
        }
        finally
        {
            _browser = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeBrowserAsync();
        _playwright?.Dispose();
        _gate.Dispose();
    }
}
