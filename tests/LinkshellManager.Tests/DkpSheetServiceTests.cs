using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Guards the DKP sheet's Biddable column against drifting from the canonical
// AuctionDkpService formula. Biddable must = current − locked bids − pending live-event
// loot spend, so the sheet never overstates what a member can bid mid-event.
public class DkpSheetServiceTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The sheet needs a pool resolver to compute its per-pool columns. With no DkpPools rows the
    // resolver provisions the default "Main" pool on demand, which is the state every linkshell is
    // in until an officer creates a second one — so these tests exercise the single-pool path,
    // where the sheet must look exactly as it did before pools existed.
    private static DkpSheetService NewSheetService(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)));

    [Fact]
    public async Task BuildAsync_Biddable_SubtractsPendingLiveLootSpend()
    {
        using var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "alice" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = 1, AppUserId = "u1", CharacterName = "Alice", LinkshellDkp = 100
        });

        // A live (commenced, not-yet-ended) Dkp event where Alice won 30 DKP of loot that
        // hasn't been committed to the ledger yet — exactly the pending spend that must be
        // blocked from biddable.
        var ev = new Event
        {
            Id = 1, LinkshellId = 1, EventName = "Sky", EventType = "Sky",
            CommencementStartTime = DateTime.UtcNow.AddHours(-1)
        };
        db.Events.Add(ev);
        db.EventLootDetails.Add(new EventLootDetail
        {
            Id = 1, Event = ev, EventId = 1, ItemWinner = "Alice", WinningDkpSpent = 30
        });
        await db.SaveChangesAsync();

        var data = await NewSheetService(db).BuildAsync(1, CancellationToken.None);

        var row = Assert.Single(data.Members);
        Assert.Equal(100, row.Current);
        Assert.Equal(70, row.Biddable); // 100 − 0 locked − 30 pending loot
    }

    [Fact]
    public async Task BuildAsync_Biddable_EqualsCurrent_WhenNothingLockedOrPending()
    {
        using var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = 1, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "u1", UserName = "bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            Id = 1, LinkshellId = 1, AppUserId = "u1", CharacterName = "Bob", LinkshellDkp = 42
        });
        await db.SaveChangesAsync();

        var data = await NewSheetService(db).BuildAsync(1, CancellationToken.None);

        var row = Assert.Single(data.Members);
        Assert.Equal(42, row.Current);
        Assert.Equal(42, row.Biddable);
    }
}
