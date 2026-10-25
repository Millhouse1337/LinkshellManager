namespace LinkshellManagerDiscordApp.Services;

// The built-in ToD option lists. Moved off the old website's TodManagerViewModel so they outlive it.
public static class TodCatalog
{
    public const string FiveMinuteCooldown = "5 Min";
    public const string TwoHourCooldown = "2 Hour";
    public const string TwentyTwoHourCooldown = "22 Hour";
    public const string SeventyOneHourCooldown = "71 Hour";
    public const string SeventyTwoHourCooldown = "72 Hour";
    public const string EightyFourHourCooldown = "84 Hour";
    public const string OneHourInterval = "1 Hour";
    public const string TenMinuteInterval = "10 Min";

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
}
