using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.7: Past Events read each attendee's DKP from the ledger (PastEventDkp). The in-memory tests
// cover the rules through the real routes; this proves the reads (the event-id and entry-type lists,
// the audit chain walk) translate and run on Postgres, across several events in one call.
[Collection("postgres")]
public class PastEventDkpTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public PastEventDkpTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EarnedRowsPlusTheirAudits_PerAttendee_LootLeftOut()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        int timedId, campId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.AddRange(
                new AppUser { Id = "user-alpha", UserName = "alpha", CharacterName = "Alpha" },
                new AppUser { Id = "user-bravo", UserName = "bravo", CharacterName = "Bravo" });
            var timed = new EventHistory
            {
                Linkshell = linkshell, EventName = "Sky", EndTime = now,
                AppUserEventHistories =
                {
                    new AppUserEventHistory { AppUserId = "user-alpha", CharacterName = "Alpha", EventDkp = 10 },
                    new AppUserEventHistory { AppUserId = "user-bravo", CharacterName = "Bravo", EventDkp = 10 },
                },
            };
            var camp = new EventHistory
            {
                Linkshell = linkshell, EventName = "Fafnir", EndTime = now,
                AppUserEventHistories =
                {
                    new AppUserEventHistory { AppUserId = "user-alpha", CharacterName = "Alpha", EventDkp = 1 },
                    new AppUserEventHistory { CharacterName = "Guest", EventDkp = 3 },
                },
            };
            db.EventHistories.AddRange(timed, camp);
            await db.SaveChangesAsync();
            timedId = timed.Id;
            campId = camp.Id;

            DkpLedgerEntry Row(string user, string type, double amount, int? historyId, DkpLedgerEntry? corrects = null) => new()
            {
                AppUserId = user, LinkshellId = linkshell.Id, EntryType = type, Amount = amount, OccurredAt = now,
                EventHistoryId = historyId, AuditRelatedLedgerEntryId = corrects?.Id,
            };

            var alphaEarned = Row("user-alpha", "EventEarned", 10, timedId);
            var bravoEarned = Row("user-bravo", "EventEarned", 10, timedId);
            var alphaLoot = Row("user-alpha", "LootSpent", -4, timedId);
            var alphaCamp = Row("user-alpha", "SnapshotEarned", 5, campId);
            db.DkpLedgerEntries.AddRange(alphaEarned, bravoEarned, alphaLoot, alphaCamp);
            await db.SaveChangesAsync();

            // Bravo audited 10 -> 8 by an old audit filed under no event, then corrected again to 7.
            var firstAudit = Row("user-bravo", "AuditAdjustment", -2, null, bravoEarned);
            // An audit of Alpha's LOOT charge, filed under the event: spending, not earnings.
            var lootAudit = Row("user-alpha", "AuditAdjustment", 1, timedId, alphaLoot);
            // Alpha's camp payout audited 5 -> 6.
            var campAudit = Row("user-alpha", "AuditAdjustment", 1, campId, alphaCamp);
            db.DkpLedgerEntries.AddRange(firstAudit, lootAudit, campAudit);
            await db.SaveChangesAsync();
            db.DkpLedgerEntries.Add(Row("user-bravo", "AuditAdjustment", -1, null, firstAudit));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var totals = await PastEventDkp.LoadAsync(db, new[] { timedId, campId }, CancellationToken.None);
            var attendees = await db.AppUserEventHistories.AsNoTracking().ToListAsync();
            double? Shown(int historyId, string name) =>
                totals.For(attendees.Single(a => a.EventHistoryId == historyId && a.CharacterName == name));

            Assert.Equal(10, Shown(timedId, "Alpha"));
            Assert.Equal(7, Shown(timedId, "Bravo"));
            Assert.Equal(6, Shown(campId, "Alpha"));
            Assert.Equal(3, Shown(campId, "Guest"));
            Assert.Equal(9, totals.TotalFor(attendees.Where(a => a.EventHistoryId == campId)));
        }
    }
}
