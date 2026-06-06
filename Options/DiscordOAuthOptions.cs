using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.Options;

/// <summary>
/// Strongly typed configuration for Discord OAuth used by the embedded Activity.
/// </summary>
public sealed class DiscordOAuthOptions
{
    [Required]
    public string ClientId { get; init; } = string.Empty;

    [Required]
    public string ClientSecret { get; init; } = string.Empty;

    /// <summary>
    /// Optional Discord bot token. When set — and the bot has been added to a
    /// linkshell's locked server with the privileged "Server Members" intent
    /// enabled — the invite player search is filtered to only members of that
    /// server. When unset (or the bot can't read a given server), the invite
    /// search is unfiltered; the access-time guild lock still protects data.
    /// Supplied as a secret (env var Discord__BotToken / user-secrets), never
    /// committed to appsettings.
    /// </summary>
    public string? BotToken { get; init; }
}
