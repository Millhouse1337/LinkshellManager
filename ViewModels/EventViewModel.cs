using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class EventViewModel
{
    public int LinkshellId { get; set; }
    public List<Linkshell> Linkshells { get; set; } = new();
    public List<string> LinkshellMembers { get; set; } = new();
    public Event Event { get; set; } = new();
    public List<Job> Jobs { get; set; } = new();
    public DateTime? CommencementStartTime { get; set; }
    public string? CreatorCharacterName { get; set; }
    public List<AppUserEvent> AppUserEvents { get; set; } = new();
    public List<EventLootDetail> EventLootDetails { get; set; } = new();

    // HNM-style multi-window attendance. Empty for single-window events.
    public int WindowCount { get; set; } = 1;
    public List<EventAttendanceWindowViewModel> AttendanceWindows { get; set; } = new();

    [DataType(DataType.DateTime)]
    public DateTime? StartTime { get; set; }

    [DataType(DataType.DateTime)]
    public DateTime? EndTime { get; set; }
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
