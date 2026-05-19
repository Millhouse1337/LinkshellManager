using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One named Discord channel webhook for a linkshell. Every `/lsm now`
// attendance snapshot is posted to every webhook configured here. Replaces
// the old single Linkshell.DiscordWebhookUrl column so a linkshell can post
// to multiple channels, each with a human label.
public class LinkshellDiscordWebhook
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Human label so officers can tell webhooks apart (e.g. "Main LS",
    // "Officers", "HNM alerts"). Optional; the UI falls back to a number.
    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(512)]
    public string Url { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }

    // When true, this channel hosts the live, edit-in-place ToD board: a
    // single message listing every ToD that is re-edited (never re-posted)
    // whenever a ToD changes.
    public bool PostTodBoard { get; set; }

    // The Discord message id of this webhook's board message. Null until the
    // first board post; set from the create response, used for subsequent
    // PATCH edits. Cleared if the message is gone (404) so it reposts.
    [MaxLength(32)]
    public string? TodBoardMessageId { get; set; }

    // When true, every DKP spend (any negative DkpLedgerEntry — auction win,
    // loot-edit, negative adjustment/audit) is posted to this channel as a
    // rich embed. Append-only: each save-burst posts a new message (unlike
    // the edit-in-place ToD board), so no message id is tracked.
    // (UI label: "Spent Points".)
    public bool PostDkpSpendLog { get; set; }

    // When true, every `/lsm now` attendance snapshot is posted to this
    // channel as a party-grouped embed. Snapshots used to fan out to ALL
    // webhooks unconditionally; this makes it an opt-in per channel.
    // (UI label: "DKP Tracking".)
    public bool PostAttendanceSnapshot { get; set; }

    // When true, auction open + close result embeds are posted to this
    // channel. Replaces the old Discord-bot per-auction-channel feature.
    // (UI label: "Auctions".)
    public bool PostAuctions { get; set; }
}
