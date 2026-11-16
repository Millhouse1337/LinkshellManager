using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5 (R5.5): CharacterWriter on real Postgres, where the unique indexes and the advisory lock
// are real. Characters is the only written source of names, jobs and crafts from R5.5 on.
[Collection("postgres")]
public class CharacterWriterPostgresTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CharacterWriterPostgresTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TheMainAndAlt1_SwapNames_InOneSave()
    {
        await SeedAsync(main: "Mine", alt1: "Other", alt2: null);
        await SetJobsAsync(Character.MainSlot, 75);
        await SetJobsAsync(Character.Alt1Slot, 50);

        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetNames("Other", "Mine", null));
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { ("Other", "other"), ("Mine", "mine") }, rows.Select(r => (r.Name, r.NormalizedName)));
        // Jobs belong to the slot: the main's jobs stay on slot 0 whatever it is called.
        Assert.Equal(75, rows[0].JobLevels![0]);
        Assert.Equal(50, rows[1].JobLevels![0]);
    }

    [Fact]
    public async Task RenamingAnAlt_KeepsItsJobsAndCrafts()
    {
        await SeedAsync(main: "Mine", alt1: "Altone", alt2: null);
        await SetJobsAsync(Character.Alt1Slot, 60);
        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetCrafts(Character.Alt1Slot, new[] { 1, 2, 3 });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, "  Altnew "));
            await db.SaveChangesAsync();
        }

        var alt = (await RowsAsync()).Single(r => r.Slot == Character.Alt1Slot);
        Assert.Equal(("Altnew", "altnew"), (alt.Name, alt.NormalizedName));
        Assert.Equal(60, alt.JobLevels![0]);
        Assert.Equal(new[] { 1, 2, 3 }, alt.CraftLevels);
    }

    [Fact]
    public async Task ClearingAnAlt_RemovesItsRowAndJobs()
    {
        await SeedAsync(main: "Mine", alt1: "Altone", alt2: "Alttwo");
        await SetJobsAsync(Character.Alt1Slot, 60);

        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, "   "));
            await db.SaveChangesAsync();
        }

        Assert.Equal(new short[] { 0, 2 }, (await RowsAsync()).Select(r => r.Slot));

        // A new alt 1 starts with no jobs.
        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, "Altone"));
            await db.SaveChangesAsync();
        }
        Assert.Null((await RowsAsync()).Single(r => r.Slot == Character.Alt1Slot).JobLevels);
    }

    [Fact]
    public async Task ChangingOnlyCapitals_UpdatesTheRowInPlace()
    {
        await SeedAsync(main: "mine", alt1: null, alt2: null);
        var before = (await RowsAsync()).Single();

        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.MainSlot, "Mine"));
            Assert.False(set.SetName(Character.MainSlot, "Mine"));
            await db.SaveChangesAsync();
        }

        var after = (await RowsAsync()).Single();
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(("Mine", "mine"), (after.Name, after.NormalizedName));
    }

    // Two saves of one account at the same moment, each in its own transaction: the second waits
    // for the first (the advisory lock), then works on what the first saved. Both succeed and the
    // later one wins. Without the lock the second would delete a row the first already deleted.
    [Fact]
    public async Task TwoSavesOfOneAccount_RunOneAfterTheOther_AndTheLastWins()
    {
        await SeedAsync(main: "Mine", alt1: "Altone", alt2: null);

        await using var first = _pg.CreateContext();
        await using var firstTx = await CharacterWriter.BeginTransactionAsync(first, CancellationToken.None);
        Assert.NotNull(firstTx);
        var firstSet = await CharacterWriter.LoadAsync(first, "me", CancellationToken.None);

        await using var second = _pg.CreateContext();
        await using var secondTx = await CharacterWriter.BeginTransactionAsync(second, CancellationToken.None);
        var secondLoad = CharacterWriter.LoadAsync(second, "me", CancellationToken.None);
        await Task.Delay(300);
        Assert.False(secondLoad.IsCompleted, "The second save should wait for the first one's lock.");

        firstSet.SetName(Character.Alt1Slot, "Firstalt");
        await first.SaveChangesAsync();
        await firstTx!.CommitAsync();

        var secondSet = await secondLoad;
        Assert.Equal("Firstalt", secondSet.Name(Character.Alt1Slot));
        secondSet.SetName(Character.Alt1Slot, "Secondalt");
        await second.SaveChangesAsync();
        await secondTx!.CommitAsync();

        Assert.Equal(new[] { "Mine", "Secondalt" }, (await RowsAsync()).Select(r => r.Name));
    }

    [Fact]
    public async Task A65CharacterName_IsRefused()
    {
        await SeedAsync(main: "Mine", alt1: null, alt2: null);
        await using var db = _pg.CreateContext();
        var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);

        Assert.Throws<ArgumentException>(() => set.SetName(Character.Alt1Slot, new string('a', 65)));
        Assert.Throws<ArgumentException>(() => set.SetNames("Mine", new string('a', 65), null));
        Assert.False(set.TrySetName(Character.Alt1Slot, new string('a', 65)));
        Assert.True(set.SetName(Character.Alt1Slot, new string('a', 64)));
        await db.SaveChangesAsync();
        Assert.Equal(64, (await RowsAsync()).Single(r => r.Slot == Character.Alt1Slot).Name.Length);
    }

    [Fact]
    public async Task ANameTheAccountAlreadyHas_IsRefusedOrSkipped()
    {
        await SeedAsync(main: "Mine", alt1: null, alt2: null);
        await using var db = _pg.CreateContext();
        var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => set.SetName(Character.Alt1Slot, " MINE "));
        Assert.Throws<InvalidOperationException>(() => set.SetNames("Mine", "Twin", "twin"));
        Assert.False(set.TrySetName(Character.Alt1Slot, "mine"));
        Assert.Equal(Character.Alt1Slot, set.FirstFreeAltSlot());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private async Task SeedAsync(string main, string? alt1, string? alt2)
    {
        await using var db = _pg.CreateContext();
        var account = new AppUser { Id = "me", UserName = "me" };
        db.Users.Add(account);
        var set = CharacterWriter.ForNewAccount(db, account);
        set.SetNames(main, alt1, alt2);
        await db.SaveChangesAsync();
    }

    private async Task SetJobsAsync(short slot, int level)
    {
        await using var db = _pg.CreateContext();
        var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
        Assert.True(set.SetJobs(slot, jobLevels: new[] { level }));
        await db.SaveChangesAsync();
    }

    private async Task<Character[]> RowsAsync()
    {
        await using var db = _pg.CreateContext();
        return await db.Characters.AsNoTracking().Where(c => c.AppUserId == "me").OrderBy(c => c.Slot).ToArrayAsync();
    }
}
