using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The DKP Pools settings card, for the Discord Activity's Configurations tab.
//
// A dedicated endpoint trio rather than fields on ActivityUpdateLinkshellRequest: the
// Configurations tab re-sends EVERY setting on any save (so a rename can't reset a flag), and a
// nullable pool list on that request would mean a rename could silently wipe the pool config.
//
// Both this and the web Customize page converge on DkpPoolEditor, so there is exactly one
// implementation of "save the pools" — and exactly one implementation of the ledger re-stamp.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/dkp-pools")]
    public async Task<IActionResult> GetDkpPoolsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage DKP pools." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var map = await _dkpPools.GetMapAsync(linkshellId, cancellationToken);
        var catalog = await _dkpPoolEventTypes.LoadAsync(linkshellId, cancellationToken);

        return Ok(new ActivityDkpPoolsDto(
            map.Pools
                .Select(pool => new ActivityDkpPoolDto(
                    pool.Id,
                    pool.Name,
                    pool.IsDefault,
                    pool.SortOrder,
                    DkpPoolAccents.Resolve(pool.Accent),
                    pool.EventTypes.Select(mapping => mapping.EventType).OrderBy(t => t).ToList()))
                .ToList(),
            catalog
                .Select(option => new ActivityDkpPoolEventTypeDto(
                    option.Key, option.IsCustom, option.EarnedTotal, option.InUse))
                .ToList(),
            DkpPoolAccents.All.ToList()));
    }

    // Dry run: what would this save actually move? Writes nothing.
    [HttpPost("linkshells/{linkshellId:int}/dkp-pools/preview")]
    public async Task<IActionResult> PreviewDkpPoolsAsync(
        int linkshellId,
        [FromBody] ActivitySaveDkpPoolsRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage DKP pools." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var preview = await _dkpPoolEditor.PreviewAsync(linkshellId, ToEdits(request), cancellationToken);
        if (preview is null)
        {
            return BadRequest(new { error = "Fix the errors in the pool setup first." });
        }

        return Ok(new ActivityDkpPoolPreviewDto(
            preview.Moves
                .Select(move => new ActivityDkpPoolMoveDto(
                    move.EventType, move.FromPool, move.ToPool, move.EarnedTotal, move.LedgerRows))
                .ToList(),
            preview.AffectedLedgerRows,
            preview.AffectedMembers,
            preview.Warnings.ToList()));
    }

    [HttpPost("linkshells/{linkshellId:int}/dkp-pools")]
    public async Task<IActionResult> SaveDkpPoolsAsync(
        int linkshellId,
        [FromBody] ActivitySaveDkpPoolsRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage DKP pools." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var error = await _dkpPoolEditor.SaveAsync(linkshellId, ToEdits(request), cancellationToken);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        return await GetDkpPoolsAsync(linkshellId, cancellationToken);
    }

    private static List<DkpPoolEdit> ToEdits(ActivitySaveDkpPoolsRequest request) =>
        (request.Pools ?? new List<ActivityDkpPoolInput>())
            .Select((pool, index) => new DkpPoolEdit(
                pool.Id,
                pool.Name,
                pool.IsDefault,
                index,
                pool.Accent,
                pool.EventTypes ?? new List<string>()))
            .ToList();
}
