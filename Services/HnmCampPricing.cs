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
// The cost is that the addon can show the number but not which of the four it is ("0.5, the open").
// That is fine — explaining the payout model is the Activity's job, and
// EventsTabComponent.attendanceWindowDkpNote already does it.
public static class HnmCampPricing
{
    // Standard-mode amounts, already gated on the camp outcome. Lifted verbatim from
    // HnmStandardCampFinalizer.BuildRosterAsync, which now calls this instead.
    //
    // `Window` is what a REGULAR window pays — the ones that are neither the open nor the close.
    // The three do not stack: see HnmStandardCampFinalizer.WindowValue, which owns that rule. It
    // shares Event.HnmPerWindowOverride with the Manual Check In rate — one column, and the camp's
    // mode decides which linkshell default it falls back to, exactly as the claim / kill overrides
    // have always worked across both modes.
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

    // What the claim and kill bonuses are WORTH on this camp, UNGATED by the outcome.
    //
    // StandardBonuses and WdAmounts zero these when the camp was not claimed / not killed, because
    // they answer "what does this camp PAY" at End Camp, with the outcome already known. A LIVE
    // camp is being asked a different question -- "what is being played for" -- and its outcome is
    // not known yet, so gating here would report 0 on every camp that has not died. That is
    // exactly what made a configured kill bonus invisible in the addon and the Activity: every
    // surface that could have shown it was reading a number that is 0 until the mob is dead.
    //
    // Same override-then-linkshell precedence and same mode branch as the two above, so the amount
    // displayed is the amount those will later resolve to once the outcome lands.
    public static (double Claim, double Kill) OutcomeBonuses(Event ev, Linkshell? linkshell)
    {
        var isWd = DiscordEventMessageBuilder.IsWd(ev);
        return (
            Math.Max(0d, ev.HnmClaimBonusOverride
                ?? (isWd ? linkshell?.WdClaimBonus : linkshell?.HnmStandardClaimBonus) ?? 0d),
            Math.Max(0d, ev.HnmKillBonusOverride
                ?? (isWd ? linkshell?.WdKillBonus : linkshell?.HnmStandardKillBonus) ?? 0d));
    }

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
        Event ev, Linkshell? linkshell, int sequence, int closeWindow, double? explicitAmount,
        bool isKillWindow = false)
    {
        // The snapshot pays NOTHING on a Manual Check In camp — credit comes from Check In /
        // Check Out, which is exactly what the Activity's card says on its face.
        if (DiscordEventMessageBuilder.IsWd(ev)) return null;

        if (DiscordEventMessageBuilder.IsHnm(ev))
        {
            var (window, open, close, _, _) = StandardBonuses(ev, linkshell, claimed: false, killed: false);
            return HnmStandardCampFinalizer.WindowValue(
                sequence, closeWindow, explicitAmount, window, open, close, isKillWindow);
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
    // closeWindow := 0, i.e. "this post is NOT the close". It used to be `sequence` — posting a
    // window made it the close under the old derivation, so the quote read open + close on a fresh
    // camp and every later post quoted the close bonus again. The addon writes its quote back as
    // EventAttendanceWindow.DkpAmount, which REPLACES the window's computed value, so that moving
    // prediction got frozen into every window on the camp: window 1 at open + close + rate, all the
    // rest at close + rate. That is the stacking this whole change removes.
    //
    // The close is now an explicit officer mark (EventAttendanceWindow.IsClosingWindow), and it is
    // not knowable at post time — so this quotes the OPEN on window 1 and the regular window rate
    // everywhere else, and the close bonus is applied when the box is ticked. The number no longer
    // moves: what is quoted is what is paid.
    public static double? DefaultWindowValue(Event ev, Linkshell? linkshell, int sequence)
        => WindowValueFor(ev, linkshell, sequence, closeWindow: 0, explicitAmount: null);
}
