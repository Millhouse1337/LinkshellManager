using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public static class LinkshellTypes
{
    public const string SkySeaDynamis = "SkySeaDynamis";
    public const string HnmOnly       = "HnmOnly";
    public const string Both          = "Both";

    public static readonly IReadOnlyList<string> All = new[]
    {
        SkySeaDynamis, HnmOnly, Both,
    };

    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && All.Contains(value);

    // Anything unknown/null is treated as Both (fail-open) so a missing or
    // stale value never hides content the linkshell expects to see.
    public static string Normalize(string? value)
        => IsValid(value) ? value! : Both;
}

public class Linkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public string? LinkshellName { get; set; }

    [NotMapped]
    public int? TotalMembers { get; set; }

    [NotMapped]
    public int? TotalItems { get; set; }

    [NotMapped]
    public int? Revenue { get; set; }

    public string? Status { get; set; }

    public string? Details { get; set; }

    // Drives which content the in-game addon and the web sidebar surface:
    //   SkySeaDynamis = timed-event experience (no HNM presets / no Window
    //                   Events nav)
    //   HnmOnly       = HNM snapshot sessions only (no Create Event / timed
    //                   presets / Queued / Active; no Event System nav)
    //   Both          = everything (default; matches legacy behavior)
    [MaxLength(16)]
    public string LinkshellType { get; set; } = LinkshellTypes.Both;

    [MaxLength(32)]
    public string LootStructure { get; set; } = "Dkp";

    [MaxLength(16)]
    public string DkpRoundingIncrement { get; set; } = "Quarter";

    public bool EnableHnmSection { get; set; } = true;

    // Disabled by default for Beta — Missions/Endgame UIs are placeholders.
    // Linkshell admins can opt in via Customize to surface the tabs for testers.
    public bool EnableMissions { get; set; } = false;

    public bool EnableAuctions { get; set; } = true;

    // When true, leadership has frozen bidding: all new bids are rejected across
    // every active auction in the linkshell (prevents collusive overbidding that
    // would release a winner's committed/biddable DKP). Toggled by CanLockAuctions.
    public bool AuctionsLocked { get; set; } = false;

    public bool EnableToDs { get; set; } = true;

    public bool EnableEndgame { get; set; } = false;

    public bool EnableEvents { get; set; } = true;

    public bool EnableDkp { get; set; } = true;

    public bool EnableItems { get; set; } = true;

    public bool EnableRevenue { get; set; } = true;

    // Pipe-separated list of monster names to hide from this linkshell's
    // ToD Tracker (Discord Activity + legacy MVC views). Empty = nothing
    // hidden. Pipe separator avoids the comma collision risk if FFXI ever
    // grows a mob name with a comma in it. Edited from the Customize panel.
    public string HiddenTodMonsters { get; set; } = string.Empty;

    // The Discord guild (server) this linkshell is tied to. NULL/empty = not
    // tied to any server. When set it is the single source of truth for every
    // server-scoped behavior:
    //   * Access lock — the Activity can only be opened from this guild
    //     (IsBlockedByGuildLock); the website is unaffected.
    //   * Player search — Add-members browse is narrowed to people in this
    //     server, and the bot can pull the server roster (incl. non-LSM users).
    //   * Discord channel posting — channels are listed from this guild.
    // Discord snowflakes are <= 20 digits. DiscordGuildName is a display cache
    // captured when the guild is set.
    [MaxLength(20)]
    public string? DiscordGuildId { get; set; }

    [MaxLength(256)]
    public string? DiscordGuildName { get; set; }

    // OPTIONAL access lock, separate from setting the server above. When false
    // (default), the linkshell is merely *associated* with DiscordGuildId for
    // roster/invite/channel features — anyone can still view it from anywhere.
    // When true, viewing access is restricted to that server (the Activity can
    // only open it when launched from DiscordGuildId, and overview membership is
    // verified). Setting a server never implies locking.
    public bool LockToDiscordGuild { get; set; } = false;

    [MaxLength(128)]
    public string? GoogleSpreadsheetId { get; set; }

    [MaxLength(64)]
    public string? GoogleSheetTabName { get; set; }

    // Tab that the generic DKP template export writes to and the template
    // import reads from (the canonical 6-column Member/Alts/Current/Total/Spent
    // layout). Default "LSM DKP". A linkshell can point this at a tab of their
    // own sheet that they've reformatted to match the template.
    [MaxLength(64)]
    public string? DkpTemplateTabName { get; set; }

    public string? GoogleOAuthRefreshTokenEnc { get; set; }

    [MaxLength(256)]
    public string? GoogleOAuthUserEmail { get; set; }

    public DateTime? GoogleOAuthConnectedAt { get; set; }

    public bool SheetSyncEnabled { get; set; } = false;

    // Per-LS override of the AttInput tab name. Default is "AttInput", but
    // each linkshell can rename their tab. The sync service appends rows here
    // rather than overwriting Main!C so the user's existing formula chain
    // (AttInput -> Tally -> Main!F -> Main!C) keeps working unchanged.
    [MaxLength(64)]
    public string? AttInputTabName { get; set; }

    // Fallback Entry Type for snapshots / window posts that have no linked
    // event with an AttInputEntryType set. Examples: "Misc Camp", "Kill".
    [MaxLength(32)]
    public string? AttInputDefaultEntryType { get; set; }

    // Per-LS override of the ManualPoints tab name. Default is "ManualPoints".
    // Each DKP audit appends a new column to this tab; the cell at the
    // member's row gets the audit amount.
    [MaxLength(64)]
    public string? ManualPointsTabName { get; set; }

    // Named Discord channel webhooks. Every `/lsm now` attendance snapshot is
    // posted to each of these as a party-grouped embed. Empty = Discord
    // posting disabled. (Replaces the former single DiscordWebhookUrl column.)
    public ICollection<LinkshellDiscordWebhook> DiscordWebhooks { get; set; } = new List<LinkshellDiscordWebhook>();

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<Event> Events { get; set; } = new List<Event>();

    public ICollection<EventHistory> EventHistories { get; set; } = new List<EventHistory>();
}
