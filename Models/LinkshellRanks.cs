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

    /// <summary>
    /// Built-in probationary rank: same access as Member but without bidding
    /// (LinkshellRole.CanBid off). Not part of the manage bar.
    /// </summary>
    public const string Trial = "Trial";

    /// <summary>True if the rank is Leader (case-insensitive).</summary>
    public static bool IsLeader(string? rank)
        => string.Equals(rank, Leader, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if the rank is Leader or Officer (case-insensitive) — the "can manage
    /// the linkshell" bar shared by most management endpoints. Use this for
    /// in-memory checks; EF queries must compare against the constants directly
    /// (e.g. <c>r.Rank == LinkshellRanks.Leader</c>) so they translate to SQL.
    /// </summary>
    public static bool IsLeaderOrOfficer(string? rank)
        => IsLeader(rank) || string.Equals(rank, Officer, StringComparison.OrdinalIgnoreCase);
}
