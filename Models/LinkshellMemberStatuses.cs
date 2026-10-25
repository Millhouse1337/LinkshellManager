namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// The statuses an officer can set on a membership (<see cref="AppUserLinkshell.Status"/>).
/// Moved off the old website's ManageTeamViewModel so it outlives it.
/// </summary>
public static class LinkshellMemberStatuses
{
    public static readonly IReadOnlyList<string> Options = new[]
    {
        "Active",
        "Pending",
        "Inactive"
    };
}
