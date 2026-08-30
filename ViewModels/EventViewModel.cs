using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class EventViewModel
{
    public int LinkshellId { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public List<string> LinkshellMembers { get; set; } = new();
    public Event Event { get; set; } = new();
    public DateTime? CommencementStartTime { get; set; }
    public string? CreatorCharacterName { get; set; }
    public List<AppUserEvent> AppUserEvents { get; set; } = new();
    public List<EventLootDetail> EventLootDetails { get; set; } = new();

    // Optional Party Setup link. PartySetupId is bound from the dropdown on the
    // create/edit form; AvailablePartySetups is server-loaded for the dropdown
    // options and is keyed to the active linkshell.
    public int? PartySetupId { get; set; }
    public List<PartySetupOption> AvailablePartySetups { get; set; } = new();
    public string? LinkedPartySetupName { get; set; }
    public string? LinkedPartySetupMonsterName { get; set; }

    // Seed for the inline "Create New Party Setup" modal on the create/edit event
    // form (parity with the Discord Activity's embedded party-setup editor). Carries
    // the linkshell + option lists the editor needs; null when no linkshell is active.
    public PartySetupEditorViewModel? PartySetupEditor { get; set; }

    // HNM camps: the monster picker options. RepeatOnTod is bound from the repeat toggle
    // (HNM only) so the board re-posts before the next predicted pop. Nothing gates the
    // "HNM" type in the create form any more — every linkshell can run a camp.
    public List<string> MonsterOptions { get; set; } = new();
    public bool RepeatOnTod { get; set; }

    // NOT form-bound: the linkshell's ENABLED Repeat-on-ToD leads, in fractional hours, keyed by
    // every spelling of each spawn (HnmConfig.MonsterMatchNames — so a board created under
    // "Fafnir" is found by a "Fafnir/Nidhogg" pick). Absent = that monster isn't repeating.
    //
    // The create form reads this the moment a monster is picked so its toggle + lead open on what
    // the monster is ALREADY configured for. Without it the form would show a fresh, empty
    // recurrence choice for a monster that is repeating, and submitting the form as it looked
    // would switch the standing board off.
    public Dictionary<string, double> MonsterRepeatLeads { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // The pops the linkshell is still waiting on, so picking a monster can pre-fill Start with
    // the repop the camp is almost certainly being staged for — the same instant
    // HnmAutoEventService stamps when it builds the event from a ToD itself.
    //
    // NOT form-bound. Keyed by every spelling of the spawn (HnmConfig.MonsterMatchNames, so a
    // "Fafnir" ToD is found by a "Fafnir/Nidhogg" pick) and valued as a
    // "yyyy-MM-ddTHH:mm:ss" wall-clock string in the viewing user's time zone — exactly what the
    // datetime-local Start input takes. Built by UpcomingRepopLookup, which the Activity's
    // create-event modal shares.
    public Dictionary<string, string> UpcomingRepopStarts { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // How many hours before the predicted repop the board re-posts, bound from the create/
    // edit form's lead input (HNM + RepeatOnTod only). Fractional, so 1.5 = 1h30m. Left
    // blank it posts as null, which HnmRecurringBoardService.UpsertAsync reads as "keep the
    // board's current lead" — so an officer who never touches the box can't wipe a lead set
    // on the End Camp / Post ToD form, which still owns the same setting.
    [Range(0, 168, ErrorMessage = "Enter a lead between 0 and 168 hours.")]
    public double? RepostLeadHours { get; set; }

    // Create/Edit form only (NOT form-bound): the linkshell's own HNM payout amounts, so the
    // form's "03 — Camp DKP" section can show what a camp pays by default and seed the
    // "Change DKP" boxes with real numbers. Which pair the defaults come from is decided by
    // HnmAttendanceMode: the linkshell stores its Standard and Manual Check In amounts
    // separately even though the per-camp override columns are shared, and a camp only ever
    // runs in one mode. Mirrors the Activity's hnmLinkshellBonus(), and behind it
    // HnmCampPricing.StandardBonuses / WdAmounts, which is the authority at payout.
    public string HnmAttendanceMode { get; set; } = HnmAttendanceModes.Standard;
    public double HnmDefaultWindowBonus { get; set; }
    public double HnmDefaultOpenBonus { get; set; }
    public double HnmDefaultCloseBonus { get; set; }
    public double HnmDefaultClaimBonus { get; set; }
    public double HnmDefaultKillBonus { get; set; }

    // Whether this linkshell runs on DKP at all (LootStructure != "LootCouncil"). False hides
    // the DKP/hour field, as the Activity's isDkpModeForSelectedLinkshell() does.
    public bool IsDkpLootStructure { get; set; } = true;

    // Index page only (NOT form-bound): the monster's standing Repeat-on-ToD board state, used
    // to pre-fill the End Camp / Post ToD modal's re-post toggle + lead so the officer sees and
    // adjusts what's actually configured. Null lead = no enabled board for this monster.
    public bool BoardRepostEnabled { get; set; }
    public double? BoardRepostLeadHours { get; set; }

    // Loaded for events on the Index page that have a linked Party Setup —
    // powers the inline "View & Sign Up" panel (alliance/parties/slots tree
    // with self-service signup and "Sign Up Manually" fallback). Null on
    // pages that don't render the panel.
    public PartySetupBoardViewModel? LinkedPartySetupBoard { get; set; }
    public bool CurrentUserOwnsLinkedPartySetupSlot { get; set; }

    // For a "defeated / awaiting re-post" HNM board, the already-logged ToD's values so the
    // "Edit ToD" modal opens pre-filled (mirrors the Activity's openEditForBoard). Null for
    // boards that haven't been posted yet (the "Post ToD" modal opens with defaults).
    public EventBoardTodPrefill? BoardTod { get; set; }

    // Index page only (NOT form-bound): this monster's cooldown and interval AS THIS LINKSHELL HAS
    // THEM CONFIGURED (LinkshellMonsterTiming, falling back to the built-in default), as the labels
    // Tod.Cooldown / Tod.Interval store. What a fresh "Post ToD" modal opens on.
    //
    // Resolved server-side because the client cannot know them: they are per-linkshell and editable.
    // The modal used to carry its own hardcoded monster tables instead, which is how it came to
    // offer 72h for the ToAU three long after the app itself had settled on 48h, and how it would
    // now offer a 1-hour interval against a 6-hour configured cadence. Null on non-HNM cards.
    public string? BoardTodDefaultCooldown { get; set; }
    public string? BoardTodDefaultInterval { get; set; }

    // HNM-style multi-window attendance. Empty for single-window events.
    public int WindowCount { get; set; } = 1;
    public List<EventAttendanceWindowViewModel> AttendanceWindows { get; set; } = new();

    // What the tab strip renders: every window the camp has SAT THROUGH, posted or not.
    // AttendanceWindows stays the posted-only list, which is what the card's "N of M" counts.
    public List<AttendanceWindowTabViewModel> AttendanceWindowTabs { get; set; } = new();

    // The camp's Claim Shield lotteries, newest first. Rendered under the attendance windows
    // because it is evidence about the same camp — and because it now PAYS: both finalizers gate
    // the claim bonus on a tag here rather than on having been scanned in the close window, so an
    // officer reading this section is reading part of the payout.
    public List<ClaimShieldCaptureViewModel> ClaimShieldCaptures { get; set; } = new();

    // Whether the viewer may tick a window as the camp's close. Same permission the management
    // endpoint enforces, so the page can't offer a control the server would refuse.
    public bool CanMarkClosingWindow { get; set; }

    // Whether the Break Room (timer, progress, and the whole Actions column on Start.cshtml)
    // applies. False for windowed HNM camps, which credit per posted window and have no timer to
    // pause. Server-computed from Services/EventBreakPolicy, replacing this view's old local
    // `EventType == "HNM"` test — that one axis disagreed with the window-count test the Activity
    // and addon used, so the same camp answered differently depending on where you asked.
    public bool SupportsBreakRoom { get; set; } = true;

    [DataType(DataType.DateTime)]
    public DateTime? StartTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndTime { get; set; }
}

// One Claim Shield lottery the addon captured during this camp, flattened for the view.
//
// MIRROR of the Activity's ActivityClaimShieldCaptureDto — the same rows rendered by the other
// client, and the two must show the same people or an officer checking one against the other has
// no way to tell which is lying.
public class ClaimShieldCaptureViewModel
{
    public int Id { get; set; }
    public string MonsterName { get; set; } = string.Empty;
    public bool Won { get; set; }
    // Players in the lottery SERVER-WIDE, off the game's own result line. Members.Count is the
    // linkshell's share of it, and the two are routinely far apart.
    public int TotalPlayers { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public List<ClaimShieldCaptureMemberViewModel> Members { get; set; } = new();
}

public class ClaimShieldCaptureMemberViewModel
{
    public string CharacterName { get; set; } = string.Empty;
    // "Azurth casts Dia on the Aspidochelone." — the line the tag decision rests on, so a wrong
    // one is visibly wrong rather than merely wrong. Null on captures from before actions were
    // recorded; the view falls back to the bare name.
    public string? ActionMessage { get; set; }
    // False when the name resolved to no current membership. Listed anyway — an unmatched name is
    // usually a roster problem an officer can fix, and it is NOT paid, which is worth seeing.
    public bool Matched { get; set; }
}

public class EventBoardTodPrefill
{
    // Local (viewer-zone) time, formatted for a datetime-local input ("yyyy-MM-ddTHH:mm:ss").
    public string? TimeLocal { get; set; }
    public string? Cooldown { get; set; }
    public string? Interval { get; set; }
    public int? DayNumber { get; set; }
    public bool? Claim { get; set; }
    // Null on ToDs logged before Killed was recorded; the modal falls back to "Yes", matching
    // the controller's "unspecified means killed" default.
    public bool? Killed { get; set; }
    public bool Hq { get; set; }
}

public class PartySetupOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? AssignedMonsterName { get; set; }
    // The setup's event type ("Any"/null = shows for every event type). Drives the
    // client-side filtering of the picker as the event type is chosen.
    public string? EventType { get; set; }
}

public class EventAttendanceWindowViewModel
{
    public int Id { get; set; }
    public int SequenceNumber { get; set; }
    public string? Label { get; set; }
    public DateTime PostedAt { get; set; }
    public List<AttendanceWindowAttendeeViewModel> Attendees { get; set; } = new();

    // What this window pays each attendee, already resolved server-side (an officer's explicit
    // price when they set one, otherwise the camp's open / close bonuses for this sequence). Null
    // means the window pays nothing on its own, which on a middle window is the normal answer.
    //
    // NOT Event.DkpPerHour, which this page used to divide by and which HnmStandardCampFinalizer
    // ignores outright — that is how two pages in the same app showed two different numbers for
    // the same window.
    public double? DkpAmount { get; set; }

    // The officer's "this window closes the camp out" tick, and the only thing that pays the close
    // bonus. It used to be derived as "the newest window posted", which is what put a close bonus
    // on every window of every camp. At most one window per event carries it.
    public bool IsClosingWindow { get; set; }

    // The addon's Post Kill roster: who was there when the mob died, which is a different list
    // from who sat the window. Worth 0 as a window (the kill bonus is what pays it) and never
    // eligible to be the close.
    public bool IsKillWindow { get; set; }

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Label) ? $"Window {SequenceNumber}" : Label;
}

// One entry in the Attendance Windows strip. Window is null for a window the camp REACHED but
// never posted a roster for — those still get a tab, because the gap is the thing an officer
// needs to see.
//
// MIRROR of the Activity's AttendanceWindowTab (discord-activity/src/app/home/tabs/
// events-tab.component.ts). Built by EventController.BuildAttendanceWindowTabs.
public class AttendanceWindowTabViewModel
{
    public int SequenceNumber { get; set; }
    public EventAttendanceWindowViewModel? Window { get; set; }

    // A posted window carries its own name (an officer's label, or "Open"/"Close" on a two-post
    // camp); an unposted one only ever has its number to go on.
    public string Label => Window?.DisplayLabel ?? $"Window {SequenceNumber}";
    public int AttendeeCount => Window?.Attendees.Count ?? 0;
}

public class AttendanceWindowAttendeeViewModel
{
    // AppUserEventWindow.Id — the join row, used for the per-row Remove action.
    public int Id { get; set; }

    // The character the roster read actually SAW — the alt, when the player was on one.
    public string? CharacterName { get; set; }

    // Their roster main, non-null ONLY when CharacterName above is an alt of it. Renders as the
    // "(alt of Edicius)" note beside the name; null is the ordinary case and shows nothing.
    public string? MainCharacterName { get; set; }

    public string? JobName { get; set; }
    public string? SubJobName { get; set; }
    public string? Zone { get; set; }
    public DateTime VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
}
