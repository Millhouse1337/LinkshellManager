using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One user-defined "route": a Discord channel the bot posts to, plus the set of
// post types it receives. Replaces the old fixed-purpose LinkshellDiscordChannel
// + the webhook flags — a linkshell now owns a list of these and assigns post
// types freely (combine several into one channel, or split across channels).
//
// The bot posts DIRECTLY to ChannelId (POST /channels/{id}/messages) so messages
// can carry interactive components (event signup buttons) and edit-in-place
// boards (ToD). Requires the bot to be in the server with Send Messages.
public class LinkshellChannelRoute
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Officer label so routes are easy to tell apart (e.g. "HENM alerts",
    // "Loot & Auctions"). Optional; the UI falls back to the channel name.
    [MaxLength(64)]
    public string? Name { get; set; }

    // Discord channel snowflake the bot posts to.
    [MaxLength(20)]
    public string ChannelId { get; set; } = string.Empty;

    // Cached channel name (e.g. "henm-events") for display in the config UI.
    [MaxLength(128)]
    public string? ChannelName { get; set; }

    // Which post types this route receives. Multiple may be true (combine types
    // into one channel). For non-event types exactly one route per linkshell may
    // carry each flag (enforced on save); PostEvents may be on several routes,
    // distinguished by EventTypeFilter.
    public bool PostEvents { get; set; }
    public bool PostLoot { get; set; }
    public bool PostAuctions { get; set; }
    public bool PostAttendance { get; set; }
    public bool PostTodBoard { get; set; }

    // Event-type filter (only meaningful when PostEvents). Pipe-delimited list of
    // EventTypeVocabulary keys this route handles; null/empty = catch-all (any
    // event type not claimed by a more specific route). Pipe separator mirrors
    // Linkshell.HiddenTodMonsters.
    [MaxLength(256)]
    public string? EventTypeFilter { get; set; }

    // Optional per-monster narrowing for HNM event routing (only meaningful when the
    // EventTypeFilter includes "HNM"). Pipe-delimited monster names (TodManagerViewModel
    // .SupportedMonsters): when set, this route only receives HNM events for THOSE monsters,
    // so a specific HNM can post to its own channel. Null/empty = catch every HNM (the
    // existing behavior). A monster-specific route is preferred over a catch-all HNM route.
    [MaxLength(512)]
    public string? HnmMonsterFilter { get; set; }

    // The Discord message id of THIS route's live, edit-in-place ToD board. Null
    // until the first post; set from the create response, used for subsequent
    // edits. Cleared if the message is gone (404) so it reposts. Only the route
    // with PostTodBoard uses this.
    [MaxLength(32)]
    public string? TodBoardMessageId { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

// The kinds of content the bot posts. Each maps to a PostX flag on a route.
public static class ChannelPostTypes
{
    public const string Events = "Events";
    public const string Loot = "Loot";
    public const string Auctions = "Auctions";
    public const string Attendance = "Attendance";
    public const string TodBoard = "TodBoard";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Events, Loot, Auctions, Attendance, TodBoard
    };

    // Non-event post types: exactly one route per linkshell may own each.
    public static readonly IReadOnlyList<string> NonEvent = new[]
    {
        Loot, Auctions, Attendance, TodBoard
    };

    public static string Label(string postType) => postType switch
    {
        Events => "Event posts",
        Loot => "Loot posts",
        Auctions => "Auction posts",
        Attendance => "Attendance posts",
        TodBoard => "ToD board",
        _ => postType
    };
}

// The event types officers can route by (the filter option list). Mirrors the
// Activity create-event dropdown. Populates the route filter multiselect and
// validates the stored filter. "Other" covers any event whose type isn't one of
// the named ones, so a route can explicitly catch those (there is no
// unfiltered catch-all — a route only receives the event types it checks).
public static class EventTypeVocabulary
{
    public static readonly IReadOnlyList<string> All = new[]
    {
        "Sky", "Sea", "HNM", "HENM", "Limbus", "Dynamis", "BCNM", "KSNM", "Other"
    };
}
