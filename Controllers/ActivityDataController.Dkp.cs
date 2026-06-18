using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    public sealed record ActivityDkpAddCandidateDto(
        int WindowEventId,
        string Label,
        DateTime OccurredAt,
        double Amount,
        string? EventName,
        string? EntryType,
        string? PrimaryZone,
        int MemberCount);

    [HttpGet("dkp-history")]
    public async Task<IActionResult> GetDkpHistoryAsync(
        int? linkshellId,
        string? appUserId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load DKP History."
            });
        }

        var accessibleMemberships = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == appUser.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (accessibleMemberships.Count == 0)
        {
            return Ok(new ActivityDkpHistoryDto(
                null,
                null,
                null,
                null,
                0,
                Array.Empty<ActivityDkpHistoryMemberDto>(),
                Array.Empty<ActivityDkpLedgerEntryDto>()));
        }

        var selectedLinkshellId = linkshellId
            ?? appUser.PrimaryLinkshellId
            ?? accessibleMemberships.First().LinkshellId;

        if (accessibleMemberships.All(link => link.LinkshellId != selectedLinkshellId))
        {
            return Forbid();
        }

        var selectedLinkshell = accessibleMemberships.First(link => link.LinkshellId == selectedLinkshellId);
        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(selectedLinkshellId, cancellationToken);

        var linkshellMembers = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // DKP each member has spent on loot in still-live events but that hasn't been
        // committed to the ledger yet. Shown as a pending deduction; already removed from
        // biddable power so it can't be double-spent before the event closes.
        var pendingLootByUser = await AuctionDkpService.ComputePendingLiveLootSpendByUserAsync(
            _dbContext, selectedLinkshellId, cancellationToken);

        var memberDtos = linkshellMembers
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new ActivityDkpHistoryMemberDto(
                link.AppUserId!,
                link.CharacterName ?? "Unknown member",
                link.LinkshellDkp ?? 0,
                pendingLootByUser.GetValueOrDefault(link.AppUserId!)))
            .ToList();

        if (memberDtos.Count == 0)
        {
            return Ok(new ActivityDkpHistoryDto(
                selectedLinkshellId,
                selectedLinkshell.Linkshell?.LinkshellName ?? "Unknown linkshell",
                null,
                null,
                0,
                Array.Empty<ActivityDkpHistoryMemberDto>(),
                Array.Empty<ActivityDkpLedgerEntryDto>()));
        }

        var selectedAppUserId = string.IsNullOrWhiteSpace(appUserId) || memberDtos.All(member => member.AppUserId != appUserId)
            ? memberDtos.FirstOrDefault(member => member.AppUserId == appUser.Id)?.AppUserId ?? memberDtos.First().AppUserId
            : appUserId;

        var ledgerEntries = await _dbContext.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == selectedLinkshellId && entry.AppUserId == selectedAppUserId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Sequence)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        // Running balance is computed in chronological (oldest-first) order so
        // each entry's "after" balance is correct, then the list is reversed
        // so the activity UI shows the most recent entry at the top.
        var runningBalance = 0d;
        var projected = ledgerEntries
            .Select(entry =>
            {
                runningBalance += entry.Amount;
                return new ActivityDkpLedgerEntryDto(
                    entry.Id,
                    entry.EntryType,
                    entry.Amount,
                    runningBalance,
                    entry.OccurredAt,
                    entry.EventName,
                    entry.EventType,
                    entry.EventLocation,
                    entry.EventStartTime,
                    entry.EventEndTime,
                    entry.ItemName,
                    entry.Details,
                    entry.EditReason);
            })
            .ToList();
        projected.Reverse();

        return Ok(new ActivityDkpHistoryDto(
            selectedLinkshellId,
            selectedLinkshell.Linkshell?.LinkshellName ?? "Unknown linkshell",
            selectedAppUserId,
            memberDtos.First(member => member.AppUserId == selectedAppUserId).CharacterName,
            memberDtos.First(member => member.AppUserId == selectedAppUserId).CurrentBalance,
            memberDtos,
            projected,
            pendingLootByUser.GetValueOrDefault(selectedAppUserId ?? string.Empty)));
    }

    [HttpPost("dkp-audit")]
    public async Task<IActionResult> CreateDkpAuditAsync(
        [FromBody] ActivityDkpAuditRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to adjust DKP."
            });
        }

        if (request.LinkshellId <= 0 || string.IsNullOrWhiteSpace(request.TargetAppUserId))
        {
            return BadRequest(new { error = "Linkshell and target member are required." });
        }

        var mode = request.Mode?.Trim();
        if (!string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Add", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Misc", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Audit mode must be 'Adjust', 'Add', or 'Misc'." });
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { error = "A reason for the audit is required." });
        }

        if (request.Reason.Length > 500)
        {
            return BadRequest(new { error = "Audit reason must be 500 characters or fewer." });
        }

        // Bound the absolute audit amount to a sane range so a typo (or
        // adversarial bid) can't corrupt a member's balance with extreme values.
        const double MaxAuditAbsAmount = 1_000_000d;
        if (double.IsNaN(request.Amount) || double.IsInfinity(request.Amount))
        {
            return BadRequest(new { error = "Audit amount must be a finite number." });
        }
        if (Math.Abs(request.Amount) > MaxAuditAbsAmount)
        {
            return BadRequest(new { error = $"Audit amount must be between -{MaxAuditAbsAmount:N0} and {MaxAuditAbsAmount:N0}." });
        }

        var membership = await GetMembershipAsync(appUser.Id, request.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanAuditDkp, cancellationToken))
        {
            return Forbid();
        }

        var targetMembership = await _dbContext.AppUserLinkshells
            .FirstOrDefaultAsync(link =>
                link.LinkshellId == request.LinkshellId &&
                link.AppUserId == request.TargetAppUserId,
                cancellationToken);
        if (targetMembership is null)
        {
            return NotFound(new { error = "The selected member is not in this linkshell." });
        }

        var nowUtc = DateTime.UtcNow;
        var officerName = appUser.CharacterName ?? appUser.UserName ?? "Officer";
        var reason = request.Reason.Trim();

        var nextSequence = await _dbContext.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == request.LinkshellId && entry.AppUserId == request.TargetAppUserId)
            .Select(entry => (int?)entry.Sequence)
            .MaxAsync(cancellationToken);
        var sequence = (nextSequence ?? 0) + 1;

        DkpLedgerEntry newEntry;
        double deltaAmount;

        if (string.Equals(mode, "Adjust", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.RelatedLedgerEntryId.HasValue)
            {
                return BadRequest(new { error = "A related ledger entry is required when adjusting a previous entry." });
            }

            var original = await _dbContext.DkpLedgerEntries.FirstOrDefaultAsync(
                entry => entry.Id == request.RelatedLedgerEntryId.Value &&
                         entry.LinkshellId == request.LinkshellId &&
                         entry.AppUserId == request.TargetAppUserId,
                cancellationToken);
            if (original is null)
            {
                return NotFound(new { error = "The selected original ledger entry was not found for this member." });
            }

            var priorAuditDelta = await _dbContext.DkpLedgerEntries
                .Where(entry =>
                    entry.LinkshellId == request.LinkshellId &&
                    entry.AppUserId == request.TargetAppUserId &&
                    entry.AuditRelatedLedgerEntryId == original.Id)
                .SumAsync(entry => (double?)entry.Amount, cancellationToken) ?? 0d;
            var currentAuditedAmount = original.Amount + priorAuditDelta;
            deltaAmount = request.Amount - currentAuditedAmount;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
                AuditRelatedLedgerEntryId = original.Id,
                EntryType = "AuditAdjustment",
                Amount = deltaAmount,
                Sequence = sequence,
                OccurredAt = nowUtc,
                CharacterName = targetMembership.CharacterName,
                EventName = original.EventName,
                EventType = original.EventType,
                EventLocation = original.EventLocation,
                EventStartTime = original.EventStartTime,
                EventEndTime = original.EventEndTime,
                ItemName = original.ItemName,
                Details = $"Audit adjustment by {officerName}: {reason} (entry #{original.Id} was {currentAuditedAmount:+0.##;-0.##;0}, corrected to {request.Amount:+0.##;-0.##;0}).",
                SourceWindowEventId = original.SourceWindowEventId,
                AttInputRowNumber = original.AttInputRowNumber
            };
        }
        else if (string.Equals(mode, "Add", StringComparison.OrdinalIgnoreCase))
        {
            if (!request.SourceWindowEventId.HasValue)
            {
                return BadRequest(new { error = "A snapshot entry is required when adding a missing member." });
            }

            var windowEventId = request.SourceWindowEventId.Value;

            var alreadyRecorded = await _dbContext.DkpLedgerEntries.AnyAsync(entry =>
                entry.LinkshellId == request.LinkshellId &&
                entry.AppUserId == request.TargetAppUserId &&
                entry.SourceWindowEventId == windowEventId &&
                entry.EntryType == "SnapshotEarned",
                cancellationToken);
            if (alreadyRecorded)
            {
                return BadRequest(new { error = "That member already has DKP recorded for the selected snapshot entry." });
            }

            var windowEvent = await _dbContext.WindowEvents
                .AsNoTracking()
                .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
                .FirstOrDefaultAsync(w => w.Id == windowEventId && w.LinkshellId == request.LinkshellId, cancellationToken);
            if (windowEvent is null)
            {
                return NotFound(new { error = "The selected snapshot entry was not found." });
            }

            if (!windowEvent.DkpAmount.HasValue || !WindowEventEntryTypes.IsValid(windowEvent.EntryType))
            {
                return BadRequest(new { error = "The selected snapshot entry has no posted DKP amount." });
            }

            // Derive the snapshot's representative time + zone from the captured
            // attendance data (DB only — this used to come back from the sheet row).
            var activeSnapshots = windowEvent.Snapshots
                .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
                .OrderByDescending(s => s.CapturedAtUtc)
                .ToList();
            var occurredAt = activeSnapshots.FirstOrDefault()?.CapturedAtUtc ?? windowEvent.LastCapturedAtUtc;
            var primaryZone = activeSnapshots
                .SelectMany(s => s.Entries)
                .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Key)
                .FirstOrDefault();

            deltaAmount = windowEvent.DkpAmount.Value;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
                EntryType = "SnapshotEarned",
                Amount = deltaAmount,
                Sequence = sequence,
                OccurredAt = occurredAt,
                CharacterName = targetMembership.CharacterName,
                EventName = string.IsNullOrWhiteSpace(windowEvent.Name) ? "Window Event" : windowEvent.Name,
                EventType = windowEvent.EntryType,
                EventLocation = primaryZone,
                EventStartTime = windowEvent.FirstCapturedAtUtc,
                EventEndTime = windowEvent.LastCapturedAtUtc,
                Details = $"Added to snapshot entry by {officerName}: {reason}",
                SourceWindowEventId = windowEvent.Id
            };
        }
        else
        {
            deltaAmount = request.Amount;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
                EntryType = "AuditMisc",
                Amount = deltaAmount,
                Sequence = sequence,
                OccurredAt = nowUtc,
                CharacterName = targetMembership.CharacterName,
                Details = $"Audit by {officerName}: {reason}"
            };
        }

        targetMembership.LinkshellDkp = (targetMembership.LinkshellDkp ?? 0) + deltaAmount;
        _dbContext.DkpLedgerEntries.Add(newEntry);

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpGet("dkp-audit/add-candidates")]
    public async Task<IActionResult> GetDkpAuditAddCandidatesAsync(
        int linkshellId,
        string? targetAppUserId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to load DKP audit entries." });
        }

        if (linkshellId <= 0)
        {
            return BadRequest(new { error = "A linkshell selection is required." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanAuditDkp, cancellationToken))
        {
            return Forbid();
        }

        await _windowEventDkpLedger.EnsurePostedWindowEventLedgerEntriesForLinkshellAsync(linkshellId, cancellationToken);

        var creditedWindowEventIds = string.IsNullOrWhiteSpace(targetAppUserId)
            ? new HashSet<int>()
            : (await _dbContext.DkpLedgerEntries
                .Where(entry =>
                    entry.LinkshellId == linkshellId &&
                    entry.AppUserId == targetAppUserId &&
                    entry.SourceWindowEventId.HasValue &&
                    entry.EntryType == "SnapshotEarned")
                .Select(entry => entry.SourceWindowEventId!.Value)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var windowEvents = await _dbContext.WindowEvents
            .AsNoTracking()
            .Where(w =>
                w.LinkshellId == linkshellId &&
                w.PostedToSheetAt.HasValue &&
                w.DkpAmount.HasValue &&
                w.EntryType != null)
            .OrderByDescending(w => w.LastCapturedAtUtc)
            .Take(100)
            .Include(w => w.Snapshots).ThenInclude(s => s.Entries)
            .ToListAsync(cancellationToken);

        var candidates = windowEvents
            .Where(w => !creditedWindowEventIds.Contains(w.Id))
            .Select(w =>
            {
                var activeEntries = w.Snapshots
                    .Where(s => s.SnapshotStatus == AttendanceSnapshotStatuses.Active)
                    .SelectMany(s => s.Entries)
                    .ToList();
                var primaryZone = activeEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Zone))
                    .GroupBy(e => e.Zone!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.Key)
                    .FirstOrDefault();
                var name = string.IsNullOrWhiteSpace(w.Name) ? "Window Event" : w.Name!;
                var label = $"{w.LastCapturedAtUtc:MM/dd/yyyy} - {name} - {w.DkpAmount:0.##} DKP";
                return new ActivityDkpAddCandidateDto(
                    w.Id,
                    label,
                    w.LastCapturedAtUtc,
                    w.DkpAmount!.Value,
                    name,
                    w.EntryType,
                    primaryZone,
                    activeEntries
                        .Where(e => !string.IsNullOrWhiteSpace(e.CharacterName))
                        .Select(e => e.CharacterName.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count());
            })
            .ToList();

        return Ok(new { entries = candidates });
    }
}
