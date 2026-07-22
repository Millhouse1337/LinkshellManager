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

    // HNM signup boards: the monster picker options + whether this linkshell allows
    // outside (account-less) Discord signups, which gates the "HNM" type in the
    // create form. RepeatOnTod / RepeatLeadHours are bound from the repeat controls
    // (HNM only) so the board re-posts before the next predicted pop.
    public List<string> MonsterOptions { get; set; } = new();
    public bool OutsidePartySignupEnabled { get; set; }
    // Gates whether "HNM" is offered in the create event-type dropdown. HNM signup boards
    // are roster-only / no-DKP and only make sense when the linkshell opts into them.
    public bool HnmOutsideSignupEnabled { get; set; }
    public bool RepeatOnTod { get; set; }
    // Fractional hours (Activity enters it as H/M/S); the web form keeps a single number.
    public double? RepeatLeadHours { get; set; }

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

    // HNM-style multi-window attendance. Empty for single-window events.
    public int WindowCount { get; set; } = 1;
    public List<EventAttendanceWindowViewModel> AttendanceWindows { get; set; } = new();

    [DataType(DataType.DateTime)]
    public DateTime? StartTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndTime { get; set; }
}

public class EventBoardTodPrefill
{
    // Local (viewer-zone) time, formatted for a datetime-local input ("yyyy-MM-ddTHH:mm:ss").
    public string? TimeLocal { get; set; }
    public string? Cooldown { get; set; }
    public string? Interval { get; set; }
    public int? DayNumber { get; set; }
    public bool? Claim { get; set; }
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

    public string DisplayLabel =>
        string.IsNullOrWhiteSpace(Label) ? $"Window {SequenceNumber}" : Label;
}

public class AttendanceWindowAttendeeViewModel
{
    // AppUserEventWindow.Id — the join row, used for the per-row Remove action.
    public int Id { get; set; }
    public string? CharacterName { get; set; }
    public string? JobName { get; set; }
    public string? SubJobName { get; set; }
    public string? Zone { get; set; }
    public DateTime VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
}
