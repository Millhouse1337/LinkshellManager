using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// The Monster Setups card, for the Discord Activity's Configurations tab.
//
// A dedicated endpoint pair rather than fields on ActivityUpdateLinkshellRequest, for the reason
// spelled out on the DKP pools endpoints: the Configurations tab re-sends EVERY setting on any
// save, so a nullable row list on that request would mean renaming the linkshell could silently
// wipe the monster setups.
//
// Both this and the web Customize page converge on MonsterTimingEditor, so there is exactly one
// implementation of "save the monster setups".
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/monster-timings")]
    public async Task<IActionResult> GetMonsterTimingsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage monster setups." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        // Opening the editor is what materializes the catalog — see the note on the provisioner
        // about why this is lazy rather than a migration backfill.
        var rows = await _monsterTimingProvisioner.EnsureSeededAsync(linkshellId, cancellationToken);
        _monsterTimings.Invalidate(linkshellId);

        return Ok(BuildMonsterTimingsDto(rows));
    }

    [HttpPost("linkshells/{linkshellId:int}/monster-timings")]
    public async Task<IActionResult> SaveMonsterTimingsAsync(
        int linkshellId,
        [FromBody] ActivitySaveMonsterTimingsRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage monster setups." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var error = await _monsterTimingEditor.SaveAsync(linkshellId, ToMonsterTimingEdits(request), cancellationToken);
        if (error is not null)
        {
            return BadRequest(new { error });
        }

        return await GetMonsterTimingsAsync(linkshellId, cancellationToken);
    }

    internal static ActivityMonsterTimingsDto BuildMonsterTimingsDto(IReadOnlyList<LinkshellMonsterTiming> rows) =>
        new(
            rows.Select(ToMonsterTimingDto).ToList(),
            MonsterTimingDefaults.Categories.ToList(),
            HnmConfig.MaxWindow);

    internal static ActivityMonsterTimingDto ToMonsterTimingDto(LinkshellMonsterTiming row)
    {
        var defaults = MonsterTimingDefaults.Build(row.MonsterName);
        var (cooldownValue, cooldownUnit) = TodDurationFormat.Split(
            row.CooldownMinutes > 0 ? row.CooldownMinutes : defaults.CooldownMinutes);
        var cadence = row.WindowCadenceMinutes is > 0
            ? TodDurationFormat.Split(row.WindowCadenceMinutes.Value)
            : ((int Value, string Unit)?)null;

        return new ActivityMonsterTimingDto(
            row.Id,
            row.MonsterName,
            row.WindowCount,
            cadence?.Value,
            cadence?.Unit,
            cooldownValue,
            cooldownUnit,
            MonsterTimingDefaults.NormalizeCategory(row.Category),
            row.IsCustom,
            defaults.WindowCount,
            defaults.WindowCadenceMinutes,
            defaults.CooldownMinutes,
            row.ClaimShieldEnabled);
    }

    private static List<MonsterTimingEdit> ToMonsterTimingEdits(ActivitySaveMonsterTimingsRequest request) =>
        (request.Rows ?? new List<ActivityMonsterTimingInput>())
            .Select(row => new MonsterTimingEdit(
                row.Id,
                row.MonsterName,
                row.Windows,
                row.CadenceValue,
                row.CadenceUnit,
                row.CooldownValue,
                row.CooldownUnit,
                row.Category,
                row.ClaimShieldEnabled))
            .ToList();
}
