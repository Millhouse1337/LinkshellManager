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

    // Member activity (Active/Inactive) tracking from event attendance. Opt-in;
    // streak thresholds configurable (defaults 3 absences -> inactive, 2 -> active).
    public bool EnableActivityTracking { get; set; } = false;
    public int InactiveAfterAbsences   { get; set; } = 3;
    public int ActiveAfterAttendances  { get; set; } = 2;

    // Named Discord channel webhooks. Every `/lsm now` snapshot is posted to
    // each as a party-grouped embed. Rows with a blank URL are dropped on
    // save (so clearing a row deletes the webhook). Empty = posting disabled.
    public List<DiscordWebhookInput> DiscordWebhooks { get; set; } = new();

    public bool CanManageRoles { get; set; }

    // True when a super admin has globally disabled the addon. The Game Addon
    // pairing card is hidden when set.
    public bool AddonGloballyDisabled { get; set; }

    // The Discord server this linkshell is associated with (powers roster,
    // invites, channel posting). Set from EligibleGuilds (servers where the
    // caller and the bot are both members) or a manual numeric ID. Setting it
    // does NOT restrict access.
    public string? DiscordGuildId { get; set; }
    public string? DiscordGuildName { get; set; }
    public List<DiscordGuildOption> EligibleGuilds { get; set; } = new();

    // Optional, separate access lock: when true, members can only open this
    // linkshell from DiscordGuildId. Off by default.
    public bool GuildLocked { get; set; }

    // Phase 2 "bot posts to channel" config. The guild whose channels we list
    // (the linkshell's Discord server), the channels the bot can post to in it,
    // and one config row per purpose (HENM/KSNM/Other events, Auctions, Loot).
    public string? DiscordChannelGuildId { get; set; }
    public List<DiscordChannelOption> AvailableChannels { get; set; } = new();
    public List<DiscordChannelConfigRow> DiscordChannels { get; set; } = new();

    public List<Linkshell> ManageableLinkshells { get; set; } = new();

    // Canonical names of monsters hidden from the ToD Tracker (Dashboard +
    // ToDs tab). Posted back as the set of checked boxes; stored
    // pipe-separated on the Linkshell entity (TodController parses it the
    // same way the Discord Activity does).
    public List<string> HiddenTodMonsters { get; set; } = new();

    // Built-in monster catalog for the Hide ToD Mobs picker. Mirrors the
    // Discord Activity's TOD_BUILT_IN_MONSTER_GROUPS (which mirrors
    // att/constants.lua) so both clients hide the same names.
    public static readonly IReadOnlyList<TodMonsterGroup> TodMonsterGroups = new[]
    {
        new TodMonsterGroup("HNMs", new[] { "Adamantoise", "Aspidochelone", "Behemoth", "Fafnir", "Jormungand", "King Behemoth", "Nidhogg", "Tiamat", "Vrtra" }),
        new TodMonsterGroup("Sky NMs", new[] { "Brigandish Blade", "Byakko", "Despot", "Faust", "Genbu", "Kirin", "Mother Globe", "Olla Grande", "Seiryu", "Steam Cleaner", "Suzaku", "Ullikummi", "Zipacna" }),
        new TodMonsterGroup("Sea NMs", new[] { "Absolute Virtue", "Ix'aern (Dark Knight)", "Ix'aern (Dragoon)", "Ix'aern (Monk)", "Jailer of Faith", "Jailer of Fortitude", "Jailer of Hope", "Jailer of Justice", "Jailer of Love", "Jailer of Prudence", "Jailer of Temperance" }),
        new TodMonsterGroup("HENMs", new[] { "Mammet-9999", "Overlord Arthro", "Ruinous Rocs", "Sacred Scorpions", "Tonberry Sovereign", "Ultimega" }),
        new TodMonsterGroup("Other NMs", new[] { "Bloodsucker", "King Arthro", "King Vinegarroon", "Serket", "Shikigami Weapon", "Simurgh", "Xolotl" }),
    };
}

// One selectable Discord server in the Customize page's server-lock pick-list:
// a server where both the caller and the LSM bot are members.
public sealed record DiscordGuildOption(string Id, string Name);

// One selectable Discord channel (id + name) the bot can post to.
public sealed record DiscordChannelOption(string Id, string Name);

// One purpose's channel binding on the Customize page (e.g. "HENM events" ->
// channel 123). ChannelId is empty when nothing is configured for the purpose.
public sealed class DiscordChannelConfigRow
{
    public string Purpose { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public string? ChannelName { get; set; }
}

public class TodMonsterGroup
{
    public string Label { get; }
    public IReadOnlyList<string> Names { get; }

    public TodMonsterGroup(string label, IReadOnlyList<string> names)
    {
        Label = label;
        Names = names;
    }
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
