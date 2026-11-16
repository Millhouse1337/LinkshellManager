using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManager.PostgresTests;

// The start-up mirror check of the R5.5 build (Data/AccountNameMirrorCheck.cs at 1adf6d6), kept here
// for the tests only. R5.6 stopped copying Characters onto the old account columns and the roster
// names, so the app no longer counts them at start-up. The tests still use the counts where the
// copies must be exact:
// - CharactersBecomeSourceRoundTripTests: that migration makes them exact;
// - the R5.6 "mirror refill" SQL (docs/round5-drop-manifest.md) makes them exact again.
//
// Three counts:
// - Accounts: accounts whose old columns would give different Characters rows than they have (by
//   the R5.4 build's own copy rules: trimmed, blank and over-256 names left out, a repeated name
//   kept in its first slot). This is the pre-check's A7 query.
// - Mains: accounts whose CharacterName is not exactly their main's (slot 0) name.
// - Roster names: memberships whose roster name is not exactly their account's CharacterName.
internal static class AccountNameMirrorCounts
{
    public sealed record Result(int Accounts, int Mains, int RosterNames)
    {
        public bool AllMatch => Accounts == 0 && Mains == 0 && RosterNames == 0;
    }

    private const string Ws = TodDerivedSql.WhitespaceChars;

    private static readonly string AccountsSql = $"""
        WITH slots AS (
            SELECT u."Id" AS account, s.slot::smallint AS slot,
                   regexp_replace(s.nm, '^[{Ws}]+|[{Ws}]+$', '', 'g') AS nm,
                   s.jl, s.sj, s.mj, s.cl
            FROM "AspNetUsers" u
            CROSS JOIN LATERAL (VALUES
                (0, u."CharacterName"::text,     u."JobLevels",     u."StrongJobs",     u."MeritJobs",     u."CraftLevels"),
                (1, u."AltCharacterName1"::text, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels"),
                (2, u."AltCharacterName2"::text, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
            ) AS s(slot, nm, jl, sj, mj, cl)
        ), kept AS (
            SELECT slots.*, row_number() OVER (PARTITION BY account, lower(nm) ORDER BY slot) AS rn
            FROM slots
            WHERE nm IS NOT NULL
              AND nm ~ '[^{Ws}]'
              AND char_length(nm) <= {Character.MaxNameLength}
        ), desired AS (
            SELECT * FROM kept WHERE rn = 1
        )
        SELECT count(DISTINCT coalesce(d.account, c."AppUserId"))::int AS "Value"
        FROM desired d
        FULL JOIN "Characters" c ON c."AppUserId" = d.account AND c."Slot" = d.slot
        WHERE c."Id" IS NULL OR d.account IS NULL
           OR (c."Name"::text, c."NormalizedName"::text, c."JobLevels", c."StrongJobs", c."MeritJobs", c."CraftLevels")
              IS DISTINCT FROM (d.nm, lower(d.nm), d.jl, d.sj, d.mj, d.cl)
        """;

    private const string MainsSql = """
        SELECT count(*)::int AS "Value"
        FROM "AspNetUsers" u
        LEFT JOIN "Characters" c ON c."AppUserId" = u."Id" AND c."Slot" = 0
        WHERE u."CharacterName" IS DISTINCT FROM c."Name"
        """;

    private const string RosterNamesSql = """
        SELECT count(*)::int AS "Value"
        FROM "AppUserLinkshells" m
        JOIN "AspNetUsers" u ON u."Id" = m."AppUserId"
        WHERE m."CharacterName" IS DISTINCT FROM u."CharacterName"
        """;

    public static async Task<Result> CountAsync(ApplicationDbContext db, CancellationToken cancellationToken) =>
        new Result(
            await CountAsync(db, AccountsSql, cancellationToken),
            await CountAsync(db, MainsSql, cancellationToken),
            await CountAsync(db, RosterNamesSql, cancellationToken));

    private static Task<int> CountAsync(ApplicationDbContext db, string sql, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<int>(sql).SingleAsync(cancellationToken);
}
