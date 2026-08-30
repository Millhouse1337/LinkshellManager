using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
        IReadOnlyList<AddonAttendanceSnapshotEntryDto>? Entries,
        string? Name,
        // ACCEPTED AND IGNORED since filing moved into the app.
        //
        // It used to be the officer's explicit window choice, overriding a grid-derived one. Both
        // are gone: /lsm now lands unlinked, and an officer picks the slot — a numbered window, or
        // Misc — on the Event System page. The field stays on the wire only so addons in the wild,
        // which still send it, don't get a 400 back at a pop.
        int? WindowNumber = null,
        // Which alliance the poster was in. Chosen in the addon launcher, because it CANNOT be
        // derived: the FFXI client exposes only your own alliance (party memory slots 0-17), so
        // alliance 2 is invisible to everyone in alliance 1 and vice versa. That is the whole
        // reason each alliance needs its own paired poster.
        //
        // Null from an addon that predates the selector; treated as alliance 1.
        int? AllianceNumber = null,
        // WHO this alliance is, as the addon recognised it: the alliance leader's character name
        // where the game confirms one (IParty:GetAllianceLeaderServerId), else the poster's own.
        //
        // This replaced the typed number. Two officers standing in the same alliance compute the
        // same key from their own clients without coordinating, so the fold is an exact match
        // rather than a bet that both of them ran `/lsm alliance` and picked the same digit.
        //
        // Null from an addon that predates it; the server then falls back to the poster's name.
        string? AllianceKey = null,
        // Set ONLY when the game actually reported a leader. Null for a solo player or a party
        // with no alliance formed -- and that null is why the UI shows no leader marker instead of
        // guessing at one.
        string? AllianceLeaderName = null);

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
        // A capture is ONE alliance, so 18 (3 parties of 6) is the real ceiling — party memory
        // cannot report a nineteenth person.
        //
        // This used to be 64, to leave room for the zone- and linkshell-scope captures that read
        // the FFXI entity list directly. Those scopes are gone: they depended on /sea and a
        // render-range entity sweep, neither of which could see reliably past the players who
        // happened to be nearby. Anything over 18 now means the client is not reading party
        // memory, which is a bug to surface rather than a roster to accept.
        const int MaxSnapshotEntries = 18;
        if (entries.Count > MaxSnapshotEntries)
        {
            return BadRequest(new
            {
                error = $"Snapshot exceeds the {MaxSnapshotEntries}-entry alliance maximum. "
                        + "Each alliance posts its own capture.",
            });
        }

        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var nowUtc = DateTime.UtcNow;
        var capturedAt = request.CapturedAtUtc.HasValue
                         && request.CapturedAtUtc.Value > nowUtc.AddDays(-7)
                         && request.CapturedAtUtc.Value < nowUtc.AddMinutes(5)
            ? request.CapturedAtUtc.Value
            : nowUtc;

        // ANY paired member may post. The token is minted per linkshell from a pairing code the
        // member generated on their own account, so holding one already proves membership — and an
        // alliance that fields no officer would otherwise have no way to be counted at all, which
        // is the situation this whole feature exists to fix.
        //
        // Rank still decides one thing: whether the capture is trusted on arrival. A moderator is
        // the reviewer, so their own post lands Active (and stamped as verified by them). Everyone
        // else's lands Pending — visible on the camp, excluded from the combined roster and from
        // DKP until an officer Confirms it.
        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        var canModerate = role?.CanModerateLiveEvent == true;
        var landingStatus = canModerate
            ? AttendanceSnapshotStatuses.Active
            : AttendanceSnapshotStatuses.Pending;

        // WHO this alliance is, as the addon recognised it. The number is derived from this, not
        // typed: `/lsm alliance N` was a manual setting that defaulted to 1, so a shell where
        // nobody ran it reported every alliance as 1 and the feature collapsed into one row.
        //
        // Falls back to the poster when the game reports no leader (solo, or a party with no
        // alliance formed) -- one person is still an identity, and it is the right one.
        var allianceKey = TruncateString(
            string.IsNullOrWhiteSpace(request.AllianceKey)
                ? TruncateString(request.CapturedByCharacterName, 256)
                : request.AllianceKey.Trim(),
            256);
        var allianceLeaderName = TruncateString(
            string.IsNullOrWhiteSpace(request.AllianceLeaderName) ? null : request.AllianceLeaderName.Trim(),
            256);
        // An explicit number is still honoured for an addon that predates the identity, so an old
        // build keeps behaving exactly as it did.
        var allianceNumber = AttendanceSnapshotAlliances.Resolve(request.AllianceNumber);

        var trimmedName = TruncateString(string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim(), 128);

        // EVERY capture lands UNLINKED. This endpoint used to find-or-create a Window Event from
        // the snapshot's name and stamp a window number derived from a time grid anchored at the
        // camp's first post — and that derivation was wrong often enough to be worse than nothing.
        // The grid measures ELAPSED TIME, so an officer who reaches camp only for the kill has no
        // elapsed time to measure and gets stamped window 1. There is no way for the client to do
        // better: it cannot know which camp an officer means, or which window they consider
        // themselves to be in.
        //
        // So the server stops guessing. An officer files the capture against an explicit event and
        // an explicit slot — a numbered window, or Misc — on the Event System page or in the
        // Activity. Unlinked is now the INTENDED outcome of /lsm now, not a fallback.
        var mergeTarget = await _windowEventLinks.FindUnlinkedMergeTargetAsync(
            token.LinkshellId, capturedAt, allianceKey, landingStatus, cancellationToken);

        var snapshot = mergeTarget ?? new AttendanceSnapshot
        {
            LinkshellId = token.LinkshellId,
            CapturedAtUtc = capturedAt,
            CapturedByCharacterName = TruncateString(request.CapturedByCharacterName, 256),
            UtcOffset = TruncateString(request.UtcOffset, 8),
            CreatedAtUtc = nowUtc,
            Name = trimmedName,
            // Filed by an officer, never here. WindowNumber stays null until then, and SlotKind
            // keeps its Window default so an unfiled capture is never mistaken for a Misc post.
            WindowEventId = null,
            WindowNumber = null,
            AllianceNumber = allianceNumber,
            AllianceKey = allianceKey,
            AllianceLeaderName = allianceLeaderName,
            PostedByAppUserId = token.IssuedToAppUserId,
            SnapshotStatus = landingStatus,
            // A moderator IS the reviewer, so their own capture arrives verified. Recording who
            // rather than just "verified" keeps the trail readable when several officers post.
            VerifiedAtUtc = canModerate ? nowUtc : null,
            VerifiedByAppUserId = canModerate ? token.IssuedToAppUserId : null,
        };

        // On a fold the target's own CapturedAtUtc is deliberately NOT moved forward. It anchors the
        // merge window, so a steady drip of posts every 2 minutes eventually starts a fresh snapshot
        // instead of chaining into one that grows all camp long.
        var newerThanTarget = mergeTarget is null || capturedAt >= mergeTarget.CapturedAtUtc;
        var existingByName = snapshot.Entries
            .GroupBy(e => NormalizeWindowEventName(e.CharacterName) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var e in entries)
        {
            var key = NormalizeWindowEventName(e.CharacterName) ?? string.Empty;
            if (existingByName.TryGetValue(key, out var already))
            {
                // Same character seen again. Keep the LATEST reading of their job/zone, matching how
                // the combined roster picks a member's row, and leave it alone for an older post.
                if (newerThanTarget)
                {
                    already.MainJob = TruncateString(e.MainJob, 8);
                    already.MainJobLevel = e.MainJobLevel;
                    already.SubJob = TruncateString(e.SubJob, 8);
                    already.SubJobLevel = e.SubJobLevel;
                    already.Zone = TruncateString(e.Zone, 128);
                }
                continue;
            }

            var added = new AttendanceSnapshotEntry
            {
                CharacterName = TruncateString(e.CharacterName, 256) ?? string.Empty,
                MainJob = TruncateString(e.MainJob, 8),
                MainJobLevel = e.MainJobLevel,
                SubJob = TruncateString(e.SubJob, 8),
                SubJobLevel = e.SubJobLevel,
                Zone = TruncateString(e.Zone, 128),
            };
            snapshot.Entries.Add(added);
            existingByName[key] = added;
        }

        snapshot.EntryCount = snapshot.Entries.Count;
        if (mergeTarget is null)
        {
            _dbContext.AttendanceSnapshots.Add(snapshot);
        }
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Fire-and-forget post to the linkshell's Discord channel (no-op if
        // no webhook URL is configured). Enqueued after the snapshot is
        // committed so the background worker can reload it; never blocks or
        // fails this addon request if Discord is slow/unreachable.
        await _discordWebhook.EnqueueSnapshotAsync(snapshot.Id, cancellationToken);

        // Sheet sync is officer-initiated on the Event System page (Post to DKP
        // Sheet button) so the officer can review the combined roster and set
        // DKP + Entry Type before any rows land in the spreadsheet.
        return Ok(new
        {
            snapshotId = snapshot.Id,
            entryCount = snapshot.EntryCount,
            capturedAtUtc = snapshot.CapturedAtUtc,
            linkedEventId = (int?)null,
            // Always null now — filing is an officer action in the app. Kept on the wire because
            // addons in the wild branch on them; both branches are simply never taken any more.
            windowEventId = (int?)null,
            windowNumber = (int?)null,
            snapshotStatus = snapshot.SnapshotStatus,
            // Echoed so the addon can name the alliance back to the poster — the one field it
            // cannot verify for itself, and the one most likely to be set wrong.
            allianceNumber = snapshot.AllianceNumber,
            // Lets the addon say "awaiting officer confirmation" rather than a bare "posted", so a
            // member knows their capture is not counted yet.
            awaitingVerification = snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
            // True when this post was absorbed into another capture from the same alliance taken
            // moments earlier, rather than creating a row of its own.
            merged = mergeTarget is not null,
        });
    }

    // What the addon's Misc Posts panel reads back.
    //
    // The addon can no longer know how a capture was filed at the moment it posts one — filing is
    // an officer action in the app now — so the only way it can show "this one went to window 3,
    // that one is Misc, these two are still waiting" is to ask. That is the whole reason this
    // exists.
    //
    // Headers only, deliberately: no .Include(Entries). The addon's HTTP layer is a BLOCKING
    // curl.exe call on the render thread, so this is polled on a slow timer and must stay cheap.
    [HttpGet("attendance-snapshots/recent")]
    [AddonApiAuth]
    public async Task<IActionResult> GetRecentAttendanceSnapshotsAsync(
        CancellationToken cancellationToken,
        [FromQuery] int limit = 25)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);
        var take = Math.Clamp(limit, 1, 50);

        var rows = await _dbContext.AttendanceSnapshots
            .AsNoTracking()
            .Where(s => s.LinkshellId == token.LinkshellId
                        && s.SnapshotStatus != AttendanceSnapshotStatuses.Ignored)
            .OrderByDescending(s => s.CapturedAtUtc)
            .Take(take)
            .Include(s => s.WindowEvent)
            .ToListAsync(cancellationToken);

        var snapshots = rows.Select(s =>
        {
            var isMisc = AttendanceSnapshotSlotKinds.IsMisc(s.SlotKind);
            // Same rule as both web mappers: a Misc capture has no window, and nothing may derive
            // one for it.
            var window = isMisc ? null : s.WindowNumber;
            var total = s.WindowEvent is not null && WindowEventWindowGrid.Minutes(s.WindowEvent) > 0
                ? WindowEventWindowGrid.WindowCount(s.WindowEvent)
                : (int?)null;

            return new
            {
                id = s.Id,
                capturedAtUtc = s.CapturedAtUtc,
                capturedByCharacterName = s.CapturedByCharacterName,
                name = s.Name,
                entryCount = s.EntryCount,
                allianceNumber = s.AllianceNumber,
                snapshotStatus = s.SnapshotStatus,
                slotKind = AttendanceSnapshotSlotKinds.Resolve(s.SlotKind),
                isMisc,
                windowNumber = window,
                slotLabel = isMisc
                    ? "Misc"
                    : window is int n ? (total is int t ? $"Window {n} of {t}" : $"Window {n}") : null,
                windowEventId = s.WindowEventId,
                windowEventName = s.WindowEvent?.Name,
                // The addon groups on this: an unfiled capture is the officer's to-do, and the
                // poster is usually the person who needs reminding it is still sitting there.
                isUnlinked = s.WindowEventId == null,
                awaitingVerification = s.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
            };
        }).ToList();

        return Ok(new
        {
            snapshots,
            unlinkedCount = snapshots.Count(s => s.isUnlinked),
        });
    }

    // Closes a Window Event from the addon's HNM session "End Event" button.
    // Mirrors the cookie-auth WindowEventsController.Close: it only flips the
    // status + stamps ClosedAtUtc. It deliberately does NOT enqueue any sheet
    // sync — posting a Window Event to the DKP sheet stays an explicit,
    // officer-initiated action on the Window Events page.
    [HttpPost("window-events/{windowEventId:int}/close")]
    [AddonApiAuth]
    public async Task<IActionResult> CloseWindowEventAsync(
        int windowEventId, CancellationToken cancellationToken)
    {
        var token = AddonApiAuthAttribute.GetToken(HttpContext);

        var role = await GetTokenIssuerRoleAsync(token, token.LinkshellId, cancellationToken);
        if (role?.CanModerateLiveEvent != true)
        {
            return Forbid();
        }

        var windowEvent = await _dbContext.WindowEvents
            .FirstOrDefaultAsync(
                e => e.Id == windowEventId && e.LinkshellId == token.LinkshellId,
                cancellationToken);
        if (windowEvent is null)
        {
            return NotFound(new { error = "Window Event not found." });
        }

        if (windowEvent.Status != WindowEventStatuses.Closed)
        {
            windowEvent.Status = WindowEventStatuses.Closed;
            windowEvent.ClosedAtUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(new { closed = true, windowEventId = windowEvent.Id });
    }

    private static string? NormalizeWindowEventName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToUpperInvariant();
    }

    private static string? TruncateString(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value.Substring(0, max);
    }
}
