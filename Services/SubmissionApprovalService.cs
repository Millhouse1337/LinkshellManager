using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Encapsulates the queue-pending-row + approve-materializes-real-row pattern for ToDs (with loot
// detail rows) that members without the add-ToD permission submit for review.
//
// Attendance used to be queued here too. It is not any more: addon attendance is either posted
// directly by a member with "Manage attendance with the addon", or, for a `/lsm now` capture by
// anyone else, lands as a Pending snapshot an officer confirms in Unlinked Snapshots.
//
// Approval calls into existing logic where possible
// (ActivityDataController.AdjustTodLootDkpAsync for ToD loot DKP) so DKP math
// stays in lockstep with the immediate-create paths.
public sealed class SubmissionApprovalService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<SubmissionApprovalService> _logger;

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public SubmissionApprovalService(
        ApplicationDbContext db,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ILogger<SubmissionApprovalService> logger)
    {
        _db = db;
        _dkpLedger = dkpLedger;
        _dkpPools = dkpPools;
        _logger = logger;
    }

    // ---------- Queue: submission entry points ----------

    public async Task<int> QueueTodAsync(int linkshellId, string submittedByAppUserId, TodSubmissionInput input, CancellationToken cancellationToken)
    {
        var submission = new PendingTodSubmission
        {
            LinkshellId = linkshellId,
            SubmittedByAppUserId = submittedByAppUserId,
            SubmittedAtUtc = DateTime.UtcNow,
            MonsterName = input.MonsterName?.Trim(),
            DayNumber = input.DayNumber,
            Claim = input.Claim,
            Time = input.Time,
            Cooldown = string.IsNullOrWhiteSpace(input.Cooldown) ? null : input.Cooldown.Trim(),
            Interval = string.IsNullOrWhiteSpace(input.Interval) ? null : input.Interval.Trim(),
            RepopTime = input.RepopTime,
            ImagePath = input.ImagePath,
            // Only a positive window is a reading; 0 / negative means the submitter had none.
            PopWindow = input.PopWindow is { } pw && pw > 0 ? pw : null,
        };

        foreach (var loot in input.LootRows ?? Enumerable.Empty<TodSubmissionLootInput>())
        {
            if (string.IsNullOrWhiteSpace(loot.ItemName) && string.IsNullOrWhiteSpace(loot.ItemWinner) && !loot.WinningDkpSpent.HasValue) continue;
            submission.LootRows.Add(new PendingTodLootSubmission
            {
                ItemName = loot.ItemName?.Trim(),
                ItemWinner = loot.ItemWinner?.Trim(),
                WinningDkpSpent = loot.WinningDkpSpent,
            });
        }

        _db.PendingTodSubmissions.Add(submission);
        await _db.SaveChangesAsync(cancellationToken);
        return submission.Id;
    }

    // ---------- Approve: materialize the real record + delete pending ----------

    public async Task<ApprovalResult> ApproveTodAsync(int submissionId, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingTodSubmissions
            .Include(s => s.LootRows)
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;

        var occurredAt = DateTime.UtcNow;
        var todTimeUtc = pending.Time;

        var tod = new Tod
        {
            MonsterName = pending.MonsterName,
            DayNumber = pending.DayNumber,
            Claim = pending.Claim,
            Time = todTimeUtc,
            Cooldown = pending.Cooldown,
            Interval = pending.Interval,
            RepopTime = pending.RepopTime ?? (todTimeUtc?.AddHours(ResolveCooldownHours(pending.Cooldown))),
            LinkshellId = pending.LinkshellId,
            TimeStamp = occurredAt,
            TotalTods = 1,
            TotalClaims = pending.Claim == true ? 1 : 0,
            ImagePath = pending.ImagePath,
            // Recorded when the pop was seen, not when the officer got round to approving it.
            PopWindow = pending.PopWindow,
        };
        // TodId is stamped after the Tod is saved below; the affordability pre-check doesn't need
        // it, so the rows are built first in order to run that check before anything is persisted.
        var lootDetails = pending.LootRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ItemName) || !string.IsNullOrWhiteSpace(r.ItemWinner) || r.WinningDkpSpent.HasValue)
            .Select(r => new TodLootDetail
            {
                ItemName = r.ItemName,
                ItemWinner = r.ItemWinner,
                WinningDkpSpent = r.WinningDkpSpent,
            })
            .ToList();

        // Reject BEFORE materializing anything. Approving into an overdraft would either mint
        // negative DKP or strand a half-approved ToD with the submission still queued; leaving the
        // submission untouched lets the officer fix the DKP (or edit the loot down) and retry.
        if (lootDetails.Count > 0)
        {
            var insufficient = await ActivityDataController.AdjustTodLootDkpAsync(
                _db, _dkpLedger, _dkpPools, tod, lootDetails, occurredAt, isRefund: false, cancellationToken,
                checkOnly: true);
            if (insufficient is not null)
            {
                return ApprovalResult.InsufficientDkp;
            }
        }

        _db.Tods.Add(tod);
        await _db.SaveChangesAsync(cancellationToken);

        if (lootDetails.Count > 0)
        {
            foreach (var lootDetail in lootDetails)
            {
                lootDetail.TodId = tod.Id;
            }
            await _db.TodLootDetails.AddRangeAsync(lootDetails, cancellationToken);
            await ActivityDataController.AdjustTodLootDkpAsync(
                _db, _dkpLedger, _dkpPools, tod, lootDetails, occurredAt, isRefund: false, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Delete the pending submission and its loot child rows (cascade).
        _db.PendingTodSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);

        // A new ToD = a new pop window, so reset any party sign-ups assigned to
        // this monster (the old roster is for the pop that just happened).
        await PartySetupController.ClearSignupsForMonsterAsync(_db, tod.LinkshellId, tod.MonsterName, cancellationToken);

        // Approving a member's ToD puts no board up either, for the same reason the addon's own
        // post doesn't: a board comes back only through Repeat-on-ToD, at its lead.

        return ApprovalResult.Approved;
    }

    // ---------- Review: the reviewer's corrections, then approve ----------
    // A reviewer may correct a pending ToD and its loot before approving it. This applies the
    // corrections to the pending rows, then runs the normal approve above. Shared by the Razor
    // review pages and the Activity's ToD Submissions Needing Approval section.
    // Scoped to linkshellId: a submission from another linkshell is NotFound and never touched.
    //
    // The corrections are saved before approving, so when approve answers InsufficientDkp the
    // pending row keeps them and the reviewer can fix the loot and approve again.

    public async Task<ApprovalResult> ApproveTodWithEditsAsync(
        int linkshellId, int submissionId, PendingTodEdits edits, CancellationToken cancellationToken)
    {
        var submission = await _db.PendingTodSubmissions
            .Include(s => s.LootRows)
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (submission is null) return ApprovalResult.NotFound;

        submission.MonsterName = string.IsNullOrWhiteSpace(edits.MonsterName) ? submission.MonsterName : edits.MonsterName.Trim();
        submission.DayNumber = edits.DayNumber;
        submission.Claim = edits.Claim;
        submission.Time = edits.Time;
        submission.Cooldown = string.IsNullOrWhiteSpace(edits.Cooldown) ? submission.Cooldown : edits.Cooldown.Trim();
        submission.Interval = string.IsNullOrWhiteSpace(edits.Interval) ? submission.Interval : edits.Interval.Trim();
        submission.RepopTime = edits.RepopTime;

        // Replace loot rows wholesale with the edited list so removed rows disappear before
        // materialization. A row with nothing in it is a blank form line, not loot.
        _db.PendingTodLootSubmissions.RemoveRange(submission.LootRows);
        foreach (var row in edits.LootRows ?? Array.Empty<TodSubmissionLootInput>())
        {
            if (string.IsNullOrWhiteSpace(row.ItemName) && string.IsNullOrWhiteSpace(row.ItemWinner) && !row.WinningDkpSpent.HasValue) continue;
            submission.LootRows.Add(new PendingTodLootSubmission
            {
                ItemName = row.ItemName?.Trim(),
                ItemWinner = row.ItemWinner?.Trim(),
                WinningDkpSpent = row.WinningDkpSpent,
            });
        }
        await _db.SaveChangesAsync(cancellationToken);

        return await ApproveTodAsync(submissionId, cancellationToken);
    }

    // ---------- Reject: delete pending row, no side effects ----------
    // Scoped to linkshellId like the review methods above, so a reviewer in one linkshell can't
    // delete another linkshell's submission by guessing its id.

    public async Task<ApprovalResult> RejectTodAsync(int linkshellId, int submissionId, string? notes, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingTodSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;
        _db.PendingTodSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Rejected;
    }

    // ---------- Helpers ----------

    // Same fix as TodController.ResolveCooldownHours: this compared against "72 Hour" alone, so
    // approving a member-submitted 84/71/2 Hour or 5 Min ToD recomputed its repop at 22 hours.
    private static double ResolveCooldownHours(string? cooldown) =>
        Controllers.ActivityDataController.ResolveTodCooldownHours(cooldown);
}

// InsufficientDkp: the submission is intact and still queued — a loot winner can't afford it.
public enum ApprovalResult { Approved, Rejected, NotFound, InsufficientDkp }

public sealed record TodSubmissionInput(
    string? MonsterName,
    int? DayNumber,
    bool? Claim,
    DateTime? Time,
    string? Cooldown,
    string? Interval,
    DateTime? RepopTime,
    string? ImagePath,
    IReadOnlyList<TodSubmissionLootInput>? LootRows,
    // Which spawn window it popped on. Optional so the surfaces that have no such field (the web
    // and Activity ToD forms post it directly) keep their existing call shape.
    int? PopWindow = null);

public sealed record TodSubmissionLootInput(string? ItemName, string? ItemWinner, int? WinningDkpSpent);

// A reviewer's corrections to a pending ToD (ApproveTodWithEditsAsync). A blank monster, cooldown
// or interval keeps what was submitted; LootRows replaces the submitted loot wholesale.
public sealed record PendingTodEdits(
    string? MonsterName,
    int? DayNumber,
    bool? Claim,
    DateTime? Time,
    string? Cooldown,
    string? Interval,
    DateTime? RepopTime,
    IReadOnlyList<TodSubmissionLootInput>? LootRows);
