using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.11: loot posted after End Camp goes to the camp's archive and is charged now. The in-memory
// tests cover the rule; this one proves the "is it running?" filter and the board -> newest
// archive lookup translate to real SQL, and that the charge lands in the ledger on Postgres.
[Collection("postgres")]
public class LootAfterEndCampTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public LootAfterEndCampTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LootOnAParkedBoard_IsFiledUnderItsNewestCamp_AndCharged()
    {
        var now = DateTime.UtcNow;
        int linkshellId, boardId, runningId, newestArchiveId;
        await using (var db = _pg.CreateContext())
        {
            var ls = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
            db.Linkshells.Add(ls);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            await db.SaveChangesAsync();
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = ls.Id, AppUserId = "alice", CharacterName = "Alice", LinkshellDkp = 100,
            });
            // Parked by End Camp, and a running camp beside it.
            var board = new Event
            {
                LinkshellId = ls.Id, EventName = "Fafnir", EventType = "HNM", AssignedMonsterName = "Fafnir",
                WdFinalizedAt = now.AddMinutes(-5), StartTime = now.AddHours(20),
            };
            var running = new Event
            {
                LinkshellId = ls.Id, EventName = "Behemoth", EventType = "HNM", CommencementStartTime = now.AddHours(-1),
            };
            db.Events.AddRange(board, running);
            var older = new EventHistory { LinkshellId = ls.Id, EventName = "Fafnir", EndTime = now.AddDays(-3) };
            var newest = new EventHistory { LinkshellId = ls.Id, EventName = "Fafnir", EndTime = now.AddMinutes(-5) };
            db.EventHistories.AddRange(older, newest);
            await db.SaveChangesAsync();
            foreach (var (history, endedAt) in new[] { (older, now.AddDays(-3)), (newest, now.AddMinutes(-5)) })
            {
                db.WindowEvents.Add(new WindowEvent
                {
                    LinkshellId = ls.Id, Name = "Fafnir", SourceEventId = board.Id, CampEventHistoryId = history.Id,
                    CampEndedAtUtc = endedAt, CreatedAtUtc = endedAt, FirstCapturedAtUtc = endedAt, LastCapturedAtUtc = endedAt,
                });
            }
            await db.SaveChangesAsync();
            (linkshellId, boardId, runningId, newestArchiveId) = (ls.Id, board.Id, running.Id, newest.Id);
        }

        await using (var db = _pg.CreateContext())
        {
            var live = await db.Events.Where(e => e.LinkshellId == linkshellId)
                .Where(EventRunState.Running).Select(e => e.Id).ToListAsync();
            Assert.Equal([runningId], live);

            var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
            var service = new ManualLootService(
                db,
                new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
                pools,
                new DkpPoolBalanceService(db, pools));
            var result = await service.AddAsync(
                linkshellId, ManualLootTarget.Parse("live", boardId, null), "Ridill", "Alice", 30, null, null,
                CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        await using (var db = _pg.CreateContext())
        {
            var loot = await db.EventLootDetails.AsNoTracking().SingleAsync();
            Assert.Null(loot.EventId);
            Assert.Equal(newestArchiveId, loot.EventHistoryId);
            Assert.NotNull(loot.DkpDebitedAt);
            var debit = await db.DkpLedgerEntries.AsNoTracking().SingleAsync();
            Assert.Equal(-30, debit.Amount);
            Assert.Equal(newestArchiveId, debit.EventHistoryId);
        }
    }
}
