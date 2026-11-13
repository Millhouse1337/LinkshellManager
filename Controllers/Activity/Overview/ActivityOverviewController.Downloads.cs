using System.Security.Cryptography;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace LinkshellManagerDiscordApp.Controllers;

// Short-lived, single-use download links, for files the Discord Activity can't save itself.
//
// The Discord iframe has nowhere to put a download, so the Activity opens the file in the
// member's own browser instead (DiscordActivityService.openExternalLink). That browser may not be
// signed in to the website, so the link carries the permission itself: the Activity asks for a
// link while signed in, and the server hands back a URL holding a protected token that names the
// file, the linkshell and the member, expires in a few minutes and works once. Opening it
// re-checks that the member still belongs to the linkshell.
public sealed partial class ActivityOverviewController
{
    private const string DownloadLinkPurpose = "LSManager.Activity.DownloadLink.v1";
    private static readonly TimeSpan DownloadLinkLifetime = TimeSpan.FromMinutes(5);

    // What a link can download: the linkshell's DKP Sheet as .xlsx (needs a linkshell), or the
    // sample Import DKP template (doesn't).
    public const string DownloadKindDkpSheet = "dkp-sheet";
    public const string DownloadKindDkpTemplate = "dkp-template";

    public sealed record ActivityDownloadLinkRequest(string? Kind, int? LinkshellId);
    public sealed record ActivityDownloadLinkResponse(string Url);

    [HttpPost("download-links")]
    public async Task<IActionResult> CreateDownloadLinkAsync(
        [FromBody] ActivityDownloadLinkRequest request,
        [FromServices] IDataProtectionProvider protection,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to download files." });

        var linkshellId = 0;
        switch (request?.Kind)
        {
            case DownloadKindDkpSheet:
                // The same check as the DKP Sheet's own export: any member of the linkshell.
                linkshellId = request.LinkshellId ?? 0;
                if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });
                if (await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken) is null) return Forbid();
                break;
            case DownloadKindDkpTemplate:
                break;
            default:
                return BadRequest(new { error = "Unknown download." });
        }

        // The random part makes every link distinct, so "used once" means this link, not this file.
        var token = protection.CreateProtector(DownloadLinkPurpose).ToTimeLimitedDataProtector().Protect(
            $"{request.Kind}|{linkshellId}|{appUser.Id}|{Guid.NewGuid():N}", DownloadLinkLifetime);

        // An absolute address on the website itself: inside Discord the page's own origin is a
        // Discord proxy, which the member's browser can't use. App:PublicBaseUrl when it's set (the
        // bot's links use it too), otherwise this request's own host, which nginx forwards.
        var baseUrl = configuration["App:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = $"{Request.Scheme}://{Request.Host}";
        }
        return Ok(new ActivityDownloadLinkResponse(
            $"{baseUrl.TrimEnd('/')}/api/activity/downloads/{Uri.EscapeDataString(token)}"));
    }

    [HttpGet("downloads/{token}")]
    public async Task<IActionResult> DownloadByLinkAsync(
        string token,
        [FromServices] IDataProtectionProvider protection,
        [FromServices] IMemoryCache usedLinks,
        CancellationToken cancellationToken)
    {
        const string Expired = "This download link has expired or was already used. Go back to LinkshellManager and download it again.";

        string payload;
        try
        {
            payload = protection.CreateProtector(DownloadLinkPurpose).ToTimeLimitedDataProtector().Unprotect(token);
        }
        catch (CryptographicException)
        {
            return NotFound(Expired);
        }

        // Single use: remembered for as long as the link could still be valid.
        var usedKey = $"download-link:{payload}";
        if (usedLinks.TryGetValue(usedKey, out _)) return NotFound(Expired);
        usedLinks.Set(usedKey, true, DownloadLinkLifetime);

        var parts = payload.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[1], out var linkshellId)) return NotFound(Expired);
        var (kind, appUserId) = (parts[0], parts[2]);

        Response.Headers.CacheControl = "no-store";
        switch (kind)
        {
            case DownloadKindDkpSheet:
                // Still a member now, not just when the link was made.
                if (await GetMembershipAsync(appUserId, linkshellId, cancellationToken) is null) return NotFound(Expired);
                var data = await _dkpSheet.BuildAsync(linkshellId, cancellationToken);
                return File(DkpWorkbookBuilder.Build(data), DkpWorkbookBuilder.ContentType,
                    DkpWorkbookBuilder.FileName(data.LinkshellName, DateTime.UtcNow));
            case DownloadKindDkpTemplate:
                return File(SampleDkpTemplateWorkbook.Build(), SampleDkpTemplateWorkbook.ContentType, SampleDkpTemplateWorkbook.FileName);
            default:
                return NotFound(Expired);
        }
    }
}
