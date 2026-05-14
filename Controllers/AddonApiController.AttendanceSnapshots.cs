using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    public sealed record AddonAttendanceSnapshotEntryDto(
        string? CharacterName,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        string? Zone);

    public sealed record AddonAttendanceSnapshotRequest(
        DateTime? CapturedAtUtc,
        string? CapturedByCharacterName,
        string? UtcOffset,
        IReadOnlyList<AddonAttendanceSnapshotEntryDto>? Entries);

    [HttpPost("attendance-snapshots")]
    [AddonApiAuth]
    public async Task<IActionResult> PostAttendanceSnapshotAsync(
        [FromBody] AddonAttendanceSnapshotRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var entries = (request.Entries ?? Array.Empty<AddonAttendanceSnapshotEntryDto>())
            .Where(e => !string.IsNullOrWhiteSpace(e.CharacterName))
            .ToList();

        if (entries.Count == 0)
        {
            return BadRequest(new { error = "Snapshot must contain at least one entry." });
        }
        // FFXI alliance caps at 18 members (3 parties of 6). Reject anything
        // larger as malformed; the addon should never produce >18 rows.
        if (entries.Count > 18)
        {
            return BadRequest(new { error = "Snapshot exceeds the 18-member alliance maximum." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;
        var capturedAt = request.CapturedAtUtc.HasValue
                         && request.CapturedAtUtc.Value > nowUtc.AddDays(-7)
                         && request.CapturedAtUtc.Value < nowUtc.AddMinutes(5)
            ? request.CapturedAtUtc.Value
            : nowUtc;

        // Three-tier permission gate. Snapshots have no DKP effect but they
        // are visible to all linkshell members on the Attendance Snapshots
        // page, so an officer review gate stops members from spamming the
        // public list.
        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        var canModerate = role?.CanModerateLiveEvent == true;
        var canSubmit = role?.CanSubmitAttendanceForApproval == true;
        if (!canModerate && !canSubmit)
        {
            return Forbid();
        }

        if (!canModerate)
        {
            var approvalSvc = HttpContext.RequestServices.GetRequiredService<SubmissionApprovalService>();
            var inputEntries = entries
                .Select(e => new SnapshotEntryInput(
                    TruncateString(e.CharacterName, 256) ?? string.Empty,
                    TruncateString(e.MainJob, 8),
                    e.MainJobLevel,
                    TruncateString(e.SubJob, 8),
                    e.SubJobLevel,
                    TruncateString(e.Zone, 128)))
                .ToList();
            var submissionId = await approvalSvc.QueueSnapshotAsync(
                token.LinkshellId,
                token.IssuedToAppUserId!,
                new SnapshotSubmissionInput(
                    capturedAt,
                    TruncateString(request.CapturedByCharacterName, 256),
                    TruncateString(request.UtcOffset, 8),
                    inputEntries),
                cancellationToken);
            return Ok(new { pending = true, submissionId, entryCount = inputEntries.Count });
        }

        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = token.LinkshellId,
            CapturedAtUtc = capturedAt,
            CapturedByCharacterName = TruncateString(request.CapturedByCharacterName, 256),
            UtcOffset = TruncateString(request.UtcOffset, 8),
            EntryCount = entries.Count,
            CreatedAtUtc = nowUtc,
        };

        foreach (var e in entries)
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry
            {
                CharacterName = TruncateString(e.CharacterName, 256) ?? string.Empty,
                MainJob = TruncateString(e.MainJob, 8),
                MainJobLevel = e.MainJobLevel,
                SubJob = TruncateString(e.SubJob, 8),
                SubJobLevel = e.SubJobLevel,
                Zone = TruncateString(e.Zone, 128),
            });
        }

        _dbContext.AttendanceSnapshots.Add(snapshot);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            snapshotId = snapshot.Id,
            entryCount = snapshot.EntryCount,
            capturedAtUtc = snapshot.CapturedAtUtc,
        });
    }

    private static string? TruncateString(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
