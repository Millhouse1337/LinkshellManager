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
    [HttpGet("dkp-history")]
    public async Task<IActionResult> GetDkpHistoryAsync(int? linkshellId, string? appUserId, CancellationToken cancellationToken)
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
        var linkshellMembers = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var memberDtos = linkshellMembers
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new ActivityDkpHistoryMemberDto(
                link.AppUserId!,
                link.CharacterName ?? "Unknown member",
                link.LinkshellDkp ?? 0))
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
            projected));
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
            !string.Equals(mode, "Misc", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Audit mode must be 'Adjust' or 'Misc'." });
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

            var original = await _dbContext.DkpLedgerEntries.AsNoTracking().FirstOrDefaultAsync(
                entry => entry.Id == request.RelatedLedgerEntryId.Value &&
                         entry.LinkshellId == request.LinkshellId &&
                         entry.AppUserId == request.TargetAppUserId,
                cancellationToken);
            if (original is null)
            {
                return NotFound(new { error = "The selected original ledger entry was not found for this member." });
            }

            deltaAmount = request.Amount - original.Amount;
            newEntry = new DkpLedgerEntry
            {
                AppUserId = request.TargetAppUserId,
                LinkshellId = request.LinkshellId,
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
                Details = $"Audit adjustment by {officerName}: {reason} (entry #{original.Id} was {original.Amount:+0.##;-0.##;0}, corrected to {request.Amount:+0.##;-0.##;0})."
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
        await _sheetSync.EnqueueAsync(targetMembership.LinkshellId, cancellationToken);
        return Ok(new { success = true });
    }
}
