using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// THE rule for "does the Break Room apply to this event".
//
// A windowed (HNM) event credits attendance PER POSTED WINDOW, not by accumulated clock time.
// There is no timer to pause, so every break-creating action — take break, force break, and the
// live "Withdraw From Event" that parks a member in the Break Room — is meaningless for it and
// must be refused. Every surface (Activity, Razor, addon, both API controllers) asks THIS
// question rather than re-deriving its own `windowCount > 1` test, which is how the app ended up
// gating the Break Room panel while still offering Force break / Verify / Deny on the same camp.
//
// Deliberately NOT in HnmConfig (string-in/int-out by design, no Models dependency) and not in
// DiscordEventMessageBuilder (owns the window-count DATA, but "may this event use the Break Room"
// is a policy question, and burying it in a message renderer is how it stays un-found).
public static class EventBreakPolicy
{
    // The one message every surface shows when a break action is refused. Both clients render the
    // response body's `error` verbatim (addon api.lua's decoded.error; the Activity's
    // formatActionError), so this text reaches the user unchanged.
    public const string WindowedRejectionMessage =
        "Break Room actions don't apply to windowed (HNM) events — attendance is credited "
      + "per posted window, not by time on the clock, so there is no timer to pause.";

    // Withdraw gets its own wording: the member pressed "Withdraw", not "Break", and telling them
    // about a timer they never touched would read as a non-sequitur.
    public const string WindowedWithdrawRejectionMessage =
        "This camp credits attendance per posted window, so there's nothing to withdraw from "
      + "mid-run — ask an officer to remove your posted windows instead.";

    // MAX of the two window-count chains that exist in this codebase:
    //
    //   "Display" chain — DiscordEventMessageBuilder.EffectiveWindowCount: WindowCountOverride
    //                     ?? DefaultWindowCadence(AssignedMonsterName) ?? GetWindowCount(
    //                     AssignedMonsterName ?? EventName). Feeds the Discord board, the Activity
    //                     DTO, and the addon's event list.
    //   "Credit" chain  — WindowCountOverride ?? GetWindowCount(EventName). Used by
    //                     EndEventCoreAsync, WdCampFinalizer, the addon late-join guard, and the
    //                     Razor view model.
    //
    // Neither dominates the other (an event named "Fafnir" with a custom AssignedMonsterName gives
    // display=1, credit=2), so taking the max keeps this gate from ever being LOOSER than whichever
    // chain a credit path happens to consult. This is a fail-closed gate, NOT a unification of the
    // two chains — unifying them would move real DKP payouts and belongs in its own change.
    public static int GatingWindowCount(Event ev) => Math.Max(
        DiscordEventMessageBuilder.EffectiveWindowCount(ev),
        Math.Clamp(ev.WindowCountOverride ?? HnmConfig.GetWindowCount(ev.EventName), 1, HnmConfig.MaxWindow));

    // Windowedness and EventType are orthogonal axes today: an EventType="HNM" board can have one
    // window (unknown monster, no override), and a multi-window board can carry a non-HNM type (the
    // addon's NMS / HENM preset categories). Razor gated on the type, the Activity and the addon on
    // the count, so the same camp answered differently depending on where you asked.
    //
    // OR-ing them is fail-closed: relative to what any surface shows today this can only ever
    // REMOVE break UI, never add it, so no surface regresses.
    public static bool IsWindowedAttendance(Event ev) =>
        GatingWindowCount(ev) > 1
        || string.Equals(ev.EventType, "HNM", StringComparison.OrdinalIgnoreCase);

    public static bool SupportsBreakRoom(Event ev) => !IsWindowedAttendance(ev);
}
