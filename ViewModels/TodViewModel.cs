using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class TodManagerViewModel
{
    public const string FiveMinuteCooldown = "5 Min";
    public const string TwoHourCooldown = "2 Hour";
    public const string TwentyTwoHourCooldown = "22 Hour";
    public const string SeventyOneHourCooldown = "71 Hour";
    public const string SeventyTwoHourCooldown = "72 Hour";
    public const string EightyFourHourCooldown = "84 Hour";
    public const string OneHourInterval = "1 Hour";
    public const string TenMinuteInterval = "10 Min";

    // Sentinel option that reveals a free-text field so officers can log a
    // monster that isn't in the curated list.
    public const string OtherMonster = "Other";

    // The BUILT-IN monster catalog: the twelve HNMs, and nothing else.
    //
    // Every non-HNM is a CUSTOM monster now. This list used to carry eleven ground NMs too -- eight
    // (Boroka, Bune, Capricious Cassie, Roc, Serket, Shikigami Weapon, Simurgh, Xolotl) left first
    // because nobody camped them, then Bloodsucker, King Arthro and King Vinegarroon followed:
    // WHICH NMs a linkshell camps is that linkshell's business, and a hardcoded three was a guess
    // every other linkshell had to delete around. So the editor's "Other NMs" heading starts EMPTY
    // and is filled with "+ Add monster", which produces a first-class campable monster rather than
    // a timings-only row (MonsterTimingMap.EventMonsterOptions / .Allows).
    //
    // Keeping the twelve HNMs built in is the deliberate other half of that. Their spawn grids are
    // what the board, the window auto-advance and the addon all reason about, and those cadences
    // are game facts rather than a linkshell's preference.
    //
    // Read this directly only where "the built-ins" is genuinely what you mean -- seeding, and the
    // defaults lookup. Everything user-facing goes through the linkshell's own catalog
    // (MonsterTimingMap), which is this list plus that linkshell's custom monsters.
    public static IReadOnlyList<string> SupportedMonsters { get; } = new[]
    {
        "Adamantoise",
        "Aspidochelone",
        "Behemoth",
        "Cerberus",
        "Fafnir",
        "Hydra",
        "Jormungand",
        "Khimaira",
        "King Behemoth",
        "Nidhogg",
        "Tiamat",
        "Vrtra"
    };

    public static IReadOnlyList<string> SupportedCooldowns { get; } = new[]
    {
        FiveMinuteCooldown,
        TwoHourCooldown,
        TwentyTwoHourCooldown,
        SeventyOneHourCooldown,
        SeventyTwoHourCooldown,
        EightyFourHourCooldown
    };

    public static IReadOnlyList<string> SupportedIntervals { get; } = new[]
    {
        OneHourInterval,
        TenMinuteInterval
    };

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

    public List<string> MonsterOptions { get; set; } = SupportedMonsters.ToList();

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
public sealed record TodMonsterTimingHint(int CooldownMinutes, int? CadenceMinutes);
