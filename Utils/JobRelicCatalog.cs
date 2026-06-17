namespace LinkshellManagerDiscordApp.Utils;

// The relic weapons each classic-15 job can equip, catalog-aligned with
// EventJobCatalog.MainJobOptions (index 0 = WAR … 14 = SMN). Mirrors the Angular
// JOB_RELIC_OPTIONS in discord-activity's event-job-options.ts — keep in sync.
// Used by the web profile's relic "add multiple" picker.
public static class JobRelicCatalog
{
    public static readonly IReadOnlyList<IReadOnlyList<string>> Options = new[]
    {
        new[] { "Bravura", "Ragnarok" },            // WAR
        new[] { "Spharai" },                        // MNK
        new[] { "Mjollnir" },                       // WHM
        new[] { "Claustrum" },                      // BLM
        new[] { "Mandau", "Excalibur" },            // RDM
        new[] { "Mandau" },                         // THF
        new[] { "Excalibur", "Ragnarok", "Aegis" }, // PLD
        new[] { "Ragnarok", "Apocalypse" },         // DRK
        new[] { "Guttler" },                        // BST
        new[] { "Mandau", "Gjallarhorn" },          // BRD
        new[] { "Yoichinoyumi", "Annihilator" },    // RNG
        new[] { "Amanomurakumo", "Yoichinoyumi" },  // SAM
        new[] { "Kikoku" },                         // NIN
        new[] { "Gungnir" },                        // DRG
        new[] { "Claustrum" },                      // SMN
    };
}
