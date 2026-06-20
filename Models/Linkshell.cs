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

    // Colour palette used when this linkshell's event signup board is rendered
    // to a PNG for Discord (see EventBoardThemes / EventBoardHtmlBuilder). One of
    // the EventBoardThemes keys (Crystal, Abyss, Ember, Verdant, Royal, Tome);
    // an unknown value falls back to the default at render time. Edited from the
    // Customize panel + the Discord Activity Configurations card.
    [MaxLength(16)]
    public string EventBoardTheme { get; set; } = "Crystal";

    public bool EnableDkp { get; set; } = true;

    public bool EnableItems { get; set; } = true;

    public bool EnableRevenue { get; set; } = true;

    // Member activity (Active/Inactive) tracking, derived from event attendance.
    // Opt-in per linkshell (off by default — not every linkshell uses it). When on,
    // a computed Active/Inactive badge shows on the roster. The rule is a streak
    // hysteresis over each member's "counting" events:
    //   * InactiveAfterAbsences consecutive absences  -> Inactive (default 3)
    //   * ActiveAfterAttendances consecutive credited attendances -> Active (default 2)
    public bool EnableActivityTracking { get; set; } = false;
    public int InactiveAfterAbsences { get; set; } = 3;
    public int ActiveAfterAttendances { get; set; } = 2;

    // Outside Party Signup: when ON, Discord-server members who have NEVER linked an
    // LSM account can still sign up for NON-HNM events from the party board. Their first
    // signup creates/adopts a placeholder member (AppUser.IsPlaceholder) keyed to their
    // Discord id, so they aren't re-prompted for their name. That placeholder has a real
    // AppUserId, so — like any member — it DOES earn DKP and IS activity-tracked (there is
    // no IsPlaceholder filter in the DKP/activity paths). Off by default; account users'
    // behavior is unaffected either way. (For HNM use HnmOutsideSignupEnabled instead.)
    public bool OutsidePartySignupEnabled { get; set; } = false;

    // HNM Outside Sign Up: independent of OutsidePartySignupEnabled (HNM works with that
    // OFF). When ON it gates everything HNM — the HNM event type in the Activity create
    // dropdown, HNM manual-create validation, and account-less Discord signups onto HNM
    // boards. HNM signups are ROSTER MEMORY ONLY: a placeholder is still created/adopted
    // so the player isn't re-prompted, but they earn NO DKP and NO active/absent credit
    // because HNM events null End/Duration, zero DkpPerHour, and set CountsTowardActive=false.
    public bool HnmOutsideSignupEnabled { get; set; } = false;

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

    // Optional Discord channel where post-event discussion comments are mirrored.
    // Null = comments stay in-app only.
    [MaxLength(20)]
    public string? DiscussionChannelId { get; set; }

    // OPTIONAL access lock, separate from setting the server above. When false
    // (default), the linkshell is merely *associated* with DiscordGuildId for
    // roster/invite/channel features — anyone can still view it from anywhere.
    // When true, viewing access is restricted to that server (the Activity can
    // only open it when launched from DiscordGuildId, and overview membership is
    // verified). Setting a server never implies locking.
    public bool LockToDiscordGuild { get; set; } = false;

    [MaxLength(128)]
    public string? GoogleSpreadsheetId { get; set; }

    // True when GoogleSpreadsheetId points at a sheet LSManager CREATED itself
    // (the dedicated "LSM DKP" sheet) rather than an externally-pasted id from
    // the old broad-scope flow. Under the drive.file scope the app can only
    // access sheets it created, so this gates the connected-sheet UI and lets us
    // nudge legacy (pasted-id) linkshells to create a dedicated sheet.
    public bool GoogleSheetAppCreated { get; set; }

    // Tab that the generic DKP template export writes to and the template
    // import reads from (the canonical 6-column Member/Alts/Current/Total/Spent
    // layout). Default "LSM DKP". A linkshell can point this at a tab of their
    // own sheet that they've reformatted to match the template.
    [MaxLength(64)]
    public string? DkpTemplateTabName { get; set; }

    // Live sync (push-only): when true, the "LSM DKP" template tab is
    // automatically re-exported whenever a member's DKP changes (event close,
    // auction, loot, audits, window events). Off by default; requires a
    // connected Google account + spreadsheet id to actually push. Import
    // (sheet → app) stays manual regardless of this flag.
    public bool SheetTemplateSyncEnabled { get; set; } = false;

    public string? GoogleOAuthRefreshTokenEnc { get; set; }

    [MaxLength(256)]
    public string? GoogleOAuthUserEmail { get; set; }

    public DateTime? GoogleOAuthConnectedAt { get; set; }

    // Named Discord channel webhooks. Every `/lsm now` attendance snapshot is
    // posted to each of these as a party-grouped embed. Empty = Discord
    // posting disabled. (Replaces the former single DiscordWebhookUrl column.)
    public ICollection<LinkshellDiscordWebhook> DiscordWebhooks { get; set; } = new List<LinkshellDiscordWebhook>();

    // User-defined Discord channel routes: which channels the bot posts each kind
    // of content to (events/loot/auctions/attendance/ToD). Replaces the fixed
    // LinkshellDiscordChannel purposes and the webhook post-type flags.
    public ICollection<LinkshellChannelRoute> ChannelRoutes { get; set; } = new List<LinkshellChannelRoute>();

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<Event> Events { get; set; } = new List<Event>();

    public ICollection<EventHistory> EventHistories { get; set; } = new List<EventHistory>();
}
