using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;

namespace LinkshellManagerDiscordApp.ViewModels;

public class TodManagerViewModel
{
    // Sentinel option that reveals a free-text field so officers can log a
    // monster that isn't in the curated list.
    public const string OtherMonster = "Other";

    public int LinkshellId { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    public Tod Tod { get; set; } = new();

    // Free-text monster name used when "Other" is picked in the dropdown.
    // Resolved into Tod.MonsterName server-side before validation/save.
    public string? CustomMonsterName { get; set; }

    public List<TodTableRowViewModel> TodItems { get; set; } = new();

    public List<TodLootDetail> TodLootDetails { get; set; } = new() { new TodLootDetail() };

    public bool NoLoot { get; set; }

    public IList<string> Notifications { get; set; } = new List<string>();

    public List<string> MonsterOptions { get; set; } = TodCatalog.SupportedMonsters.ToList();

    // Per-monster cooldown / cadence for the selected linkshell, keyed by the exact MonsterOptions
    // text. Serialized onto the form as a data- attribute so picking a monster pre-fills its
    // durations without a round trip — the same job the Activity does from the overview payload.
    public Dictionary<string, TodMonsterTimingHint> MonsterTimings { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Cooldown and Interval are entered as a number + a unit now rather than picked from a fixed
    // list, because each monster carries its own configured value. Composed into Tod.Cooldown /
    // Tod.Interval server-side before validation runs.
    public double? CooldownValue { get; set; }
    public string? CooldownUnit { get; set; }
    public double? IntervalValue { get; set; }
    public string? IntervalUnit { get; set; }

    // The Log ToD form's "Additional seconds" — the fine offset added on top of the cooldown when
    // the repop is computed. Bound here rather than straight onto Tod.AdditionalSeconds because
    // that one is a non-nullable int: an officer who clears the box would otherwise post an empty
    // string at it and get a binding error instead of "no offset". Folded into the Tod in
    // ApplyPostedDurationsAsync.
    public int? AdditionalSeconds { get; set; }

    public List<string> CharacterNames { get; set; } = new();

    // Drives the submit-button label on Create.cshtml. When false, the user has
    // only CanSubmitTodForApproval (not CanManageTods) so the button reads
    // "Submit for Approval" and a hint is shown explaining the review step.
    public bool CanCreateImmediately { get; set; } = true;

    // Monster name -> assigned Party Setup, so the ToD Tracker can surface a
    // link to the planned composition on each monster's row. Case-insensitive
    // keys; most-recently-updated setup wins when several target one monster.
    public Dictionary<string, (int Id, string Name)> AssignedPartySetups { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Monster name -> the full assigned Party Setup tree, rendered inline
    // (expand in place, no page nav) under the monster's ToD row so members
    // can sign up for a slot. Same keying/precedence as AssignedPartySetups.
    public Dictionary<string, PartySetupBoardViewModel> AssignedPartySetupBoards { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Current viewer, so the inline panel can show "you" + a Withdraw button
    // on the slot they hold. CanManagePartiesForSelected lets officers clear
    // anyone's sign-up.
    public string? CurrentAppUserId { get; set; }
    public bool CanManagePartiesForSelected { get; set; }

    // Role/main/sub option lists for the inline sign-up form. A slot that
    // leaves any of Role/MainJob/SubJob open lets the member fill it in
    // at sign-up time; the dropdowns pull from these (shared across every
    // board on the page, so one copy on the page model is enough).
    public List<string> SignUpRoleOptions { get; set; } = LinkshellManagerDiscordApp.Utils.EventJobCatalog.JobTypeOptions.ToList();
    public List<string> SignUpMainJobOptions { get; set; } = LinkshellManagerDiscordApp.Utils.EventJobCatalog.MainJobOptions.ToList();
    public List<string> SignUpSubJobOptions { get; set; } = LinkshellManagerDiscordApp.Utils.EventJobCatalog.SubJobOptions.ToList();

    // Recent claim-shield lottery windows posted from the lsm addon, newest
    // first. Shown in a card below the Submitted ToDs table.
    public List<ClaimShieldCaptureRowViewModel> RecentClaimShieldCaptures { get; set; } = new();
}

// The assigned Party Setup for one monster, with its full alliance/party/slot
// tree, surfaced inline on the ToD Tracker page.
public class PartySetupBoardViewModel
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public List<PartySetupAllianceView> Alliances { get; init; } = new();
}

public class ClaimShieldCaptureRowViewModel
{
    public int Id { get; init; }

    public string MonsterName { get; init; } = string.Empty;

    public bool Won { get; init; }

    public int TotalPlayers { get; init; }

    public string CapturedAtDisplay { get; init; } = string.Empty;

    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();

    public int MatchedCount { get; init; }
}

public class TodTableRowViewModel
{
    public int Id { get; init; }

    public string MonsterName { get; init; } = string.Empty;

    public int? DayNumber { get; init; }

    public string TodDisplay { get; init; } = string.Empty;

    public string Cooldown { get; init; } = string.Empty;

    public string RepopTimeDisplay { get; init; } = string.Empty;

    public string Interval { get; init; } = string.Empty;

    public DateTime? RepopTimeUtc { get; init; }

    public bool? Claim { get; init; }

    public int LootCount { get; init; }

    public string? ImagePath { get; init; }
}
// One monster's configured durations, in canonical minutes, for the ToD form's pre-fill. Cadence is
// null when the monster has no spawn grid and no configured check interval.
//
// HasHqVariant / HasSpawnGrid answer the two questions the form only asks of SOME monsters -- the
// Day box and HQ toggle (the three NQ/HQ families) and "Popped on window" (a monster with a spawn
// grid). They ride along here so the form can show and hide those fields the moment the picker
// changes, which is what the Activity's Log ToD form does off its monster-setups payload.
public sealed record TodMonsterTimingHint(
    int CooldownMinutes,
    int? CadenceMinutes,
    bool HasHqVariant,
    bool HasSpawnGrid,
    // Seconds on top of the cooldown, so the page's repop preview lands where the server will.
    int AdditionalSeconds = 0);
