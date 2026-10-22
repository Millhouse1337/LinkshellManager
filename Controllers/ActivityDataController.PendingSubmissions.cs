using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The review queue for member-submitted ToDs, attendance windows and attendance snapshots — the
// JSON twin of LinkshellPendingSubmissionsController, for the Activity's website-only Pending
// Submissions tab (members submit from the Activity already; only reviewing was web-only).
//
// ToDs need CanManageTods; windows and snapshots need CanModerateLiveEvent. Through CanAsync the
// Leader always passes, which the Razor pages' own role lookup didn't. The review itself runs in
// SubmissionApprovalService, scoped to the linkshell in the URL.
public sealed partial class ActivityDataController
{
    [HttpGet("linkshells/{linkshellId:int}/pending-submissions")]
    public async Task<IActionResult> GetPendingSubmissionsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to review submissions." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        var canReviewTods = await CanAsync(membership, role => role.CanManageTods, cancellationToken);
        var canReviewAttendance = await CanAsync(membership, role => role.CanModerateLiveEvent, cancellationToken);
        if (!canReviewTods && !canReviewAttendance) return Forbid();

        Response.Headers.CacheControl = "no-store";

        var tods = !canReviewTods ? new List<ActivityPendingTodDto>() : (await _dbContext.PendingTodSubmissions
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

        var windows = !canReviewAttendance ? new List<ActivityPendingWindowDto>() : (await _dbContext.PendingAttendanceWindowSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .Include(s => s.Event)
                .Include(s => s.Members)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync(cancellationToken))
            .Select(s => new ActivityPendingWindowDto(
                s.Id, SubmitterName(s.SubmittedBy), s.SubmittedAtUtc,
                s.EventId, s.Event?.EventName ?? $"Event #{s.EventId}", s.WindowIndex,
                s.Members.OrderBy(m => m.CharacterName)
                    .Select(m => new ActivityPendingAttendeeDto(m.Id, m.CharacterName, m.MainJob, m.MainJobLevel, m.SubJob, m.SubJobLevel, null))
                    .ToList()))
            .ToList();

        var snapshots = !canReviewAttendance ? new List<ActivityPendingSnapshotDto>() : (await _dbContext.PendingAttendanceSnapshotSubmissions
                .AsNoTracking()
                .Where(s => s.LinkshellId == linkshellId)
                .Include(s => s.SubmittedBy)
                .Include(s => s.Entries)
                .OrderBy(s => s.SubmittedAtUtc)
                .ToListAsync(cancellationToken))
            .Select(s => new ActivityPendingSnapshotDto(
                s.Id, SubmitterName(s.SubmittedBy), s.SubmittedAtUtc,
                s.CapturedAtUtc, s.CapturedByCharacterName, s.UtcOffset,
                s.Entries.OrderBy(e => e.CharacterName)
                    .Select(e => new ActivityPendingAttendeeDto(e.Id, e.CharacterName, e.MainJob, e.MainJobLevel, e.SubJob, e.SubJobLevel, e.Zone))
                    .ToList()))
            .ToList();

        // The ToD editor's pick-lists, as the Razor edit page offered them.
        var rosterNames = !canReviewTods ? new List<string>() : await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == linkshellId && m.CharacterName != null)
            .Select(m => m.CharacterName!)
            .OrderBy(n => n)
            .ToListAsync(cancellationToken);
        var monsterOptions = canReviewTods
            ? (await _monsterTimings.GetMapAsync(linkshellId, cancellationToken)).EventMonsterOptions
            : Array.Empty<string>();

