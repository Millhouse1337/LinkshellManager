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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using LinkshellManager.TestSupport;

namespace LinkshellManager.PostgresTests;

// Loot posted to a camp at the moment End Camp settles it. End Camp reads the board's loot, charges
// it and parks the board; loot saved in between used to stay on the parked board, uncharged, until
// the NEXT camp ended (or for ever). Every loot post on an event now holds the event row and gives
// it a new row version while it saves (EventRunState.HoldForLootAsync), so an End Camp that read
// the board before the loot landed saves nothing and is retried -- and the retry charges the loot.
[Collection("postgres")]
public class EndCampLootRaceTests : IAsyncLifetime
{
    private const string Officer = "officer";

    private readonly PostgresFixture _pg;

    public EndCampLootRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // A running Fafnir camp whose board repeats (so End Camp parks it rather than deleting it), and
    // Alice with 100 DKP. Nobody is on the camp's roster, so End Camp stages no review card and
    // charges the camp's loot straight away.
    private async Task<(int LinkshellId, int BoardId)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS", LootStructure = "Dkp" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        db.DkpPools.Add(DkpPoolDefaults.BuildDefaultPool(linkshell.Id));
        db.Users.AddRange(
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Boss" },
            new AppUser { Id = "alice", UserName = "alice", CharacterName = "Alice" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = Officer, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = linkshell.Id, AppUserId = "alice", CharacterName = "Alice", LinkshellDkp = 100 });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard { LinkshellId = linkshell.Id, MonsterName = "Fafnir", Enabled = true });
        var board = new Event
        {
            LinkshellId = linkshell.Id, EventName = "Fafnir", EventType = "HNM", AssignedMonsterName = "Fafnir",
            StartTime = DateTime.UtcNow.AddHours(-1), CommencementStartTime = DateTime.UtcNow.AddHours(-1),
        };
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return (linkshell.Id, board.Id);
    }

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new HnmCampReviewHandoffService(
            db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<HnmCampReviewHandoffService>.Instance);
    }

    // The Loot History / addon path.
    private async Task<ManualLootResult> PostLootWithServiceAsync(int linkshellId, int boardId)
    {
        await using var db = _pg.CreateContext();
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var service = new ManualLootService(
            db, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools, new DkpPoolBalanceService(db, pools));
        return await service.AddAsync(
            linkshellId, ManualLootTarget.Parse("live", boardId, null), "Ridill", "Alice", 30, null, null, CancellationToken.None);
    }

    // The live camp card's Add loot.
    private async Task<IActionResult> PostLootFromCardAsync(int boardId)
    {
        await using var db = _pg.CreateContext();
        return await NewActivityController(db).AddLootAsync(
            boardId, new ActivityAddLootRequest("Ridill", "Alice", 30),
            CancellationToken.None);
    }

    // End Camp, held just before its save while `postLoot` runs to completion. The loot lands on
    // the running camp (End Camp has not saved yet); End Camp's save must then refuse.
    private async Task EndCampWithLootPostedMeanwhileAsync(int boardId, Func<Task> postLoot)
    {
        var lootDuringEndCamp = new BeforeFirstSave(postLoot);
        await using var db = _pg.CreateContext(lootDuringEndCamp);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => NewHandoff(db).HandOffAndRecycleAsync(boardId, CancellationToken.None));
    }

    private async Task AssertRetriedEndCampChargesTheLootAsync(int boardId)
    {
        // The first End Camp saved nothing: the board is still running, the loot sits on it unpaid.
        await using (var check = _pg.CreateContext())
        {
            Assert.Null((await check.Events.AsNoTracking().SingleAsync()).WdFinalizedAt);
            var waiting = await check.EventLootDetails.AsNoTracking().SingleAsync();
            Assert.Equal(boardId, waiting.EventId);
            Assert.Null(waiting.DkpDebitedAt);
        }

        // Pressed again (or re-run by the retry): it sees the loot and charges it.
        await using (var db = _pg.CreateContext())
        {
            Assert.True(await NewHandoff(db).HandOffAndRecycleAsync(boardId, CancellationToken.None));
        }

        await using var after = _pg.CreateContext();
        Assert.NotNull((await after.Events.AsNoTracking().SingleAsync()).WdFinalizedAt); // parked
        var loot = await after.EventLootDetails.AsNoTracking().SingleAsync();
        Assert.Null(loot.EventId);
        Assert.NotNull(loot.DkpDebitedAt);
        var spent = await after.DkpLedgerEntries.AsNoTracking().SingleAsync(e => e.EntryType == "LootSpent");
        Assert.Equal(-30d, spent.Amount);
        Assert.Equal(70d, (await after.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.AppUserId == "alice")).LinkshellDkp);
    }

    [Fact]
    public async Task LootFromLootHistoryOrTheAddon_DuringEndCamp_IsChargedByTheRetriedEndCamp()
    {
        var (linkshellId, boardId) = await SeedAsync();

        ManualLootResult? posted = null;
        await EndCampWithLootPostedMeanwhileAsync(boardId, async () =>
            posted = await PostLootWithServiceAsync(linkshellId, boardId));

        Assert.True(posted!.Success, posted.Error);
        await AssertRetriedEndCampChargesTheLootAsync(boardId);
    }

    [Fact]
    public async Task LootFromTheLiveCard_DuringEndCamp_IsChargedByTheRetriedEndCamp()
    {
        var (_, boardId) = await SeedAsync();

        IActionResult? posted = null;
        await EndCampWithLootPostedMeanwhileAsync(boardId, async () =>
            posted = await PostLootFromCardAsync(boardId));

        Assert.IsType<OkObjectResult>(posted);
        await AssertRetriedEndCampChargesTheLootAsync(boardId);
    }

    // After End Camp has parked the board, the live card refuses (the board isn't running), and
    // Loot History files it under the camp -- both read the board as it is once End Camp is done.
    [Fact]
    public async Task LootFromTheLiveCard_AfterEndCamp_IsRefused()
    {
        var (_, boardId) = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            Assert.True(await NewHandoff(db).HandOffAndRecycleAsync(boardId, CancellationToken.None));
        }

        var posted = await PostLootFromCardAsync(boardId);

        Assert.IsType<BadRequestObjectResult>(posted);
        await using var check = _pg.CreateContext();
        Assert.Empty(await check.EventLootDetails.AsNoTracking().ToListAsync());
    }

    // The app's controller with only what Add loot touches, and a signed-in Leader.
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
        return ActivityControllerFactory.Create<ActivityDataController>(
            http, db, userManager, new MemberActivityService(db),
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance), pools,
            new DkpPoolBalanceService(db, pools),
            permissions);
    }

    // Runs `then` once, just before this context's first save.
    private sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeFirstSave(Func<Task> then) => _then = then;

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
