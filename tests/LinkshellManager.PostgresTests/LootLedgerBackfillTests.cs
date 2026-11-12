using System;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.10: the FileLootEditLedgerRowsUnderTheirEvent migration files old Loot History refunds and
// edit charges under the past event they belong to, so deleting that event reverses them too.
[Collection("postgres")]
public class LootLedgerBackfillTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public LootLedgerBackfillTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static DkpLedgerEntry Row(
        int linkshellId, string entryType, double amount, int? lootId, int? historyId) => new()
    {
        LinkshellId = linkshellId,
        AppUserId = "alice",
        EntryType = entryType,
        Amount = amount,
        SourceEventLootDetailId = lootId,
        EventHistoryId = historyId,
        OccurredAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task BackfillSql_FilesRefundsUnderTheLootRowsEvent_AndTouchesNothingElse()
    {
        int editRefund, editSpend, deletedRefund, orphanRefund, unrelated;
        int historyId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            await db.SaveChangesAsync();

            var history = new EventHistory { LinkshellId = linkshell.Id, EventName = "Dynamis" };
            db.EventHistories.Add(history);
            await db.SaveChangesAsync();
            historyId = history.Id;

            // A loot row that still exists, edited once in Loot History.
            var loot = new EventLootDetail
            {
                LinkshellId = linkshell.Id, EventHistoryId = history.Id, ItemName = "Aketon", ItemWinner = "Alice",
            };
            db.EventLootDetails.Add(loot);
            await db.SaveChangesAsync();

            var spent = Row(linkshell.Id, "LootSpent", -30, loot.Id, history.Id);
            var r1 = Row(linkshell.Id, "LootEditRefund", 30, loot.Id, null);
            var s1 = Row(linkshell.Id, "LootEditSpent", -20, loot.Id, null);
            // A loot row deleted in Loot History (id 9001 no longer exists); only the close's charge
            // still names its event.
            var goneSpent = Row(linkshell.Id, "LootSpent", -40, 9001, history.Id);
            var goneRefund = Row(linkshell.Id, "LootDeleteRefund", 40, 9001, null);
            // A deleted loot row with nothing left that names an event: stays unfiled.
            var orphan = Row(linkshell.Id, "LootDeleteRefund", 10, 9002, null);
            // Not a loot edit row: left alone even though it shares the loot id.
            var other = Row(linkshell.Id, "AuditAdjustment", 1, loot.Id, null);
            db.DkpLedgerEntries.AddRange(spent, r1, s1, goneSpent, goneRefund, orphan, other);
            await db.SaveChangesAsync();

            editRefund = r1.Id;
            editSpend = s1.Id;
            deletedRefund = goneRefund.Id;
            orphanRefund = orphan.Id;
            unrelated = other.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            var before = await db.DkpLedgerEntries.AsNoTracking().SumAsync(e => e.Amount);
            await db.Database.ExecuteSqlRawAsync(FileLootEditLedgerRowsUnderTheirEvent.BackfillSql);

            var rows = await db.DkpLedgerEntries.AsNoTracking().ToDictionaryAsync(e => e.Id);
            Assert.Equal(historyId, rows[editRefund].EventHistoryId);
            Assert.Equal(historyId, rows[editSpend].EventHistoryId);
            Assert.Equal(historyId, rows[deletedRefund].EventHistoryId);
            Assert.Null(rows[orphanRefund].EventHistoryId);
            Assert.Null(rows[unrelated].EventHistoryId);
            // Labels only: no amount moved.
            Assert.Equal(before, rows.Values.Sum(e => e.Amount));
        }
    }
}