        return Ok(new ActivityPendingSubmissionsResponse(
            canReviewTods, canReviewAttendance, tods, windows, snapshots,
            monsterOptions, TodManagerViewModel.SupportedCooldowns, TodManagerViewModel.SupportedIntervals, rosterNames));
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/tod/{submissionId:int}/approve")]
    public async Task<IActionResult> ApprovePendingTodAsync(
        int linkshellId, int submissionId, [FromBody] ActivityApprovePendingTodRequest request,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if (await ReviewGateAsync(linkshellId, role => role.CanManageTods, cancellationToken) is { } denied) return denied;

        var edits = new PendingTodEdits(
            request.MonsterName, request.DayNumber, request.Claim, request.Time,
            request.Cooldown, request.Interval, request.RepopTime,
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
        if (await ReviewGateAsync(linkshellId, role => role.CanManageTods, cancellationToken) is { } denied) return denied;
        return ReviewOutcome(await approvals.RejectTodAsync(linkshellId, submissionId, null, cancellationToken),
            "ToD submission rejected and removed.");
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/attendance-window/{submissionId:int}/approve")]
    public async Task<IActionResult> ApprovePendingWindowAsync(
        int linkshellId, int submissionId, [FromBody] ActivityApprovePendingAttendanceRequest request,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if (await ReviewGateAsync(linkshellId, role => role.CanModerateLiveEvent, cancellationToken) is { } denied) return denied;
        return ReviewOutcome(await approvals.ApproveAttendanceWindowKeepingAsync(
            linkshellId, submissionId, request.KeepIds ?? new List<int>(), cancellationToken),
            "Attendance window approved.");
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/attendance-window/{submissionId:int}/reject")]
    public async Task<IActionResult> RejectPendingWindowAsync(
        int linkshellId, int submissionId,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if (await ReviewGateAsync(linkshellId, role => role.CanModerateLiveEvent, cancellationToken) is { } denied) return denied;
        return ReviewOutcome(await approvals.RejectAttendanceWindowAsync(linkshellId, submissionId, null, cancellationToken),
            "Attendance window rejected.");
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/attendance-snapshot/{submissionId:int}/approve")]
    public async Task<IActionResult> ApprovePendingSnapshotAsync(
        int linkshellId, int submissionId, [FromBody] ActivityApprovePendingAttendanceRequest request,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if (await ReviewGateAsync(linkshellId, role => role.CanModerateLiveEvent, cancellationToken) is { } denied) return denied;
        return ReviewOutcome(await approvals.ApproveSnapshotKeepingAsync(
            linkshellId, submissionId, request.KeepIds ?? new List<int>(), cancellationToken),
            "Attendance snapshot approved.");
    }

    [HttpPost("linkshells/{linkshellId:int}/pending-submissions/attendance-snapshot/{submissionId:int}/reject")]
    public async Task<IActionResult> RejectPendingSnapshotAsync(
        int linkshellId, int submissionId,
        [FromServices] SubmissionApprovalService approvals, CancellationToken cancellationToken)
    {
        if (await ReviewGateAsync(linkshellId, role => role.CanModerateLiveEvent, cancellationToken) is { } denied) return denied;
        return ReviewOutcome(await approvals.RejectSnapshotAsync(linkshellId, submissionId, null, cancellationToken),
            "Attendance snapshot rejected.");
    }

    // Null when the caller may review this kind of submission in this linkshell.
    private async Task<IActionResult?> ReviewGateAsync(
        int linkshellId, Func<LinkshellRole, bool> permission, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null) return Unauthorized(new { error = "Sign in to review submissions." });

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        return await CanAsync(membership, permission, cancellationToken) ? null : Forbid();
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

// One pending window attendee or snapshot entry; Zone is snapshots only.
public sealed record ActivityPendingAttendeeDto(
    int Id, string CharacterName, string? MainJob, int? MainJobLevel, string? SubJob, int? SubJobLevel, string? Zone);

public sealed record ActivityPendingWindowDto(
    int Id, string SubmittedBy, DateTime SubmittedAtUtc,
    int EventId, string EventName, int WindowIndex,
    IReadOnlyList<ActivityPendingAttendeeDto> Members);

public sealed record ActivityPendingSnapshotDto(
    int Id, string SubmittedBy, DateTime SubmittedAtUtc,
    DateTime CapturedAtUtc, string? CapturedByCharacterName, string? UtcOffset,
    IReadOnlyList<ActivityPendingAttendeeDto> Entries);

public sealed record ActivityPendingSubmissionsResponse(
    bool CanReviewTods,
    bool CanReviewAttendance,
    IReadOnlyList<ActivityPendingTodDto> Tods,
    IReadOnlyList<ActivityPendingWindowDto> Windows,
    IReadOnlyList<ActivityPendingSnapshotDto> Snapshots,
    IReadOnlyList<string> MonsterOptions,
    IReadOnlyList<string> CooldownOptions,
    IReadOnlyList<string> IntervalOptions,
    IReadOnlyList<string> RosterNames);

public sealed record ActivityApprovePendingTodRequest(
    string? MonsterName, int? DayNumber, bool? Claim, DateTime? Time,
    string? Cooldown, string? Interval, DateTime? RepopTime,
    List<ActivityPendingTodLootDto>? LootRows);

// KeepIds: the member / entry ids still ticked; everything else is dropped before approving.
public sealed record ActivityApprovePendingAttendanceRequest(List<int>? KeepIds);
