namespace LinkshellManagerDiscordApp.Services;

// Single source of truth for HNM-event window counts and labels.
// Tiamat / Jormungand / Vrtra spawn over a long pop window split into 25 hourly attendance slots;
// the ToAU three (Cerberus / Hydra / Khimaira) cover that same 24-hour band in 5 six-hour slots.
// Fafnir / Nidhogg / Behemoth / King Behemoth / Adamantoise / Aspidochelone use 2 slots
// ("Open" + "Close"), as do the timed NMs that share their 7 × 10-min spawn band
// (Capricious Cassie / Bune / Boroka / Roc). Everything else is a single-window event.
public static class HnmConfig
{
    public static readonly HashSet<string> LongWindowHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tiamat",
        "Jormungand",
        "Vrtra",
        // The ToAU HNMs. In this set for its SHAPE — a long pop band split into windows, with the
        // camp re-forming at every boundary — and NOT for its numbers. They run their own
        // 5 × 6-hour band and their own repop; see ToauHnms, which every lookup that resolves a
        // number tests FIRST, precisely because these three are a subset of this set.
        "Cerberus",
        "Hydra",
        "Khimaira"
    };

    // The ToAU three, split out of the set above because every number they carry differs from the
    // wyrms': the repop, the window count and the cadence. This file (DefaultWindowCadence,
    // GetWindowCount) and MonsterTimingDefaults.DefaultCooldownMinutes all read it, and all three
    // must test it BEFORE LongWindowHnms — these names are in both sets, so testing the wider one
    // first silently hands them the wyrms' band.
    //
    // Their cooldown is 48h — the moment the window OPENS. The 5 × 6-hour band then runs it out
    // to 72h, and 72 is what these were seeded as before: the window's CLOSE stored where its open
    // belongs, so a board set to re-post BEFORE the pop only came back after the whole window had
    // already passed. Every other cooldown in this file is a window open (the wyrms' 84h opens a
    // band that closes at 108h), so these three now agree with the rest.
    public static readonly HashSet<string> ToauHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cerberus",
        "Hydra",
        "Khimaira"
    };

    // The ToAU band: FIVE windows six hours apart. It spans exactly the same 24 hours the wyrms'
    // 25 × 60-min band does — window 1 at the pop (ToD + 48h), window 5 a full day later at
    // ToD + 72h — so when one of these camps runs out has not moved; only how coarsely the band is
    // bucketed has. A camp that used to take twenty-five hourly roster reads now takes five.
    //
    // Named rather than written as literals in DefaultWindowCadence because the migration that
    // rewrites already-seeded LinkshellMonsterTiming rows names the same two numbers, and a test
    // holds the two side by side.
    public const int ToauWindowCount = 5;
    public const int ToauWindowCadenceMinutes = 6 * 60;

    // The window-cycle HNMs above pop across successive windows, and their signup board shows
    // "Window N of M" against a counter (Event.HnmWindowNumber) the cadence advances.
    //
    // This is the CEILING on M, not the length of any one band: the longest band in the file is the
    // wyrms' 25, and Discord caps a select menu at 25 options, so nothing may exceed it. The band a
    // given camp actually runs comes from DefaultWindowCadence (or the linkshell's own setup) —
    // 5 for the ToAU three, 7 for the kings/dragons — and is what stops that camp's counter.
    public const int MaxWindow = 25;

    public static bool SupportsWindowAdvance(string? monsterName) =>
        MonsterSegments(monsterName).Any(LongWindowHnms.Contains);

    // Whether stepping to the next window CLEARS the camp's roster.
    //
    // The LongWindowHnms only — the wyrms' 25 hourly windows and the ToAU three's 5 six-hour ones.
    // A window on either band is long enough to be its own sitting, so the camp re-signs for each
    // one (hence "🔒 Stay Next Window" to pin a slot through it). The
    // 7-window kings/dragons — Fafnir, Behemoth, Adamantoise and their HQ halves — march through
    // ONE camp at 10-minute steps, so wiping there threw away a roster nobody meant to clear.
    //
    // The MONSTER decides this outright — attendance mode does not enter into it. A Manual Check In
    // wyrm re-forms its camp every hour exactly like a Standard one, so it wipes too; what differs
    // there is only that the check-in ledger (AppUserEvent.WdArrivalWindow) survives the wipe, which
    // is EventPartySignupService.ClearWindowRosterAsync's business, not this predicate's.
    public static bool WindowAdvanceWipesRoster(string? monsterName) =>
        MonsterSegments(monsterName).Any(LongWindowHnms.Contains);

    // ZERO: a wyrm board wipes the moment its window changes. The clear rides the very same
    // HnmWindowAdvanceBackgroundService tick that moves the counter, so "Window N" and an empty
    // roster always appear together — the board is never seen showing a new window number against
    // the previous window's signups.
    //
    // Kept as a named constant rather than inlined because the stale-boundary guard in that service
    // still reasons about "boundary + grace", and because a delay here is the one knob that would
    // reintroduce a gap. Raising it above zero means accepting that gap, bounded below by the
    // service's PollInterval.
    public static readonly TimeSpan WindowClearGrace = TimeSpan.Zero;

    // (ManualNextShouldStep lived here: it decided whether an officer's "Next Window" press stepped
    // the counter or merely settled a turnover the cadence had already started. There is no manual
    // step any more — the cadence owns the counter outright — so the question no longer arises.)

    // A stored monster name may be a COMBINED "Base/Stronger" label (e.g.
    // "Adamantoise/Aspidochelone") chosen from the merged create-event dropdown. Split it
    // into its individual monster names so every name lookup below (zone, window count,
    // true-HNM, routing, recurrence) matches on either half. Single names split to one.
    public static string[] MonsterSegments(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? Array.Empty<string>()
            : name.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Every spelling of a monster that refers to the SAME spawn, for matching stored names
    // against each other: the name itself, each half of a combined "Base/Stronger" label,
    // and — when either half belongs to a merge pair — the pair's other half plus the
    // combined label.
    //
    // MonsterSegments alone is not enough here. A ToD logged from an HNM board records the
    // board's AssignedMonsterName verbatim, which on day 4+ is the COMBINED "Fafnir/Nidhogg";
    // a recurring board keyed on that same combined name has segments ["Fafnir","Nidhogg"],
    // so a segments-only comparison never matched the very ToD that board just produced and
    // the board silently stopped re-posting. Matching on this set is symmetric: a board on
    // "Fafnir", "Nidhogg" or "Fafnir/Nidhogg" finds a ToD logged under any of the three.
    public static IReadOnlyCollection<string> MonsterMatchNames(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Array.Empty<string>();
        }

        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name.Trim() };
        foreach (var segment in MonsterSegments(name))
        {
            matches.Add(segment);
            foreach (var (baseName, stronger) in MonsterMergePairs)
            {
                if (segment.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || segment.Equals(stronger, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(baseName);
                    matches.Add(stronger);
                    matches.Add($"{baseName}/{stronger}");
                }
            }
        }
        return matches;
    }

    // Lower-cased MonsterMatchNames, ready to hand to a `names.Contains(x.ToLower())`
    // EF predicate (the shape every call site here uses).
    public static List<string> MonsterMatchNamesLower(string? name) =>
        MonsterMatchNames(name).Select(n => n.ToLowerInvariant()).ToList();

    // Base monster -> stronger counterpart. On the create-event monster dropdown the two
    // are offered as ONE merged entry: it shows the base name on early days and the combined
    // "Base/Stronger" name from CombinedFromDay onward (the day the stronger version can also
    // pop). The chosen text is stored verbatim in Event.AssignedMonsterName.
    public static readonly IReadOnlyList<(string Base, string Stronger)> MonsterMergePairs = new[]
    {
        ("Adamantoise", "Aspidochelone"),
        ("Behemoth", "King Behemoth"),
        ("Fafnir", "Nidhogg"),
    };

    // The name a KILL/CLAIM should be COUNTED under. Either half of a merge pair — and the
    // combined "Base/Stronger" label itself — collapse to the one combined entry, so a camp
    // logged as "Behemoth" (a manual ToD, or a board on a day below CombinedFromDay) and one
    // logged as "Behemoth/King Behemoth" (the stored AssignedMonsterName an HNM board writes)
    // land in the SAME bucket instead of splitting one monster's claims across two slices.
    // Every other monster groups under its own trimmed name.
    public static string ClaimGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var segments = MonsterSegments(name);
        foreach (var (baseName, stronger) in MonsterMergePairs)
        {
            if (segments.Any(segment =>
                    segment.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || segment.Equals(stronger, StringComparison.OrdinalIgnoreCase)))
            {
                return $"{baseName}/{stronger}";
            }
        }
        return name.Trim();
    }

    // The half of a merge pair a claim should be COUNTED under once the ToD's HQ answer is taken
    // into account — "Fafnir" or "Nidhogg" rather than the combined "Fafnir/Nidhogg" — so the
    // Claims donut can chart the NQ and the HQ as separate monsters. Everything else comes back
    // unchanged with HasHqVariant false: NQ/HQ is not a question those monsters have.
    //
    // The stored NAME decides on its own when it names one half AND ONLY that half: a ToD recorded
    // as "Nidhogg" is an HQ kill whether or not anyone touched the toggle, and that is the only way
    // rows written before the toggle existed can be read at all. Otherwise the toggle decides,
    // which is the case that matters for a board-logged ToD — its AssignedMonsterName is the
    // combined label whichever half actually popped, so the name cannot answer.
    public static (string Name, bool IsHq, bool HasHqVariant) ResolveClaimHalf(string? monsterName, bool hq)
    {
        var segments = MonsterSegments(monsterName);
        foreach (var (baseName, stronger) in MonsterMergePairs)
        {
            var namesBase = segments.Any(segment => segment.Equals(baseName, StringComparison.OrdinalIgnoreCase));
            var namesStronger = segments.Any(segment => segment.Equals(stronger, StringComparison.OrdinalIgnoreCase));
            if (!namesBase && !namesStronger)
            {
                continue;
            }

            var isHq = hq || (namesStronger && !namesBase);
            return (isHq ? stronger : baseName, isHq, true);
        }

        return (monsterName?.Trim() ?? string.Empty, false, false);
    }

    // From this day number onward a merged entry shows the combined "Base/Stronger" label;
    // below it, only the base name.
    public const int CombinedFromDay = 4;

    // The stronger halves, folded into their base entry on the create dropdown (so they no
    // longer appear as standalone options there). They remain first-class monsters elsewhere
    // (ToD tracker, channel routes, party setups).
    public static readonly HashSet<string> MergedStrongerMonsters =
        new(MonsterMergePairs.Select(pair => pair.Stronger), StringComparer.OrdinalIgnoreCase);

    // Both halves of every merge pair (base + stronger), i.e. every monster that has an NQ/HQ
    // distinction: Behemoth/King Behemoth, Adamantoise/Aspidochelone, Fafnir/Nidhogg.
    private static readonly HashSet<string> HqVariantMonsters = new(
        MonsterMergePairs.SelectMany(pair => new[] { pair.Base, pair.Stronger }),
        StringComparer.OrdinalIgnoreCase);

    // True when the monster is one of the three NQ/HQ families — so the End Camp / ToD form can
    // conditionally ask "was it NQ or HQ?". Tolerant of a combined "Base/Stronger" label.
    public static bool HasHqVariant(string? monster) =>
        MonsterSegments(monster).Any(HqVariantMonsters.Contains);

    // Which TIER a monster belongs to on the create-event form: the HNMs (the six
    // long-window monsters plus the three NQ/HQ families) against everything else, which is
    // the NMs. This is the split the in-game addon's preset list has always drawn —
    // "HNMS (9)" over "NMS (11)" — and the form's HNM / NM buttons are the same cut.
    //
    // Deliberately NOT ShortWindowHnms membership, which is the neighbouring and wrong
    // answer: the timed NMs (Capricious Cassie, Bune, Boroka, Roc) live in that set because
    // they run the kings' 7 x 10-min spawn band, and they are still NMs. A monster's spawn
    // cadence and its tier are different questions.
    //
    // Tolerant of a combined "Base/Stronger" label, since that is the form the dropdown
    // offers and the form Event.AssignedMonsterName stores.
    public static bool IsHnmTierMonster(string? monster) =>
        MonsterSegments(monster).Any(seg =>
            LongWindowHnms.Contains(seg) || HqVariantMonsters.Contains(seg));

    // The create-event monster dropdown options: each merge pair shown as ONE combined
    // "Base/Stronger" entry (always, regardless of day), its stronger half dropped as a
    // standalone, and every other monster left intact and in order.
    public static List<string> CombinedMonsterOptions(IEnumerable<string> supported)
    {
        var pairByBase = MonsterMergePairs.ToDictionary(pair => pair.Base, StringComparer.OrdinalIgnoreCase);
        var options = new List<string>();
        foreach (var monster in supported)
        {
            if (MergedStrongerMonsters.Contains(monster)) continue; // folded into the base entry
            options.Add(pairByBase.TryGetValue(monster, out var pair) ? $"{pair.Base}/{pair.Stronger}" : monster);
        }
        return options;
    }

    // The board-DISPLAY form of a (possibly combined "Base/Stronger") monster name for a
    // given day. Below CombinedFromDay only the base half is shown — early days, only the
    // weaker version pops; at/above it, and when no day is set, the full combined name shows
    // as stored. The STORED Event.AssignedMonsterName is unchanged (always the combined form);
    // this only affects what the sign-up board prints.
    public static string? DisplayMonsterName(string? name, int? day)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        if (day is { } d && d < CombinedFromDay)
        {
            var segments = MonsterSegments(name);
            if (segments.Length > 1) return segments[0]; // base half only
        }
        return name;
    }

    // The day number the NEXT sign-up board should carry, given the pop that just happened.
    //
    //   HQ killed          -> 1. The stronger half popped, so the cycle is spent and starts
    //                         over on the NQ. Day 1 is below CombinedFromDay, so the board
    //                         also goes back to printing just the base name (DisplayMonsterName).
    //   NQ killed on day N -> N + 1, the next day of the same cycle.
    //   no day recorded    -> still none. Monsters without a day cycle (Kirin, the wyrms)
    //                         must not suddenly grow a "Day 1" tile.
    //
    // Deliberately uncapped: HnmDayCycles lengths are for the dashboard indicator, and in
    // practice the counter climbs until the HQ actually pops.
    public static int? NextDayNumber(int? currentDay, bool wasHq)
    {
        if (wasHq) return 1;
        if (currentDay is not { } day || day <= 0) return null;
        return day + 1;
    }

    // The NQ (base) half of a merge pair, given either half. Used when a day cycle resets
    // after an HQ kill: the next pop is the weaker version, so a board left sitting on the
    // bare stronger name ("Nidhogg") has to go back to its base ("Fafnir").
    //
    // A COMBINED "Base/Stronger" label is returned unchanged — it already renders as the base
    // half on days below CombinedFromDay via DisplayMonsterName, and keeping the stored name
    // combined is what lets the board climb back into HQ territory on later days.
    public static string? BaseMonsterName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var trimmed = name.Trim();
        foreach (var (baseName, stronger) in MonsterMergePairs)
        {
            if (trimmed.Equals(stronger, StringComparison.OrdinalIgnoreCase)) return baseName;
        }
        return trimmed;
    }

    // Everything on the SHORT band: 7 spawn windows at a 10-minute cadence, read for attendance
    // twice (Open + Close). The kings/dragons, plus the four timed NMs below — those are NMs by
    // tier and by where the addon lists them, but their camp runs on exactly this shape, so they
    // belong to the same cadence rather than to a second copy of it. Membership is what gives a
    // monster automatic window advance (DefaultWindowCadence), the board's window controls
    // (UsesWindows), and a 2-post Open/Close camp (GetWindowCount).
    public static readonly HashSet<string> ShortWindowHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fafnir",
        "Nidhogg",
        "Behemoth",
        "King Behemoth",
        "Adamantoise",
        "Aspidochelone",
        // Timed NMs on the same 7 × 10-min band (all ~21-24h repop, like the kings/dragons).
        "Capricious Cassie",
        "Bune",
        "Boroka",
        "Roc"
    };

    // The eight Sky farm NMs, in the order the Charts → Sky board stacks them: the two that feed
    // each god, god by god, in SkyGodOrder. Each pair plus its god is one COLUMN on that board, so
    // the order within a pair is the order they appear top to bottom above their god.
    //
    // This is the ORDERED source of truth; SkyFarmNms below is built from it, so the two can never
    // drift into disagreeing about which NMs these are. The other consumers only ask Contains.
    //
    // DECLARATION ORDER MATTERS: static field initializers run top-to-bottom, so this must stay
    // above SkyFarmNms. Moving it below hands the HashSet a null enumerable and turns startup into a
    // TypeInitializationException a long way from the edit — the same trap SkyGodOrder documents.
    public static readonly IReadOnlyList<string> SkyFarmNmOrder = new[]
    {
        "Faust",         "Brigandish Blade", // → Suzaku
        "Zipacna",       "Olla Grande",      // → Genbu
        "Steam Cleaner", "Mother Globe",     // → Seiryu
        "Despot",        "Ullikummi",        // → Byakko
    };

    // Sky-farm NMs that share a 2-hour repop. Used by GetDefaultTodCooldown so
    // an addon-posted ToD for any of these picks up "2 Hour" automatically.
    public static readonly HashSet<string> SkyFarmNms =
        new(SkyFarmNmOrder, StringComparer.OrdinalIgnoreCase);

    // The four Sky Gods + Kirin, in the order the Charts → Sky cards show them.
    //
    // This is the ORDERED source of truth; SkyGods below is built from it, so the two can never
    // drift into disagreeing about which monsters are Sky Gods. Reordering here reorders the Sky
    // chart and nothing else — the other consumers only ask Contains.
    //
    // DECLARATION ORDER MATTERS: static field initializers run top-to-bottom, so SkyGodOrder must
    // stay above SkyGods. Moving it below hands the HashSet a null enumerable and turns startup
    // into a TypeInitializationException a long way from the edit.
    public static readonly IReadOnlyList<string> SkyGodOrder = new[]
    {
        "Suzaku",
        "Genbu",
        "Seiryu",
        "Byakko",
        "Kirin",
    };

    // The four Sky Gods + Kirin. Pop-only encounters with a 5-minute repop
    // window from the moment they're defeated — distinct from the farm NM
    // cycle. Used by GetDefaultTodCooldown so an addon-posted ToD for any of
    // these picks up "5 Min" automatically.
    public static readonly HashSet<string> SkyGods = new(SkyGodOrder, StringComparer.OrdinalIgnoreCase);

    // Whether a name is one of the five, however it was typed.
    public static bool IsSkyGod(string? name) =>
        !string.IsNullOrWhiteSpace(name) && SkyGods.Contains(name.Trim());

    // Case-insensitive input ("byakko", "BYAKKO ") to the canonical spelling used for storage and
    // for the theme key on both surfaces. Null for anything that isn't a Sky God, so callers can
    // reject an unknown god rather than storing a row nothing will ever group.
    public static string? NormalizeSkyGod(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return SkyGodOrder.FirstOrDefault(
            god => string.Equals(god, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    // CoP Sea NMs (Jailers, Ix'aern variants, Absolute Virtue). Pop-only
    // encounters that share the Sky Gods' 5-minute cooldown — included here
    // so addon-posted ToDs for any Sea NM default to "5 Min". Mirrors the
    // names defined under constants.SEA_NMS_GROUPS in the addon.
    public static readonly HashSet<string> SeaNms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Jailer of Temperance",
        "Jailer of Fortitude",
        "Jailer of Faith",
        "Ix'aern (Monk)",
        "Ix'aern (Dark Knight)",
        "Ix'aern (Dragoon)",
        // Bare chat-line name. Addon disambiguates to one of the variants
        // above via mob ID, but if the entity table has already cleared by
        // the time the parser runs, the bare name lands here and we still
        // resolve the cooldown to 5 Min.
        "Ix'aern",
        "Jailer of Hope",
        "Jailer of Justice",
        "Jailer of Prudence",
        "Jailer of Love",
        "Absolute Virtue",
    };

    // HENMs (Promyvion / Lumoria-tier pops). Mirrors constants.HENMS in the addon.
    // Listed here only so they can be folded into PopOnlyNms below.
    public static readonly HashSet<string> Henms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mammet-9999",
        "Overlord Arthro",
        "Ruinous Rocs",
        "Sacred Scorpions",
        "Tonberry Sovereign",
        "Ultimega",
    };

    // Every monster that spawns from a POP ITEM rather than a repop timer: the four Sky
    // Gods + Kirin, the Sea NMs, and the HENMs. A per-linkshell ToD cooldown/interval means
    // nothing for these, so they're deliberately absent from the ToD monster catalog
    // (LinkshellCustomizeViewModel.TodMonsterGroups and the Activity's
    // TOD_BUILT_IN_MONSTER_GROUPS / POP_ONLY_TOD_MONSTERS).
    //
    // The addon still CAPTURES their kills — that's what feeds loot and kill history — and
    // GetDefaultTodCooldown still resolves them to "5 Min", so an addon-posted ToD for one
    // lands with a sane window. This set exists so a timing saved back when they were in the
    // catalog gets stripped out of the settings payload instead of coming back as a "custom"
    // monster in the picker.
    public static readonly HashSet<string> PopOnlyNms =
        new(SkyGods.Concat(SeaNms).Concat(Henms), StringComparer.OrdinalIgnoreCase);

    // Testing presets — temporary in-zone monsters used by QA to validate the
    // post-by-window attendance flow without a real HNM. Mirrored on the
    // addon side in att/constants.lua's TESTING_MONSTERS. Treated identically
    // to ShortWindowHnms (2 windows, "Open" / "Close" labels).
    public static readonly HashSet<string> TestingHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Goblin Furrier",
        "Goblin Shaman"
    };

    // Returns true when the given name refers to a curated HNM that participates
    // in the streamlined HNM workflow (auto-event-from-ToD, dedicated dashboard).
    // Mirrors the union of LongWindow/Short Window/Testing sets.
    public static bool IsTrueHnm(string? name) =>
        MonsterSegments(name).Any(seg =>
            LongWindowHnms.Contains(seg) || ShortWindowHnms.Contains(seg) || TestingHnms.Contains(seg));

    // Canonical camp zone per HNM. Used to pre-fill Event.EventLocation when
    // the addon's ToD post auto-creates the next-repop event. Testing monsters
    // are intentionally absent (their EventLocation is left null and the
    // officer can fill it in if the QA flow needs a value).
    public static readonly Dictionary<string, string> HnmZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Behemoth"]       = "Behemoth's Dominion",
        ["King Behemoth"]  = "Behemoth's Dominion",
        ["Fafnir"]         = "Dragon's Aery",
        ["Nidhogg"]        = "Dragon's Aery",
        ["Adamantoise"]    = "Qufim Island",
        ["Aspidochelone"]  = "Qufim Island",
        ["Tiamat"]         = "Attohwa Chasm",
        ["Jormungand"]     = "Uleguerand Range",
        ["Vrtra"]          = "King Ranperre's Tomb",
        // The ToAU three. Same 24-hour band as the wyrms above, cut into 5 six-hour windows.
        ["Cerberus"]       = "Mount Zhayolm",
        ["Hydra"]          = "Wajaom Woodlands",
        ["Khimaira"]       = "Caedarva Mire",
        // Timed NMs on the short band. Same job as the rows above: the camp a board sends people to.
        ["Capricious Cassie"] = "Fei'Yin",
        ["Bune"]              = "Gustav Tunnel",
        ["Boroka"]            = "Riverne - Site #B01",
        ["Roc"]               = "Sauromugue Champaign",
    };

    // Camp zone for a monster name, tolerant of a combined "Base/Stronger" label (both
    // halves of every merge pair share a zone, so the first matching segment wins).
    public static string? ZoneFor(string? name)
    {
        foreach (var segment in MonsterSegments(name))
        {
            if (HnmZones.TryGetValue(segment, out var zone)) return zone;
        }
        return null;
    }

    // HNMs that rotate through a day cycle (Nidhogg D1/D2/D3 etc.). The value
    // is the cycle length, used only to render the day indicator on the
    // dashboard. Day numbers themselves come from Tod.DayNumber (the addon's
    // launcher captures them via state.eventPresetDayInputs).
    public static readonly Dictionary<string, int> HnmDayCycles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Nidhogg"]       = 3,
        ["King Behemoth"] = 5,
        ["Aspidochelone"] = 3,
    };

    // (DefaultRepopHours lived here: a second repop table for the Discord "Pop / End Camp"
    // quick-end. That path resolves the LINKSHELL'S configured cooldown now
    // (HnmCampPopService -> GetDefaultTodCooldownAsync -> MonsterTimingResolver), so nothing had
    // called this in some time — and it had already drifted, answering 72h for Jormungand and
    // Vrtra where MonsterTimingDefaults says 84h. A dead duplicate of the very fact this file
    // owns is exactly what it looks like next time someone edits a band, so it is gone;
    // MonsterTimingDefaults.DefaultCooldownMinutes is the one answer.)

    // Real spawn-window timing per HNM. Minutes and Windows drive the timed auto-advance: window 1
    // opens at the pop time and window N opens (N-1)×Minutes later. The long-window wyrms run
    // 25 windows at 60-min cadence — window 1 at the pop through window 25 a full 24h later (= MaxWindow);
    // the ToAU three cover that same 24h in 5 windows at 6-hour cadence;
    // the kings/dragons run 7 windows at 10-min cadence — window 1 at the pop through window 7 a full
    // hour later (1:00 … 2:00 at 10-min marks). Returns null for anything not on a timed cadence
    // (Testing monsters, non-HNMs) — those advance manually. Tolerant of a combined "Base/Stronger"
    // label (first matching segment wins).
    public static (int Minutes, int Windows)? DefaultWindowCadence(string? monster)
    {
        foreach (var seg in MonsterSegments(monster))
        {
            // ToAU first: these names are ALSO in LongWindowHnms, so testing the wider set first
            // would hand them the wyrms' 25 × 60. Same ordering GetWindowCount and
            // MonsterTimingDefaults.DefaultCooldownMinutes use, for the same reason.
            if (ToauHnms.Contains(seg)) return (ToauWindowCadenceMinutes, ToauWindowCount);
            if (LongWindowHnms.Contains(seg)) return (60, 25);
            if (ShortWindowHnms.Contains(seg)) return (10, 7);
        }
        return null;
    }

    // Which window a moment falls in, given window 1 opens at `anchor` and each window lasts
    // `minutes`. Window N runs [anchor + (N-1)×minutes, anchor + N×minutes), so this is the BUCKET
    // a timestamp lands in, not the nearest boundary to it: a scan taken at 9:58 on a 10-minute
    // grid anchored at 9:00 belongs to window 6 — the window that was actually open — even though
    // window 7 starts two minutes later. Attendance is about what was open when you scanned.
    //
    // Clamped to [1, windowCount]; anything at or before the anchor is window 1, and a monster with
    // no cadence (minutes <= 0) is always window 1. Pure, and the single source of truth for this
    // mapping — the live board's auto-advance and attendance-snapshot labelling must agree.
    public static int WindowNumberAt(DateTime anchor, DateTime at, int minutes, int windowCount)
    {
        var maxWindow = Math.Max(1, windowCount);
        if (minutes <= 0 || at <= anchor)
        {
            return 1;
        }
        var expected = 1 + (int)Math.Floor((at - anchor).TotalMinutes / minutes);
        return Math.Clamp(expected, 1, maxWindow);
    }

    // The window an attendance snapshot belongs to, or null when the camp's monster runs no timed
    // cadence at all (Sky gods, farm NMs, ad-hoc `/lsm now` posts). Null means "this camp has no
    // window grid", which is different from window 1 — the UI shows no window tag rather than
    // claiming everything happened in the first window.
    public static int? SnapshotWindowNumber(string? monster, DateTime anchor, DateTime capturedAt)
        => DefaultWindowCadence(monster) is { } cadence
            ? WindowNumberAt(anchor, capturedAt, cadence.Minutes, cadence.Windows)
            : null;

    // How close two attendance posts must be to count as ONE capture of the same roster. A post
    // landing within this of an existing snapshot on the same Window Event is FOLDED INTO it — its
    // members unioned in — instead of becoming a snapshot of its own.
    //
    // This replaced duplicate DETECTION (a +/-8 min / 75%-name-overlap guess that marked the later
    // post "PossibleDuplicate"), which is now gone entirely. It was the wrong shape twice over:
    // several people scanning one camp is not a mistake to flag, and a flagged snapshot was
    // EXCLUDED from the combined roster (BuildCombinedMembers filters to Active), so anyone who
    // appeared only in the flagged post silently lost their credit.
    //
    // The guessing was only ever needed because the server could not tell "one alliance captured
    // twice" from "two alliances captured at once". The alliance number settles that outright, so
    // the fold is now a plain rule: same alliance + same window + inside this bound = one roster,
    // unioned by character name. The union only ever ADDS people — a later post that is missing
    // someone the earlier one saw never removes them.
    //
    // Scaled to how long a window actually lasts, so a merge can never swallow two genuinely
    // different windows: the 10-minute kings/dragons get 3 minutes, the hour-long wyrms get 5.
    // Anything with no window cadence at all (Sky gods, farm NMs, ad-hoc `/lsm now` posts) takes
    // the tighter 3 minutes — without a known window length, the safer bound is the short one.
    public static TimeSpan SnapshotMergeWindow(string? monster) =>
        SnapshotMergeWindow(DefaultWindowCadence(monster)?.Minutes ?? 0);

    // Scaled to the window length a camp ACTUALLY runs, which is what a configurable cadence needs:
    // an hour-long window gets the wider 5-minute merge, anything shorter takes 3. A camp with no
    // grid at all also takes 3 — without a known window length the safer bound is the short one.
    //
    // The name-based overload above delegates here, so the built-in 60/10 monsters get byte-identical
    // answers to before and a custom 30-minute cadence gets a sane one instead of falling off the
    // end of a hardcoded 25/7 switch.
    public static TimeSpan SnapshotMergeWindow(int cadenceMinutes) =>
        cadenceMinutes >= 60 ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(3);

    // The BUILT-IN number of windows a camp runs: the real per-monster cadence count when the
    // monster is on one (25 for the wyrms, 5 for the ToAU three, 7 for the kings/dragons), else the
    // name-based bucket (2 for a Testing monster, 1 for anything else).
    //
    // No longer the last word. A linkshell can configure its own window count and cadence per
    // monster (LinkshellMonsterTiming), and a camp CAPTURES that grid at creation into
    // Event.SpawnWindowCount / SpawnWindowMinutes. This is the fallback the capture falls back TO —
    // for a camp created before setups existed, and for a monster nobody configured.
    //
    // Read DiscordEventMessageBuilder.EffectiveWindowCount(Event) instead when you have an event;
    // it applies the stamp first. This overload answers only "what does this monster run by
    // default", which is what seeding a new setup needs and not much else.
    public static int EffectiveWindowCount(string? monster) =>
        DefaultWindowCadence(monster)?.Windows ?? GetWindowCount(monster);

    // Minutes between automatic window advances, 0 when the monster has no timed cadence
    // (Testing monsters, non-HNMs) — those only advance when an officer clicks "Next Window".
    public static int WindowAdvanceMinutes(string? monster) =>
        DefaultWindowCadence(monster)?.Minutes ?? 0;

    // Every monster on a built-in timed cadence with its window setup, most windows first
    // (the 25-window wyrms, then the 7-window kings/dragons and timed NMs, then the 5-window ToAU
    // three), alphabetical within each band. Ordered by window COUNT, which is not the same as by
    // band length: the ToAU three sort last on five windows while covering a full 24 hours.
    // Derived from the membership sets + DefaultWindowCadence, so adding a monster to either set
    // surfaces it here automatically. Testing presets are excluded — they carry no timed cadence.
    //
    // It used to back the read-only "Window setups" list in the Activity's HNM Settings card; that
    // list is per-linkshell and editable now (LinkshellMonsterTiming), so this survives as the
    // canonical enumeration of the BUILT-IN bands — which is what the cadence tests sweep.
    public static IReadOnlyList<(string Monster, int Windows, int Minutes)> WindowedHnmSetups() =>
        LongWindowHnms.Concat(ShortWindowHnms)
            .Select(name => (Monster: name, Cadence: DefaultWindowCadence(name)))
            .Where(entry => entry.Cadence is not null)
            .Select(entry => (entry.Monster, entry.Cadence!.Value.Windows, entry.Cadence.Value.Minutes))
            .OrderByDescending(setup => setup.Windows)
            .ThenBy(setup => setup.Monster, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // The ATTENDANCE POST count by name — how many times the roster is read.
    //
    // A wyrm posts once per hour-long window, so its post count is its window count: 25, the same
    // number DefaultWindowCadence and MaxWindow carry. This read 24 — a leftover from when the 24h
    // band was modelled as 24 windows rather than as 25 openings an hour apart — which put the
    // Attendance Windows card ("1 of 24") one short of the board above it ("Window 1 of 25") and
    // left window 25 with no place in the count, even though ingestion accepts a post for it.
    //
    // The ToAU three coincide the same way at their own number: one post per six-hour window, so 5.
    // They must be tested BEFORE LongWindowHnms, which they are also members of — see ToauHnms.
    //
    // The kings/dragons keep 2 on purpose: Open + Close across the 7 spawn windows they sit
    // through. There the two counts genuinely differ; on a wyrm they genuinely coincide.
    public static int GetWindowCount(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return 1;
        var trimmed = eventName.Trim();
        if (ToauHnms.Contains(trimmed)) return ToauWindowCount;
        if (LongWindowHnms.Contains(trimmed)) return 25;
        if (ShortWindowHnms.Contains(trimmed)) return 2;
        if (TestingHnms.Contains(trimmed)) return 2;
        // Combined display labels such as "Behemoth/King Behemoth" come from
        // the addon's Event Presets UI. Split on '/' and match each segment
        // so post-by-window attendance still engages for those events.
        foreach (var segment in trimmed.Split('/'))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;
            if (ToauHnms.Contains(seg)) return ToauWindowCount;
            if (LongWindowHnms.Contains(seg)) return 25;
            if (ShortWindowHnms.Contains(seg)) return 2;
            if (TestingHnms.Contains(seg)) return 2;
        }
        return 1;
    }

    // Window 1 / window 2 of a 2-post camp.
    public const string OpenWindowLabel = "Open";
    public const string CloseWindowLabel = "Close";

    // Labels these two carried before the rename. Only ever read, never written — see
    // NormalizeWindowLabel.
    private const string LegacyOpenWindowLabel = "On Time";
    private const string LegacyCloseWindowLabel = "Claim/Kill";

    public static string? GetDefaultWindowLabel(string? eventName, int sequenceNumber, int? effectiveWindowCount = null)
    {
        // A 2-post camp — the kings/dragons (Fafnir, Behemoth, Adamantoise and their HQ halves)
        // and the Testing monsters — takes one roster snapshot when the camp opens and one when
        // it closes, and those are what the open/close bonuses pay on. Numbering them "Window 1"
        // and "Window 2" invites "which of the seven?", which they are not: the 7 spawn windows
        // are pop chances the camp sits through, not attendance posts. So they are NAMED.
        //
        // Any other effective count — e.g. a short-window HNM defaulting to 7 windows, where
        // window 5 must not read "Close" — falls through to numbered "Window N" labels. Mirrors
        // the addon's constants.window_label; the two must agree or a camp reads one way in game
        // and another in the app.
        var count = effectiveWindowCount ?? GetWindowCount(eventName);
        return count == 2
            ? (sequenceNumber == 1 ? OpenWindowLabel : CloseWindowLabel)
            : null;
    }

    // The label to SHOW for a stored window. Rows written before the rename hold "On Time" /
    // "Claim/Kill"; mapping them here means a camp that was mid-flight when this shipped doesn't
    // end up with one window named the old way and the next the new way. Anything else (a
    // numbered null, or a label a future version writes) passes through untouched.
    public static string? NormalizeWindowLabel(string? storedLabel)
    {
        var trimmed = storedLabel?.Trim();
        if (string.Equals(trimmed, LegacyOpenWindowLabel, StringComparison.OrdinalIgnoreCase))
        {
            return OpenWindowLabel;
        }
        if (string.Equals(trimmed, LegacyCloseWindowLabel, StringComparison.OrdinalIgnoreCase))
        {
            return CloseWindowLabel;
        }
        return storedLabel;
    }
}
