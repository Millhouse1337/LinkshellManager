using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

#pragma warning disable CS0618 // These tests check the old account columns are left alone.

// Round 5: CharacterWriter changes an account's Characters rows, and the save copies the main's name
// onto the account (CharacterName). Since R5.6 the old account alt, job and craft columns and the
// roster name are not written: they keep what they held (what the R5.5 build last copied there).
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

    // An account whose old columns and roster names hold what the R5.5 build copied there (its
    // Characters rows are written through the writer in the same save, so the save does not adopt the
    // old columns).
    private static async Task<string> SeedWithOldColumnsAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        var account = new AppUser
        {
            Id = "me", UserName = "login",
            JobLevels = new[] { 75 }, StrongJobs = new[] { 1 }, MeritJobs = new[] { "x" }, CraftLevels = new[] { 9 },
            AltCharacterName1 = "Altone", Alt1JobLevels = new[] { 50 }, Alt1CraftLevels = new[] { 3 },
        };
        db.Users.Add(account);
        var set = CharacterWriter.ForNewAccount(db, account);
        set.SetNames("Main", "Altone", null);
        set.SetJobs(Character.MainSlot, jobLevels: new[] { 75 }, strongJobs: new[] { 1 }, meritJobs: new[] { "x" });
        set.SetCrafts(Character.MainSlot, new[] { 9 });
        set.SetJobs(Character.Alt1Slot, jobLevels: new[] { 50 });
        set.SetCrafts(Character.Alt1Slot, new[] { 3 });
        // R5.5 copied the main onto the roster names.
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 7, AppUserId = "me", CharacterName = "Main" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = 8, AppUserId = "me", CharacterName = "Main" });
        await db.SaveChangesAsync();
        return name;
    }

    private static string OldColumns(AppUser user) => string.Join("|",
        user.AltCharacterName1, user.AltCharacterName2,
        Show(user.JobLevels), Show(user.StrongJobs), Show(user.MeritJobs), Show(user.CraftLevels),
        Show(user.Alt1JobLevels), Show(user.Alt1StrongJobs), Show(user.Alt1MeritJobs), Show(user.Alt1CraftLevels),
        Show(user.Alt2JobLevels), Show(user.Alt2StrongJobs), Show(user.Alt2MeritJobs), Show(user.Alt2CraftLevels));

    private static string Show<T>(T[]? values) => values is null ? "-" : string.Join(",", values);

    // What SeedWithOldColumnsAsync left in the old columns.
    private const string SeededOldColumns = "Altone||75|1|x|9|50|-|-|3|-|-|-|-";

    [Fact]
    public async Task ANewAccount_GetsItsMain_ButNoOldColumnsOrRosterNames()
    {
        var name = await SeedAsync(" Main ", "Altone", null);
        await using var db = NewDb(name);

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Main", user.CharacterName);
        // R5.6: the old columns and the roster names are not written, so a new account's stay empty.
        Assert.Equal("||-|-|-|-|-|-|-|-|-|-|-|-", OldColumns(user));
        Assert.All(await db.AppUserLinkshells.AsNoTracking().ToListAsync(), link => Assert.Null(link.CharacterName));
        Assert.Equal(new[] { ("Main", "main"), ("Altone", "altone") },
            (await db.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => (c.Name, c.NormalizedName)));
    }

    [Fact]
    public async Task RenamingTheMain_RenamesTheAccount_AndLeavesTheOldColumnsAndRosterNames()
    {
        var name = await SeedWithOldColumnsAsync();
        await using (var db = NewDb(name))
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetNames("Altone", "Main", "Alttwo"));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var user = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Altone", user.CharacterName);
        Assert.Equal(SeededOldColumns, OldColumns(user));
        Assert.All(await check.AppUserLinkshells.AsNoTracking().ToListAsync(), link => Assert.Equal("Main", link.CharacterName));
        Assert.Equal(new[] { "Altone", "Main", "Alttwo" },
            (await check.Characters.AsNoTracking().OrderBy(c => c.Slot).ToListAsync()).Select(c => c.Name));
    }

    [Fact]
    public async Task JobsAndCrafts_StayOnTheirSlot_AClearedAltTakesThemAway_AndTheOldColumnsAreLeft()
    {
        var name = await SeedWithOldColumnsAsync();
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
            Assert.Equal(SeededOldColumns, OldColumns(await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me")));

            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            Assert.True(set.SetName(Character.Alt1Slot, null));
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        Assert.False(await check.Characters.AnyAsync(c => c.Slot == Character.Alt1Slot));
        Assert.Equal(new[] { 70 }, (await check.Characters.AsNoTracking().SingleAsync(c => c.Slot == Character.MainSlot)).JobLevels);
        Assert.Equal(SeededOldColumns, OldColumns(await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me")));
    }

    [Fact]
    public async Task AnEditMadeStraightOnACharactersRow_IsCopiedToTheAccountsMainName()
    {
        var name = await SeedWithOldColumnsAsync();
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
        Assert.Equal(SeededOldColumns, OldColumns(user));
    }

    // Code that writes an old column itself (none does: the compiler refuses it) has it undone, and
    // the same for CharacterName, so they never change by hand.
    [Fact]
    public async Task AStraightChangeOfTheOldColumnsOrTheMainName_IsUndone()
    {
        var name = await SeedWithOldColumnsAsync();
        await using (var db = NewDb(name))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            user.AltCharacterName2 = "Sneakyalt";
            user.Alt1JobLevels = new[] { 99 };
            await db.SaveChangesAsync();
        }
        // In a save that also changes the account's characters, CharacterName follows the main and
        // the old columns are still undone.
        await using (var db = NewDb(name))
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            user.JobLevels = new[] { 1 };
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetName(Character.Alt2Slot, "Alttwo");
            await db.SaveChangesAsync();
        }

        await using var check = NewDb(name);
        var after = await check.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Main", after.CharacterName);
        Assert.Equal(SeededOldColumns, OldColumns(after));
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

    // Since R5.6 the save never writes a roster name: a new membership gets none.
    [Fact]
    public async Task ANewMembership_GetsNoRosterName()
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
        Assert.All(await check.AppUserLinkshells.AsNoTracking().Where(l => l.LinkshellId >= 9).ToListAsync(),
            link => Assert.Null(link.CharacterName));
        Assert.Equal(2, await check.AppUserLinkshells.CountAsync(l => l.LinkshellId >= 9));
    }

    // A membership moved to another account (a placeholder claim) keeps the roster name it had: the
    // save no longer stamps it. Nothing reads it; the roster shows the new account's main.
    [Fact]
    public async Task AMembershipMovedToAnotherAccount_KeepsItsOldRosterName()
    {
        var name = await SeedWithOldColumnsAsync();
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
        Assert.Equal(("other", "Main"), (moved.AppUserId, moved.CharacterName));
        Assert.Equal("Othermain", moved.AppUser!.CharacterName);
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
