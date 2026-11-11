using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P2.5: "Pay newly registered" on a posted camp card. Two officers pressing it at the same moment
// take turns on the card's row lock: the newcomer is paid once and added to the camp's Past Event
// once. Without the lock both presses add her to the Past Event, and the second fails its unique
// index on (EventHistoryId, AppUserId).
[Collection("postgres")]
public class PayNewlyRegisteredTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public PayNewlyRegisteredTests(PostgresFixture pg) => _pg = pg;

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

    // A camp card priced at 10 with Alice and Carol on it, posted while only Alice was registered;
    // then Carol registers. Returns the card id.
    private async Task<int> SeedPostedCampThenRegisterCarolAsync()
    {
        int cardId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                Linkshell = linkshell, AppUserId = "alice", CharacterName = "Alice", LinkshellDkp = 0,
            });
            var history = new EventHistory
            {
                Linkshell = linkshell,
                EventName = "Fafnir",
                EventType = "HNM",
                StartTime = Now,
                EndTime = Now.AddHours(1),
            };
            db.EventHistories.Add(history);
            var card = new WindowEvent
            {
                Linkshell = linkshell,
                Name = "Fafnir",
                Status = WindowEventStatuses.Closed,
                CreatedAtUtc = Now,
                FirstCapturedAtUtc = Now,
                LastCapturedAtUtc = Now,
                EntryType = WindowEventEntryTypes.KingsCamp,
                DkpAmount = 10d,
                CampStartedAtUtc = Now,
                CampEndedAtUtc = Now.AddHours(1),
                CampEventType = "HNM",
                CampEventHistory = history,
            };
            db.WindowEvents.Add(card);
            db.AttendanceSnapshots.Add(new AttendanceSnapshot
            {
                Linkshell = linkshell,
                WindowEvent = card,
                CapturedAtUtc = Now,
                CreatedAtUtc = Now,
                SnapshotStatus = AttendanceSnapshotStatuses.Active,
                EntryCount = 2,
                Entries =
                {
                    new AttendanceSnapshotEntry { CharacterName = "Alice" },
                    new AttendanceSnapshotEntry { CharacterName = "Carol" },
                },
            });
            await db.SaveChangesAsync();
            cardId = card.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            var card = await db.WindowEvents.SingleAsync(w => w.Id == cardId);
            Assert.Equal(1, await NewLedgerService(db).PostAndPayAsync(card, Now.AddHours(2), CancellationToken.None));
        }

        await using (var db = _pg.CreateContext())
        {
            var linkshellId = await db.Linkshells.Select(l => l.Id).SingleAsync();
            db.Users.Add(new AppUser { Id = "carol", UserName = "carol", CharacterName = "Carol" });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = linkshellId, AppUserId = "carol", CharacterName = "Carol", LinkshellDkp = 0,
            });
            await db.SaveChangesAsync();
        }

        return cardId;
    }

    private async Task<int?> PressAsync(int cardId, params IInterceptor[] interceptors)
    {
        await using var db = _pg.CreateContext(interceptors);
        return await NewLedgerService(db).PayNewlyRegisteredAsync(cardId, CancellationToken.None);
    }

    // Officer A's press is held just after its first save (the Past Event sync, inside the lock).
    // Officer B presses meanwhile and must wait; once A commits, B finds Carol paid and pays nobody.
    [Fact]
    public async Task TwoPressesAtOnce_PayTheNewcomerOnce()
    {
        var cardId = await SeedPostedCampThenRegisterCarolAsync();

        // The attendance payload's count runs as real SQL here: it lists the card, owing Carol.
        Assert.Equal(1, Assert.Single(await OwingAsync()).Value);

        Task<int?>? officerB = null;
        var bWasWaiting = false;
        var pauseAfterFirstSave = new OnFirstSave(async () =>
        {
            officerB = PressAsync(cardId);
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            bWasWaiting = !officerB.IsCompleted;
        });

        var paidByA = await PressAsync(cardId, pauseAfterFirstSave);
        var paidByB = await officerB!;

        Assert.True(bWasWaiting, "Officer B's press should wait on the card until officer A's commits.");
        Assert.Equal(1, paidByA);
        Assert.Equal(0, paidByB);

        await using var check = _pg.CreateContext();
        var earned = await check.DkpLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == "SnapshotEarned")
            .ToListAsync();
        Assert.Single(earned, e => e.AppUserId == "alice");
        Assert.Single(earned, e => e.AppUserId == "carol");
        Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), m => Assert.Equal(10d, m.LinkshellDkp));
        Assert.Single(await check.AppUserEventHistories.AsNoTracking().Where(r => r.AppUserId == "carol").ToListAsync());
        Assert.Empty(await OwingAsync());
    }

    private async Task<Dictionary<int, int>> OwingAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshellId = await db.Linkshells.Select(l => l.Id).SingleAsync();
        return await NewLedgerService(db).CountUnpaidAccountsAsync(linkshellId, Now.AddDays(-1), CancellationToken.None);
    }

    // Many presses with no pause: whatever the interleaving, Carol is paid exactly once.
    [Fact]
    public async Task ManyPressesAtOnce_PayTheNewcomerOnce()
    {
        var cardId = await SeedPostedCampThenRegisterCarolAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PressAsync(cardId)));

        Assert.Equal(1, results.Sum(r => r ?? 0));
        await using var check = _pg.CreateContext();
        Assert.Equal(10d, (await check.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.AppUserId == "carol")).LinkshellDkp);
        Assert.Equal(1, await check.DkpLedgerEntries.CountAsync(e => e.AppUserId == "carol"));
    }

    // Runs `then` the first time this context saves.
    private sealed class OnFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public OnFirstSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }
}
