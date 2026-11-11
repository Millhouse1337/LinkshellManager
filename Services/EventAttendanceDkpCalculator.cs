namespace LinkshellManagerDiscordApp.Services;

/// <summary>
/// Single source of truth for what an event pays ONE participant at close.
///
/// There are two payout models and they must never be confused:
/// <list type="bullet">
///   <item><b>Timed</b> — DKP for the ACTUAL time present, i.e.
///   <c>durationHours × DkpPerHour</c>. <c>durationHours</c> comes from
///   CalculateAccumulatedDurationHours, which honours break state, so a paused member is
///   paid less. That is the whole point of the Break Room.</item>
///   <item><b>Windowed (HNM)</b> — DKP per WINDOW ATTENDED, i.e.
///   <c>windowsAttended × DkpPerWindow</c> (the DkpPerHour column is reused as the
///   per-window rate when WindowCount &gt; 1). Clock time — and therefore break state —
///   must not enter the figure at all. This is why windowed camps have no Break Room:
///   see <see cref="EventBreakPolicy"/>.</item>
/// </list>
///
/// Extracted because the two end-event paths had drifted:
/// EventEndService.EndEventCoreAsync (web + addon) branched correctly, while
/// ActivityDataController.EndEventAsync (the Activity's "End event" button, reachable for
/// windowed camps) had no windowed branch at all and paid every event by duration. That was
/// the one place left in the app where break state still moved windowed DKP. Both now call
/// this, so a third variant can't quietly appear.
///
/// Rounding is applied to the DKP value, not the duration: rounding the duration first
/// floored sub-quarter-hour events to 0h and paid present members 0 DKP.
/// </summary>
public static class EventAttendanceDkpCalculator
{
    public static double Compute(
        bool isWindowed,
        int windowsAttended,
        double durationHours,
        double dkpPerHour,
        double roundingStep)
    {
        if (isWindowed)
        {
            // Deliberately unrounded: the rate is already the per-window grain, and snapping a
            // whole-number multiple of it to the rounding step would silently re-scale payouts
            // for linkshells running a fractional per-window rate.
            return Math.Max(0, windowsAttended) * dkpPerHour;
        }

        return DkpRounding.Round(durationHours * dkpPerHour, roundingStep);
    }

    /// <summary>
    /// What ONE participant is credited at close: DKP plus whether the event counts toward
    /// their Active status. Both end-event loops call this, so the Absent rule lives in one place.
    ///
    /// An officer marking a live attendee Absent stores <c>IsVerified == false</c>. Before this,
    /// neither close loop looked at it, so an Absent member was paid in full and got Active
    /// credit. Now they earn 0 and no Active credit. Their history row is still written (with
    /// IsVerified = false) so the denial stays on record. <c>null</c> (not yet checked) is NOT
    /// absent: those members are paid as before.
    /// </summary>
    public static ParticipantCredit ForParticipant(
        bool? isVerified,
        bool countsTowardActive,
        bool isWindowed,
        int windowsAttended,
        double durationHours,
        double dkpPerHour,
        double roundingStep)
    {
        if (isVerified == false)
        {
            return new ParticipantCredit(Dkp: 0, ActiveCredit: false, IsAbsent: true);
        }

        return new ParticipantCredit(
            Dkp: Compute(isWindowed, windowsAttended, durationHours, dkpPerHour, roundingStep),
            ActiveCredit: countsTowardActive,
            IsAbsent: false);
    }
}

/// <summary>Result of <see cref="EventAttendanceDkpCalculator.ForParticipant"/>.</summary>
public readonly record struct ParticipantCredit(double Dkp, bool ActiveCredit, bool IsAbsent)
{
    // An Absent member still gets a 0 "EventEarned" ledger row, not none: Past Events edits
    // (EventHistoryEditService) find the member's ledger row to move their balance, so without
    // it an officer who later corrects a wrong Absent would only change the label, not the DKP.
    public string LedgerDetails => IsAbsent
        ? "Marked absent - no DKP for this event."
        : "DKP earned from completed event.";
}
