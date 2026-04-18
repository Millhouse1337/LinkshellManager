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
}
