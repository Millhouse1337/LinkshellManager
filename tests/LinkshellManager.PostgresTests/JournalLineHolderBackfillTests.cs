using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The FillJournalLineHolderAccounts migration fills JournalEntryLines.HolderAppUserId on older
// gil-on-hand lines from the holder's name, only when exactly one member of that line's own
// linkshell has the name. Then "Who's holding the gil" groups by member.
[Collection("postgres")]
public class JournalLineHolderBackfillTests : IAsyncLifetime
{
    private static readonly TreasuryActor Officer = new("officer", "Millhouse");

    // The read-only pre-check the owner runs on production before deploying: how many lines the
    // backfill would fill, and how many it leaves NULL (and why). Pinned here so the numbers it
    // prints are the numbers the migration really produces.
    public const string PreCheckSql = """
        WITH lines AS (
            SELECT l."Id" AS line_id,
                   l."LinkshellId" AS linkshell_id,
                   lower(btrim(l."HolderCharacterName")) AS holder
            FROM "JournalEntryLines" l
            WHERE l."HolderAppUserId" IS NULL
              AND l."AccountNumber" = 1000
              AND l."HolderCharacterName" IS NOT NULL
              AND btrim(l."HolderCharacterName") <> ''
        ),
        matches AS (
            SELECT DISTINCT lines.line_id, m."AppUserId" AS app_user_id
            FROM lines
            JOIN "AppUserLinkshells" m ON m."LinkshellId" = lines.linkshell_id AND m."AppUserId" IS NOT NULL
            JOIN "AspNetUsers" u ON u."Id" = m."AppUserId"
            WHERE lower(btrim(m."CharacterName")) = lines.holder
               OR lower(btrim(u."CharacterName")) = lines.holder
               OR lower(btrim(u."AltCharacterName1")) = lines.holder
               OR lower(btrim(u."AltCharacterName2")) = lines.holder
        ),
        per_line AS (
            SELECT lines.line_id, COUNT(matches.app_user_id) AS accounts
            FROM lines
            LEFT JOIN matches ON matches.line_id = lines.line_id
            GROUP BY lines.line_id
        )
        SELECT
            COUNT(*) FILTER (WHERE accounts = 1) AS would_fill,
            COUNT(*) FILTER (WHERE accounts = 0) AS left_null_no_match,
            COUNT(*) FILTER (WHERE accounts > 1) AS left_null_ambiguous
        FROM per_line
        """;

    private readonly PostgresFixture _pg;

    public JournalLineHolderBackfillTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static TreasuryJournalWriter NewWriter(ApplicationDbContext db) =>
        new(db, new LedgerAccountProvisioner(db));

    private static TreasuryEntryRequest Sale(long amount, string holder, string? holderAccount = null) =>
        new(TreasuryTransactionKinds.SoldAnItem, amount, new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            HolderAppUserId: holderAccount, HolderCharacterName: holder);

    private sealed record PreCheck(long WouldFill, long LeftNullNoMatch, long LeftNullAmbiguous);

    private static async Task<PreCheck> RunPreCheckAsync(ApplicationDbContext db)
    {
        var row = Assert.Single(await db.Database.SqlQueryRaw<PreCheckRow>(PreCheckSql).ToListAsync());
        return new PreCheck(row.WouldFill, row.LeftNullNoMatch, row.LeftNullAmbiguous);
    }

    private sealed class PreCheckRow
    {
        [Column("would_fill")] public long WouldFill { get; set; }
        [Column("left_null_no_match")] public long LeftNullNoMatch { get; set; }
        [Column("left_null_ambiguous")] public long LeftNullAmbiguous { get; set; }
    }

