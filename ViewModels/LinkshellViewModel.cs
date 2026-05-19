using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class LinkshellViewModel
{
    [Required]
    [MaxLength(100)]
    public string LinkshellName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Details { get; set; }
}

// Backs the /Linkshell/Customize page. Mirrors the Discord Activity's
// "Customize Linkshell" card on its Configurations tab.
public class LinkshellCustomizeViewModel
{
    public int LinkshellId { get; set; }
    public string? LinkshellName { get; set; }

    [Required, MaxLength(16)]
    public string? LinkshellType { get; set; } = LinkshellTypes.Both;

    [Required, MaxLength(32)]
    public string? LootStructure { get; set; } = "Dkp";

    [Required, MaxLength(16)]
    public string? DkpRoundingIncrement { get; set; } = "Quarter";

    // Endgame + Missions start OFF by default (match the Linkshell entity
    // defaults); linkshells opt in from the Customize page.
    public bool EnableEndgame    { get; set; } = false;
    public bool EnableHnmSection { get; set; } = true;
    public bool EnableMissions   { get; set; } = false;
    public bool EnableAuctions   { get; set; } = true;
    public bool EnableToDs       { get; set; } = true;
    public bool EnableEvents     { get; set; } = true;
    public bool EnableDkp        { get; set; } = true;
    public bool EnableItems      { get; set; } = true;
    public bool EnableRevenue    { get; set; } = true;

    // Named Discord channel webhooks. Every `/lsm now` snapshot is posted to
    // each as a party-grouped embed. Rows with a blank URL are dropped on
    // save (so clearing a row deletes the webhook). Empty = posting disabled.
    public List<DiscordWebhookInput> DiscordWebhooks { get; set; } = new();

    public bool CanManageRoles { get; set; }

    public List<Linkshell> ManageableLinkshells { get; set; } = new();
}

public class DiscordWebhookInput
{
    // Dropdown values. A channel now has ONE purpose (or none) instead of
    // independent toggles; the three Post* booleans are derived from this on
    // save so the DB model / publishers are unchanged.
    public const string PurposeNone = "";
    public const string PurposeDkpTracking = "DkpTracking";
    public const string PurposePopTracker = "PopTracker";
    public const string PurposeSpentPoints = "SpentPoints";
    public const string PurposeAuctions = "Auctions";

    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(512)]
    public string? Url { get; set; }

    // Single channel purpose chosen from the dropdown. Mapped to the booleans
    // below server-side; null/empty means the channel receives nothing.
    [MaxLength(32)]
    public string? Purpose { get; set; }

    // When set, this channel hosts the live, edit-in-place ToD board.
    public bool PostTodBoard { get; set; }

    // When set, every DKP spend is posted to this channel as a rich embed
    // (append-only — one message per save-burst). UI: "Spent Points".
    public bool PostDkpSpendLog { get; set; }

    // When set, every `/lsm now` attendance snapshot is posted to this
    // channel. UI: "DKP Tracking".
    public bool PostAttendanceSnapshot { get; set; }

    // When set, auction open/close embeds are posted to this channel.
    // UI: "Auctions".
    public bool PostAuctions { get; set; }

    // Collapse the legacy multi-flag state into the single dropdown value.
    // Priority when an older row had more than one set: DKP Tracking, then
    // Pop Tracker, then Spent Points, then Auctions.
    public static string PurposeFor(
        bool postAttendanceSnapshot, bool postTodBoard, bool postDkpSpendLog, bool postAuctions)
        => postAttendanceSnapshot ? PurposeDkpTracking
         : postTodBoard ? PurposePopTracker
         : postDkpSpendLog ? PurposeSpentPoints
         : postAuctions ? PurposeAuctions
         : PurposeNone;
}
