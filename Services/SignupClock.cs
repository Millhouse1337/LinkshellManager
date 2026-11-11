using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// When a brand-new sign-up's attendance clock starts. Before the event starts the clock
// stays empty and starting the event stamps it (StartTime ??= commencement). Once the
// event is live the person is a late join, so the clock starts NOW and the row is marked
// IsQuickJoin, exactly like quick-join and late party-slot claims. Copying the event's
// start time here used to pay a last-minute sign-up for the whole event.
// Shared by the app's /signup and the Discord board's Sign up wizard so both agree.
// Only for NEW rows: someone already signed up who switches jobs keeps their clock.
public static class SignupClock
{
    public static (DateTime? StartTime, bool IsQuickJoin) ForNewSignup(Event eventEntity, DateTime nowUtc)
    {
        return eventEntity.CommencementStartTime.HasValue
            ? (nowUtc, true)
            : (null, false);
    }
}
