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

    // Optional dashboard banner image. Stored in a separate LinkshellBanner table
    // (1:1) so the image bytes never load on the hot dashboard/overview paths —
    // only fetched when the banner endpoint serves them. Navigation is NOT
    // eager-loaded anywhere; presence/version is read via a lightweight projection.
    public LinkshellBanner? Banner { get; set; }

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

    // "Fill earlier alliances first" nudge: when a member signs up for a slot in a
    // LATER alliance while an open slot their job can fill is still free in an EARLIER
    // alliance, warn them and offer to take that earlier slot (or sign up anyway).
    // Soft (never blocks) and job-aware (no nudge when nothing earlier fits them).
    // Default ON; automatic no-op on single-alliance boards.
    public bool FillAlliancesInOrder { get; set; } = true;

    // HNM Outside Sign Up: independent of OutsidePartySignupEnabled (HNM works with that
    // OFF). When ON it gates everything HNM — the HNM event type in the Activity create
    // dropdown, HNM manual-create validation, and account-less Discord signups onto HNM
    // boards. HNM signups are ROSTER MEMORY ONLY: a placeholder is still created/adopted
    // so the player isn't re-prompted, but they earn NO DKP and NO active/absent credit
    // because HNM events null End/Duration, zero DkpPerHour, and set CountsTowardActive=false.
    public bool HnmOutsideSignupEnabled { get; set; } = false;

    // Components V2 event boards (experimental): when ON, this linkshell's Discord
    // event sign-up boards are posted as Discord "Components V2" messages — the
    // rendered board PNG sits in a full-width media gallery (no embed chrome) and the
    // card is rendered on a wider internal canvas — instead of the classic image-in-embed.
    // Same buttons, same signup flow. Off by default so existing boards are untouched.
    // NOTE: Discord forbids toggling the V2 flag on edit, so a board keeps whichever
    // mode it was FIRST posted with for its whole lifecycle — flipping this only affects
    // boards posted afterwards.
    public bool UseComponentsV2Boards { get; set; } = false;

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

    // DEPRECATED — the Google Sheets integration was removed 2026-06-24 (replaced by the
    // app-native DKP sheet; see DkpSheetService). These columns are retained ONLY to keep
    // migrations additive (a destructive drop is a separate, riskier change). Nothing reads
    // or writes them anymore — the only references are EF migration snapshots and the
    // DbWipe utility's NULL-out SQL.
    [MaxLength(128)]
    public string? GoogleSpreadsheetId { get; set; }

    public bool GoogleSheetAppCreated { get; set; }

    [MaxLength(64)]
    public string? DkpTemplateTabName { get; set; }

    public bool SheetTemplateSyncEnabled { get; set; } = false;

    public string? GoogleOAuthRefreshTokenEnc { get; set; }

    [MaxLength(256)]
    public string? GoogleOAuthUserEmail { get; set; }

    public DateTime? GoogleOAuthConnectedAt { get; set; }

    // Discord channel the live DKP sheet auto-posts to (full member table embed + the
    // .xlsx attached), refreshed in place whenever DKP changes. Null = don't post to
    // Discord (the feature is off). The name is cached for the picker. The bot posts
    // here directly, so it must be a channel the bot can post in (any channel a route
    // already uses).
    [MaxLength(20)]
    public string? DkpExportDiscordChannelId { get; set; }

    [MaxLength(128)]
    public string? DkpExportDiscordChannelName { get; set; }

    // The Discord message id of the live, edit-in-place DKP sheet post. Null until the
    // first post; set from the create response and used for subsequent edits. Cleared
    // when the message is gone (so it reposts) or the channel changes.
    [MaxLength(32)]
    public string? DkpSheetDiscordMessageId { get; set; }

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
