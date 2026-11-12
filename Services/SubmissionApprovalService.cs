using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Encapsulates the queue-pending-row + approve-materializes-real-row pattern for ToDs that members
// without the add-ToD permission submit for review.
//
// A ToD carries no loot. Submissions used to queue loot rows (PendingTodLootSubmissions, dropped
// 2026-11-09) and approval charged DKP for them; loot is recorded in the Loot tab against an event
// instead.
//
// Attendance used to be queued here too. It is not any more: addon attendance is either posted
// directly by a member with "Manage attendance with the addon", or, for a `/lsm now` capture by
// anyone else, lands as a Pending snapshot an officer confirms in Unlinked Snapshots.
public sealed class SubmissionApprovalService
{
    private readonly ApplicationDbContext _db;
    private readonly TodRecorder _recorder;
    private readonly ILogger<SubmissionApprovalService> _logger;

    public SubmissionApprovalService(
        ApplicationDbContext db,
        TodRecorder recorder,
        ILogger<SubmissionApprovalService> logger)
    {
        _db = db;
        _recorder = recorder;
        _logger = logger;
    }

    // ---------- Queue: submission entry points ----------

    public async Task<int> QueueTodAsync(int linkshellId, string submittedByAppUserId, TodSubmissionInput input, CancellationToken cancellationToken)
    {
        // Another member already queued this kill: one thing to approve, holding what both knew.
        if (input.Time is { } defeatedAtUtc
            && await TodSameKill.FindPendingAsync(_db, linkshellId, input.MonsterName, defeatedAtUtc, cancellationToken) is { } sameKill)
        {
            sameKill.Claim ??= input.Claim;
            sameKill.PopWindow = TodSameKill.EarliestWindow(
                sameKill.PopWindow, input.PopWindow is { } window && window > 0 ? window : null);
            await _db.SaveChangesAsync(cancellationToken);
            return sameKill.Id;
        }

        var submission = new PendingTodSubmission
        {
            LinkshellId = linkshellId,
            SubmittedByAppUserId = submittedByAppUserId,
            SubmittedAtUtc = DateTime.UtcNow,
            MonsterName = input.MonsterName?.Trim(),
            DayNumber = input.DayNumber,
            Claim = input.Claim,
            Hq = input.Hq,
            AdditionalSeconds = Math.Max(0, input.AdditionalSeconds),
            Time = input.Time,
            Cooldown = string.IsNullOrWhiteSpace(input.Cooldown) ? null : input.Cooldown.Trim(),
            Interval = string.IsNullOrWhiteSpace(input.Interval) ? null : input.Interval.Trim(),
            RepopTime = input.RepopTime,
            ImagePath = input.ImagePath,
            // Only a positive window is a reading; 0 / negative means the submitter had none.
            PopWindow = input.PopWindow is { } pw && pw > 0 ? pw : null,
        };

        _db.PendingTodSubmissions.Add(submission);
        await _db.SaveChangesAsync(cancellationToken);
        return submission.Id;
    }

    // ---------- Approve: materialize the real record + delete pending ----------

    public async Task<ApprovalResult> ApproveTodAsync(int submissionId, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingTodSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;

        // Staged first, so the recorder's save takes the submission off the queue in the same commit
        // as the ToD it becomes. The save below covers a kill that was already saved with nothing new
        // to add, where the recorder has nothing of its own to save.
        _db.PendingTodSubmissions.Remove(pending);

        // Saved the one shared way. When the kill got its ToD while this waited (an officer's post),
        // approving adds what this one knew to that ToD instead of saving a second row.
        await _recorder.RecordAsync(
            new TodRecordInput(
                pending.LinkshellId,
                pending.MonsterName,
                pending.Hq,
                pending.Time,
                pending.AdditionalSeconds,
                // Recorded when the pop was seen, not when the officer got round to approving it.
                pending.PopWindow,
                pending.Claim,
                Killed: null,
                pending.DayNumber,
                TodSources.Approval,
                pending.SubmittedByAppUserId)
            {
                Cooldown = pending.Cooldown,
                Interval = pending.Interval,
                ImagePath = pending.ImagePath,
                // The repop the member submitted, or the one the reviewer typed. Blank = worked out.
                RepopUtcOverride = pending.RepopTime,
            },
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        // Approving a member's ToD puts no board up either, for the same reason the addon's own
        // post doesn't: a board comes back only through Repeat-on-ToD, at its lead.

        return ApprovalResult.Approved;
    }

    // ---------- Review: the reviewer's corrections, then approve ----------
    // A reviewer may correct a pending ToD before approving it. This applies the corrections to the
    // pending row, then runs the normal approve above. Used by the Activity's ToD Submissions
    // Needing Approval section.
    // Scoped to linkshellId: a submission from another linkshell is NotFound and never touched.

    public async Task<ApprovalResult> ApproveTodWithEditsAsync(
        int linkshellId, int submissionId, PendingTodEdits edits, CancellationToken cancellationToken)
    {
        var submission = await _db.PendingTodSubmissions
            .FirstOrDefaultAsync(s => s.Id == submissionId && s.LinkshellId == linkshellId, cancellationToken);
        if (submission is null) return ApprovalResult.NotFound;

        var monsterName = string.IsNullOrWhiteSpace(edits.MonsterName) ? submission.MonsterName : edits.MonsterName.Trim();
        // A reviewer who picks a different monster has corrected which one died, and the editor has
        // no HQ box. So the new name decides HQ, as it did before the queue kept Hq: "Nidhogg" is the
        // HQ, "Fafnir" or "Fafnir/Nidhogg" the NQ, anything else neither. Without this, a member's
        // Nidhogg corrected to the NQ was still approved as HQ and sent the next board back to Day 1.
        if (!string.Equals(monsterName?.Trim(), submission.MonsterName?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            submission.Hq = HnmConfig.ResolveClaimHalf(monsterName, false).IsHq;
        }
        submission.MonsterName = monsterName;
        submission.DayNumber = edits.DayNumber;
        submission.Claim = edits.Claim;
        submission.Time = edits.Time;
        submission.Cooldown = string.IsNullOrWhiteSpace(edits.Cooldown) ? submission.Cooldown : edits.Cooldown.Trim();
        submission.Interval = string.IsNullOrWhiteSpace(edits.Interval) ? submission.Interval : edits.Interval.Trim();
        submission.RepopTime = edits.RepopTime;
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
}

public enum ApprovalResult { Approved, Rejected, NotFound }

public sealed record TodSubmissionInput(
    string? MonsterName,
    int? DayNumber,
    bool? Claim,
    DateTime? Time,
    string? Cooldown,
    string? Interval,
    DateTime? RepopTime,
    string? ImagePath,
    // Which spawn window it popped on. 0 / negative = not recorded.
    int? PopWindow = null,
    // Which half of a merge pair died, and the monster's Additional Seconds (already in RepopTime).
    bool Hq = false,
    int AdditionalSeconds = 0);

// A reviewer's corrections to a pending ToD (ApproveTodWithEditsAsync). A blank monster, cooldown
// or interval keeps what was submitted.
public sealed record PendingTodEdits(
    string? MonsterName,
    int? DayNumber,
    bool? Claim,
    DateTime? Time,
    string? Cooldown,
    string? Interval,
    DateTime? RepopTime);
