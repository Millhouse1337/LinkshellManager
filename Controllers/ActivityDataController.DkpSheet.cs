using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Discord Activity read-only DKP sheet — the linkshell's live DKP straight from
// the app's own data (DkpSheetService), the same source the web page and the Excel
// export use. No Google connection involved; always available. Member-gated. Posting
// the sheet to a Discord channel is configured in the channel-routes editor (the "DKP
// sheet" post type on the Configurations tab), not here.
public sealed partial class ActivityDataController
{
    // PoolCurrent is a parallel array aligned to the response's Pools, so the client walks columns
    // and cells in the same order. Empty when the linkshell has a single pool.
    public sealed record ActivityDkpSheetMemberDto(
        int Id, string Name, string Alt1, string Alt2,
        double Current, double Biddable, double Total, double Spent,
        IReadOnlyList<double> PoolCurrent);

    public sealed record ActivityDkpSheetPoolDto(int PoolId, string Name, string Accent);

    public sealed record ActivityDkpSheetResponse(
        int LinkshellId,
        string LinkshellName,
        int TotalMembers,
        double TotalDkp,
        double Biddable,
        double TotalSpent,
        IReadOnlyList<ActivityDkpSheetMemberDto> Members,
        // Empty unless the linkshell has more than one DKP pool — the client's cue to render the
        // sheet exactly as it did before pools existed.
        IReadOnlyList<ActivityDkpSheetPoolDto> Pools,
        IReadOnlyList<double> PoolTotals);

    [HttpGet("dkp-sheet")]
    public async Task<IActionResult> GetDkpSheetAsync([FromQuery] int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to view the DKP sheet." });
        if (linkshellId <= 0) return BadRequest(new { error = "A linkshell selection is required." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        Response.Headers.CacheControl = "no-store";

        var data = await _dkpSheet.BuildAsync(linkshellId, cancellationToken);
        var members = data.Members
            .Select(m => new ActivityDkpSheetMemberDto(
                m.Id, m.Name, m.Alt1, m.Alt2, m.Current, m.Biddable, m.Total, m.Spent, m.PoolCurrent))
            .ToList();

        return Ok(new ActivityDkpSheetResponse(
            data.LinkshellId,
            data.LinkshellName,
            data.TotalMembers,
            data.TotalDkp,
            data.Biddable,
            data.TotalSpent,
            members,
            data.Pools.Select(pool => new ActivityDkpSheetPoolDto(pool.PoolId, pool.Name, pool.Accent)).ToList(),
            data.PoolTotals));
    }
}
