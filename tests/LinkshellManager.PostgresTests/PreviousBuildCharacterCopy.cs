using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace LinkshellManager.PostgresTests;

// A frozen copy of what the build before Round 5's name switch (603439d) does with Characters: at
// start-up (CharacterSync.ResyncAllAsync) and after every account save, it copies the account's
// old name, job and craft columns INTO Characters (CharacterSync.SyncSql, word for word below).
//
// Rolling back to that build is safe only if that copy changes nothing: the old columns must hold
// exactly what Characters holds. Since R5.6 they do only after the "mirror refill" SQL
// (docs/round5-drop-manifest.md). AccountNameStampTests runs the refill and then this copy after
// every writer path of the current build, and checks that not one Characters row changed.
internal static class PreviousBuildCharacterCopy
{
    private const string Ws = @" \t\n\v\f\r\u0085\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000";
    private const int MaxNameLength = 256;
    private const int LockClassId = -1128808786;

    private static string TrimSql(string column) => $"regexp_replace({column}, '^[{Ws}]+|[{Ws}]+$', '', 'g')";

    private static string SlotRowsSql(string? accountFilter) => $"""
        SELECT u."Id" AS account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl
        FROM "AspNetUsers" u
        CROSS JOIN LATERAL (VALUES
            (0::smallint, {TrimSql("u.\"CharacterName\"")}, u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels"),
            (1::smallint, {TrimSql("u.\"AltCharacterName1\"")}, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
            (2::smallint, {TrimSql("u.\"AltCharacterName2\"")}, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
        ) AS s(slot, nm, jl, sj, mj, cl)
        {(accountFilter is null ? "" : "WHERE " + accountFilter)}
        """;

    private static readonly string KeepSql =
        $"(s.nm IS NOT NULL AND s.nm ~ '[^{Ws}]' AND char_length(s.nm) <= {MaxNameLength})";

    private static string DesiredRowsSql(string? accountFilter) => $"""
        SELECT k.account, k.slot, k.nm, k.jl, k.sj, k.mj, k.cl
        FROM (
            SELECT s.account, s.slot, s.nm, s.jl, s.sj, s.mj, s.cl,
                   row_number() OVER (PARTITION BY s.account, lower(s.nm) ORDER BY s.slot) AS rn
            FROM ({SlotRowsSql(accountFilter)}) s
            WHERE {KeepSql}
        ) k
        WHERE k.rn = 1
        """;

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

    // The old build's start-up copy, for every account.
    public static async Task RunForAllAccountsAsync(ApplicationDbContext db)
    {
        var ids = await db.Users.AsNoTracking().OrderBy(user => user.Id).Select(user => user.Id).ToArrayAsync();
        if (ids.Length == 0)
        {
            return;
        }
        await db.Database.ExecuteSqlRawAsync(SyncSql,
            new NpgsqlParameter("ids", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = ids },
            new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = System.DateTime.UtcNow });
    }
}
