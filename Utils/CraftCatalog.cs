namespace LinkshellManagerDiscordApp.Utils;

// The nine FFXI crafts in the canonical profile order. Craft levels are stored
// as a plain int[] aligned to this list (index 0 = Alchemy … 8 = Fishing), so
// the client and server agree on which number belongs to which craft.
public static class CraftCatalog
{
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Alchemy",
        "Bonecraft",
        "Clothcraft",
        "Cooking",
        "Goldsmithing",
        "Leathercraft",
        "Smithing",
        "Woodworking",
        "Fishing",
    };

    public static int Count => Names.Count;

    // A fixed-length (Count) level array from the stored value: pads missing
    // entries with 0 and clamps negatives, so the client always gets 9 values.
    public static IReadOnlyList<int> Normalize(int[]? stored)
    {
        var result = new int[Names.Count];
        if (stored is not null)
        {
            for (var i = 0; i < Names.Count && i < stored.Length; i++)
            {
                result[i] = stored[i] < 0 ? 0 : stored[i];
            }
        }
        return result;
    }
}
