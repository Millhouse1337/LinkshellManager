using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One Discord channel a linkshell posts a given kind of content to, addressed by
// channel ID. The bot posts to it DIRECTLY (POST /channels/{id}/messages) so the
// messages can carry interactive components (job-signup select, withdraw button)
// — unlike the incoming-webhook rows in LinkshellDiscordWebhook, which Discord
// silently strips components from. One row per (linkshell, purpose).
public class LinkshellDiscordChannel
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // What this channel receives. One of DiscordChannelPurposes.
    [MaxLength(32)]
    public string Purpose { get; set; } = string.Empty;

    // Discord channel snowflake the bot posts to.
    [MaxLength(20)]
    public string ChannelId { get; set; } = string.Empty;

    // Cached channel name (e.g. "henm-events") for display in the config UI.
    [MaxLength(128)]
    public string? ChannelName { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

public static class DiscordChannelPurposes
{
    public const string HenmEvents = "HenmEvents";
    public const string KsnmEvents = "KsnmEvents";
    public const string Events = "Events";
    public const string Auctions = "Auctions";
    public const string Loot = "Loot";

    public static readonly IReadOnlyList<string> All = new[]
    {
        HenmEvents, KsnmEvents, Events, Auctions, Loot
    };

    // Human label for the config UI.
    public static string Label(string purpose) => purpose switch
    {
        HenmEvents => "HENM events",
        KsnmEvents => "KSNM events",
        Events => "Other events",
        Auctions => "Auctions",
        Loot => "Loot",
        _ => purpose
    };

    // Maps an Event.EventType to the channel purpose it posts to: HENM events to
    // the HENM channel, KSNM events to the KSNM channel, and everything else to
    // the general "Other events" channel (used as the fallback when no
    // type-specific channel is configured).
    public static string ForEventType(string? eventType)
    {
        var type = (eventType ?? string.Empty).Trim();
        if (string.Equals(type, "HENM", StringComparison.OrdinalIgnoreCase))
        {
            return HenmEvents;
        }
        if (string.Equals(type, "KSNM", StringComparison.OrdinalIgnoreCase))
        {
            return KsnmEvents;
        }
        return Events;
    }
}
