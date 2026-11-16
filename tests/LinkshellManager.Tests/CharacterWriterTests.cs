using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Round 5: CharacterWriter changes an account's Characters rows, and the save copies the main's name
// onto the account (CharacterName). Nothing else is copied: the old account alt, job and craft
// columns and the roster name were dropped (Round5Contract, R5.7).
public class CharacterWriterTests
{
    private static ApplicationDbContext NewDb(string? name = null) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options);

    private static async Task<string> SeedAsync(string main, string? alt1 = null, string? alt2 = null)
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        var account = new AppUser { Id = "me", UserName = "login" };
        db.Users.Add(account);
        CharacterWriter.ForNewAccount(db, account).SetNames(main, alt1, alt2);
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 7, AppUserId = "me" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = 8, AppUserId = "me" });
        await db.SaveChangesAsync();
        return name;
    }

    // An account with a main and alt 1, both with jobs and crafts, in two linkshells.
    private static async Task<string> SeedWithJobsAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        var account = new AppUser { Id = "me", UserName = "login" };
        db.Users.Add(account);
        var set = CharacterWriter.ForNewAccount(db, account);
        set.SetNames("Main", "Altone", null);
        set.SetJobs(Character.MainSlot, jobLevels: new[] { 75 }, strongJobs: new[] { 1 }, meritJobs: new[] { "x" });
        set.SetCrafts(Character.MainSlot, new[] { 9 });
        set.SetJobs(Character.Alt1Slot, jobLevels: new[] { 50 });
        set.SetCrafts(Character.Alt1Slot, new[] { 3 });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 7, AppUserId = "me" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = 8, AppUserId = "me" });
        await db.SaveChangesAsync();
        return name;
    }

    private static string Show<T>(T[]? values) => values is null ? "-" : string.Join(",", values);

    [Fact]
    public async Task ANewAccount_GetsItsMainName()
    {
        var name = await SeedAsync(" Main ", "Altone", null);
        await using var db = NewDb(name);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Main", user.CharacterName);
        Assert.Equal(new[] { ("Main", "main"), ("Altone", "altone") },
            (await db.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => (c.Name, c.NormalizedName)));
    }

    [Fact]
    public async Task RenamingTheMain_RenamesTheAccount()
    {
        var name = await SeedWithJobsAsync();
        await using (var db = NewDb(name))
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetNames("Altone", "Main", "Alttwo"));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Altone", user.CharacterName);
        Assert.Equal(new[] { "Altone", "Main", "Alttwo" },
            (await check.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task JobsAndCrafts_StayOnTheirSlot_AClearedAltTakesThemAway()
    {
        var name = await SeedWithJobsAsync();
        await using (var db = NewDb(name))
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetJobs(Character.MainSlot, jobLevels: new[] { 70 }, strongJobs: new[] { 0 }, meritJobs: new[] { "y" }));
            Assert.True(set.SetJobs(Character.Alt1Slot, jobLevels: new[] { 40 }));
            Assert.True(set.SetCrafts(Character.Alt1Slot, new[] { 4 }));
            Assert.False(set.SetJobs(Character.Alt2Slot, jobLevels: new[] { 1 })); // no alt 2
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb(name))
        {
            var main = await db.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.MainSlot);
            var alt1 = await db.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.Alt1Slot);
            Assert.Equal("70|0|y|9", string.Join("|", Show(main.JobLevels), Show(main.StrongJobs), Show(main.MeritJobs), Show(main.CraftLevels)));
            Assert.Equal("40|4", string.Join("|", Show(alt1.JobLevels), Show(alt1.CraftLevels)));

            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, null));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        Assert.False(await check.Characters.AnyAsync(c => c.Slot == Character.Alt1Slot));
        Assert.Equal(new[] { 70 }, (await check.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.MainSlot)).JobLevels);
    }

    [Fact]
    public async Task AnEditMadeStraightOnACharactersRow_IsCopiedToTheAccountsMainName()
    {
        var name = await SeedWithJobsAsync();
        await using (var db = NewDb(name))
        {
            var row = await db.Characters.SingleAsync(c => c.Slot == Character.Alt1Slot);
            row.Name = "  Altnew ";
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb(name))
        {
            var row = await db.Characters.SingleAsync(c => c.Slot == Character.MainSlot);
            row.Name = " Mainnew";
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var stored = await check.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.Alt1Slot);
        Assert.Equal(("Altnew", "altnew"), (stored.Name, stored.NormalizedName));
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Mainnew", user.CharacterName);
    }

    // Code that writes CharacterName itself has it undone, so it never changes by hand.
    [Fact]
    public async Task AStraightChangeOfTheMainName_IsUndone()
    {
        var name = await SeedWithJobsAsync();
        await using (var db = NewDb(name))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            await db.SaveChangesAsync();
        }
        // In a save that also changes the account's characters, CharacterName follows the main.
        await using (var db = NewDb(name))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetName(Character.Alt2Slot, "Alttwo");
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var after = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Main", after.CharacterName);
        Assert.Equal(new[] { "Main", "Altone", "Alttwo" },
            (await check.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task ABlankOrTooLongRowName_FailsTheSave()
    {
        var name = await SeedAsync("Main");
        await using var db = NewDb(name);
        var row = await db.Characters.SingleAsync();

        row.Name = "   ";
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        row.Name = new string('a', CharacterWriter.MaxNameLength + 1);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // A membership has no name of its own (Round5Contract dropped the roster name): a new one saves
    // and shows the account's main.
    [Fact]
    public async Task ANewMembership_ShowsTheAccountsMain()
    {
        var name = await SeedAsync("Main");
        await using (var db = NewDb(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 3, LinkshellId = 9, AppUserId = "me" });
            var joined = LinkshellMembership.NewMember(await db.Users.SingleAsync(u => u.Id == "me"), 10, DateTime.UtcNow);
            joined.Id = 4;
            db.AppUserLinkshells.Add(joined);
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        Assert.All(await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).Where(l => l.LinkshellId >= 9).ToListAsync(),
            link => Assert.Equal("Main", link.AppUser!.CharacterName));
        Assert.Equal(2, await check.AppUserLinkshells.CountAsync(l => l.LinkshellId >= 9));
    }

    // A membership moved to another account (a placeholder claim) shows the new account's main.
    [Fact]
    public async Task AMembershipMovedToAnotherAccount_ShowsTheNewAccountsMain()
    {
        var name = await SeedWithJobsAsync();
        await using (var db = NewDb(name))
        {
            var other = new AppUser { Id = "other", UserName = "other" };
            db.Users.Add(other);
            CharacterWriter.ForNewAccount(db, other).SetName(Character.MainSlot, "Othermain");
            await db.SaveChangesAsync();

            var link = await db.AppUserLinkshells.SingleAsync(l => l.Id == 2);
            link.AppUserId = "other";
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var moved = await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).SingleAsync(l => l.Id == 2);
        Assert.Equal(("other", "Othermain"), (moved.AppUserId, moved.AppUser!.CharacterName));
        var stayed = await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).SingleAsync(l => l.Id == 1);
        Assert.Equal(("me", "Main"), (stayed.AppUserId, stayed.AppUser!.CharacterName));
    }

    [Fact]
    public async Task SetName_InPlaceForCapitals_RemoveAndAddForANewName()
    {
        var name = await SeedAsync("main", "Altone");
        await using var db = NewDb(name);
        var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
        var mainRow = set[Character.MainSlot]!;
        var altRow = set[Character.Alt1Slot]!;

        Assert.True(set.SetName(Character.MainSlot, "Main"));
        Assert.Same(mainRow, set[Character.MainSlot]);
        Assert.Equal(EntityState.Modified, db.Entry(mainRow).State);

        Assert.True(set.SetName(Character.Alt1Slot, "Other"));
        Assert.Equal(EntityState.Deleted, db.Entry(altRow).State);
        Assert.NotSame(altRow, set[Character.Alt1Slot]);
        Assert.Equal("Other", set.Name(Character.Alt1Slot));
        Assert.Equal(Character.Alt2Slot, set.FirstFreeAltSlot());
        Assert.True(set.HasName(" other "));
        Assert.False(set.HasName("Altone"));
    }

    [Fact]
    public async Task DiscardChanges_ForgetsWhatWasNotSaved()
    {
        var name = await SeedAsync("Main", "Altone");
        await using var db = NewDb(name);
        var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
        set.SetNames("Renamed", null, "Newalt");

        set.DiscardChanges();

        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Equal(new[] { "Main", "Altone" }, set.Rows.Select(r => r.Name));
    }
}
