using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Loot winners used to be matched by the typed name only. Loot given to a name nobody has was saved
// with a price and never charged, and a winner who renamed lost the link to what they had paid.
// Rows now record the winning ACCOUNT (EventLootDetail.ItemWinnerAppUserId) when the loot is
// recorded, and charging and refunds go by it.
public class LootWinnerAccountTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) => new(db, new DkpPoolProvisioner(db));

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, NewPools(db), NullLogger<DkpLedgerWriter>.Instance);

    private static LootEditService NewLootEdits(ApplicationDbContext db) =>
        new(db, NewWriter(db), NewPools(db), NullLogger<LootEditService>.Instance);

    // u1 won the item as "Oldname" and has since renamed to "Newname". u2 has taken "Oldname".
    private static void SeedRenamedWinner(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "one", CharacterName = "Newname" });
        db.Users.Add(new AppUser { Id = "u2", UserName = "two", CharacterName = "Oldname" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "u1", CharacterName = "Newname", LinkshellDkp = 100 });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Oldname", LinkshellDkp = 100 });
    }

    private static double Dkp(ApplicationDbContext db, string appUserId) =>
        db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == appUserId).LinkshellDkp ?? 0;

    [Fact]
    public async Task TheClose_ChargesTheRecordedAccount_NotWhoeverHasTheNameNow()
    {
        await using var db = NewDb();
        SeedRenamedWinner(db);
        var start = DateTime.UtcNow.AddHours(-1);
        db.Events.Add(new Event
        {
            Id = 100, LinkshellId = Ls, EventName = "Sky", EventType = "Sky",
            StartTime = start, CommencementStartTime = start, DkpPerHour = 0,
        });
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 60, EventId = 100, ItemName = "Ridill", ItemWinner = "Oldname", ItemWinnerAppUserId = "u1", WinningDkpSpent = 30,
        });
        db.SaveChanges();
        var ev = db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .First(e => e.Id == 100);

        await EventEndService.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev);

        Assert.Equal(70, Dkp(db, "u1"));
        Assert.Equal(100, Dkp(db, "u2"));
    }

    [Fact]
    public void ARecordedAccountThatLeft_ChargesNobody_NotANamesake()
    {
        var namesake = new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Oldname" };
        var loot = new EventLootDetail { ItemWinner = "Oldname", ItemWinnerAppUserId = "gone" };

        var winner = EventEndService.ResolveLootWinnerMembership(
            loot,
            new Dictionary<string, AppUserLinkshell> { ["u2"] = namesake },
            new Dictionary<string, AppUserEvent>(),
            CharacterResolver.ForMemberships(new[] { namesake }));

        Assert.Null(winner);
    }

    [Fact]
    public void ARowWithoutAnAccount_StillMatchesByName()
    {
        var member = new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Oldname" };

        var winner = EventEndService.ResolveLootWinnerMembership(
            new EventLootDetail { ItemWinner = "oldname" },
            new Dictionary<string, AppUserLinkshell> { ["u2"] = member },
            new Dictionary<string, AppUserEvent>(),
            CharacterResolver.ForMemberships(new[] { member }));

        Assert.Same(member, winner);
    }

    [Fact]
    public async Task DeletingLoot_RefundsTheAccountThatPaid()
    {
        await using var db = NewDb();
        SeedRenamedWinner(db);
        db.EventHistories.Add(new EventHistory { Id = 50, LinkshellId = Ls, EventName = "Sky", EventType = "Sky" });
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 60, EventHistoryId = 50, LinkshellId = Ls, ItemName = "Ridill", ItemWinner = "Oldname",
            ItemWinnerAppUserId = "u1", WinningDkpSpent = 30, ActualDeductedDkp = 30, DkpDebitedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var actor = db.Users.Single(u => u.Id == "u1");

        var result = await NewLootEdits(db).DeleteEventLootAsync(60, actor, "Wrong item", DateTime.UtcNow);

        Assert.True(result.Success);
        Assert.Equal("u1", Assert.Single(db.DkpLedgerEntries).AppUserId);
    }

    [Fact]
    public async Task EditingTheWinner_RecordsTheNewAccount_AndAPriceFixKeepsTheOld()
    {
        await using var db = NewDb();
        SeedRenamedWinner(db);
        db.EventHistories.Add(new EventHistory { Id = 50, LinkshellId = Ls, EventName = "Sky", EventType = "Sky" });
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 60, EventHistoryId = 50, LinkshellId = Ls, ItemName = "Ridill", ItemWinner = "Oldname",
            ItemWinnerAppUserId = "u1", WinningDkpSpent = 30, ActualDeductedDkp = 30, DkpDebitedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var actor = db.Users.Single(u => u.Id == "u1");
        var edits = NewLootEdits(db);

        // Same name, new price: still u1 (who renamed), not u2 who now has the name.
        var priceFix = await edits.EditEventLootAsync(
            60, new LootEditRequest("Ridill", "Oldname", 20, "Price"), actor, DateTime.UtcNow);
        Assert.True(priceFix.Success, priceFix.ErrorMessage);
        Assert.Equal(110, Dkp(db, "u1"));
        Assert.Equal(100, Dkp(db, "u2"));
        Assert.Equal("u1", db.EventLootDetails.Single().ItemWinnerAppUserId);

        // A different name is a different winner.
        var moved = await edits.EditEventLootAsync(
            60, new LootEditRequest("Ridill", "Newname", 20, "Typo"), actor, DateTime.UtcNow);
        Assert.True(moved.Success, moved.ErrorMessage);
        Assert.Equal("u1", db.EventLootDetails.Single().ItemWinnerAppUserId);

        var unknown = await edits.EditEventLootAsync(
            60, new LootEditRequest("Ridill", "Nobody", 20, "Typo"), actor, DateTime.UtcNow);
        Assert.False(unknown.Success);
    }

    [Fact]
    public async Task HandEnteredLoot_RecordsTheWinnerAccount()
    {
        await using var db = NewDb();
        SeedRenamedWinner(db);
        db.SaveChanges();
        var pools = NewPools(db);
        var manual = new ManualLootService(db, NewWriter(db), pools, new DkpPoolBalanceService(db, pools));

        var result = await manual.AddAsync(
            Ls, ManualLootTarget.None, "Ridill", "newname", 10, null, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal("u1", db.EventLootDetails.Single().ItemWinnerAppUserId);
    }

    [Theory]
    [InlineData("Newname", "u1")]
    [InlineData("  newname ", "u1")]
    [InlineData("Alty", "u1")]
    [InlineData("Nobody", null)]
    [InlineData("", null)]
    public async Task GuardResolvesAWinnerByMainOrAlt(string typed, string? expected)
    {
        await using var db = NewDb();
        SeedRenamedWinner(db);
        db.SaveChanges();
        db.Users.Single(u => u.Id == "u1").AltCharacterName1 = "Alty";
        db.SaveChanges();

        var winner = await LootDkpGuard.ResolveWinnerAsync(db, Ls, typed, CancellationToken.None);

        Assert.Equal(expected, winner?.AppUserId);
    }

    // AddLootAsync needs a signed-in HTTP context, so its winner rule is pinned by source: it
    // resolves the typed name to a member, refuses one it cannot find, and records the account and
    // the linkshell.
    [Fact]
    public void LiveCardAddLoot_RefusesUnknownWinners_AndRecordsTheAccount()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "Controllers", "Activity", "Events", "ActivityEventsController.EventsParticipation.cs"));
        var start = source.IndexOf("public async Task<IActionResult> AddLootAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "AddLootAsync not found -- did it move?");
        var body = source[start..];

        Assert.Contains("LootDkpGuard.ResolveWinnerAsync(", body, StringComparison.Ordinal);
        Assert.Contains("Winner must be a current linkshell member.", body, StringComparison.Ordinal);
        Assert.Contains("ItemWinnerAppUserId = winner?.AppUserId", body, StringComparison.Ordinal);
        Assert.Contains("LinkshellId = eventEntity.LinkshellId", body, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
