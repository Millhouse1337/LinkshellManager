using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    // One member the addon confirmed landed an action on the mob, and the line
    // that proves it ("Azurth casts Dia on the Aspidochelone.").
    public sealed record AddonClaimShieldMember(string? Name, string? Action);

    public sealed record AddonClaimShieldRequest(
        string? Monster,
        bool Won,
        int? TotalPlayers,
        DateTime? CapturedAtUtc,
        string? CapturedMessage,
        // Names only. Kept because an addon built before actions were recorded
        // still sends this shape, and a claim record with no sentences beats
        // dropping the capture on the floor. MemberActions wins when both arrive.
        IReadOnlyList<string>? Members,
        IReadOnlyList<AddonClaimShieldMember>? MemberActions = null,
        // The camp that was linked in the addon when the pop landed. Verified
        // against the token's linkshell below; null falls back to a monster +
        // time match.
        int? EventId = null);

    // The camp a capture belongs to.
    //
    // The addon's own linked event is trusted first -- it knows what the officer
    // had open -- but only after checking it belongs to this linkshell, since it
    // arrives from the client. Everything else is matched here: a pop can be
    // captured by someone who hasn't linked the camp yet, and that record should
    // still land on the right event rather than nowhere.
    //
    // The match is deliberately narrow: same linkshell, the monster's name
    // appears in the event name, the camp had started, and it hadn't ended.
    // Newest first, so a re-pop of the same monster attaches to the current camp
    // rather than last week's.
    private async Task<int?> ResolveClaimShieldEventAsync(
        int? requestedEventId, int linkshellId, string monster, DateTime capturedAt,
        CancellationToken cancellationToken)
    {
        if (requestedEventId is int claimed && claimed > 0)
        {
            var owned = await _dbContext.Events
                .AsNoTracking()
                .AnyAsync(e => e.Id == claimed && e.LinkshellId == linkshellId, cancellationToken);
            if (owned)
            {
                return claimed;
            }
            // Fall through rather than reject: a stale id in the addon is not a
            // reason to lose the capture.
        }

        return await _dbContext.Events
            .AsNoTracking()
            .Where(e => e.LinkshellId == linkshellId
                        && e.EventName != null
                        && EF.Functions.Like(e.EventName, "%" + monster + "%")
                        && e.CommencementStartTime != null
                        && e.CommencementStartTime <= capturedAt
                        && (e.EndTime == null || e.EndTime >= capturedAt))
            .OrderByDescending(e => e.CommencementStartTime)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // Records one claim-shield lottery window the addon parsed out of chat.
    // Permission mirrors ToD (CanManageTods OR CanSubmitTodForApproval) but —
    // unlike PostTod — there is NO approval queue: a claim-shield capture has
    // no DKP / repop / auto-event side effect, so there is nothing for an
    // officer to approve. Both permission tiers store directly. The server is
    // authoritative for member resolution: it re-resolves the addon-sent names
    // against the live linkshell roster (same membershipByName index that
    // PostAttendanceAsync builds), so a stale addon roster cache can't poison
    // the stored record.
    //
    // ONE capture per camp. Every officer's addon reads the same lottery off chat, so a camp with
    // three officers at it used to collect three copies of one lottery. The first post makes the
    // capture; each later one JOINS it, adding only the people it doesn't have yet -- someone who
    // was in one officer's chat log and not another's is still counted. These posts land within
    // milliseconds of each other, so the joining goes through UniqueRaceRetry.
    //
    // A capture with no camp is stored as it arrives: with no camp to group by there is no
    // "same lottery" to join.
    [HttpPost("claim-shield-captures")]
    [AddonApiAuth]
    public Task<IActionResult> PostClaimShieldCaptureAsync(
        [FromBody] AddonClaimShieldRequest request,
        CancellationToken cancellationToken) =>
        UniqueRaceRetry.RunAsync(
            _dbContext,
            () => PostClaimShieldCaptureCoreAsync(request, cancellationToken),
            "ClaimShieldCaptures", "ClaimShieldCaptureMembers");

    private async Task<IActionResult> PostClaimShieldCaptureCoreAsync(
        AddonClaimShieldRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var monster = request.Monster?.Trim();
        if (string.IsNullOrWhiteSpace(monster))
        {
            return BadRequest(new { error = "Monster name is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        // Same clamp as attendance-snapshots: accept a recent client time,
        // otherwise fall back to now.
        var capturedAt = request.CapturedAtUtc.HasValue
                         && request.CapturedAtUtc.Value > nowUtc.AddDays(-7)
                         && request.CapturedAtUtc.Value < nowUtc.AddMinutes(5)
            ? request.CapturedAtUtc.Value
            : nowUtc;

        // Same bar as posting a ToD from the addon: Manage ToDs, or Submit ToD for approval.
        if (!await TokenIssuerCanAsync(
                token, token.LinkshellId, r => r.CanManageTods || r.CanSubmitTodForApproval, cancellationToken))
        {
            return Forbid();
        }

        var totalPlayers = request.TotalPlayers is > 0 ? request.TotalPlayers.Value : 0;

        // Authoritative member resolution — identical index construction to
        // PostAttendanceAsync (membership CharacterName + AppUser
        // CharacterName / AltCharacterName1 / AltCharacterName2,
        // first-write-wins, case-insensitive).
        var membershipsWithUser = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(m => m.LinkshellId == token.LinkshellId && m.AppUserId != null)
            .Join(_dbContext.Users.AsNoTracking(),
                  m => m.AppUserId,
                  u => u.Id,
                  (m, u) => new { Membership = m, User = u })
            .ToListAsync(cancellationToken);

        var membershipByName = new Dictionary<string, AppUserLinkshell>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in membershipsWithUser)
        {
            foreach (var candidate in new[]
                     {
                         pair.Membership.CharacterName,
                         pair.User.CharacterName,
                         pair.User.AltCharacterName1,
                         pair.User.AltCharacterName2,
                     })
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                var key = candidate.Trim();
                if (!membershipByName.ContainsKey(key))
                {
                    membershipByName[key] = pair.Membership;
                }
            }
        }

        var eventId = await ResolveClaimShieldEventAsync(
            request.EventId, token.LinkshellId, monster, capturedAt, cancellationToken);

        // Posts joining the same capture TAKE TURNS. Without this, two officers joining at once each
        // count the members they can see, and the later save's MemberCount overwrites the earlier
        // one's (found against a real Postgres: six people on the capture, a count of four). The
        // row lock is held until this post commits; the next joiner then reads the capture fresh.
        // In-memory tests have neither transactions nor row locks, and run one post at a time.
        var relational = _dbContext.Database.IsRelational();
        await using var transaction = relational
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // The camp's capture, when another officer's post already made it.
        ClaimShieldCapture? capture = null;
        if (eventId is int campId)
        {
            if (relational)
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""SELECT 1 FROM "ClaimShieldCaptures" WHERE "EventId" = {campId} FOR UPDATE""",
                    cancellationToken);
            }
            capture = await _dbContext.ClaimShieldCaptures
                .Include(existing => existing.Members)
                .FirstOrDefaultAsync(existing => existing.EventId == campId, cancellationToken);
        }
        var joined = capture is not null;

        if (capture is null)
        {
            capture = new ClaimShieldCapture
            {
                LinkshellId = token.LinkshellId,
                EventId = eventId,
                MonsterName = TruncateString(monster, 128) ?? monster,
                Won = request.Won,
                TotalPlayers = totalPlayers,
                CapturedAtUtc = capturedAt,
                CapturedByCharacterName = TruncateString(
                    await ResolveTokenIssuerNameAsync(token, cancellationToken), 256),
                CapturedMessage = TruncateString(request.CapturedMessage, 512),
                CreatedAtUtc = nowUtc,
            };
            _dbContext.ClaimShieldCaptures.Add(capture);
        }
        else
        {
            // Every post read the same result line, so these normally agree already. When they
            // don't, a client missed part of it: a win any of them saw was a win, and the bigger
            // lottery count is the fuller read. The first post's time and "captured by" stand.
            capture.Won |= request.Won;
            capture.TotalPlayers = Math.Max(capture.TotalPlayers, totalPlayers);
            capture.CapturedMessage ??= TruncateString(request.CapturedMessage, 512);
        }

        // One list to iterate whichever shape arrived. MemberActions is the
        // current addon; Members is the older names-only form.
        var incoming = request.MemberActions is { Count: > 0 }
            ? request.MemberActions
            : (request.Members ?? Array.Empty<string>())
                .Select(name => new AddonClaimShieldMember(name, null))
                .ToList();

        // Seeded with whoever is already on the capture, so joining it adds only the people it was
        // missing. A person is the same by name OR by account: two alt names of one player collapse
        // to a single row, within this post and across posts.
        var seenRawNames = capture.Members
            .Select(member => member.CharacterName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenAppUserIds = capture.Members
            .Where(member => member.AppUserId != null)
            .Select(member => member.AppUserId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var entry in incoming)
        {
            if (string.IsNullOrWhiteSpace(entry?.Name)) continue;
            var name = entry.Name.Trim();
            if (!seenRawNames.Add(name)) continue;

            membershipByName.TryGetValue(name, out var membership);
            if (membership?.AppUserId is { } appUserId && !seenAppUserIds.Add(appUserId))
            {
                continue;
            }

            added++;
            capture.Members.Add(new ClaimShieldCaptureMember
            {
                CharacterName = TruncateString(name, 256) ?? name,
                AppUserId = membership?.AppUserId,
                Matched = membership is not null,
                ActionMessage = string.IsNullOrWhiteSpace(entry.Action)
                    ? null
                    : TruncateString(entry.Action.Trim(), 512),
            });
        }

        capture.MemberCount = capture.Members.Count;
        capture.MatchedCount = capture.Members.Count(m => m.Matched);

        await _dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return Ok(new
        {
            captureId = capture.Id,
            monster = capture.MonsterName,
            won = capture.Won,
            totalPlayers = capture.TotalPlayers,
            // The whole capture's counts, joined or not -- what the addon prints as "N matched".
            memberCount = capture.MemberCount,
            matchedCount = capture.MatchedCount,
            // True when another officer's post had already made this camp's capture, and how many
            // of this post's people it was missing.
            joined,
            added,
            // Echoed so the addon can say which camp it landed on -- or show
            // that it landed on none, which is the case worth noticing.
            eventId = capture.EventId,
        });
    }
}
