using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class AddonApiController
{
    public sealed record AddonPresenceMemberDto(
        string? CharacterName,
        int? ZoneId,
        string? MainJob,
        int? MainJobLevel,
        string? SubJob,
        int? SubJobLevel,
        // True only when the reporter's game confirmed this character leads the alliance.
        bool IsAllianceLeader = false);

    public sealed record AddonPresenceRequest(
        string? SelfCharacterName,
        // Who this alliance is, as the reporter's client recognised it: the leader's character name
        // where the game confirms one, else the reporter's own.
        string? AllianceKey,
        string? AllianceLeaderName,
        int? AllianceNumber,
        IReadOnlyList<AddonPresenceMemberDto>? Members);

    // POST /api/addon/presence — the heartbeat.
    //
    // Each addon reports ITS OWN alliance (party memory slots 0-17) and gets back everyone else's.
    // That round trip is the only way a client can learn about another alliance at all: the FFXI
    // client cannot see past your own, which is stated on AttendanceSnapshot.AllianceNumber and is
    // the reason attendance is posted per alliance in the first place.
    //
    // Write-then-read in ONE request on purpose — the addon's HTTP layer is a blocking curl.exe on
    // the render thread, so a separate GET would double the cost of every heartbeat.
    //
    // This records NO attendance and moves NO DKP. It is a cache of who is standing where.
    [HttpPost("presence")]
    [AddonApiAuth]
    public async Task<IActionResult> PostPresenceAsync(
        [FromBody] AddonPresenceRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return BadRequest(new { error = "Request body is required." });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;

        var reported = (request.Members ?? Array.Empty<AddonPresenceMemberDto>())
            .Where(member => !string.IsNullOrWhiteSpace(member.CharacterName))
            .ToList();

        // Party memory cannot report a nineteenth person, so more than 18 means the client is
        // reading something other than its own alliance — a bug to surface, not a roster to accept.
        if (reported.Count > LinkshellPresenceWindow.MaxMembers)
        {
            return BadRequest(new
            {
                error = $"Presence exceeds the {LinkshellPresenceWindow.MaxMembers}-entry alliance maximum.",
            });
        }

        // ANY paired member may report. There is deliberately no moderation gate: presence has no
        // DKP consequence, and the entire point is that an alliance fielding no officer still shows
        // up. Gating this would reproduce the invisibility it exists to fix.
        var allianceKey = TruncateString(
            string.IsNullOrWhiteSpace(request.AllianceKey)
                ? TruncateString(request.SelfCharacterName, 256)
                : request.AllianceKey.Trim(),
            256);
        var allianceNumber = AttendanceSnapshotAlliances.Resolve(request.AllianceNumber);

        // Only names this linkshell already knows are stored, plus the reporter. Without this the
        // table fills with every pick-up player who happened to be in somebody's alliance.
        var roster = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(link => link.LinkshellId == token.LinkshellId && link.AppUserId != null)
            .Join(_dbContext.Users, link => link.AppUserId, user => user.Id, (link, user) => new
            {
                link.AppUserId,
                link.CharacterName,
                user.AltCharacterName1,
                user.AltCharacterName2,
            })
            .ToListAsync(cancellationToken);

        var knownByName = new Dictionary<string, (string AppUserId, string? Main)>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in roster)
        {
            if (string.IsNullOrWhiteSpace(member.AppUserId)) continue;
            if (!string.IsNullOrWhiteSpace(member.CharacterName))
            {
                knownByName.TryAdd(member.CharacterName.Trim(), (member.AppUserId!, null));
            }
            foreach (var alt in new[] { member.AltCharacterName1, member.AltCharacterName2 })
            {
                if (string.IsNullOrWhiteSpace(alt)) continue;
                knownByName.TryAdd(alt.Trim(), (member.AppUserId!, member.CharacterName));
            }
        }

        var selfName = TruncateString(request.SelfCharacterName, 256);
        // The game reports exactly one alliance leader. Two means a client bug, so the first wins
        // rather than letting one roster carry two crowns.
        var leaderTaken = false;
        var accepted = 0;
        var skipped = 0;

        var existing = await _dbContext.LinkshellPresences
            .Where(item => item.LinkshellId == token.LinkshellId)
            .ToListAsync(cancellationToken);
        var existingByName = existing
            .GroupBy(item => item.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var member in reported)
        {
            var name = TruncateString(member.CharacterName!.Trim(), 256)!;
            var isSelf = selfName is not null
                && string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase);
            if (!knownByName.TryGetValue(name, out var known) && !isSelf)
            {
                skipped++;
                continue;
            }

            var isLeader = member.IsAllianceLeader && !leaderTaken;
            if (isLeader) leaderTaken = true;

            if (!existingByName.TryGetValue(name, out var row))
            {
                row = new LinkshellPresence { LinkshellId = token.LinkshellId, CharacterName = name };
                _dbContext.LinkshellPresences.Add(row);
                existingByName[name] = row;
            }

            row.AppUserId = known.AppUserId;
            row.MainCharacterName = known.Main;
            row.ZoneId = member.ZoneId;
            row.AllianceNumber = allianceNumber;
            row.AllianceKey = allianceKey;
            row.IsAllianceLeader = isLeader;
            row.MainJob = TruncateString(member.MainJob, 8);
            row.MainJobLevel = member.MainJobLevel;
            row.SubJob = TruncateString(member.SubJob, 8);
            row.SubJobLevel = member.SubJobLevel;
            row.ReportedByCharacterName = selfName;
            row.LastSeenUtc = nowUtc;
            accepted++;
        }

        // Opportunistic sweep rather than a background service. Presence is worthless once stale,
        // so the only thing that has to happen is that the table does not grow without bound.
        //
        // Rows a reporter STOPPED listing are deliberately left alone: somebody leaving your
        // alliance has not left the world, and another alliance's poster may legitimately own that
        // row on the next beat. Age is the only thing that removes anyone.
        var purgeBefore = nowUtc.AddMinutes(-LinkshellPresenceWindow.PurgeMinutes);
        var stale = existing.Where(item => item.LastSeenUtc < purgeBefore).ToList();
        if (stale.Count > 0)
        {
            _dbContext.LinkshellPresences.RemoveRange(stale);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var (members, alliances) = await BuildPresencePayloadAsync(token.LinkshellId, nowUtc, cancellationToken);
        return Ok(new
        {
            accepted,
            skipped,
            freshSeconds = LinkshellPresenceWindow.FreshSeconds,
            serverNowUtc = nowUtc,
            members,
            alliances,
        });
    }

    // GET /api/addon/presence — the same read without reporting, for a manual refresh.
    [HttpGet("presence")]
    [AddonApiAuth]
    public async Task<IActionResult> GetPresenceAsync(CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;
        var (members, alliances) = await BuildPresencePayloadAsync(token.LinkshellId, nowUtc, cancellationToken);
        return Ok(new
        {
            freshSeconds = LinkshellPresenceWindow.FreshSeconds,
            serverNowUtc = nowUtc,
            members,
            alliances,
        });
    }

    // The fresh slice, plus the per-alliance roll-up.
    //
    // The roll-up is computed HERE rather than in the addon so every client agrees about how many
    // alliances there are and who leads them, and so the addon never re-derives it per frame.
    private async Task<(object Members, object Alliances)> BuildPresencePayloadAsync(
        int linkshellId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var freshFrom = nowUtc.AddSeconds(-LinkshellPresenceWindow.FreshSeconds);
        var rows = await _dbContext.LinkshellPresences
            .AsNoTracking()
            .Where(item => item.LinkshellId == linkshellId && item.LastSeenUtc >= freshFrom)
            .OrderBy(item => item.AllianceNumber)
            .ThenBy(item => item.CharacterName)
            .ToListAsync(cancellationToken);

        var members = rows.Select(item => new
        {
            characterName = item.CharacterName,
            mainCharacterName = item.MainCharacterName,
            zoneId = item.ZoneId,
            allianceNumber = item.AllianceNumber,
            allianceKey = item.AllianceKey,
            isAllianceLeader = item.IsAllianceLeader,
            mainJob = item.MainJob,
            mainJobLevel = item.MainJobLevel,
            subJob = item.SubJob,
            subJobLevel = item.SubJobLevel,
            lastSeenUtc = item.LastSeenUtc,
        }).ToList();

        var alliances = rows
            .GroupBy(item => item.AllianceNumber)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var key = group
                    .Select(item => item.AllianceKey)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                // Null when nobody's client confirmed a leader — the UI then shows no marker at all
                // rather than nominating whoever happened to post first.
                var leader = group.FirstOrDefault(item => item.IsAllianceLeader)?.CharacterName;
                return new
                {
                    number = group.Key,
                    key,
                    // Named by WHO it is rather than by whichever ordinal it happened to get.
                    label = AttendanceSnapshotAlliances.Label(group.Key, key, leader),
                    count = group.Count(),
                    leaderCharacterName = leader,
                };
            })
            .ToList();

        return (members, alliances);
    }
}
