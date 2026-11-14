namespace LinkshellManagerDiscordApp.Services;

// A linkshell's loot structure ("Dkp", "LootCouncil" or "Hybrid"), which decides whether a
// winner pays DKP for an item. The settings page checks and saves it; End Event, End Camp, loot
// edits and the addon read it back.
//
// These lived in the Activity controllers, so services that charge for loot had to reach into a
// controller class to ask. They are plain string rules, so they sit here instead.
public static class LootStructures
{
    internal static string NormalizeLootStructure(string structure)
    {
        if (string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase)) return "LootCouncil";
        if (string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase)) return "Hybrid";
        return "Dkp";
    }

    internal static bool IsValidLootStructure(string? structure)
    {
        return string.Equals(structure, "Dkp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "LootCouncil", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(structure, "Hybrid", StringComparison.OrdinalIgnoreCase);
    }
}
