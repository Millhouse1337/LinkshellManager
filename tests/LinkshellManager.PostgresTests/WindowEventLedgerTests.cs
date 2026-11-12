using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Post and Update of a review card on the real database: one SnapshotEarned row per account, and
// Update leaves an officer's audit correction alone. The in-memory tests cover the same rules;
// this proves the queries (the EntryType filter, the audit lookup) translate and run on Postgres.
[Collection("postgres")]
public class WindowEventLedgerTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public WindowEventLedgerTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static WindowEventDkpLedgerService NewLedgerService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new WindowEventDkpLedgerService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<WindowEventDkpLedgerService>.Instance);
    }

    [Fact]
    public async Task MainAndAltPaidOnce_AndUpdateKeepsTheAudit()
    {
        var now = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        int cardId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser
            {
                Id = "user-alpha", UserName = "alpha", CharacterName = "Alpha", AltCharacterName1 = "Alphalt",
            });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Linkshell = linkshell, AppUserId = "user-alpha", CharacterName = "Alpha", LinkshellDkp = 0,
            });
            var card = new WindowEvent
            {
                Linkshell = linkshell,
                Name = "Kings",
                Status = WindowEventStatuses.Open,
                CreatedAtUtc = now,
                FirstCapturedAtUtc = now,
                LastCapturedAtUtc = now,
                EntryType = WindowEventEntryTypes.KingsCamp,
                DkpAmount = 10d,
                PostedToSheetAt = now,
            };
            db.WindowEvents.Add(card);
            foreach (var name in new[] { "Alpha", "Alphalt" })
            {
                db.AttendanceSnapshots.Add(new AttendanceSnapshot
                {
                    Linkshell = linkshell,
                    WindowEvent = card,
                    CapturedAtUtc = now,
                    SnapshotStatus = AttendanceSnapshotStatuses.Active,
                    EntryCount = 1,
                    Entries = { new AttendanceSnapshotEntry { CharacterName = name } },
                });
            }
            await db.SaveChangesAsync();
            cardId = card.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            await NewLedgerService(db).EnsurePostedWindowEventLedgerEntriesAsync(cardId, CancellationToken.None);
        }

        // An officer audits Alpha's 10 down to 8.
        await using (var db = _pg.CreateContext())
        {
            var earned = await db.DkpLedgerEntries.SingleAsync(e => e.EntryType == "SnapshotEarned");
            Assert.Equal(10d, earned.Amount);
            db.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = "user-alpha",
                LinkshellId = earned.LinkshellId,
                EntryType = "AuditAdjustment",
                Amount = -2d,
                OccurredAt = now,
                EventType = earned.EventType,
                DkpPoolId = earned.DkpPoolId,
                SourceWindowEventId = cardId,
                AuditRelatedLedgerEntryId = earned.Id,
            });
            var membership = await db.AppUserLinkshells.SingleAsync();
            membership.LinkshellDkp = 8d;
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            await NewLedgerService(db).ReconcilePostedWindowEventLedgerAsync(cardId, CancellationToken.None);
        }

        await using (var db = _pg.CreateContext())
        {
            var rows = await db.DkpLedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
            Assert.Equal(new[] { "SnapshotEarned", "AuditAdjustment" }, rows.Select(r => r.EntryType).ToArray());
            Assert.Equal(new[] { 10d, -2d }, rows.Select(r => r.Amount).ToArray());
            Assert.Equal(8d, (await db.AppUserLinkshells.AsNoTracking().SingleAsync()).LinkshellDkp);
        }
    }
}
