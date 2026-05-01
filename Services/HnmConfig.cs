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

    public static readonly HashSet<string> ShortWindowHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fafnir",
        "Nidhogg",
        "Behemoth",
        "King Behemoth",
        "Adamantoise",
        "Aspidochelone"
    };

    // Sky-farm NMs that share a 2-hour repop. Mirrors the addon's curated
    // constants.SKY_FARM_NMS list. Used by GetDefaultTodCooldown so an
    // addon-posted ToD for any of these picks up "2 Hour" automatically.
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

    // Testing presets — temporary in-zone monsters used by QA to validate the
    // post-by-window attendance flow without a real HNM. Mirrored on the
    // addon side in att/constants.lua's TESTING_MONSTERS. Treated identically
    // to ShortWindowHnms (2 windows, "On Time" / "Claim/Kill" labels).
    public static readonly HashSet<string> TestingHnms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Goblin Furrier",
        "Goblin Shaman"
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

    public static string? GetDefaultWindowLabel(string? eventName, int sequenceNumber)
    {
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
