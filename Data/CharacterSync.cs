using System.Globalization;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LinkshellManagerDiscordApp.Data;

// Keeps the Characters table (AddCharacters) equal to the accounts' own name and job columns.
//
// The copy is made by SQL that reads the account row AS SAVED, run right after the save
// (ApplicationDbContext.SyncCharactersAsync), rather than by adding tracked rows to the save:
// - two saves of one account at the same moment cannot both insert a slot and have one fail on the
//   (AppUserId, Slot) key: INSERT ... ON CONFLICT DO UPDATE lets Postgres settle it;
// - each copy first takes a per-account lock (see SyncSql), so two copies of one account run one
//   after the other, and the later one reads the account as last saved;
// - it runs on its own (inside a savepoint when the caller has a transaction), so a failure here is
//   logged and never fails the user's save. The account's next save repairs it, and so does every
//   start-up (ResyncAllAsync).
// The InMemory test database has no SQL: there the same rules run in C# (DesiredRows).
//
// The rules (the migrations' too): names are trimmed of the same whitespace .NET's Trim removes
// (TodDerivedSql.WhitespaceChars), a blank name has no row, a name longer than 256
// characters has no row, and NormalizedName is lower(Name). Since CharactersSwitch, a slot that
// repeats an earlier slot's name in the same account (an alt equal to the main, say) has no row
// either: the earlier slot already gives that name to the account, and the unique
// (AppUserId, NormalizedName) index allows one row per name per account.
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

    private const string Ws = TodDerivedSql.WhitespaceChars;

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

    // Whether a slot row (alias s) gets a Characters row, before repeats are taken out. Never NULL.
    public static readonly string KeepSql =
        $"(s.nm IS NOT NULL AND s.nm ~ '[^{Ws}]' AND char_length(s.nm) <= {Character.MaxNameLength})";

    // The name with the leading and trailing whitespace .NET's string.Trim removes taken off.
    public static string TrimSql(string column) => $"regexp_replace({column}, '^[{Ws}]+|[{Ws}]+$', '', 'g')";

    // The check on Characters.Name: at least one character that is not whitespace.
    public static readonly string NameNotBlankCheckSql = $"\"Name\" ~ '[^{Ws}]'";

    // The rows the accounts should have (same columns as SlotRowsSql): the kept slots, with a slot
    // that repeats an earlier slot's name in the same account (ignoring capitals) left out.
    public static string DesiredRowsSql(string? accountFilter) => $"""
        SELECT k.account, k.slot, k.nm, k.jl, k.sj, k.mj, k.cl
        FROM (
            SELECT s.account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl,
                   row_number() OVER (PARTITION BY s.account, lower(s.nm) ORDER BY s.slot) AS rn
            FROM ({SlotRowsSql(accountFilter)}) s
            WHERE {KeepSql}
        ) k
        WHERE k.rn = 1
        """;

    // pg_advisory_xact_lock(class, key) class for "copying this account's characters". Negative, so
    // it never meets TodRecorder's locks (whose class is a linkshell id).
    public const int LockClassId = -1128808786;

    // Brings the given accounts' rows in line (@ids text[]; @now is the UpdatedAt of changed rows).
    // Three statements, sent as one batch, so they run as one transaction of their own (or inside
    // the caller's):
    // 1. Lock each account, in a fixed order. A second copy of the same account waits here, and its
    //    next statements then read the account as saved by now. The lock is held until the batch
    //    (or the caller's transaction) ends. An advisory lock rather than FOR UPDATE on the account
    //    row: only copies take it, so a copy never waits on, or deadlocks with, a user's own save.
    // 2. Delete the rows that should not be there, and the rows whose name changed (ignoring
    //    capitals). Deleting a renamed row before the insert is what lets a main and an alt swap
    //    names in one save without breaking the one-name-per-account index half-way.
    // 3. Insert new slots and update the ones that differ (a change of capitals, jobs, crafts).
    //    Rows already equal are not touched, so UpdatedAt says when a row last changed.
    public static readonly string SyncSql = $"""
        SELECT pg_advisory_xact_lock({LockClassId}, l.key)
        FROM (SELECT DISTINCT hashtext(a) AS key FROM unnest(@ids) AS a ORDER BY 1 OFFSET 0) l;

        DELETE FROM "Characters" c
        WHERE c."AppUserId" = ANY(@ids)
          AND NOT EXISTS (
              SELECT 1 FROM ({DesiredRowsSql("u.\"Id\" = ANY(@ids)")}) d
              WHERE d.account = c."AppUserId" AND d.slot = c."Slot" AND lower(d.nm) = c."NormalizedName");

        INSERT INTO "Characters" ("AppUserId", "Slot", "Name", "NormalizedName", "JobLevels", "StrongJobs", "MeritJobs", "CraftLevels", "UpdatedAt")
        SELECT d.account, d.slot, d.nm, lower(d.nm), d.jl, d.sj, d.mj, d.cl, @now
        FROM ({DesiredRowsSql("u.\"Id\" = ANY(@ids)")}) d
        ORDER BY d.account, d.slot
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

    // One wanted Characters row, worked out in C#. The arrays are the account's own (not copies).
    public sealed record DesiredRow(
        short Slot, string Name, string NormalizedName,
        int[]? JobLevels, int[]? StrongJobs, string[]? MeritJobs, int[]? CraftLevels);

    // The rows an account should have, by the same rules as DesiredRowsSql: .NET Trim, not blank,
    // at most 256 characters (counted as Postgres counts them), lower-cased with ToLowerInvariant
    // (Postgres lower() gives the same on the server's locale), and a repeat of an earlier slot's
    // name left out. The InMemory test database copies with this; Postgres tests compare it with
    // the SQL.
    public static IReadOnlyList<DesiredRow> DesiredRows(AppUser user)
    {
        var slots = new (string? Name, int[]? Jobs, int[]? Strong, string[]? Merits, int[]? Crafts)[]
        {
            (user.CharacterName, user.JobLevels, user.StrongJobs, user.MeritJobs, user.CraftLevels),
            (user.AltCharacterName1, user.Alt1JobLevels, user.Alt1StrongJobs, user.Alt1MeritJobs, user.Alt1CraftLevels),
            (user.AltCharacterName2, user.Alt2JobLevels, user.Alt2StrongJobs, user.Alt2MeritJobs, user.Alt2CraftLevels),
        };
        var rows = new List<DesiredRow>(3);
        for (short slot = 0; slot < slots.Length; slot++)
        {
            var name = slots[slot].Name?.Trim();
            if (string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > Character.MaxNameLength)
            {
                continue;
            }
            var normalized = name.ToLowerInvariant();
            if (rows.Any(row => string.Equals(row.NormalizedName, normalized, StringComparison.Ordinal)))
            {
                continue;
            }
            rows.Add(new DesiredRow(slot, name, normalized,
                slots[slot].Jobs, slots[slot].Strong, slots[slot].Merits, slots[slot].Crafts));
        }
        return rows;
    }

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
    // SyncSql's two parameters.
    public static object[] Parameters(IEnumerable<string> accountIds, DateTime now) => new object[]
    {
        new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = accountIds.ToArray() },
        new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now },
    };

    // Accounts per SyncSql run in ResyncAllAsync: each takes one advisory lock per account, and
    // the server's lock table is not unlimited.
    internal const int ResyncBatchSize = 200;

    // Copies every account again. Program.cs runs it at start-up, right after the migrations, so a
    // copy that failed (or one an older build skipped after a rollback) never outlives a restart.
    // Rows that are already right are not touched. Returns the number of accounts checked.
    public static async Task<int> ResyncAllAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            return 0;
        }
        var ids = await db.Users.AsNoTracking().OrderBy(user => user.Id).Select(user => user.Id).ToListAsync(cancellationToken);
        foreach (var batch in ids.Chunk(ResyncBatchSize))
        {
            await db.Database.ExecuteSqlRawAsync(SyncSql, Parameters(batch, DateTime.UtcNow), cancellationToken);
        }
        return ids.Count;
    }
}
