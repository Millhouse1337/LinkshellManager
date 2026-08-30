using System.ComponentModel.DataAnnotations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;

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
    // Cache-busted banner image URL for the current linkshell, or null when none.
    public string? BannerUrl { get; set; }

    [Required, MaxLength(32)]
    public string? LootStructure { get; set; } = "Dkp";

    [Required, MaxLength(16)]
    public string? DkpRoundingIncrement { get; set; } = "Quarter";

    // Palette for this linkshell's event signup board image (one of the
    // EventBoardThemes keys). Rendered as swatches on the Customize page.
    [MaxLength(16)]
    public string? EventBoardTheme { get; set; } = "Crystal";

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

    // Allow Discord-server members with no LSM account to sign up (or Check In) from a
    // board, for EVERY event type including HNM. Backed by a placeholder member, so they
    // DO earn DKP + tracked.
    public bool OutsidePartySignupEnabled { get; set; } = false;

    // Experimental: post Discord event boards as Components V2 (wide media-gallery card)
    // instead of the classic image-in-embed. Off by default; only affects boards posted
    // after it's turned on.
    public bool UseComponentsV2Boards { get; set; } = false;

    public bool CanManageRoles { get; set; }

    // True when a super admin has globally disabled the addon. The Game Addon
    // pairing card is hidden when set.
    public bool AddonGloballyDisabled { get; set; }

    // Server-wide Claim Shield switch. The per-monster switches on this page are inert while it is
    // on, so the card greys them out and says why instead of showing ticks nothing honours.
    public bool ClaimShieldGloballyDisabled { get; set; }

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

    // Discord channel-routes config. The guild whose channels we list (the
    // linkshell's Discord server), the channels the bot can post to in it, and
    // the user-defined routes (each = a channel + the post types it receives).
    public string? DiscordChannelGuildId { get; set; }
    public List<DiscordChannelOption> AvailableChannels { get; set; } = new();
    public List<ChannelRouteInput> ChannelRoutes { get; set; } = new();

    // ---- DKP pools ----
    //
    // The officer's pools, plus one assignment row per assignable event type. The assignments bind
    // by POOL INDEX rather than pool id, so a new pool (which has no id yet) can be created and have
    // event types moved into it in the same save.
    public List<DkpPoolInput> DkpPools { get; set; } = new();
    public List<DkpPoolAssignmentInput> DkpPoolAssignments { get; set; } = new();

    // Optional channel where post-event discussion comments are mirrored.
    public string? DiscussionChannelId { get; set; }

    public List<Linkshell> ManageableLinkshells { get; set; } = new();

    // Canonical names of monsters hidden from the ToD Tracker (Dashboard +
    // ToDs tab). Posted back as the set of checked boxes; stored
    // pipe-separated on the Linkshell entity (TodController parses it the
    // same way the Discord Activity does).
    public List<string> HiddenTodMonsters { get; set; } = new();

    // The linkshell's per-monster setups for the Monster Setups card. Populated by
    // LinkshellController.LoadMonsterTimingInputsAsync, which also seeds the catalog on first view.
    public List<MonsterTimingInput> MonsterTimings { get; set; } = new();
    public List<string> MonsterTimingCategories { get; set; } = new();
    public int MonsterTimingMaxWindows { get; set; } = 25;

    // Built-in monster catalog for the Hide ToD Mobs picker. Mirrors the
    // Discord Activity's TOD_BUILT_IN_MONSTER_GROUPS so both clients list the
    // same names. Timed open-world spawns only — the pop-only mobs (Sky Gods,
    // Sea NMs, HENMs; see HnmConfig.PopOnlyNms) have no repop window to track,
    // so there is nothing about them to hide from the Tracked Windows panel.
    public static readonly IReadOnlyList<TodMonsterGroup> TodMonsterGroups = new[]
    {
        new TodMonsterGroup("HNMs", new[] { "Adamantoise", "Aspidochelone", "Behemoth", "Cerberus", "Fafnir", "Hydra", "Jormungand", "Khimaira", "King Behemoth", "Nidhogg", "Tiamat", "Vrtra" }),
        // (A "Sky NMs" group listed the eight farm NMs here. They are no longer part of the seeded
        // monster catalog or the ToD picker, so there is nothing left to hide from the tracker.)
        new TodMonsterGroup("Other NMs", new[] { "Bloodsucker", "Boroka", "Bune", "Capricious Cassie", "King Arthro", "King Vinegarroon", "Roc", "Serket", "Shikigami Weapon", "Simurgh", "Xolotl" }),
    };
}

