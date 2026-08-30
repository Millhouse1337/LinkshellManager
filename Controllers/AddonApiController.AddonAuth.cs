using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    [HttpPost("pair")]
    [EnableRateLimiting("addon-pair")]
    public async Task<IActionResult> PairAsync([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Pairing code is required." });
        }

        if (request.Code.Length > 64)
        {
            return BadRequest(new { error = "Pairing code is malformed." });
        }

        // Global kill-switch: block redeeming new codes while the addon is
        // disabled, so a leader can't bring a fresh token online during a
        // shutdown. Existing tokens are already blocked in ValidateTokenAsync.
        if (await _globalSettings.IsAddonGloballyDisabledAsync(cancellationToken))
        {
            return BadRequest(new { error = "The addon is currently disabled by the server administrator." });
        }

        // The addon is an in-game Lua process and cannot provide an HTTP
        // session credential — the pairing code itself is the credential. We
        // protect against leaked-code abuse with: (1) a short TTL on the
        // pairing code, (2) a per-IP rate limit on this endpoint (see
        // [EnableRateLimiting]), and (3) a 32-char alphabet × 8-char length
        // search space (~1.1e12). Users are warned not to share pairing codes.
        // One code pairs EVERY linkshell the issuing user belongs to, so a
        // member of several doesn't have to generate and redeem a code per
        // linkshell. Tokens are still minted per-linkshell (see the service);
        // only the count changes, not how requests are authorized.
        var results = await _auth.RedeemPairingCodeForAllAsync(request.Code, cancellationToken);
        if (results.Count == 0)
        {
            return BadRequest(new { error = "Pairing code is invalid, expired, or already used." });
        }

        // The redeemed linkshell stays in the top-level fields so an addon
        // built before `pairings` existed keeps working unchanged; newer
        // addons read the array and store all of them.
        var primary = results[0];
        return Ok(new
        {
            token = primary.RawToken,
            linkshellId = primary.Linkshell.Id,
            linkshellName = primary.Linkshell.LinkshellName,
            label = primary.Record.Label,
            pairings = results.Select(r => new
            {
                token = r.RawToken,
                linkshellId = r.Linkshell.Id,
                linkshellName = r.Linkshell.LinkshellName,
                label = r.Record.Label
            }).ToList()
        });
    }

    [HttpGet("me")]
    [AddonApiAuth]
    public async Task<IActionResult> MeAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var linkshell = await _dbContext.Linkshells
            .FirstOrDefaultAsync(ls => ls.Id == token.LinkshellId, cancellationToken);

        string? issuedToCharacterName = null;
        if (!string.IsNullOrEmpty(token.IssuedToAppUserId))
        {
            var membership = await _dbContext.AppUserLinkshells
                .FirstOrDefaultAsync(
                    m => m.LinkshellId == token.LinkshellId && m.AppUserId == token.IssuedToAppUserId,
                    cancellationToken);
            issuedToCharacterName = membership?.CharacterName;
        }

        var canModerate = await TokenIssuerCanModerateAsync(token, token.LinkshellId, cancellationToken);

        var claimShieldGloballyDisabled = await _globalSettings.IsClaimShieldGloballyDisabledAsync(cancellationToken);
        var claimShieldDisabled = claimShieldGloballyDisabled
            ? Array.Empty<string>()
            : (await _dbContext.LinkshellMonsterTimings
                    .AsNoTracking()
                    .Where(row => row.LinkshellId == token.LinkshellId && !row.ClaimShieldEnabled)
                    .Select(row => row.MonsterName)
                    .ToListAsync(cancellationToken))
                .SelectMany(name => HnmConfig.MonsterMatchNames(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        return Ok(new
        {
            linkshellId = token.LinkshellId,
            linkshellName = linkshell?.LinkshellName,
            issuedToCharacterName,
            issuedToAppUserId = token.IssuedToAppUserId,
            canModerateLiveEvent = canModerate,
            label = token.Label,
            // Manual Check In HNM attendance mode (Standard | Wd). In Manual Check In mode, HNM attendance
            // is Discord-button-driven (self-serve x-in), so the addon can hide its
            // leader-driven "post window" UI. Normalized; fail-closed to Standard.
            attendanceMode = HnmAttendanceModes.Normalize(linkshell?.HnmAttendanceMode),
            // Automatic per-window attendance snapshots. This says only "officers MAY arm" —
            // arming stays an explicit per-officer, per-camp action in the addon, because a camp
            // can have people in the zone who must not be counted. Fail-closed on a null
            // linkshell so a broken lookup can never silently enable capture.
            //
            // This endpoint is the ONLY channel linkshell settings reach the addon through, and
            // the addon's /me sweep is gated on pairing rather than launcher-open — which is what
            // lets an armed camp keep posting with the launcher closed.
            hnmAutoSnapshotEnabled = linkshell?.HnmAutoSnapshotEnabled == true,
            hnmAutoSnapshotDelaySeconds = Math.Clamp(linkshell?.HnmAutoSnapshotDelaySeconds ?? 20, 5, 300),
            // How wide a capture is allowed to be. "alliance" means party memory only — the server
            // rejects anything over 18 entries, so an addon still offering a zone scan would post
            // captures that bounce. Sent as a string rather than a bool so a future scope can be
            // added without every addon build having to understand it: an unrecognized value should
            // be treated as the most restrictive one the addon knows.
            snapshotScope = "alliance",
            // The most alliances a poster may claim to be in. Drives the addon's selector range so
            // it cannot offer a number the server will clamp away.
            maxAllianceNumber = AttendanceSnapshotAlliances.MaxAllianceNumber,
            // Claim Shield. The master switch is the super-admin's; the per-monster switches are
            // the linkshell's, and only apply while the master is on.
            //
            // Fails OPEN, unlike the auto-snapshot flag above, and deliberately: capture is a
            // read-only audit record with no DKP consequence, and the failure this whole feature
            // has repeatedly produced is a silent NON-capture. A linkshell whose rows haven't
            // loaded should record too much, not nothing.
            claimShieldEnabled = !claimShieldGloballyDisabled,
            // Monsters this linkshell has switched OFF. Sent as the exclusion list because the
            // default is on, so it is empty until someone opts out.
            //
            // EXPANDED to every alias here rather than in the addon: rows are stored merged
            // ("Fafnir/Nidhogg") but the lottery line names one half ("Fafnir"), so an addon doing
            // a plain string compare would never match a disabled merge pair. The server already
            // owns that expansion (HnmConfig.MonsterMatchNames); the addon stays a lookup.
            claimShieldDisabledMonsters = claimShieldDisabled
        });
    }
}
