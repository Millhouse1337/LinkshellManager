using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// Add New Post on an attendance card that is not posted to the DKP sheet yet -- the exception path
// for members a camp's posts missed. Same shape as a live camp's Add New Post
// (AddonApiController.AddMiscPostManagementAsync): one named Misc post, the members an officer
// picked, and what it pays each of them.
public sealed partial class ActivityWindowEventsController
{
    public sealed record ActivityAddWindowEventPostRequest(
        string? Name, IReadOnlyList<string>? AppUserIds, double? DkpAmount = null);

    // The most one post may pay each person, matching the window and misc price edits.
    private const double MaxNewPostDkp = 10000d;

    // GET /api/activity/window-events/{id}/member-candidates -- the whole linkshell roster, for the
    // New Post member picker.
    [HttpGet("window-events/{windowEventId:int}/member-candidates")]
    public async Task<IActionResult> GetWindowEventMemberCandidatesAsync(
        int windowEventId,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (loaded.Result is not null) return loaded.Result;

        var linkshellId = loaded.Value!.LinkshellId;
        var candidates = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == linkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ThenBy(link => link.AppUser!.CharacterName)
            .Select(link => new ActivityEventAddMemberCandidateDto(
                link.AppUserId!,
                link.CharacterName ?? link.AppUser!.CharacterName ?? link.AppUser!.UserName ?? "Unknown member",
                link.Rank))
            .ToListAsync(cancellationToken);
        return Ok(candidates);
    }

    // POST /api/activity/window-events/{id}/misc-posts
    //
    // Files a new Misc post on the card with the picked members, verified because an officer made
    // it. On a card paid from its captures (PerCaptureDkp, every Standard camp) each person on it is
    // priced as End Camp prices a misc post: the typed amount, else the linkshell's Misc post rate.
    // Any other card pays it the way it pays every misc post, from its own Misc DKP.
    //
    // No LinkedEventId, on purpose: the camp's board is recycled for the next pop, and a post
    // pointing at it would follow the board there if it were ever unlinked from this card.
    [HttpPost("window-events/{windowEventId:int}/misc-posts")]
    public async Task<IActionResult> AddWindowEventMiscPostAsync(
        int windowEventId,
        [FromBody] ActivityAddWindowEventPostRequest request,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadManageableWindowEventAsync(windowEventId, cancellationToken);
        if (loaded.Result is not null) return loaded.Result;
        var windowEvent = loaded.Value!;

        if (windowEvent.PostedToSheetAt.HasValue)
        {
            return BadRequest(new { error = "This event is already posted to the DKP sheet." });
        }

        var ids = (request.AppUserIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return BadRequest(new { error = "Pick at least one member for the post." });
        }

        var memberships = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Include(m => m.AppUser)
            .Where(m => m.LinkshellId == windowEvent.LinkshellId && m.AppUserId != null && ids.Contains(m.AppUserId))
            .ToListAsync(cancellationToken);
        if (memberships.Select(m => m.AppUserId).Distinct().Count() != ids.Count)
        {
            return BadRequest(new { error = "Some of those members aren't in this linkshell." });
        }

        var linkshell = await _dbContext.Linkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == windowEvent.LinkshellId, cancellationToken);

        // The post's own price: null keeps it on the Misc post rate. Snapped to the linkshell's DKP
        // grid, like every other price edit, so the card never shows a number nothing pays.
        double? dkpAmount = null;
        if (request.DkpAmount is { } typed)
        {
            if (double.IsNaN(typed) || double.IsInfinity(typed)) return BadRequest(new { error = "dkpAmount must be a number." });
            if (typed < 0) return BadRequest(new { error = "dkpAmount must be non-negative." });
            if (typed > MaxNewPostDkp) return BadRequest(new { error = $"dkpAmount must be {MaxNewPostDkp:0} or less." });
            dkpAmount = DkpRounding.Round(typed, DkpRounding.StepFor(linkshell?.DkpRoundingIncrement));
        }
        var perMember = Math.Max(0d, dkpAmount ?? linkshell?.HnmStandardMiscBonus ?? 0d);

        var actor = await ResolveAppUserAsync(cancellationToken);
        var actorMembership = actor is null
            ? null
            : await _dbContext.AppUserLinkshells.AsNoTracking()
                .FirstOrDefaultAsync(m => m.AppUserId == actor.Id && m.LinkshellId == windowEvent.LinkshellId, cancellationToken);
        var actorName = (actorMembership?.CharacterName ?? actor?.CharacterName ?? actor?.UserName ?? "officer").Trim();

        var nowUtc = DateTime.UtcNow;
        var snapshot = new AttendanceSnapshot
        {
            LinkshellId = windowEvent.LinkshellId,
            CapturedAtUtc = nowUtc,
            CapturedByCharacterName = TrimToNull(actorName, 256),
            Name = TrimToNull(request.Name, 128) ?? "Misc post",
            PostedByAppUserId = actor?.Id,
            SnapshotStatus = AttendanceSnapshotStatuses.Active,
            VerifiedAtUtc = nowUtc,
            VerifiedByAppUserId = actor?.Id,
            DkpAmount = dkpAmount,
            PostSource = AttendanceSnapshotPostSources.App,
        };
        WindowEventLinkService.ApplySlot(snapshot, windowEvent, AttendanceSnapshotSlotKinds.Misc, windowNumber: null);
        snapshot.WindowEventId = windowEvent.Id;

        foreach (var membership in memberships.GroupBy(m => m.AppUserId!, StringComparer.Ordinal).Select(g => g.First()))
        {
            snapshot.Entries.Add(new AttendanceSnapshotEntry
            {
                Snapshot = snapshot,
                CharacterName = (membership.CharacterName ?? membership.AppUser?.CharacterName
                    ?? membership.AppUser?.UserName ?? "Unknown member").Trim(),
                AppUserId = membership.AppUserId,
                AddedManually = true,
                // What this card pays them for the post. Only a per-capture card is paid from it.
                DkpAmount = windowEvent.PerCaptureDkp ? perMember : null,
            });
        }
        snapshot.EntryCount = snapshot.Entries.Count;

        _dbContext.AttendanceSnapshots.Add(snapshot);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { snapshotId = snapshot.Id, name = snapshot.Name, added = snapshot.EntryCount });
    }
}
