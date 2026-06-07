using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // How long a single long-poll request blocks waiting for a change before
    // returning empty so the client immediately re-polls. Comfortably under the
    // nginx 300s proxy timeout, and short enough that an idle hold is cheap.
    private static readonly TimeSpan ChangeFeedHold = TimeSpan.FromSeconds(25);

    // Long-poll change feed for the Discord Activity. The client passes its
    // last-seen `since` version; we return as soon as the linkshell's version
    // advances (with the coarse areas that changed) or after a short hold with
    // empty areas. A first connect (since < 0) returns the current baseline
    // version immediately so the client can start polling without a refetch.
    //
    // Normal bearer/cookie auth — no streaming, no special headers — so it rides
    // the Discord proxy + Cloudflare + nginx chain unchanged. The notifier is
    // taken via [FromServices] to avoid widening the shared controller ctor.
    [HttpGet("changes")]
    public async Task<IActionResult> GetChangesAsync(
        [FromServices] LinkshellChangeNotifier changeNotifier,
        [FromQuery] int linkshellId,
        [FromQuery] long since = -1,
        CancellationToken cancellationToken = default)
    {
        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to receive live updates."
            });
        }

        // Reuse the standard membership + guild-lock gate so the feed never
        // signals changes for a linkshell the caller can't access.
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (since < 0)
        {
            return Ok(new
            {
                version = changeNotifier.CurrentVersion(linkshellId),
                areas = Array.Empty<string>()
            });
        }

        try
        {
            var result = await changeNotifier.WaitForChangeAsync(
                linkshellId, since, ChangeFeedHold, cancellationToken);
            return Ok(new { version = result.Version, areas = result.Areas });
        }
        catch (OperationCanceledException)
        {
            // Client navigated away / closed the Activity mid-hold. Nothing to
            // return; the connection is already gone.
            return new EmptyResult();
        }
    }
}
