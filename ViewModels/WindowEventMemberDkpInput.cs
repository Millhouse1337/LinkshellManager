namespace LinkshellManagerDiscordApp.ViewModels;

// Per-character DKP override posted with the Save / Post / Update-on-Sheet
// actions (ActivityWindowEventsController.WindowEvents, WindowEventMiscDkp): one
// CharacterName + DkpAmount per row in the combined-members table.
public sealed class WindowEventMemberDkpInput
{
    public string? CharacterName { get; set; }
    public double? DkpAmount { get; set; }
}
