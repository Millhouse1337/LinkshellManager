namespace LinkshellManagerDiscordApp.Utils;

// Maps between the profile editor's per-job level inputs (the 15 classic jobs in
// EventJobCatalog.MainJobOptions order) and the stored AppUserLinkshell.JobLevels
// array, which is indexed by FFXI job id (index 0 = None, 1 = WAR ... 15 = SMN) —
// the same contract the game addon writes via AddonPostJobLevelsRequest
// ("Index = FFXI job id (0..21), value = level"). Centralized here so the single
// assumption (the job-id index base) lives in one place; if the addon's base ever
// turns out to differ, only FirstJobId changes.
public static class ProfileJobLevels
{
    // FFXI job id of the first catalog job (WAR). MainJobOptions[i] -> job id FirstJobId + i.
    public const int FirstJobId = 1;

    // HorizonXI is classic-75, so levels are capped at 75.
    public const int MaxLevel = 75;

    public static int JobCount => EventJobCatalog.MainJobOptions.Length; // 15

    // Reads the stored FFXI-job-id array into a catalog-aligned list of levels
    // (index i = level of MainJobOptions[i]); missing/short entries become 0.
    public static List<int> ToCatalogLevels(int[]? stored)
    {
        var levels = new List<int>(JobCount);
        for (var i = 0; i < JobCount; i++)
        {
            var jobId = FirstJobId + i;
            var level = stored is not null && jobId < stored.Length ? stored[jobId] : 0;
            levels.Add(Clamp(level));
        }
        return levels;
    }

    // Writes catalog-aligned levels back into the stored FFXI-job-id array,
    // preserving index 0 (None) and any higher job ids the addon may have set
    // (jobs outside the classic-75 set). Returns a new array.
    public static int[] MergeIntoStored(int[]? existing, IReadOnlyList<int>? catalogLevels)
    {
        var minLength = FirstJobId + JobCount; // covers indices 0..(FirstJobId + JobCount - 1)
        var length = Math.Max(existing?.Length ?? 0, minLength);
        var result = new int[length];
        if (existing is not null)
        {
            Array.Copy(existing, result, existing.Length);
        }
        if (catalogLevels is not null)
        {
            for (var i = 0; i < JobCount && i < catalogLevels.Count; i++)
            {
                result[FirstJobId + i] = Clamp(catalogLevels[i]);
            }
        }
        return result;
    }

    private static int Clamp(int level) => level < 0 ? 0 : level > MaxLevel ? MaxLevel : level;
}
