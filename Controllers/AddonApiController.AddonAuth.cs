using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    [HttpPost("pair")]
    public async Task<IActionResult> PairAsync([FromBody] PairRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return BadRequest(new { error = "Pairing code is required." });
        }

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
            label = token.Label
        });
    }
}