    [Fact]
    public async Task BackfillSql_FillsOnlyUnambiguousMembersOfTheLinesOwnLinkshell()
    {
        int linkshellId;
        var entries = new Dictionary<string, JournalEntry>();
        await using (var db = _pg.CreateContext())
        {
            var ls = new Linkshell { LinkshellName = "LS" };
            var other = new Linkshell { LinkshellName = "Other" };
            db.Linkshells.AddRange(ls, other);
            await db.SaveChangesAsync();
            linkshellId = ls.Id;
            await new LedgerAccountProvisioner(db).EnsureAccountsAsync(ls.Id, CancellationToken.None);

            // Recorded BEFORE anyone is on the roster, so the writer cannot fill the account: what
            // lines from before Batch U look like.
            var writer = NewWriter(db);
            async Task Record(string key, long amount, string holder, string? account = null) =>
                entries[key] = await writer.RecordAsync(ls.Id, Sale(amount, holder, account), Officer, CancellationToken.None);
            await Record("main", 1_000, " alice ");
            await Record("alt", 2_000, "ALICEALT");
            // Bob's and Carol's alts share a name: ambiguous, left alone.
            await Record("twoAccounts", 4_000, "Twin");
            await Record("unknown", 8_000, "Nobody");
            // Dave exists, but only in the other linkshell.
            await Record("otherShell", 16_000, "Dave");
            // Already recorded: not overwritten.
            await Record("alreadySet", 32_000, "Alice", account: "carol");
            await db.SaveChangesAsync();

            db.Users.AddRange(
                new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice", AltCharacterName1 = "Alicealt" },
                new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName1 = "Twin" },
                new AppUser { Id = "carol", UserName = "carol", CharacterName = "Carol", AltCharacterName1 = "Twin" },
                new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
            await db.SaveChangesAsync();
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "alice", CharacterName = "Alice" },
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "bob", CharacterName = "Bob" },
                new AppUserLinkshell { LinkshellId = ls.Id, AppUserId = "carol", CharacterName = "Carol" },
                new AppUserLinkshell { LinkshellId = other.Id, AppUserId = "dave", CharacterName = "Dave" });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(new PreCheck(WouldFill: 2, LeftNullNoMatch: 2, LeftNullAmbiguous: 1), await RunPreCheckAsync(db));

            await db.Database.ExecuteSqlRawAsync(FillJournalLineHolderAccounts.BackfillSql);
            // Idempotent: a second run changes nothing.
            Assert.Equal(0, await db.Database.ExecuteSqlRawAsync(FillJournalLineHolderAccounts.BackfillSql));

            var lines = await db.JournalEntryLines.AsNoTracking().ToListAsync();
            string? HolderAccount(string key) => lines
                .Single(l => l.JournalEntryId == entries[key].Id && l.AccountNumber == TreasuryAccounts.GilOnHand)
                .HolderAppUserId;

            Assert.Equal("alice", HolderAccount("main"));
            Assert.Equal("alice", HolderAccount("alt"));
            Assert.Null(HolderAccount("twoAccounts"));
            Assert.Null(HolderAccount("unknown"));
            Assert.Null(HolderAccount("otherShell"));
            Assert.Equal("carol", HolderAccount("alreadySet"));
            // The other half of every entry never carries a holder.
            Assert.All(lines.Where(l => l.AccountNumber != TreasuryAccounts.GilOnHand), l => Assert.Null(l.HolderAppUserId));

            // Nothing left to fill; the ambiguous and unknown names stay as they were.
            Assert.Equal(new PreCheck(WouldFill: 0, LeftNullNoMatch: 2, LeftNullAmbiguous: 1), await RunPreCheckAsync(db));

            // And the panel: Alice's main and alt are one row now, under her newest name.
            var sheet = await new TreasuryBalanceService(db).GetBalanceSheetAsync(linkshellId, null, null, CancellationToken.None);
            var alice = Assert.Single(sheet.GilHolders, h => h.CharacterName == "ALICEALT");
            Assert.Equal(3_000, alice.Amount);
            Assert.Equal(sheet.Snapshot.CashOnHand, sheet.GilHolders.Sum(h => h.Amount));
        }
    }
}
