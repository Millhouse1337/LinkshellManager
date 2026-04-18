using System.ComponentModel.DataAnnotations;

namespace LinkshellManagerDiscordApp.Models;

public sealed class DiscordActivityUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(32)]
    public string DiscordUserId { get; set; } = string.Empty;

    [MaxLength(32)]
    public string Username { get; set; } = string.Empty;

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

    public DateTimeOffset LastSeenAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
