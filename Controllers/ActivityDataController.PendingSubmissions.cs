using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The review queue for member-submitted ToDs — the JSON twin of LinkshellPendingSubmissionsController,
// for the ToD Submissions Needing Approval section on the Activity's ToDs tab.
//
// Reviewing needs CanManageTods. Through CanAsync the Leader always passes, which the Razor pages'
// own role lookup didn't. The review itself runs in SubmissionApprovalService, scoped to the
// linkshell in the URL.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/pending-submissions")]
    public async Task<IActionResult> GetPendingSubmissionsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to review submissions." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, role => role.CanManageTods, cancellationToken)) return Forbid();

        Response.Headers.CacheControl = "no-store";

        var tods = (await _dbContext.PendingTodSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .Include(s => s.LootRows)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync(cancellationToken))
            .Select(s => new ActivityPendingTodDto(
                s.Id, SubmitterName(s.SubmittedBy), s.SubmittedAtUtc,
                s.MonsterName, s.DayNumber, s.Claim, s.Time, s.Cooldown, s.Interval, s.RepopTime, s.ImagePath,
                s.LootRows.OrderBy(r => r.Id)
                    .Select(r => new ActivityPendingTodLootDto(r.ItemName, r.ItemWinner, r.WinningDkpSpent))
                    .ToList()))
            .ToList();

        // The ToD editor's pick-lists, as the Razor edit page offered them.
        var rosterNames = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.CharacterName != null)
            .Select(m => m.CharacterName!)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);
        var monsterOptions = (await _monsterTimings.GetMapAsync(linkshellId, cancellationToken)).EventMonsterOptions;

        return Ok(new ActivityPendingSubmissionsResponse(
            tods,
            monsterOptions, TodCatalog.SupportedCooldowns, TodCatalog.SupportedIntervals, rosterNames));
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}/approve")]
    public async Task<IActionResult> ApprovePendingTodAsync(
        int linkshellId, int submissionId, [FromBody] ActivityApprovePendingTodRequest request,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        var (reviewer, denied) = await ReviewGateAsync(linkshellId, role => role.CanManageTods, cancellationToken);
        if (denied is not null) return denied;

        // The editor shows times in the reviewer's profile zone (as every Activity form does);
        // a blank box clears the time.
        if (!TryReadReviewTime(request.TimeLocal, reviewer!, out var time)
            || !TryReadReviewTime(request.RepopTimeLocal, reviewer!, out var repopTime))
        {
            return BadRequest(new { error = "Enter valid dates and times." });
        }

        var edits = new PendingTodEdits(
            request.MonsterName, request.DayNumber, request.Claim, time,
            request.Cooldown, request.Interval, repopTime,
            (request.LootRows ?? new List<ActivityPendingTodLootDto>())
                .Select(row => new TodSubmissionLootInput(row.ItemName, row.ItemWinner, row.WinningDkpSpent))
                .ToList());
        return ReviewOutcome(await approvals.ApproveTodWithEditsAsync(linkshellId, submissionId, edits, cancellationToken),
            "ToD submission approved.");
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}/reject")]
    public async Task<IActionResult> RejectPendingTodAsync(
        int linkshellId, int submissionId,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if ((await ReviewGateAsync(linkshellId, role => role.CanManageTods, cancellationToken)).Denied is { } denied) return denied;
        return ReviewOutcome(await approvals.RejectTodAsync(linkshellId, submissionId, null, cancellationToken),
            "ToD submission rejected and removed.");
    }

    // Denied is null (and Reviewer set) when the caller may review this kind of submission here.
    private async Task<(AppUser? Reviewer, IActionResult? Denied)> ReviewGateAsync(
        int linkshellId, Func<LinkshellRole, bool> permission, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return (null, Unauthorized(new { error = "Sign in to review submissions." }));

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        return await CanAsync(membership, permission, cancellationToken) ? (appUser, null) : (null, Forbid());
    }

    // A datetime-local value in the reviewer's profile zone (or an explicit UTC instant) to UTC.
    // Blank is a valid "no time".
    private bool TryReadReviewTime(string? value, AppUser reviewer, out DateTime? utc)
    {
        utc = null;
        return string.IsNullOrWhiteSpace(value) || TryConvertUserTimeZoneToUtc(value, reviewer.TimeZone, out utc);
    }

    private IActionResult ReviewOutcome(ApprovalResult result, string success) => result switch
    {
        ApprovalResult.NotFound => NotFound(new { error = "That submission is gone — someone may have reviewed it already." }),
        // The submission (with the reviewer's edits) stays queued.
        ApprovalResult.InsufficientDkp => Conflict(new
        {
            error = "Approval blocked: a loot winner doesn't have enough DKP for their item. "
                + "Adjust their DKP or edit the submission's loot, then approve again."
        }),
        _ => Ok(new { message = success })
    };

    private static string SubmitterName(AppUser? user) =>
        user is null ? "(unknown)" : user.CharacterName ?? user.UserName ?? user.Id;
}

public sealed record ActivityPendingTodLootDto(string? ItemName, string? ItemWinner, int? WinningDkpSpent);

public sealed record ActivityPendingTodDto(
    int Id, string SubmittedBy, DateTime SubmittedAtUtc,
    string? MonsterName, int? DayNumber, bool? Claim, DateTime? Time,
    string? Cooldown, string? Interval, DateTime? RepopTime, string? ImagePath,
    IReadOnlyList<ActivityPendingTodLootDto> LootRows);

public sealed record ActivityPendingSubmissionsResponse(
    IReadOnlyList<ActivityPendingTodDto> Tods,
    IReadOnlyList<string> MonsterOptions,
    IReadOnlyList<string> CooldownOptions,
    IReadOnlyList<string> IntervalOptions,
    IReadOnlyList<string> RosterNames);

// TimeLocal / RepopTimeLocal: datetime-local values in the reviewer's profile zone, like every
// other Activity form (datetimeLocalToUtcIso on the client); blank clears the time.
public sealed record ActivityApprovePendingTodRequest(
    string? MonsterName, int? DayNumber, bool? Claim, string? TimeLocal,
    string? Cooldown, string? Interval, string? RepopTimeLocal,
    List<ActivityPendingTodLootDto>? LootRows);
