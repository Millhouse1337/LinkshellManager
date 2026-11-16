using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Round 5 (AddCampSpawnKeys): every save keeps SpawnKey on the camp tables in step with the monster
// name, as it already does for Tods and boards. Events and party setups take it from
// AssignedMonsterName (never EventName), captures and Monster Setups rows from MonsterName. Nothing
// reads the columns yet, so this only has to be right.
public class CampSpawnKeyStampTests
{
    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Theory]
    [InlineData("Nidhogg", "fafnir/nidhogg")]
    [InlineData(" fafnir/NIDHOGG ", "fafnir/nidhogg")]
    [InlineData("King Behemoth", "behemoth/king behemoth")]
    [InlineData("Aspidochelone", "adamantoise/aspidochelone")]
    [InlineData("Foo / Bar", "foo/bar")]
    [InlineData("Tiamat", "tiamat")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public async Task NewCampRows_AreStampedFromTheirMonsterName(string? monster, string? key)
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.Events.Add(new Event { LinkshellId = 1, EventName = "Camp", AssignedMonsterName = monster });
            db.PartySetups.Add(new PartySetup { LinkshellId = 1, Name = "Setup", AssignedMonsterName = monster });
            db.ClaimShieldCaptures.Add(new ClaimShieldCapture { LinkshellId = 1, MonsterName = monster ?? string.Empty });
            db.LinkshellMonsterTimings.Add(new LinkshellMonsterTiming
            {
                LinkshellId = 1, MonsterName = monster ?? string.Empty, NormalizedMonsterName = "n", CooldownMinutes = 1320,
            });
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        Assert.Equal(key, (await check.Events.SingleAsync()).SpawnKey);
        Assert.Equal(key, (await check.PartySetups.SingleAsync()).SpawnKey);
        Assert.Equal(key, (await check.ClaimShieldCaptures.SingleAsync()).SpawnKey);
        Assert.Equal(key, (await check.LinkshellMonsterTimings.SingleAsync()).SpawnKey);
    }

    // The event's name is free text ("Fafnir Day 3"); only the assigned monster is a spawn.
    [Fact]
    public async Task Event_IsNeverKeyedFromItsEventName()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var ev = new Event { LinkshellId = 1, EventName = "Fafnir", AssignedMonsterName = null };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        Assert.Null(ev.SpawnKey);
    }

    // An edit to the name re-stamps the key, on the synchronous save too.
    [Fact]
    public void EditedNames_AreReStamped_OnTheSynchronousSave()
    {
        using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var ev = new Event { LinkshellId = 1, AssignedMonsterName = "Behemoth" };
        var setup = new PartySetup { LinkshellId = 1, Name = "Setup", AssignedMonsterName = "Behemoth" };
        var capture = new ClaimShieldCapture { LinkshellId = 1, MonsterName = "Behemoth" };
        var timing = new LinkshellMonsterTiming { LinkshellId = 1, MonsterName = "Behemoth", NormalizedMonsterName = "behemoth" };
        db.AddRange(ev, setup, capture, timing);
        db.SaveChanges();
        Assert.Equal("behemoth/king behemoth", ev.SpawnKey);

        ev.AssignedMonsterName = "Cerberus";
        setup.AssignedMonsterName = null;
        capture.MonsterName = "Nidhogg";
        timing.MonsterName = "Khimaira";
        db.SaveChanges();

        Assert.Equal("cerberus", ev.SpawnKey);
        Assert.Null(setup.SpawnKey);
        Assert.Equal("fafnir/nidhogg", capture.SpawnKey);
        Assert.Equal("khimaira", timing.SpawnKey);
    }

    // A key typed straight into the column (Data Admin shows it read-only, but any writer could
    // try) never sticks: the name decides.
    [Fact]
    public async Task KeySetByHand_IsReplacedFromTheName()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var ev = new Event { LinkshellId = 1, AssignedMonsterName = "Tiamat" };
        var timing = new LinkshellMonsterTiming { LinkshellId = 1, MonsterName = "Tiamat", NormalizedMonsterName = "tiamat" };
        db.AddRange(ev, timing);
        await db.SaveChangesAsync();

        ev.SpawnKey = "jormungand";
        timing.SpawnKey = "jormungand";
        await db.SaveChangesAsync();

        Assert.Equal("tiamat", ev.SpawnKey);
        Assert.Equal("tiamat", timing.SpawnKey);
    }

    // A save that changes something else leaves the key unmodified.
    [Fact]
    public async Task UnrelatedEdit_DoesNotMarkTheKeyModified()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var ev = new Event { LinkshellId = 1, AssignedMonsterName = "Tiamat", EventName = "Tiamat" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();

        ev.EventName = "Tiamat Day 2";
        var entry = db.Entry(ev);
        // Not accepted, so the entry still shows what this save wrote.
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        Assert.False(entry.Property(nameof(Event.SpawnKey)).IsModified);
        Assert.True(entry.Property(nameof(Event.EventName)).IsModified);
    }

    // The start-up refill is SQL, so on a database that is not Postgres it does nothing and
    // does not throw (SpawnKeyRefillTests covers Postgres).
    [Fact]
    public async Task StartUpRefill_OnTheInMemoryDatabase_DoesNothing()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        db.Events.Add(new Event { LinkshellId = 1, AssignedMonsterName = "Tiamat", EventName = "Tiamat" });
        await db.SaveChangesAsync();

        Assert.Equal(0, await SpawnKeyRefill.RunAsync(
            db, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None));
    }
}
