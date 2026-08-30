using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Find-or-create a Window Event by name, and attach a snapshot to one.
//
// Three call sites used to own private copies of this — the addon ingest
// (AddonApiController.AttendanceSnapshots), the web attach (WindowEventsController) and the
// Activity attach (ActivityDataController.WindowEvents) — and they had drifted apart in ways that
// showed up as bugs rather than as style:
//
//   * only the addon copy stamped WindowAnchorAtUtc / WindowCount / WindowMinutes, so an event
//     created from either web surface had NO window grid and every snapshot filed under it showed
//     an unlabelled window;
//   * only the Activity copy had `forceNew`, so the web had no way to express "Create New Event";
//   * the reuse cutoff was 21 hours in two copies and 24 in the third.
//
// One implementation, three callers. The cutoff is 21 hours everywhere now (the addon's value,
// since the addon is what produces almost every snapshot): long enough to cover a camp that runs
// overnight, short enough that tomorrow's pop of the same monster starts its own event.
public sealed class WindowEventLinkService
{
    private const int ReuseWindowHours = 21;

    private readonly ApplicationDbContext _db;
    private readonly MonsterTimingResolver _monsterTimings;

    public WindowEventLinkService(ApplicationDbContext db, MonsterTimingResolver monsterTimings)
    {
        _db = db;
        _monsterTimings = monsterTimings;
    }

    // The open Window Event a capture named `name` belongs to.
    //
    // `forceNew` skips the reuse lookup: reuse is right when the name is a routing hint (an addon
    // post, or an officer typing the monster to file this snapshot with the rest of that camp's)
    // and WRONG when the officer pressed a button that says "Create New Event" — silently folding
    // into a 20-hour-old event of the same name is the opposite of what they asked for, and on a
    // repeat camp the same monster name comes round often.
    //
    // `allowCreate: false` returns null instead of minting one. That is how an unverified post
    // from a rank-and-file member is kept from inventing a camp out of a typo: it can join a camp
    // an officer already opened, and otherwise lands unlinked for triage.
    public async Task<WindowEvent?> FindOrCreateAsync(
        int linkshellId,
        string? name,
        DateTime capturedAtUtc,
        string? capturedByCharacterName,
        DateTime nowUtc,
        CancellationToken cancellationToken,
        bool forceNew = false,
        bool allowCreate = true)
    {
        var normalized = NormalizeName(name);
        if (normalized is null)
        {
            // No name means no camp to file under. The snapshot stays unlinked, which is a
            // first-class state, not a failure — see the Unlinked Snapshots section.
            return null;
        }

        if (!forceNew)
        {
            var staleCutoff = capturedAtUtc.AddHours(-ReuseWindowHours);
            var existing = await _db.WindowEvents
                .Where(item =>
                    item.LinkshellId == linkshellId &&
                    item.Status == WindowEventStatuses.Open &&
                    item.NormalizedName == normalized &&
                    item.LastCapturedAtUtc >= staleCutoff)
                .OrderByDescending(item => item.LastCapturedAtUtc)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null) return existing;
        }

        if (!allowCreate) return null;

        var spawnGrid = await _monsterTimings.ResolveAsync(linkshellId, name, cancellationToken);

