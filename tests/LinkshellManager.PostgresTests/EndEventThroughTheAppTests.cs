using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.5: the app's End Event now runs the shared close (EventEndService.EndEventCoreAsync with
// EndEventOptions.Activity) instead of its own copy. This runs it on real Postgres, where the
// foreign keys and unique indexes are enforced: one history row per account, the late board
// sign-up's never-saved row dropped cleanly, loot and posted windows moved onto the past event
// before the event row is deleted (the window FK cascades), and every balance equal to its ledger.
[Collection("postgres")]
public class EndEventThroughTheAppTests : IAsyncLifetime
{
    private const string Officer = "officer";

    private readonly PostgresFixture _pg;

    public EndEventThroughTheAppTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // The app's controller with only what End Event touches, and a signed-in officer.
    private static ActivityDataController NewActivityController(ApplicationDbContext db)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var permissions = new LinkshellPermissions(
            db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, Officer) }, authenticationType: "Test")),
        };
        return new ActivityDataController(
            db, null!, null!, null!, null!, userManager, null!, null!, null!, null!, null!, null!,
            NullLogger<ActivityDataController>.Instance, null!, null!, new MemberActivityService(db), null!,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            permissions)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    // A 3-window event: alice in all three windows (wins an item on her alt), an outside sign-up in
    // one, and dave on the party board only (brought in at close).
    private async Task<(int LinkshellId, int EventId)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.AddRange(
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice", AltCharacterName1 = "Alicealt" },
            new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "alice", CharacterName = "Alice", Rank = LinkshellRanks.Member, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "dave", CharacterName = "Dave", Rank = LinkshellRanks.Member, LinkshellDkp = 0 });

        var setup = new PartySetup { LinkshellId = linkshell.Id, Name = "Dynamis" };
        var slot = new PartySetupSlot { SortOrder = 0 };
        setup.Alliances.Add(new PartySetupAlliance
        {
            Name = "Alliance A",
            Parties = { new PartySetupParty { Name = "Party 1", Slots = { slot } } },
        });
        db.PartySetups.Add(setup);

        var start = DateTime.UtcNow.AddHours(-2);
        var ev = new Event
        {
            LinkshellId = linkshell.Id, EventName = "Dynamis - Xarcabard", EventType = "Dynamis",
            StartTime = start, CommencementStartTime = start, DkpPerHour = 10, WindowCountOverride = 3,
            CountsTowardActive = true, PartySetup = setup,
        };
        var alice = new AppUserEvent { AppUserId = "alice", CharacterName = "Alice", IsVerified = true, StartTime = start };
        var guest = new AppUserEvent { DiscordUserId = "555", CharacterName = "Guest", StartTime = start };
        ev.AppUserEvents.Add(alice);
        ev.AppUserEvents.Add(guest);
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        for (var n = 1; n <= 3; n++)
        {
            var window = new EventAttendanceWindow { EventId = ev.Id, SequenceNumber = n, PostedAt = start.AddMinutes(30 * n) };
            window.Attendees.Add(new AppUserEventWindow { AppUserEventId = alice.Id, AppUserId = "alice" });
            if (n == 2)
            {
                window.Attendees.Add(new AppUserEventWindow { AppUserEventId = guest.Id });
            }
            db.EventAttendanceWindows.Add(window);
        }
        db.EventPartySlotSignups.Add(new EventPartySlotSignup
        {
            EventId = ev.Id, PartySetupSlotId = slot.Id, AppUserId = "dave", CharacterName = "Dave", SignedUpAtUtc = start,
        });
        db.EventLootDetails.Add(new EventLootDetail
        {
            EventId = ev.Id, LinkshellId = linkshell.Id, ItemName = "Kraken Club", ItemWinner = "Alicealt", WinningDkpSpent = 20,
        });
        await db.SaveChangesAsync();
        return (linkshell.Id, ev.Id);
    }

    [Fact]
    public async Task EndEvent_ArchivesAndPaysOnce_AndEveryBalanceMatchesItsLedger()
    {
        var (linkshellId, eventId) = await SeedAsync();

        await using (var db = _pg.CreateContext())
        {
            var action = await NewActivityController(db).EndEventAsync(eventId, CancellationToken.None);
            Assert.IsType<OkObjectResult>(action);
        }

        await using var check = _pg.CreateContext();
        Assert.False(await check.Events.AnyAsync(e => e.Id == eventId));
        Assert.Empty(await check.AppUserEvents.ToListAsync());

        var history = await check.EventHistories.Include(h => h.AppUserEventHistories).SingleAsync();
        // One row per account; the outside sign-up gets none.
        Assert.Equal(new[] { "alice", "dave" },
            history.AppUserEventHistories.Select(r => r.AppUserId).OrderBy(id => id).ToArray());
        Assert.Equal(3, history.AppUserEventHistories.Single(r => r.AppUserId == "alice").WindowsAttended);

        // The windows and their roster rows now belong to the past event.
        var windows = await check.EventAttendanceWindows.ToListAsync();
        Assert.Equal(3, windows.Count);
        Assert.All(windows, w => { Assert.Null(w.EventId); Assert.Equal(history.Id, w.EventHistoryId); });
        Assert.Equal(4, await check.AppUserEventWindows.CountAsync());

        // Loot won on the alt is charged to the account and moved onto the past event.
        var loot = await check.EventLootDetails.SingleAsync();
        Assert.Null(loot.EventId);
        Assert.Equal(history.Id, loot.EventHistoryId);
        Assert.Equal(20, loot.ActualDeductedDkp);

        // Alice: 3 windows x 10 - 20; Dave: in no window.
        var balances = await check.AppUserLinkshells.Where(m => m.LinkshellId == linkshellId)
            .ToDictionaryAsync(m => m.AppUserId!, m => m.LinkshellDkp ?? 0);
        Assert.Equal(10, balances["alice"]);
        Assert.Equal(0, balances["dave"]);
        foreach (var (appUserId, balance) in balances)
        {
            var ledger = await check.DkpLedgerEntries
                .Where(e => e.LinkshellId == linkshellId && e.AppUserId == appUserId)
                .SumAsync(e => e.Amount);
            Assert.Equal(balance, ledger);
        }
    }
}
