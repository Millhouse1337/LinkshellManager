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

    // ----- "Strong" job flags -----
    // A parallel array, indexed the same FFXI-job-id way as the level arrays, where
    // a non-zero value marks the job as "strong" (well-geared / merited — a hint the
    // member surfaces to the rest of the linkshell). Stored as int[] (0/1) so it
    // reuses the same jsonb column shape as the level arrays.

    // Reads the stored FFXI-job-id flag array into a catalog-aligned list of bools
    // (index i = is MainJobOptions[i] strong); missing/short entries become false.
    public static List<bool> ToCatalogFlags(int[]? stored)
    {
        var flags = new List<bool>(JobCount);
        for (var i = 0; i < JobCount; i++)
        {
            var jobId = FirstJobId + i;
            flags.Add(stored is not null && jobId < stored.Length && stored[jobId] != 0);
        }
        return flags;
    }

    // Writes catalog-aligned strong flags back into the stored FFXI-job-id array
    // (1 = strong, 0 = not), preserving index 0 (None) and any higher job ids.
    // Returns a new array.
    public static int[] MergeFlagsIntoStored(int[]? existing, IReadOnlyList<bool>? catalogFlags)
    {
        var minLength = FirstJobId + JobCount; // covers indices 0..(FirstJobId + JobCount - 1)
        var length = Math.Max(existing?.Length ?? 0, minLength);
        var result = new int[length];
        if (existing is not null)
        {
            Array.Copy(existing, result, existing.Length);
        }
        if (catalogFlags is not null)
        {
            for (var i = 0; i < JobCount && i < catalogFlags.Count; i++)
            {
                result[FirstJobId + i] = catalogFlags[i] ? 1 : 0;
            }
        }
        return result;
    }

    private static int Clamp(int level) => level < 0 ? 0 : level > MaxLevel ? MaxLevel : level;

    // ----- Per-job merit notes -----
    // Free-text, catalog-aligned (index i = MainJobOptions[i]) — NOT FFXI-job-id
    // indexed like the level/flag arrays. Stored as-is (no remap needed).

    // Normalizes a merit-note array to the catalog length (15), trimming + capping
    // each entry. Missing/short entries become empty strings.
    public static string[] NormalizeMerits(string[]? source)
    {
        const int maxLen = 500;
        var result = new string[JobCount];
        for (var i = 0; i < JobCount; i++)
        {
            var note = (source is not null && i < source.Length ? source[i] : null)?.Trim() ?? string.Empty;
            result[i] = note.Length > maxLen ? note[..maxLen] : note;
        }
        return result;
    }
}
