using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// The one place a Past Event's rows are built: the EventHistory header and one
// AppUserEventHistory row per member. Every path that writes a Past Event builds its rows here --
// End Event (EventEndService), End Camp (HnmCampReviewHandoffService), Post (the camp roster
// re-sync and the legacy archive in WindowEventDkpLedgerService) and adding a member later
// (EventHistoryEditService) -- so a column added to or dropped from these tables changes here once.
//
// It only BUILDS. The callers still add the rows to the database, write the ledger rows and save,
// at the same moments they always did.
//
// Where the paths really differ, the caller passes the value in rather than this class picking
// one, so each difference stays visible at its call site:
//   - StartTime: End Event keeps the event's StartTime; End Camp uses the commencement, because
//     the recycled board's StartTime already points at the next repop.
//   - Duration: End Event falls back to the event's planned Duration when it never commenced;
//     End Camp times it from the board's StartTime instead.
//   - TimeStamp: End Event reads the clock again; End Camp uses its end time.
// PastEventRowsPinnedTests pins every field each path writes.
internal static class EventHistoryBuilder
{
    // The Past Event for a live Event that has just ended. The party setup must already be
    // resolved to a TEMPLATE (PartySetupInheritance.ResolveTemplateIdAsync): a per-event copy is
    // deleted with its event, so storing one here would dangle.
    public static EventHistory FromEvent(
        Event ev,
        int? partySetupTemplateId,
        DateTime? startTime,
        DateTime endTime,
        double? duration,
        DateTime timeStamp) => new()
    {
        LinkshellId = ev.LinkshellId,
        EventName = ev.EventName,
        EventType = ev.EventType,
        EventLocation = ev.EventLocation,
        PartySetupId = partySetupTemplateId,
        StartTime = startTime,
        EndTime = endTime,
        CommencementStartTime = ev.CommencementStartTime,
        Duration = duration,
        DkpPerHour = ev.DkpPerHour,
        CountsTowardActive = ev.CountsTowardActive,
        TimeStamp = timeStamp,
        AppUserEventHistories = new List<AppUserEventHistory>(),
    };

    // The Past Event for a camp whose review card has none yet, built only from the card's Camp*
    // columns (the camp's Event row has been recycled or deleted by then). No party setup, no
    // DkpPerHour and no Duration: the card does not keep them.
    public static EventHistory FromCampReviewCard(WindowEvent windowEvent) => new()
    {
        LinkshellId = windowEvent.LinkshellId,
        EventName = windowEvent.Name,
        EventType = windowEvent.CampEventType,
        EventLocation = windowEvent.CampEventLocation,
        StartTime = windowEvent.CampStartedAtUtc,
        EndTime = windowEvent.CampEndedAtUtc,
        CommencementStartTime = windowEvent.CampStartedAtUtc,
        Duration = null,
        CountsTowardActive = true,
        TimeStamp = windowEvent.PostedToSheetAt ?? windowEvent.CampEndedAtUtc,
        AppUserEventHistories = new List<AppUserEventHistory>(),
    };

    // One member's row. Every field is a parameter on purpose: a camp row has no job type,
    // duration or window count, a timed row does, and each caller says which.
    public static AppUserEventHistory ParticipantRow(
        string? appUserId,
        string? characterName,
        string? jobName,
        string? subJobName,
        string? jobType,
        double? duration,
        double? eventDkp,
        bool activeCredit,
        int? windowsAttended) => new()
    {
        AppUserId = appUserId,
        CharacterName = characterName,
        JobName = jobName,
        SubJobName = subJobName,
        JobType = jobType,
        Duration = duration,
        EventDkp = eventDkp,
        ActiveCredit = activeCredit,
        WindowsAttended = windowsAttended,
    };
}
