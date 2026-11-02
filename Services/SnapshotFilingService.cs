using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Files an unlinked capture: onto a LIVE camp, onto an open attendance event, or onto a brand-new
// attendance event named after it. Always as a MISC post.
//
// There is no post type to choose any more (decided 2026-11-02). A /lsm now capture is a misc
// post wherever it lands: window attendance is what the addon's own window posts record, and a
// capture filed as a window could pay that window twice.
//
// Two callers, one set of rules: the Activity's Unlinked Snapshots row (cookie/bearer) and the
// addon's Unlinked Posts panel (pairing token). Each authorizes and loads the snapshot (with its
// Entries) itself; this owns validation and the write.
public sealed class SnapshotFilingService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly WindowEventLinkService _windowEventLinks;
    private readonly AllianceIdentityService _allianceIdentity;

    public SnapshotFilingService(
        ApplicationDbContext dbContext,
        WindowEventLinkService windowEventLinks,
        AllianceIdentityService allianceIdentity)
    {
        _dbContext = dbContext;
        _windowEventLinks = windowEventLinks;
        _allianceIdentity = allianceIdentity;
    }

    // LinkedEventId (with no WindowEventId) = a live camp. WindowEventId = an open attendance event.
    // Neither = find-or-create an attendance event under Name (falling back to the capture's own
    // name); CreateNew forces a fresh one rather than folding into an open one of the same name.
    public sealed record Request(int? WindowEventId, string? Name, int? LinkedEventId, bool CreateNew);

    // Error null = filed. StatusCode is 400 or 404 when it was refused.
    public sealed record Result(int StatusCode, string? Error, bool FiledAgainstCamp = false)
    {
        public static Result Refused(string error) => new(StatusCodes.Status400BadRequest, error);
        public static Result NotFound(string error) => new(StatusCodes.Status404NotFound, error);
    }

    public async Task<Result> FileAsync(AttendanceSnapshot snapshot, Request request, CancellationToken cancellationToken)
    {
        // A LIVE CAMP as the target: file the capture on the camp itself and mint nothing.
        //
        // This used to go through the name path below, which created a Window Event named after
        // the camp -- a second card under the live board -- and End Camp, which builds the camp's
        // real review card from its posted windows, never picked that second card up. Parked on
        // the Event row instead, the capture rides onto the review card at End Camp in the slot
        // Misc slot. See WindowEventLinkService.ApplyCampSlot.
        if (!request.WindowEventId.HasValue && request.LinkedEventId is int campId && campId > 0)
        {
            var camp = await _dbContext.Events
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == campId && e.LinkshellId == snapshot.LinkshellId, cancellationToken);
            if (camp is null)
            {
                return Result.NotFound("That camp is no longer open.");
            }
            // A parked board (ended, waiting for the next pop) is not a camp anyone is standing at.
            // Filing against it would hand the capture to the NEXT pop's End Camp.
            if (camp.EndTime is not null)
            {
                return Result.Refused("That camp has ended. File this capture on its review card under "
                                      + "Events Pending DKP Post instead.");
            }
            if (camp.CommencementStartTime is null)
            {
                return Result.Refused("That camp hasn't started yet. Start it first, then file this capture.");
            }

            snapshot.LinkedEventId = camp.Id;
            WindowEventLinkService.ApplyCampSlot(
                snapshot,
                DiscordEventMessageBuilder.EffectiveWindowCount(camp),
                AttendanceSnapshotSlotKinds.Misc,
                windowNumber: null);

            await _dbContext.SaveChangesAsync(cancellationToken);
            return new Result(StatusCodes.Status200OK, null, FiledAgainstCamp: true);
        }

        WindowEvent? windowEvent = null;
        if (request.WindowEventId.HasValue)
        {
            windowEvent = await _dbContext.WindowEvents
                .FirstOrDefaultAsync(e => e.Id == request.WindowEventId.Value && e.LinkshellId == snapshot.LinkshellId, cancellationToken);
        }
        else
        {
            // Falls back to the snapshot's own name so "Create New Event" works straight off a
            // capture the addon already named, with no retyping.
            var name = TrimToNull(request.Name, 128) ?? TrimToNull(snapshot.Name, 128);
            if (name is null)
            {
                return Result.Refused("Choose an existing attendance event, or name this snapshot first.");
            }
            windowEvent = await _windowEventLinks.FindOrCreateAsync(
                snapshot.LinkshellId,
                name,
                snapshot.CapturedAtUtc,
                snapshot.CapturedByCharacterName,
                DateTime.UtcNow,
                cancellationToken,
                forceNew: request.CreateNew);
            snapshot.Name ??= name;
        }

        if (windowEvent is null) return Result.NotFound("Window Event not found.");

        // Camp association, when the officer picked one. Verified against the snapshot's own
        // linkshell because it arrives from the client; an id that doesn't belong is dropped
        // rather than rejected, so a stale pick still completes the attach it was really for.
        if (request.LinkedEventId is int linkedEventId && linkedEventId > 0)
        {
            var campOwned = await _dbContext.Events
                .AsNoTracking()
                .AnyAsync(e => e.Id == linkedEventId && e.LinkshellId == snapshot.LinkshellId, cancellationToken);
            if (campOwned)
            {
                snapshot.LinkedEventId = linkedEventId;
            }
        }

        WindowEventLinkService.ApplySlot(snapshot, windowEvent, AttendanceSnapshotSlotKinds.Misc, windowNumber: null);

        // The alliance NUMBER is an ordinal within THIS camp, so it is assigned on attach rather
        // than at ingest -- until a capture is filed there is no camp to be first or second on.
        snapshot.AllianceNumber = await _allianceIdentity.ResolveNumberAsync(
            windowEvent.Id, snapshot.AllianceKey, cancellationToken);

        // Note what is NOT here: the snapshot's status. Filing a capture and vouching for it are
        // separate decisions, and this used to force Active — which would have verified a Pending
        // capture the instant an officer sorted it into the right camp.
        _windowEventLinks.Attach(snapshot, windowEvent);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return new Result(StatusCodes.Status200OK, null);
    }

    internal static string? TrimToNull(string? value, int maxLength)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (trimmed is { Length: > 0 } && trimmed.Length > maxLength) trimmed = trimmed[..maxLength];
        return trimmed;
    }
}
