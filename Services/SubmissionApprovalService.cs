using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Encapsulates the queue-pending-row + approve-materializes-real-row pattern
// for the three submission types members can submit for officer review:
//   1. ToDs (with loot detail rows)
//   2. Per-window event attendance posts
//   3. Alliance attendance snapshots
//
// Approval calls into existing logic where possible
// (ActivityDataController.AdjustTodLootDkpAsync for ToD loot DKP) so DKP math
// stays in lockstep with the immediate-create paths. For per-window attendance,
// the v1 approve flow uses an exact-character-name match against the linkshell
// roster -- the alt-name resolution + self-character backfill in
// AddonApiController.PostAttendanceAsync isn't reproduced here yet.
public sealed class SubmissionApprovalService
{
    private readonly ApplicationDbContext _db;
    private readonly HnmAutoEventService _hnmAutoEvent;
    private readonly ILogger<SubmissionApprovalService> _logger;

    private readonly DkpLedgerWriter _dkpLedger;
    private readonly DkpPoolResolver _dkpPools;

    public SubmissionApprovalService(
        ApplicationDbContext db,
        HnmAutoEventService hnmAutoEvent,
        DkpLedgerWriter dkpLedger,
        DkpPoolResolver dkpPools,
        ILogger<SubmissionApprovalService> logger)
    {
        _db = db;
        _hnmAutoEvent = hnmAutoEvent;
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

    public async Task<int> QueueAttendanceWindowAsync(int linkshellId, string submittedByAppUserId, AttendanceWindowSubmissionInput input, CancellationToken cancellationToken)
    {
        var submission = new PendingAttendanceWindowSubmission
        {
            LinkshellId = linkshellId,
            SubmittedByAppUserId = submittedByAppUserId,
            SubmittedAtUtc = DateTime.UtcNow,
            EventId = input.EventId,
            WindowIndex = input.WindowIndex,
        };

        foreach (var member in input.Members ?? Enumerable.Empty<AttendanceWindowMemberInput>())
        {
            if (string.IsNullOrWhiteSpace(member.CharacterName)) continue;
            submission.Members.Add(new PendingAttendanceWindowMemberSubmission
            {
                CharacterName = member.CharacterName.Trim(),
                MainJob = member.MainJob,
                MainJobLevel = member.MainJobLevel,
                SubJob = member.SubJob,
                SubJobLevel = member.SubJobLevel,
            });
        }

        _db.PendingAttendanceWindowSubmissions.Add(submission);
        await _db.SaveChangesAsync(cancellationToken);
        return submission.Id;
    }

    public async Task<int> QueueSnapshotAsync(int linkshellId, string submittedByAppUserId, SnapshotSubmissionInput input, CancellationToken cancellationToken)
    {
        var submission = new PendingAttendanceSnapshotSubmission
        {
            LinkshellId = linkshellId,
            SubmittedByAppUserId = submittedByAppUserId,
            SubmittedAtUtc = DateTime.UtcNow,
            CapturedAtUtc = input.CapturedAtUtc,
            CapturedByCharacterName = input.CapturedByCharacterName?.Trim(),
            UtcOffset = input.UtcOffset?.Trim(),
            EntryCount = (input.Entries?.Count) ?? 0,
            Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim(),
        };

        foreach (var entry in input.Entries ?? Enumerable.Empty<SnapshotEntryInput>())
        {
            if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;
            submission.Entries.Add(new PendingAttendanceSnapshotEntry
            {
                CharacterName = entry.CharacterName.Trim(),
                MainJob = entry.MainJob,
                MainJobLevel = entry.MainJobLevel,
                SubJob = entry.SubJob,
                SubJobLevel = entry.SubJobLevel,
                Zone = entry.Zone,
            });
        }

        _db.PendingAttendanceSnapshotSubmissions.Add(submission);
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
        };
        _db.Tods.Add(tod);
        await _db.SaveChangesAsync(cancellationToken);

        var lootDetails = pending.LootRows
            .Where(r => !string.IsNullOrWhiteSpace(r.ItemName) || !string.IsNullOrWhiteSpace(r.ItemWinner) || r.WinningDkpSpent.HasValue)
            .Select(r => new TodLootDetail
            {
                TodId = tod.Id,
                ItemName = r.ItemName,
                ItemWinner = r.ItemWinner,
                WinningDkpSpent = r.WinningDkpSpent,
            })
            .ToList();

        if (lootDetails.Count > 0)
        {
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

        // Streamlined HNM workflow: mirror the immediate-addon path so an
        // approved member-submitted ToD also kicks off auto-event creation
        // when the captured monster is a tracked HNM. Failures here are
        // logged but don't roll back the approval -- the materialized Tod
        // is the contract of this method.
        try
        {
            await _hnmAutoEvent.CreateAutoEventForTodAsync(tod.Id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "HNM auto-event creation failed for approved tod {TodId}.", tod.Id);
        }

        return ApprovalResult.Approved;
    }

    public async Task<ApprovalResult> ApproveAttendanceWindowAsync(int submissionId, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingAttendanceWindowSubmissions
            .Include(s => s.Members)
            .Include(s => s.Event)
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;
        if (pending.Event is null) return ApprovalResult.NotFound;
        if (pending.Event.LinkshellId != pending.LinkshellId) return ApprovalResult.NotFound;

        var nowUtc = DateTime.UtcNow;

        // Find or create the EventAttendanceWindow for this sequence number.
        var attendanceWindow = await _db.EventAttendanceWindows
            .FirstOrDefaultAsync(w => w.EventId == pending.EventId && w.SequenceNumber == pending.WindowIndex, cancellationToken);
        if (attendanceWindow is null)
        {
            attendanceWindow = new EventAttendanceWindow
            {
                EventId = pending.EventId,
                SequenceNumber = pending.WindowIndex,
                // The POST count names the window (2 = Open/Close). Not the raw override, which on
                // an app-made HNM camp holds the SPAWN count and would number a king/dragon's
                // windows here while the addon's own posts on the same camp read Open / Close.
                Label = HnmConfig.GetDefaultWindowLabel(pending.Event.EventName, pending.WindowIndex, DiscordEventMessageBuilder.AttendancePostCount(pending.Event)),
                PostedAt = nowUtc,
                PostedBySource = "approval"
            };
            _db.EventAttendanceWindows.Add(attendanceWindow);
            await _db.SaveChangesAsync(cancellationToken);
        }

        // Exact-name match against the LS roster (v1 simplification — alt-name
        // resolution from PostAttendanceAsync is not replayed here).
        var memberNames = pending.Members.Select(m => m.CharacterName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var memberships = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == pending.LinkshellId
                        && m.AppUserId != null
                        && m.CharacterName != null
                        && memberNames.Contains(m.CharacterName))
            .ToListAsync(cancellationToken);
        var membershipByName = memberships
            .Where(m => !string.IsNullOrWhiteSpace(m.CharacterName))
            .GroupBy(m => m.CharacterName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var existingParticipations = memberships.Count == 0
            ? new Dictionary<string, AppUserEvent>(StringComparer.Ordinal)
            : await _db.AppUserEvents
                .Where(ue => ue.EventId == pending.EventId && memberships.Select(m => m.AppUserId).Contains(ue.AppUserId))
                .ToDictionaryAsync(ue => ue.AppUserId!, cancellationToken);

        // Keyed on the DENORMALIZED AppUserId rather than the participation id: a cleared roster
        // replaces a member's AppUserEvent while their snapshots survive, so participation-keying
        // would let the same person be credited twice for one window. Mirrors the addon's
        // PostAttendanceAsync.
        var existingWindowAppUserIds = (await _db.AppUserEventWindows
                .Where(w => w.EventAttendanceWindowId == attendanceWindow.Id && w.AppUserId != null)
                .Select(w => w.AppUserId!)
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.Ordinal);

        var matched = 0;
        foreach (var member in pending.Members)
        {
            if (!membershipByName.TryGetValue(member.CharacterName.Trim(), out var membership)) continue;
            if (string.IsNullOrWhiteSpace(membership.AppUserId)) continue;

            existingParticipations.TryGetValue(membership.AppUserId, out var participation);
            if (participation is null)
            {
                participation = new AppUserEvent
                {
                    AppUserId = membership.AppUserId,
                    EventId = pending.EventId,
                    CharacterName = membership.CharacterName,
                    JobName = string.IsNullOrWhiteSpace(member.MainJob) ? null : member.MainJob.Trim(),
                    SubJobName = string.IsNullOrWhiteSpace(member.SubJob) ? null : member.SubJob.Trim(),
                    JobType = null,
                    StartTime = nowUtc,
                    IsQuickJoin = true,
                    IsVerified = true
                };
                _db.AppUserEvents.Add(participation);
                existingParticipations[membership.AppUserId] = participation;
            }
            else
            {
                if (participation.IsVerified != true)
                {
                    participation.IsVerified = true;
                    participation.StartTime ??= nowUtc;
                }
            }

            if (!existingWindowAppUserIds.Add(membership.AppUserId)) continue;

            _db.AppUserEventWindows.Add(new AppUserEventWindow
            {
                AppUserEvent = participation,
                EventAttendanceWindow = attendanceWindow,
                // Denormalized so the snapshot outlives a cleared roster — see AppUserEventWindow.
                AppUserId = membership.AppUserId,
                CharacterName = participation.CharacterName ?? membership.CharacterName,
                VerifiedAt = nowUtc,
                VerifiedBy = "approval",
            });

            _db.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
            {
                AppUserEvent = participation,
                EventId = pending.EventId,
                AppUserId = membership.AppUserId,
                ActionType = "Verify",
                OccurredAt = nowUtc,
                RequiresVerification = false,
                VerifiedAt = nowUtc,
                VerifiedBy = "approval",
                Source = "approval",
                EventAttendanceWindow = attendanceWindow,
            });

            matched++;
        }

        _db.PendingAttendanceWindowSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Approved;
    }

    public async Task<ApprovalResult> ApproveSnapshotAsync(int submissionId, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingAttendanceSnapshotSubmissions
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;

        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = pending.LinkshellId,
            CapturedAtUtc = pending.CapturedAtUtc,
            CapturedByCharacterName = pending.CapturedByCharacterName ?? string.Empty,
            UtcOffset = pending.UtcOffset,
            EntryCount = pending.Entries.Count,
            CreatedAtUtc = DateTime.UtcNow,
            Name = pending.Name,
            LinkedEventId = pending.LinkedEventId,
        };
        _db.AttendanceSnapshots.Add(snapshot);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var entry in pending.Entries)
        {
            _db.AttendanceSnapshotEntries.Add(new AttendanceSnapshotEntry
            {
                SnapshotId = snapshot.Id,
                CharacterName = entry.CharacterName,
                MainJob = entry.MainJob,
                MainJobLevel = entry.MainJobLevel,
                SubJob = entry.SubJob,
                SubJobLevel = entry.SubJobLevel,
                Zone = entry.Zone,
            });
        }

        _db.PendingAttendanceSnapshotSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Approved;
    }

    // ---------- Reject: delete pending row, no side effects ----------

    public async Task<ApprovalResult> RejectTodAsync(int submissionId, string? notes, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingTodSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;
        _db.PendingTodSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Rejected;
    }

    public async Task<ApprovalResult> RejectAttendanceWindowAsync(int submissionId, string? notes, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingAttendanceWindowSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;
        _db.PendingAttendanceWindowSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Rejected;
    }

    public async Task<ApprovalResult> RejectSnapshotAsync(int submissionId, string? notes, CancellationToken cancellationToken)
    {
        var pending = await _db.PendingAttendanceSnapshotSubmissions.FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (pending is null) return ApprovalResult.NotFound;
        _db.PendingAttendanceSnapshotSubmissions.Remove(pending);
        await _db.SaveChangesAsync(cancellationToken);
        return ApprovalResult.Rejected;
    }

    // ---------- Helpers ----------

    private static double ResolveCooldownHours(string? cooldown)
    {
        return string.Equals(cooldown, ViewModels.TodManagerViewModel.SeventyTwoHourCooldown, StringComparison.OrdinalIgnoreCase)
            ? 72d
            : 22d;
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
    IReadOnlyList<TodSubmissionLootInput>? LootRows);

public sealed record TodSubmissionLootInput(string? ItemName, string? ItemWinner, int? WinningDkpSpent);

public sealed record AttendanceWindowSubmissionInput(int EventId, int WindowIndex, IReadOnlyList<AttendanceWindowMemberInput>? Members);

public sealed record AttendanceWindowMemberInput(string CharacterName, string? MainJob, int? MainJobLevel, string? SubJob, int? SubJobLevel);

public sealed record SnapshotSubmissionInput(DateTime CapturedAtUtc, string? CapturedByCharacterName, string? UtcOffset, IReadOnlyList<SnapshotEntryInput>? Entries, string? Name = null);

public sealed record SnapshotEntryInput(string CharacterName, string? MainJob, int? MainJobLevel, string? SubJob, int? SubJobLevel, string? Zone);
