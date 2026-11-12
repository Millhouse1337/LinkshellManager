using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.Models;

public sealed class DiscordActivityUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(32)]
    public string DiscordUserId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Username { get; set; } = string.Empty;

    // PHASE4B-DROP: DiscordActivityUsers.Discriminator - no longer written or read (Discord retired discriminators). NOT NULL: new rows insert the default "0".
    [MaxLength(10)]
    public string Discriminator { get; set; } = "0";

    [MaxLength(64)]
    public string? GlobalName { get; set; }

    [MaxLength(128)]
    public string? Avatar { get; set; }

    [MaxLength(450)]
    public string? IdentityUserId { get; set; }

    public AppUser? IdentityUser { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    // PHASE4B-DROP: DiscordActivityUsers.LastSeenAtUtc - was stamped on every request and never read; no longer written.
    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
