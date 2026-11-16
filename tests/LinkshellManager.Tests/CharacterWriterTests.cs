using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

#pragma warning disable CS0618 // These tests check the mirror columns, which only the save hook writes.

// Round 5 (R5.5): CharacterWriter changes an account's Characters rows, and the save copies them
// onto the account (its main's name, the old columns as an exact mirror) and onto the roster name.
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

    [Fact]
    public async Task ANewAccount_GetsItsMainAndMirror_AndItsMembershipsTheMain()
    {
        var name = await SeedAsync(" Main ", "Altone", null);
        await using var db = NewDb(name);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Main", user.CharacterName);
        Assert.Equal("Altone", user.AltCharacterName1);
        Assert.Null(user.AltCharacterName2);
        Assert.All(await db.AppUserLinkshells.AsNoTracking().ToListAsync(), link => Assert.Equal("Main", link.CharacterName));
        Assert.Equal(new[] { ("Main", "main"), ("Altone", "altone") },
            (await db.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => (c.Name, c.NormalizedName)));
    }

    [Fact]
    public async Task RenamingTheMain_RenamesTheAccountAndEveryMembership()
    {
        var name = await SeedAsync("Main", "Altone", "Alttwo");
        await using (var db = NewDb(name))
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetNames("Altone", "Main", "Alttwo"));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal(("Altone", "Main", "Alttwo"), (user.CharacterName, user.AltCharacterName1, user.AltCharacterName2));
        Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), link => Assert.Equal("Altone", link.CharacterName));
    }

    [Fact]
    public async Task JobsAndCrafts_AreMirroredPerSlot_AndAClearedAltTakesThemAway()
    {
        var name = await SeedAsync("Main", "Altone", null);
        await using (var db = NewDb(name))
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetJobs(Character.MainSlot, jobLevels: new[] { 75 }, strongJobs: new[] { 1 }, meritJobs: new[] { "x" }));
            Assert.True(set.SetJobs(Character.Alt1Slot, jobLevels: new[] { 50 }));
            Assert.True(set.SetCrafts(Character.Alt1Slot, new[] { 3 }));
            Assert.False(set.SetJobs(Character.Alt2Slot, jobLevels: new[] { 1 })); // no alt 2
            await db.SaveChangesAsync();
        }
        await using (var db = NewDb(name))
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
            Assert.Equal(new[] { 75 }, user.JobLevels);
            Assert.Equal(new[] { 1 }, user.StrongJobs);
            Assert.Equal(new[] { "x" }, user.MeritJobs);
            Assert.Equal(new[] { 50 }, user.Alt1JobLevels);
            Assert.Equal(new[] { 3 }, user.Alt1CraftLevels);
            Assert.Null(user.Alt2JobLevels);

            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, null));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var after = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Null(after.AltCharacterName1);
        Assert.Null(after.Alt1JobLevels);
        Assert.Null(after.Alt1CraftLevels);
        Assert.Equal(new[] { 75 }, after.JobLevels);
    }

    [Fact]
    public async Task AnEditMadeStraightOnACharactersRow_IsCopiedToo()
    {
        var name = await SeedAsync("Main", "Altone", null);
        await using (var db = NewDb(name))
        {
            var row = await db.Characters.SingleAsync(c => c.Slot == Character.Alt1Slot);
            row.Name = "  Altnew ";
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var stored = await check.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.Alt1Slot);
        Assert.Equal(("Altnew", "altnew"), (stored.Name, stored.NormalizedName));
        Assert.Equal("Altnew", (await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me")).AltCharacterName1);
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

    [Fact]
    public async Task ANewMembership_TakesTheAccountsMain_WhateverItWasGiven()
    {
        var name = await SeedAsync("Main");
        await using (var db = NewDb(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 3, LinkshellId = 9, AppUserId = "me", CharacterName = "Something else" });
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        Assert.Equal("Main", (await check.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.Id == 3)).CharacterName);
    }

    [Fact]
    public async Task AMembershipMovedToAnotherAccount_TakesThatAccountsMain()
    {
        var name = await SeedAsync("Main");
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
        Assert.Equal("Othermain", (await check.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.Id == 2)).CharacterName);
        Assert.Equal("Main", (await check.AppUserLinkshells.AsNoTracking().SingleAsync(l => l.Id == 1)).CharacterName);
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
