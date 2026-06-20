namespace LinkshellManagerDiscordApp.Services;

// Single source of truth for HNM-event window counts and labels.
// Tiamat / Jormungand / Vrtra spawn over a long pop window split into 24 attendance slots.
// Fafnir / Nidhogg / Behemoth / King Behemoth / Adamantoise / Aspidochelone use 2 slots
// ("On Time" + "Claim/Kill"). Everything else is a single-window event (current behavior).
public static class HnmConfig
{
    public static readonly HashSet<string> LongWindowHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tiamat",
        "Jormungand",
        "Vrtra"
    };

    // The window-cycle HNMs above pop across successive windows. Their signup board shows
    // "Window N" + an officer-only "Next Window" button that wipes the signups and advances
    // N (Event.HnmWindowNumber) up to MaxWindow, then stops.
    public const int MaxWindow = 25;

    public static bool SupportsWindowAdvance(string? monsterName) =>
        !string.IsNullOrWhiteSpace(monsterName) && LongWindowHnms.Contains(monsterName.Trim());

    public static readonly HashSet<string> ShortWindowHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fafnir",
        "Nidhogg",
        "Behemoth",
        "King Behemoth",
        "Adamantoise",
        "Aspidochelone"
    };

    // Sky-farm NMs that share a 2-hour repop. Used by GetDefaultTodCooldown so
    // an addon-posted ToD for any of these picks up "2 Hour" automatically.
    public static readonly HashSet<string> SkyFarmNms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Despot",
        "Mother Globe",
        "Zipacna",
        "Ullikummi",
        "Olla Grande",
        "Steam Cleaner",
        "Brigandish Blade",
        "Faust",
    };

    // The four Sky Gods + Kirin. Pop-only encounters with a 5-minute repop
    // window from the moment they're defeated — distinct from the farm NM
    // cycle. Used by GetDefaultTodCooldown so an addon-posted ToD for any of
    // these picks up "5 Min" automatically.
    public static readonly HashSet<string> SkyGods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Seiryu",
        "Suzaku",
        "Byakko",
        "Genbu",
        "Kirin",
    };

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

    // Testing presets — temporary in-zone monsters used by QA to validate the
    // post-by-window attendance flow without a real HNM. Mirrored on the
    // addon side in att/constants.lua's TESTING_MONSTERS. Treated identically
    // to ShortWindowHnms (2 windows, "On Time" / "Claim/Kill" labels).
    public static readonly HashSet<string> TestingHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Goblin Furrier",
        "Goblin Shaman"
    };

    // Returns true when the given name refers to a curated HNM that participates
    // in the streamlined HNM workflow (auto-event-from-ToD, dedicated dashboard).
    // Mirrors the union of LongWindow/Short Window/Testing sets.
    public static bool IsTrueHnm(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        return LongWindowHnms.Contains(trimmed)
            || ShortWindowHnms.Contains(trimmed)
            || TestingHnms.Contains(trimmed);
    }

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
    };

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

    public static int GetWindowCount(string? eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName)) return 1;
        var trimmed = eventName.Trim();
        if (LongWindowHnms.Contains(trimmed)) return 24;
        if (ShortWindowHnms.Contains(trimmed)) return 2;
        if (TestingHnms.Contains(trimmed)) return 2;
        // Combined display labels such as "Behemoth/King Behemoth" come from
        // the addon's Event Presets UI. Split on '/' and match each segment
        // so post-by-window attendance still engages for those events.
        foreach (var segment in trimmed.Split('/'))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;
            if (LongWindowHnms.Contains(seg)) return 24;
            if (ShortWindowHnms.Contains(seg)) return 2;
            if (TestingHnms.Contains(seg)) return 2;
        }
        return 1;
    }

    public static string? GetDefaultWindowLabel(string? eventName, int sequenceNumber, int? effectiveWindowCount = null)
    {
        // Explicit-override path: the addon's "Claim/Kill" style sets a
        // custom 2-window count on a user-named event that won't match any
        // of the curated HNM lookups below. Use the same labels the
        // ShortWindowHnms cohort uses.
        if (effectiveWindowCount == 2)
        {
            return sequenceNumber == 1 ? "On Time" : "Claim/Kill";
        }

        if (string.IsNullOrWhiteSpace(eventName)) return null;
        var trimmed = eventName.Trim();
        if (ShortWindowHnms.Contains(trimmed) || TestingHnms.Contains(trimmed))
        {
            return sequenceNumber == 1 ? "On Time" : "Claim/Kill";
        }
        foreach (var segment in trimmed.Split('/'))
        {
            var seg = segment.Trim();
            if (seg.Length == 0) continue;
            if (ShortWindowHnms.Contains(seg) || TestingHnms.Contains(seg))
            {
                return sequenceNumber == 1 ? "On Time" : "Claim/Kill";
            }
        }
        return null;
    }
}
