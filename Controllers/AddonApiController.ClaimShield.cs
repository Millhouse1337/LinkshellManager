using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    public sealed record AddonClaimShieldRequest(
        string? Monster,
        bool Won,
        int? TotalPlayers,
        DateTime? CapturedAtUtc,
        string? CapturedMessage,
        IReadOnlyList<string>? Members);

    // Records one claim-shield lottery window the addon parsed out of chat.
    // Permission mirrors ToD (CanManageTods OR CanSubmitTodForApproval) but —
    // unlike PostTod — there is NO approval queue: a claim-shield capture has
    // no DKP / repop / auto-event side effect, so there is nothing for an
    // officer to approve. Both permission tiers store directly. The server is
    // authoritative for member resolution: it re-resolves the addon-sent names
    // against the live linkshell roster (same membershipByName index that
    // PostAttendanceAsync builds), so a stale addon roster cache can't poison
    // the stored record.
    [HttpPost("claim-shield-captures")]
    [AddonApiAuth]
    public async Task<IActionResult> PostClaimShieldCaptureAsync(
        [FromBody] AddonClaimShieldRequest request,
        CancellationToken cancellationToken)
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

        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        var canManage = role?.CanManageTods == true;
        var canSubmit = role?.CanSubmitTodForApproval == true;
        if (!canManage && !canSubmit)
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

        var capture = new ClaimShieldCapture
        {
            LinkshellId = token.LinkshellId,
            MonsterName = TruncateString(monster, 128) ?? monster,
            Won = request.Won,
            TotalPlayers = totalPlayers,
            CapturedAtUtc = capturedAt,
            CapturedByCharacterName = TruncateString(
                await ResolveTokenIssuerNameAsync(token, cancellationToken), 256),
            CapturedMessage = TruncateString(request.CapturedMessage, 512),
            CreatedAtUtc = nowUtc,
        };

        var seenRawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenMembershipIds = new HashSet<int>();
        foreach (var raw in request.Members ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var name = raw.Trim();
            if (!seenRawNames.Add(name)) continue;

            membershipByName.TryGetValue(name, out var membership);
            if (membership is not null && !seenMembershipIds.Add(membership.Id))
            {
                // Two alt names of one player collapse to a single row.
                continue;
            }

            capture.Members.Add(new ClaimShieldCaptureMember
            {
                CharacterName = TruncateString(name, 256) ?? name,
                AppUserId = membership?.AppUserId,
                Matched = membership is not null,
            });
        }

        capture.MemberCount = capture.Members.Count;
        capture.MatchedCount = capture.Members.Count(m => m.Matched);

        _dbContext.ClaimShieldCaptures.Add(capture);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            captureId = capture.Id,
            monster = capture.MonsterName,
            won = capture.Won,
            totalPlayers = capture.TotalPlayers,
            memberCount = capture.MemberCount,
            matchedCount = capture.MatchedCount,
        });
    }
}