// One selectable Discord server in the Customize page's server-lock pick-list:
// a server where both the caller and the LSM bot are members.
public sealed record DiscordGuildOption(string Id, string Name);

// One selectable Discord channel (id + name) the bot can post to.
public sealed record DiscordChannelOption(string Id, string Name);

// One Discord channel route on the Customize page: a channel + which post types
// the bot posts there. Id is 0 for a new (unsaved) route. EventTypeFilter is the
// list of event types an event route handles (empty = catch-all).
public sealed class ChannelRouteInput
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    [MaxLength(20)]
    public string? ChannelId { get; set; }

    public bool PostEvents { get; set; }
    public bool PostLoot { get; set; }
    public bool PostAuctions { get; set; }
    public bool PostAttendance { get; set; }
    public bool PostTodBoard { get; set; }
    public bool PostDkpSheet { get; set; }

    public List<string> EventTypeFilter { get; set; } = new();
    // Per-monster narrowing for an HNM route (only used when EventTypeFilter includes HNM).
    public List<string> HnmMonsterFilter { get; set; } = new();
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


// One DKP pool row on the Customize page's "DKP pools" card. Id is 0 for a pool the officer just
// added — DkpPoolEditor treats that as "create".
public sealed class DkpPoolInput
{
    public int Id { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    public bool IsDefault { get; set; }

    [MaxLength(16)]
    public string? Accent { get; set; }
}

// One event type and the pool it's assigned to, bound by the pool's INDEX in the DkpPools list (not
// its id — new pools don't have one yet). This shape is what makes the partition
// unrepresentable-if-invalid: an event type has exactly one <select>, so it cannot end up in two
// pools no matter what the officer does.
public sealed class DkpPoolAssignmentInput
{
    [MaxLength(256)]
    public string EventType { get; set; } = string.Empty;

    // -1 (or out of range) = unassigned, which means it falls through to the default pool.
    public int PoolIndex { get; set; } = -1;

    // Display-only, repopulated server-side on every render.
    public double EarnedTotal { get; set; }
    public bool IsCustom { get; set; }
}

// One monster's setup row on the web Customize page's Monster Setups card. Id is 0 for a row the
// officer just added. Durations post as the number + the unit they typed; MonsterTimingEditor
// normalizes to canonical minutes, the same as the Activity's input DTO.
public class MonsterTimingInput
{
    public int Id { get; set; }

    [MaxLength(128)]
    public string? MonsterName { get; set; }

    // Blank = this monster runs no spawn-window cycle, which is a real answer and not the same as 1.
    public int? Windows { get; set; }

    public double? CadenceValue { get; set; }
    public string? CadenceUnit { get; set; }

    public double? CooldownValue { get; set; }
    public string? CooldownUnit { get; set; }

    [MaxLength(32)]
    public string? Category { get; set; }

    // Built-ins are RESET rather than removed, so the view only offers a delete on a custom row.
    public bool IsCustom { get; set; }

    // Whether the addon records claim-shield lotteries for this monster. Posts as a checkbox, so
    // an unchecked box sends nothing and the model binder leaves this false — which is exactly the
    // semantics wanted here, unlike the Activity's nullable input.
    public bool ClaimShieldEnabled { get; set; }

    public static MonsterTimingInput From(LinkshellMonsterTiming row)
    {
        var (cooldownValue, cooldownUnit) = TodDurationFormat.Split(row.CooldownMinutes);
        var cadence = row.WindowCadenceMinutes is > 0
            ? TodDurationFormat.Split(row.WindowCadenceMinutes.Value)
            : ((int Value, string Unit)?)null;

        return new MonsterTimingInput
        {
            Id = row.Id,
            MonsterName = row.MonsterName,
            Windows = row.WindowCount,
            CadenceValue = cadence?.Value,
            CadenceUnit = cadence?.Unit ?? TodDurationFormat.MinutesUnit,
            CooldownValue = cooldownValue,
            CooldownUnit = cooldownUnit,
            Category = MonsterTimingDefaults.NormalizeCategory(row.Category),
            IsCustom = row.IsCustom,
            ClaimShieldEnabled = row.ClaimShieldEnabled,
        };
    }
}
