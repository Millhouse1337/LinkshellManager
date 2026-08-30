using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The Misc rate, materialized as ordinary per-member DKP overrides.
//
// WHY IT WORKS THIS WAY. A Window Event pays one flat DkpAmount per member, with per-character
// exceptions in WindowEventMemberDkp. "Misc-only members are paid MiscDkpAmount" is exactly such an
// exception, so it is expressed as one rather than as a second pricing rule threaded through
// WindowEventDkpLedgerService — which has no test coverage and is the last file worth changing
// speculatively. Both ledger methods already read override rows correctly; nothing there had to
// learn about Misc at all.
//
// It is also the more honest UI: the officer sees the actual number in each member's DKP input
// before pressing Post, instead of a rate that only materializes somewhere downstream.
//
// This class is the SAFETY NET, not the main path. The card seeds every member's input from
// AttendanceSectionsBuilder.BuildCombinedMembers (which already applies the misc rate) and submits
// them all, so on the web every name arrives in `submittedNames` and ApplyMiscOverrides does
// nothing. It exists so an API caller that submits nothing — or a partial payload — still pays the
// misc rate rather than silently paying everyone the default.
public static class WindowEventMiscDkp
{
    // Amounts are doubles, and DKP is fractional by design (quarter/half increments), so equality
    // is compared with the same tolerance ApplyMemberDkpOverrides uses.
    private const double Epsilon = 0.0001;

    // Characters credited ONLY by Misc snapshots.
    //
    // ACTIVE snapshots only, matching BuildCombinedMembers exactly. If this counted Pending or
    // Ignored captures, a rejected misc post could make a member who is genuinely a window attendee
    // look misc-only and quietly reprice them.
    //
    // A member seen in ANY window capture is an ordinary attendee even if they also show up in a
    // misc post — the misc rate is for the people who were only ever there off-window.
    public static HashSet<string> MiscOnlyCharacterNames(IEnumerable<AttendanceSnapshot> snapshots)
    {
        var sawWindow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sawMisc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var snapshot in snapshots)
        {
            if (snapshot.SnapshotStatus != AttendanceSnapshotStatuses.Active) continue;
            var isMisc = AttendanceSnapshotSlotKinds.IsMisc(snapshot.SlotKind);

            foreach (var entry in snapshot.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.CharacterName)) continue;
                var name = entry.CharacterName.Trim();
                (isMisc ? sawMisc : sawWindow).Add(name);
            }
        }

        sawMisc.ExceptWith(sawWindow);
        return sawMisc;
    }

    // Brings the override rows in line with the misc rate for every character the caller did NOT
    // submit a value for.
    //
    // `submittedNames` is what keeps this from overruling a person. An officer who deliberately
    // types a misc-only member back to the event default has submitted that name, so this leaves
    // them alone; ApplyMemberDkpOverrides has already removed their row.
    //
    // Requires windowEvent.Snapshots (with Entries) and MemberDkpOverrides to be loaded.
    public static void ApplyMiscOverrides(
        WindowEvent windowEvent,
        double resolvedDefault,
        double miscAmount,
        ISet<string>? submittedNames)
    {
        var miscOnly = MiscOnlyCharacterNames(windowEvent.Snapshots);
        var miscMatchesDefault = Math.Abs(miscAmount - resolvedDefault) < Epsilon;

        var existingByName = windowEvent.MemberDkpOverrides
            .Where(o => !string.IsNullOrWhiteSpace(o.CharacterName))
            .GroupBy(o => o.CharacterName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in miscOnly)
        {
            if (submittedNames is not null && submittedNames.Contains(name)) continue;
            existingByName.TryGetValue(name, out var existing);

            // Misc equal to the default is the DEFAULT STATE (MiscDkpAmount null). Storing a row
            // that merely repeats the event amount would be noise, and ApplyMemberDkpOverrides
            // removes exactly such rows — so this has to agree with it or the two would fight.
            if (miscMatchesDefault)
            {
                if (existing is not null) windowEvent.MemberDkpOverrides.Remove(existing);
                continue;
            }

            if (existing is null)
            {
                windowEvent.MemberDkpOverrides.Add(new WindowEventMemberDkp
                {
                    WindowEventId = windowEvent.Id,
                    CharacterName = name,
                    DkpAmount = miscAmount,
                });
            }
            else if (Math.Abs(existing.DkpAmount - miscAmount) > Epsilon)
            {
                existing.DkpAmount = miscAmount;
            }
        }

        // The cleanup half, and it is not optional: a snapshot can be RE-SLOTTED from Misc to a
        // window, at which point its members stop being misc-only. Without this their old misc row
        // would survive and keep paying the misc rate for a window attendee, forever.
        //
        // Only rows whose amount still equals the misc rate are removed, and only for names the
        // caller did not submit — those are the ones this rule can have written. A hand-typed
        // amount that happens to equal the misc rate is the one collision, and losing it costs a
        // re-type rather than a wrong payout.
        if (miscMatchesDefault) return;

        foreach (var (name, existing) in existingByName)
        {
            if (miscOnly.Contains(name)) continue;
            if (submittedNames is not null && submittedNames.Contains(name)) continue;
            if (Math.Abs(existing.DkpAmount - miscAmount) > Epsilon) continue;
            windowEvent.MemberDkpOverrides.Remove(existing);
        }
    }

    // The names a form payload spoke for, so ApplyMiscOverrides can leave them alone.
    public static HashSet<string> SubmittedNames(IEnumerable<ViewModels.WindowEventMemberDkpInput>? inputs)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (inputs is null) return names;
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.CharacterName)) continue;
            names.Add(input.CharacterName.Trim());
        }
        return names;
    }
}
