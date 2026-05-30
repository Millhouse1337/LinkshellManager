namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// The three built-in linkshell ranks, stored on <see cref="AppUserLinkshell.Rank"/>
/// and as the system <see cref="LinkshellRole.Name"/>. Centralized so authorization
/// checks compare against a single source of truth instead of scattered string
/// literals — a typo in one of which would silently grant or deny access.
/// </summary>
public static class LinkshellRanks
{
    public const string Leader = "Leader";
    public const string Officer = "Officer";
    public const string Member = "Member";
}