        var windowEvent = new WindowEvent
        {
            LinkshellId = linkshellId,
            Name = name!.Trim(),
            NormalizedName = normalized,
            Status = WindowEventStatuses.Open,
            CreatedAtUtc = nowUtc,
            FirstCapturedAtUtc = capturedAtUtc,
            LastCapturedAtUtc = capturedAtUtc,
            // Window 1 opens here, and this never moves again — every snapshot's window number is
            // measured off it. The first post IS the camp start for a snapshot-native event, so it
            // doubles as the grid anchor.
            WindowAnchorAtUtc = capturedAtUtc,
            CreatedByCharacterName = capturedByCharacterName,
            // ...and so does the grid it is measured against, for the same reason: a missing window
            // number is DERIVED at read time, so a camp that re-read the live monster setup would
            // silently renumber its own history the first time someone edited that cadence.
            WindowCount = spawnGrid.WindowCount,
            WindowMinutes = spawnGrid.WindowCadenceMinutes,
            // Pre-select the camp from the monster name so officers don't have to set it manually
            // on every newly created event.
            EntryType = WindowEventEntryTypes.FromMonsterName(name),
        };
        _db.WindowEvents.Add(windowEvent);
        await _db.SaveChangesAsync(cancellationToken);
        return windowEvent;
    }

    // Points a snapshot at a Window Event and widens that event's captured-at span to cover it.
    //
    // Deliberately does NOT touch SnapshotStatus. Both attach paths used to force it to Active,
    // which would have quietly verified a Pending capture the moment an officer filed it — filing
    // and vouching are separate decisions, and only Confirm makes the second one.
    public void Attach(AttendanceSnapshot snapshot, WindowEvent windowEvent)
    {
        snapshot.WindowEventId = windowEvent.Id;
        windowEvent.FirstCapturedAtUtc = windowEvent.FirstCapturedAtUtc <= snapshot.CapturedAtUtc
            ? windowEvent.FirstCapturedAtUtc
            : snapshot.CapturedAtUtc;
        windowEvent.LastCapturedAtUtc = windowEvent.LastCapturedAtUtc >= snapshot.CapturedAtUtc
            ? windowEvent.LastCapturedAtUtc
            : snapshot.CapturedAtUtc;
    }

    // How many captures on this event are still waiting for an officer's Confirm. Posting to the
    // DKP sheet is blocked while this is non-zero: a Pending snapshot is excluded from the combined
    // roster, so posting anyway would pay out a roster the officer can still see names missing from
    // and read as an app that lost them.
    public Task<int> CountPendingSnapshotsAsync(int windowEventId, CancellationToken cancellationToken)
        => _db.AttendanceSnapshots
            .CountAsync(
                item => item.WindowEventId == windowEventId
                        && item.SnapshotStatus == AttendanceSnapshotStatuses.Pending,
                cancellationToken);

    // The snapshot an incoming UNLINKED post should fold into — the only kind of fold left, now
    // that `/lsm now` never auto-files.
    //
    // It exists because the two officers in alliance 2 who both hammer Post the second a pop lands
    // are capturing ONE roster, and that has nothing to do with the window guessing that made
    // auto-filing go away. Without it every double-tap doubles the triage queue an officer works.
    //
    // A flat 3-minute window (HnmConfig.SnapshotMergeWindow(0)) rather than a cadence-scaled one:
    // an unlinked snapshot has no camp, so there is no cadence to scale to. Three minutes cannot
    // reach across a real window boundary — the shortest is 10 — so this can only ever fold posts
    // that genuinely coincide.
    //
    // The alliance is the important half of the key, and it is what makes per-alliance attendance
    // legible at all: two people in one alliance are capturing one roster and must fold; two people
    // in DIFFERENT alliances are capturing two rosters that merely coincide in time, and folding
    // those would erase the distinction the camp card exists to show.
    //
    // Status is in the key too: a Pending capture must never be absorbed into a verified one, or an
    // unvouched-for roster would ride into the payout on somebody else's Confirm.
    //
    // Checked symmetrically (±window) so an addon retry carrying an older CapturedAtUtc still lands
    // in the right place rather than opening a snapshot behind the one it belongs to.
    public async Task<AttendanceSnapshot?> FindUnlinkedMergeTargetAsync(
        int linkshellId,
        DateTime capturedAtUtc,
        string? allianceKey,
        string snapshotStatus,
        CancellationToken cancellationToken)
    {
        var mergeWindow = HnmConfig.SnapshotMergeWindow(0);
        var fromUtc = capturedAtUtc - mergeWindow;
        var toUtc = capturedAtUtc + mergeWindow;
        var normalized = AllianceIdentityService.NormalizeKey(allianceKey);

        // No identity means we cannot tell one alliance from another, and folding on time alone is
        // exactly the bug per-alliance posting was introduced to fix. Start a new row instead.
        if (normalized is null)
        {
            return null;
        }

        var candidates = await _db.AttendanceSnapshots
            .Include(item => item.Entries)
            .Where(item =>
                item.LinkshellId == linkshellId &&
                item.WindowEventId == null &&
                item.SnapshotStatus == snapshotStatus &&
                item.AllianceKey != null &&
                item.CapturedAtUtc >= fromUtc &&
                item.CapturedAtUtc <= toUtc)
            .OrderByDescending(item => item.CapturedAtUtc)
            .ToListAsync(cancellationToken);

        // Compared in memory so the match uses the SAME normalization the writer used. A database
        // collation deciding what counts as the same character name is how two clients that agree
        // end up in different rows.
        return candidates.FirstOrDefault(item =>
            string.Equals(AllianceIdentityService.NormalizeKey(item.AllianceKey), normalized,
                StringComparison.Ordinal));
    }

    // Files a snapshot into a slot on its Window Event: either a numbered window, or Misc.
    //
    // One helper because four endpoints set this (web attach + re-slot, Activity attach + re-slot)
    // and the clamp and the Misc/null invariant have to be identical in all four. Ingest used to
    // derive the number itself; it no longer does, which is the whole point of the change.
    public static void ApplySlot(
        AttendanceSnapshot snapshot,
        WindowEvent windowEvent,
        string? slotKind,
        int? windowNumber)
    {
        if (AttendanceSnapshotSlotKinds.IsMisc(slotKind))
        {
            snapshot.SlotKind = AttendanceSnapshotSlotKinds.Misc;
            // Misc ALWAYS nulls the number. Leaving a stale one would make the display re-derive a
            // "Window 4 of 25" label to sit beside the Misc chip.
            snapshot.WindowNumber = null;
            return;
        }

        snapshot.SlotKind = AttendanceSnapshotSlotKinds.Window;
        // An explicit choice is stamped CONCRETELY rather than left null to be re-derived at read
        // time: the grid can move, and the officer's decision is the thing worth keeping. With no
        // choice given, fall back to the grid — which lands null on an ungridded camp, and that is
        // correct and distinct from Misc.
        snapshot.WindowNumber = windowNumber is int chosen
            ? Math.Clamp(chosen, 1, Math.Max(1, WindowEventWindowGrid.WindowCount(windowEvent)))
            : WindowEventWindowGrid.SnapshotWindowNumber(windowEvent, snapshot.CapturedAtUtc);
    }

    // Trim, collapse interior whitespace, upper-case. The stored NormalizedName of every Window
    // Event is produced by this, so all three callers must agree on it exactly.
    public static string? NormalizeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts).ToUpperInvariant();
    }
}
