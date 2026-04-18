using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.ViewModels;

public class TodManagerViewModel
{
    public const string TwentyTwoHourCooldown = "22 Hour";
    public const string SeventyTwoHourCooldown = "72 Hour";
    public const string OneHourInterval = "1 Hour";
    public const string TenMinuteInterval = "10 Min";

    public static IReadOnlyList<string> SupportedMonsters { get; } = new[]
    {
        "Fafnir",
        "Nidhogg",
        "Behemoth",
        "King Behemoth",
        "Adamantoise",
        "Aspidochelone",
        "Tiamat",
        "Jormungand",
        "Vrtra",
        "King Arthro",
        "Simurgh"
    };

    public static IReadOnlyList<string> SupportedCooldowns { get; } = new[]
    {
        TwentyTwoHourCooldown,
        SeventyTwoHourCooldown
    };

    public static IReadOnlyList<string> SupportedIntervals { get; } = new[]
    {
        OneHourInterval,
        TenMinuteInterval
    };

    public int LinkshellId { get; set; }

    public List<Linkshell> Linkshells { get; set; } = new();

    public Tod Tod { get; set; } = new();

    public List<TodTableRowViewModel> TodItems { get; set; } = new();

    public List<TodLootDetail> TodLootDetails { get; set; } = new() { new TodLootDetail() };

    public bool NoLoot { get; set; }

    public IList<string> Notifications { get; set; } = new List<string>();

    public List<string> MonsterOptions { get; set; } = SupportedMonsters.ToList();

    public List<string> CooldownOptions { get; set; } = SupportedCooldowns.ToList();

    public List<string> IntervalOptions { get; set; } = SupportedIntervals.ToList();

    public List<string> CharacterNames { get; set; } = new();
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

    public bool Claim { get; init; }

    public int LootCount { get; init; }
}