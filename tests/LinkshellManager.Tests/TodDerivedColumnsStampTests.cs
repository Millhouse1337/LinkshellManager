using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Every save keeps the derived ToD columns (AddTodDurationsAndSpawnKeys) in step with their text:
// SpawnKey from MonsterName, CooldownMinutes / IntervalMinutes from Cooldown / Interval, on Tods,
// PendingTodSubmissions and HnmRecurringBoards. Nothing reads them yet, so this only has to be right.
public class TodDerivedColumnsStampTests
{
    private static ApplicationDbContext NewInMemoryContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    [Theory]
    [InlineData("Fafnir", "22 Hour", "10 Min", "fafnir/nidhogg", 1320, 10)]
    [InlineData(" King Behemoth ", "22", "10", "behemoth/king behemoth", 1320, 10)]
    [InlineData("Tiamat", "84 Hour", "1 Hour", "tiamat", 5040, 60)]
    [InlineData("Cerberus", "1 Hour 30 Min", null, "cerberus", 90, null)]
    [InlineData(null, "abc", "0 Min", null, null, null)]
    public async Task NewTod_IsStampedFromItsText(
        string? monster, string? cooldown, string? interval, string? key, int? cooldownMinutes, int? intervalMinutes)
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.Tods.Add(new Tod { LinkshellId = 1, MonsterName = monster, Cooldown = cooldown, Interval = interval });
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        var tod = await check.Tods.SingleAsync();
        Assert.Equal(key, tod.SpawnKey);
        Assert.Equal(cooldownMinutes, tod.CooldownMinutes);
        Assert.Equal(intervalMinutes, tod.IntervalMinutes);
    }

    // The synchronous SaveChanges stamps too, and a later edit to the text re-stamps it.
    [Fact]
    public void EditedTod_IsReStamped_OnTheSynchronousSave()
    {
        using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var tod = new Tod { LinkshellId = 1, MonsterName = "Adamantoise", Cooldown = "22 Hour", Interval = "10 Min" };
        db.Tods.Add(tod);
        db.SaveChanges();
        Assert.Equal("adamantoise/aspidochelone", tod.SpawnKey);

        tod.MonsterName = "Cerberus";
        tod.Cooldown = "48 Hour";
        tod.Interval = "6 Hour";
        db.SaveChanges();

        Assert.Equal("cerberus", tod.SpawnKey);
        Assert.Equal(2880, tod.CooldownMinutes);
        Assert.Equal(360, tod.IntervalMinutes);
    }

    // A value typed straight into a derived column never sticks: the text decides.
    [Fact]
    public async Task DerivedValueSetByHand_IsReplacedFromTheText()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var tod = new Tod { LinkshellId = 1, MonsterName = "Tiamat", Cooldown = "84 Hour" };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        tod.SpawnKey = "jormungand";
        tod.CooldownMinutes = 1;
        await db.SaveChangesAsync();

        Assert.Equal("tiamat", tod.SpawnKey);
        Assert.Equal(5040, tod.CooldownMinutes);
    }

    // A save that changes something else leaves the derived columns unmodified.
    [Fact]
    public async Task UnrelatedEdit_DoesNotMarkTheDerivedColumnsModified()
    {
        await using var db = NewInMemoryContext(Guid.NewGuid().ToString());
        var tod = new Tod { LinkshellId = 1, MonsterName = "Tiamat", Cooldown = "84 Hour", Interval = "1 Hour" };
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        tod.Claim = true;
        var entry = db.Entry(tod);
        // Not accepted, so the entry still shows what this save wrote.
        await db.SaveChangesAsync(acceptAllChangesOnSuccess: false);
        Assert.False(entry.Property(nameof(Tod.SpawnKey)).IsModified);
        Assert.False(entry.Property(nameof(Tod.CooldownMinutes)).IsModified);
        Assert.False(entry.Property(nameof(Tod.IntervalMinutes)).IsModified);
        Assert.True(entry.Property(nameof(Tod.Claim)).IsModified);
    }

    [Fact]
    public async Task PendingTod_IsStampedFromItsText()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.PendingTodSubmissions.Add(new PendingTodSubmission
            {
                LinkshellId = 1, MonsterName = "Nidhogg", Cooldown = "22 Hour", Interval = "10 Min",
            });
            await db.SaveChangesAsync();

            var pending = await db.PendingTodSubmissions.SingleAsync();
            pending.MonsterName = "Khimaira";
            pending.Cooldown = "48 Hour";
            pending.Interval = "6 Hour";
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        var saved = await check.PendingTodSubmissions.SingleAsync();
        Assert.Equal("khimaira", saved.SpawnKey);
        Assert.Equal(2880, saved.CooldownMinutes);
        Assert.Equal(360, saved.IntervalMinutes);
    }

    [Fact]
    public async Task Board_IsStampedFromItsMonsterName()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.HnmRecurringBoards.Add(new HnmRecurringBoard { LinkshellId = 1, MonsterName = "Behemoth/King Behemoth" });
            db.HnmRecurringBoards.Add(new HnmRecurringBoard { LinkshellId = 1, MonsterName = "Hydra" });
            await db.SaveChangesAsync();

            var hydra = await db.HnmRecurringBoards.SingleAsync(board => board.MonsterName == "Hydra");
            hydra.MonsterName = "Fafnir";
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        var keys = await check.HnmRecurringBoards.OrderBy(board => board.Id).Select(board => board.SpawnKey).ToListAsync();
        Assert.Equal(new[] { "behemoth/king behemoth", "fafnir/nidhogg" }, keys);
    }
}
