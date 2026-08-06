using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// Where "what does this camp pay?" is RESOLVED — never where it is computed.
//
// The two finalizers own the formulas (HnmStandardCampFinalizer.ComputeMemberDkp,
// WdCampFinalizer.ComputeDkp) and keep them. What lived in both of them, copied, was the
// PRECEDENCE: `Event.Hnm*Override ?? Linkshell.<the matching setting>`, plus the Standard/Wd
// branch that decides which four settings even apply. That is what moved here, because a third
// caller appeared: GET /api/addon/events has to tell the in-game addon what one more window would
// be worth, and it must arrive at the identical number the finalizer will later pay.
//
// The addon deliberately gets a RESOLVED NUMBER rather than the four bonuses and the mode. Two
// reasons, both learned the hard way:
//
//   1. AddonApiController.AddonEvents.cs:23-27 — "re-deriving any of that here is exactly how the
//      addon drifted out of sync with the board in the first place." A client that re-implements
//      the precedence is a second implementation that can disagree, and the window-number incident
//      recorded at lines 68-82 of that file is what that disagreement looks like.
//   2. The close window MOVES. It is the highest sequence posted so far, so a client holding only
//      the four bonuses cannot price its own next window without also being told the posted set.
//      One scalar, recomputed on every poll, tracks it for free.
//
// The cost is that the addon can show the number but not the breakdown ("0.5 open + 0.5 close").
// That is fine — explaining the payout model is the Activity's job, and
// EventsTabComponent.attendanceWindowDkpNote already does it.
public static class HnmCampPricing
{
    // Standard-mode amounts, already gated on the camp outcome. Lifted verbatim from
    // HnmStandardCampFinalizer.BuildRosterAsync, which now calls this instead.
    //
    // `Window` is the base rate EVERY scanned window pays; Open and Close ride on top of it, which
    // is what makes them read as bonuses. It shares Event.HnmPerWindowOverride with the Manual
    // Check In rate — one column, and the camp's mode decides which linkshell default it falls back
    // to, exactly as the claim / kill overrides have always worked across both modes.
    public static (double Window, double Open, double Close, double Claim, double Kill) StandardBonuses(
        Event ev, Linkshell? linkshell, bool claimed, bool killed)
        => (
            Math.Max(0d, ev.HnmPerWindowOverride ?? linkshell?.HnmStandardWindowBonus ?? 0d),
            Math.Max(0d, ev.HnmOpenBonusOverride ?? linkshell?.HnmStandardOpenBonus ?? 0d),
            Math.Max(0d, ev.HnmCloseBonusOverride ?? linkshell?.HnmStandardCloseBonus ?? 0d),
            claimed ? Math.Max(0d, ev.HnmClaimBonusOverride ?? linkshell?.HnmStandardClaimBonus ?? 0d) : 0d,
            killed ? Math.Max(0d, ev.HnmKillBonusOverride ?? linkshell?.HnmStandardKillBonus ?? 0d) : 0d);

    // Manual Check In amounts, already gated on the camp outcome. Lifted verbatim from
    // WdCampFinalizer.BuildRosterAsync, which now calls this instead.
    //
    // Open and Close share Event.HnmOpen/CloseBonusOverride with the Standard bonuses of the same
    // name — same one-column-per-amount rule as above. They are NOT outcome-gated here: whether a
    // member earns them depends on their own check-in range, which only the finalizer knows.
    public static (double Rate, double Open, double Close, double Claim, double Kill) WdAmounts(
        Event ev, Linkshell? linkshell, bool claimed, bool killed)
        => (
            Math.Max(0d, ev.HnmPerWindowOverride ?? linkshell?.WdDkpPerWindow ?? 0d),
            Math.Max(0d, ev.HnmOpenBonusOverride ?? linkshell?.WdOpenBonus ?? 0d),
            Math.Max(0d, ev.HnmCloseBonusOverride ?? linkshell?.WdCloseBonus ?? 0d),
            claimed ? Math.Max(0d, ev.HnmClaimBonusOverride ?? linkshell?.WdClaimBonus ?? 0d) : 0d,
            killed ? Math.Max(0d, ev.HnmKillBonusOverride ?? linkshell?.WdKillBonus ?? 0d) : 0d);

    // True when EventAttendanceWindow.DkpAmount is honoured at payout for this camp. ONLY Standard
    // HNM: that is the one path whose finalizer reads the column (HnmStandardCampFinalizer).
    //
    // Everything else must refuse the write rather than accept a number it will never pay:
    //   Manual Check In — WdCampFinalizer credits the check-in RANGE, so a member is paid for
    //                    windows that have no EventAttendanceWindow row at all. See its header.
    //   Claim/Kill-style windowed — the two EndEvent bodies pay windowsAttended × DkpPerHour and
    //                    do not look at this column.
    public static bool HonoursWindowAmount(Event ev)
        => DiscordEventMessageBuilder.IsHnm(ev) && !DiscordEventMessageBuilder.IsWd(ev);

    // What ONE window is worth per attendee on this camp, or null when the camp does not price
    // windows at all. Null is a REAL answer that both clients render as "nothing" — never as 0.
    //
    // `closeWindow` is what HnmStandardCampFinalizer.ResolveCloseWindow would return right now.
    // `explicitAmount` is the officer's EventAttendanceWindow.DkpAmount for this sequence.
    public static double? WindowValueFor(
        Event ev, Linkshell? linkshell, int sequence, int closeWindow, double? explicitAmount)
    {
        // The snapshot pays NOTHING on a Manual Check In camp — credit comes from Check In /
        // Check Out, which is exactly what the Activity's card says on its face.
        if (DiscordEventMessageBuilder.IsWd(ev)) return null;

        if (DiscordEventMessageBuilder.IsHnm(ev))
        {
            var (window, open, close, _, _) = StandardBonuses(ev, linkshell, claimed: false, killed: false);
            return HnmStandardCampFinalizer.WindowValue(
                sequence, closeWindow, explicitAmount, window, open, close);
        }

        // Claim/Kill-style windowed events really do pay windowsAttended × DkpPerHour via
        // EndEventCoreAsync, so they keep reporting it. The CREDIT chain, not the display chain —
        // the same expression both EndEvent bodies use, because this has to match what they pay.
        var creditWindows = Math.Clamp(
            ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName), 1, HnmConfig.MaxWindow);
        if (creditWindows <= 1) return null;   // paid by the clock, not by the window
        return Math.Max(0d, ev.DkpPerHour ?? 0);
    }

    // What ONE MORE window, posted right now against `sequence`, would be worth per attendee.
    //
    // closeWindow := sequence, because posting a window MAKES it the close (ResolveCloseWindow
    // takes the highest posted sequence). So a fresh Standard camp's window 1 is priced as
    // open + close, and drops to open alone once a later window lands. This number moves on
    // purpose — it is a prediction about the next post, not a fact about the past.
    public static double? DefaultWindowValue(Event ev, Linkshell? linkshell, int sequence)
        => WindowValueFor(ev, linkshell, sequence, closeWindow: sequence, explicitAmount: null);
}
