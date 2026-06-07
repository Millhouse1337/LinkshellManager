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
        var result = await _auth.RedeemPairingCodeAsync(request.Code, cancellationToken);
        if (result is null)
        {
            return BadRequest(new { error = "Pairing code is invalid, expired, or already used." });
        }

        return Ok(new
        {
            token = result.RawToken,
            linkshellId = result.Linkshell.Id,
            linkshellName = result.Linkshell.LinkshellName,
            label = result.Record.Label
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

        return Ok(new
        {
            linkshellId = token.LinkshellId,
            linkshellName = linkshell?.LinkshellName,
            issuedToCharacterName,
            issuedToAppUserId = token.IssuedToAppUserId,
            canModerateLiveEvent = canModerate,
            label = token.Label,
            // Drives addon section visibility (Create Event / presets /
            // Queued / Active / HNM type). Normalized so the addon never
            // has to handle null. The addon fail-opens to "Both" anyway.
            linkshellType = LinkshellTypes.Normalize(linkshell?.LinkshellType)
        });
    }
}
