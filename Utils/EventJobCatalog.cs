namespace LinkshellManagerDiscordApp.Utils;

public static class EventJobCatalog
{
    // ORDER IS A CONTRACT — it is the catalog index used by the profile job
    // levels/strong flags/merits, JobRating.JobIndex and the relic catalog, and it
    // must stay in FFXI job-id order (index i ↔ job id i + 1) because
    // ProfileJobLevels maps between the two arithmetically. Only ever APPEND, and
    // only in job-id order (… 15 = SMN, 16 = BLU, 17 = COR, 18 = PUP).
    public static readonly string[] MainJobOptions =
    {
        "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK",
        "BST", "BRD", "RNG", "SAM", "NIN", "DRG", "SMN",
        "BLU", "COR", "PUP"
    };

    public static readonly string[] SubJobOptions =
    {
        "WAR", "MNK", "WHM", "BLM", "RDM", "THF", "PLD", "DRK",
        "BST", "BRD", "RNG", "SAM", "NIN", "DRG", "SMN",
        "BLU", "COR", "PUP"
    };

    public static readonly string[] JobTypeOptions =
    {
        "Tank", "Heal", "Support", "DPS"
    };
}
