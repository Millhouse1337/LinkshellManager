using System.Globalization;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Data;

// Keeps the Characters table (AddCharacters) equal to the accounts' own name and job columns.
//
// The copy is made by SQL that reads the account row AS SAVED, run right after the save
// (ApplicationDbContext.SyncCharactersAsync), rather than by adding tracked rows to the save:
// - two saves of one account at the same moment cannot both insert a slot and have one fail on the
//   (AppUserId, Slot) key: INSERT ... ON CONFLICT DO UPDATE lets Postgres settle it;
// - whatever order two saves finish in, the copy ends up equal to the account as last saved;
// - it runs on its own (inside a savepoint when the caller has a transaction), so a failure here is
//   logged and never fails the user's save. The table is a shadow that nothing reads yet; the next
//   deploy's rebuild repairs anything a failed copy missed.
// The InMemory test database has no SQL, so it is skipped there.
//
// The rules are the migration's (AddCharacters.FillSql) exactly: names are trimmed of the same
// whitespace .NET's Trim removes (AddTodDurationsAndSpawnKeys.WhitespaceChars), a blank name has
// no row, a name longer than 256 characters has no row, and NormalizedName is lower(Name).
public static class CharacterSync
{
    // The AppUser properties the Characters rows are copied from. A save that changes none of
    // them leaves Characters alone.
    public static readonly IReadOnlyList<string> SourceProperties = new[]
    {
        nameof(AppUser.CharacterName), nameof(AppUser.JobLevels), nameof(AppUser.StrongJobs),
        nameof(AppUser.MeritJobs), nameof(AppUser.CraftLevels),
        nameof(AppUser.AltCharacterName1), nameof(AppUser.Alt1JobLevels), nameof(AppUser.Alt1StrongJobs),
        nameof(AppUser.Alt1MeritJobs), nameof(AppUser.Alt1CraftLevels),
        nameof(AppUser.AltCharacterName2), nameof(AppUser.Alt2JobLevels), nameof(AppUser.Alt2StrongJobs),
        nameof(AppUser.Alt2MeritJobs), nameof(AppUser.Alt2CraftLevels),
    };

    private const string Ws = AddTodDurationsAndSpawnKeys.WhitespaceChars;

    // One row per account per slot: (account, slot, trimmed name, jobs...). accountFilter is an
    // extra WHERE on "AspNetUsers" u, or null for every account.
    public static string SlotRowsSql(string? accountFilter) => $"""
        SELECT u."Id" AS account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl
        FROM "AspNetUsers" u
        CROSS JOIN LATERAL (VALUES
            (0::smallint, {TrimSql("u.\"CharacterName\"")}, u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels"),
            (1::smallint, {TrimSql("u.\"AltCharacterName1\"")}, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
            (2::smallint, {TrimSql("u.\"AltCharacterName2\"")}, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
        ) AS s(slot, nm, jl, sj, mj, cl)
        {(accountFilter is null ? "" : "WHERE " + accountFilter)}
        """;

    // Whether a slot row (alias s) gets a Characters row. Never NULL.
    public static readonly string KeepSql =
        $"(s.nm IS NOT NULL AND s.nm ~ '[^{Ws}]' AND char_length(s.nm) <= {Character.MaxNameLength})";

    // The name with the leading and trailing whitespace .NET's string.Trim removes taken off.
    public static string TrimSql(string column) => $"regexp_replace({column}, '^[{Ws}]+|[{Ws}]+$', '', 'g')";

    // The check on Characters.Name: at least one character that is not whitespace.
    public static readonly string NameNotBlankCheckSql = $"\"Name\" ~ '[^{Ws}]'";

    // Brings the given accounts' rows in line (@ids text[]; @now is the UpdatedAt of changed rows).
    // Deletes the slots whose name is now blank or too long, then inserts new slots and updates the
    // ones that differ. Rows already equal are not touched, so UpdatedAt says when a row last changed.
    public static readonly string SyncSql = $"""
        DELETE FROM "Characters" c
        USING ({SlotRowsSql("u.\"Id\" = ANY(@ids)")}) s
        WHERE c."AppUserId" = s.account AND c."Slot" = s.slot AND NOT {KeepSql};

        INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
        SELECT s.account, s.slot, s.nm, lower(s.nm), s.jl, s.sj, s.mj, s.cl, @now
        FROM ({SlotRowsSql("u.\"Id\" = ANY(@ids)")}) s
        WHERE {KeepSql}
        ORDER BY s.account, s.slot
        ON CONFLICT ("AppUserId", "Slot") DO UPDATE SET
            "Name" = EXCLUDED."Name",
            "NormalizedName" = EXCLUDED."NormalizedName",
            "JobLevels" = EXCLUDED."JobLevels",
            "StrongJobs" = EXCLUDED."StrongJobs",
            "MeritJobs" = EXCLUDED."MeritJobs",
            "CraftLevels" = EXCLUDED."CraftLevels",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        WHERE ("Characters"."Name", "Characters"."NormalizedName", "Characters"."JobLevels",
               "Characters"."StrongJobs", "Characters"."MeritJobs", "Characters"."CraftLevels")
            IS DISTINCT FROM (EXCLUDED."Name", EXCLUDED."NormalizedName", EXCLUDED."JobLevels",
               EXCLUDED."StrongJobs", EXCLUDED."MeritJobs", EXCLUDED."CraftLevels");
        """;

    // The account's names that are too long to copy, as (slot, length in characters), for the log.
    // Lengths are counted the way Postgres's char_length counts them (code points, after the trim).
    public static IEnumerable<(short Slot, int Length)> TooLongNames(AppUser user)
    {
        var names = new[] { user.CharacterName, user.AltCharacterName1, user.AltCharacterName2 };
        for (short slot = 0; slot < names.Length; slot++)
        {
            var trimmed = names[slot]?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }
            var length = trimmed.EnumerateRunes().Count();
            if (length > Character.MaxNameLength)
            {
                yield return (slot, length);
            }
        }
    }

    internal static string Describe(IEnumerable<(short Slot, int Length)> names) =>
        string.Join(", ", names.Select(name => string.Create(CultureInfo.InvariantCulture, $"slot {name.Slot} ({name.Length} characters)")));
}
