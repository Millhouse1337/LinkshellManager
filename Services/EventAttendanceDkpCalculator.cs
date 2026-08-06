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
/// EventController.EndEventCoreAsync (web + addon) branched correctly, while
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
}
