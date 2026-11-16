using System.Text.Json;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A character's jobs live on its Characters row (written by CharacterWriter since Round 5), which
// these tests read (the old account columns are not written since R5.6). The addon (POST
// character/jobs) may write the job levels of any of its token owner's OWN characters: the main, or
// since Round 5 one of the two alts. Anybody else's character is refused with 403.
// (This was MainCharacterJobsTests, when the addon could write the main only.)
public class AddonCharacterJobsTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // Bob (account "bob", alts Bobalt and Bobtwo) is in both linkshells, spelled "Bob" on one roster
    // and " bob " on the other. Carol (alt Carolalt) is in linkshell 1. Dave's account is only in the
    // other linkshell.
    private static ApplicationDbContext Seeded()
    {
        var db = NewDb();
        db.Linkshells.AddRange(
            new Linkshell { Id = Ls, LinkshellName = "LS" },
            new Linkshell { Id = OtherLs, LinkshellName = "Other" });
        db.Users.AddRange(
            new AppUser
            {
                Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName1 = "Bobalt", AltCharacterName2 = "Bobtwo",
                JobLevels = new[] { 0, 1 }, Alt1JobLevels = new[] { 0, 11 }, Alt2JobLevels = new[] { 0, 22 },
            },
            new AppUser
            {
                Id = "carol", UserName = "carol", CharacterName = "Carol", AltCharacterName1 = "Carolalt",
                JobLevels = new[] { 0, 50 }, Alt1JobLevels = new[] { 0, 51 },
            },
            new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob" },
            new AppUserLinkshell { Id = 2, LinkshellId = OtherLs, AppUserId = "bob", CharacterName = " bob " },
            new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "carol", CharacterName = "Carol" },
            new AppUserLinkshell { Id = 4, LinkshellId = OtherLs, AppUserId = "dave", CharacterName = "Dave" });
        db.SaveChanges();
        return db;
    }

    private static AddonApiController NewAddonController(ApplicationDbContext db, string? issuer)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = issuer, TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = Ls;
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new AddonApiController(
            db, null!, null!, null!, null!, new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools, new DkpPoolBalanceService(db, pools), null!, null!, TestPermissions.For(db),
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static Task<IActionResult> PostAsync(ApplicationDbContext db, string? issuer, string name, int[] levels) =>
        NewAddonController(db, issuer).PostCharacterJobsAsync(
            new AddonApiController.AddonPostJobLevelsRequest(name, levels), CancellationToken.None);

    // Every job array of every account (main, alt 1, alt 2, from the Characters rows), so a test can
    // say "nothing else changed".
    private static List<string> AllJobs(ApplicationDbContext db)
    {
        var rows = db.Characters.AsNoTracking().ToList();
        string Jobs(string id, short slot) => Show(rows.SingleOrDefault(c => c.AppUserId == id && c.Slot == slot)?.JobLevels);
        return db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).ToList()
            .Select(id => $"{id}:{Jobs(id, 0)}|{Jobs(id, 1)}|{Jobs(id, 2)}")
            .ToList();
    }

    private static string Show(int[]? levels) => levels is null ? "-" : string.Join(",", levels);

    // ---- the writer ----------------------------------------------------------------------------

    // The addon's levels go to one character's Characters row (CharacterWriter.SetJobs); a null
    // argument leaves that list alone, and the save mirrors the row onto the account.
    [Fact]
    public async Task SetJobs_SetsTheRow_AndLeavesNullArgumentsAlone()
    {
        await using var db = Seeded();
        var set = await CharacterWriter.LoadAsync(db, "carol", CancellationToken.None);
        set.SetJobs(Character.MainSlot, strongJobs: new[] { 0, 1 }, meritJobs: new[] { "kept" });
        await db.SaveChangesAsync();

        set.SetJobs(Character.MainSlot, jobLevels: new[] { 0, 75 });
        await db.SaveChangesAsync();

        var main = db.Characters.AsNoTracking().Single(c => c.AppUserId == "carol" && c.Slot == Character.MainSlot);
        Assert.Equal(new[] { 0, 75 }, main.JobLevels);
        Assert.Equal(new[] { 0, 1 }, main.StrongJobs);
        Assert.Equal(new[] { "kept" }, main.MeritJobs);
    }

    [Theory]
    [InlineData(0, "bob:0,75|0,11|0,22")]
    [InlineData(1, "bob:0,1|0,75|0,22")]
    [InlineData(2, "bob:0,1|0,11|0,75")]
    public async Task SetJobs_WritesOnlyThatSlot(short slot, string expected)
    {
        await using var db = Seeded();
        var set = await CharacterWriter.LoadAsync(db, "bob", CancellationToken.None);

        set.SetJobs(slot, jobLevels: new[] { 0, 75 });
        await db.SaveChangesAsync();

        Assert.Equal(expected, AllJobs(db)[0]);
    }

    [Fact]
    public async Task SetJobs_RefusesASlotThatDoesNotExist()
    {
        await using var db = Seeded();
        var set = await CharacterWriter.LoadAsync(db, "bob", CancellationToken.None);
        Assert.Throws<ArgumentOutOfRangeException>(() => set.SetJobs(3, jobLevels: new[] { 1 }));
    }

    // ---- OwnCharacterSlot ----------------------------------------------------------------------

    [Theory]
    [InlineData("Bob", 0, "Bob")]
    [InlineData("  BOB ", 0, "Bob")]       // capitals and spaces do not matter
    [InlineData("Bobalt", 1, "Bobalt")]    // alt 1
    [InlineData(" bobTWO", 2, "Bobtwo")]   // alt 2
    [InlineData("Carol", -1, null)]        // somebody else's main on the same roster
    [InlineData("Carolalt", -1, null)]     // somebody else's alt
    [InlineData("Dave", -1, null)]         // somebody in another linkshell
    [InlineData("Nobody", -1, null)]
    [InlineData("", -1, null)]
    [InlineData(null, -1, null)]
    public void OwnCharacterSlot_AcceptsOnlyTheOwnersCharacters(string? typed, int slot, string? stored)
    {
        using var db = Seeded();
        var owner = db.Users.Single(u => u.Id == "bob");
        var characters = db.Characters.Where(c => c.AppUserId == "bob").ToList();

        var matched = AccountCharacters.OwnCharacterSlot(typed, owner, characters);

        if (stored is null)
        {
            Assert.Null(matched);
        }
        else
        {
            Assert.Equal(((short)slot, stored), matched);
        }
    }

    // Another account's Characters rows never count, even if the caller passes them in.
    [Fact]
    public void OwnCharacterSlot_IgnoresCharactersOfAnotherAccount()
    {
        var owner = new AppUser { Id = "bob", CharacterName = "Bob" };
        var characters = new[] { new Character { AppUserId = "carol", Slot = 1, Name = "Carolalt" } };

        Assert.Null(AccountCharacters.OwnCharacterSlot("Carolalt", owner, characters));
    }

    // The account's own name is still the main when Characters does not have it. A roster name is
    // not read any more (Round 5): it was only ever a copy of the main.
    [Fact]
    public void OwnCharacterSlot_FallsBackToTheAccountName_NotTheRosterNames()
    {
        var owner = new AppUser { Id = "me", CharacterName = " Mainname " };
        var none = Array.Empty<Character>();

        Assert.Equal(((short)0, "Mainname"), AccountCharacters.OwnCharacterSlot("mainname", owner, none));
        Assert.Null(AccountCharacters.OwnCharacterSlot("ROSTERNAME", owner, none));
        Assert.Null(AccountCharacters.OwnCharacterSlot("ELSEWHERE", owner, none));
    }

    // ---- POST character/jobs -------------------------------------------------------------------

    [Fact]
    public async Task OwnMain_IsAccepted_AndWritesTheMainOnly()
    {
        await using var db = Seeded();

        var result = await PostAsync(db, "bob", " bob ", new[] { 0, 75, 37 });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value));
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Bob", json.RootElement.GetProperty("characterName").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("slot").GetInt32());
        Assert.Equal(
            new[] { "bob:0,75,37|0,11|0,22", "carol:0,50|0,51|-", "dave:-|-|-" },
            AllJobs(db));
    }

    [Theory]
    [InlineData("Bobalt", 1, "Bobalt", "bob:0,1|0,75,37|0,22")]
    [InlineData("Bobtwo", 2, "Bobtwo", "bob:0,1|0,11|0,75,37")]
    [InlineData("  BOBALT  ", 1, "Bobalt", "bob:0,1|0,75,37|0,22")] // capitals and spaces do not matter
    public async Task OwnAlt_IsAccepted_AndWritesThatAltOnly(string name, int slot, string stored, string expectedBob)
    {
        await using var db = Seeded();

        var result = await PostAsync(db, "bob", name, new[] { 0, 75, 37 });

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value));
        Assert.Equal(stored, json.RootElement.GetProperty("characterName").GetString());
        Assert.Equal(slot, json.RootElement.GetProperty("slot").GetInt32());
        Assert.Equal(new[] { expectedBob, "carol:0,50|0,51|-", "dave:-|-|-" }, AllJobs(db));
    }

    // The levels are written to the alt's Characters row.
    [Fact]
    public async Task OwnAlt_ItsCharactersRowGetsTheLevels_AfterTheSave()
    {
        await using var db = Seeded();

        Assert.IsType<OkObjectResult>(await PostAsync(db, "bob", "Bobalt", new[] { 0, 75, 37 }));

        var rows = db.Characters.AsNoTracking().Where(c => c.AppUserId == "bob").OrderBy(c => c.Slot).ToList();
        Assert.Equal(new[] { "Bob", "Bobalt", "Bobtwo" }, rows.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1 }, rows[0].JobLevels);
        Assert.Equal(new[] { 0, 75, 37 }, rows[1].JobLevels);
        Assert.Equal(new[] { 0, 22 }, rows[2].JobLevels);
    }

    // What the old guard let through, or should never let through.
    [Theory]
    [InlineData("bob", "Carol")]     // another member's main on the same linkshell
    [InlineData("bob", "Carolalt")]  // another member's alt
    [InlineData("bob", "Nobody")]    // a name nobody has
    [InlineData("dave", "Dave")]     // own main, but the owner is not on the token's linkshell
    [InlineData(null, "Bob")]        // a token with no owner
    [InlineData("ghost", "Bob")]     // an owner account that no longer exists
    public async Task AnythingButTheOwnersCharacters_IsRefused_AndChangesNothing(string? issuer, string name)
    {
        await using var db = Seeded();
        var before = AllJobs(db);

        var result = await PostAsync(db, issuer, name, new[] { 0, 99 });

        Assert.IsType<ForbidResult>(result);
        Assert.Equal(before, AllJobs(db));
    }

    // Dave's own alt, while Dave is not on the token's linkshell: still refused.
    [Fact]
    public async Task OwnAlt_OfAnOwnerNotOnTheTokensLinkshell_IsRefused()
    {
        await using var db = Seeded();
        (await CharacterWriter.LoadAsync(db, "dave", CancellationToken.None)).SetName(Character.Alt1Slot, "Davealt");
        db.SaveChanges();
        var before = AllJobs(db);

        Assert.IsType<ForbidResult>(await PostAsync(db, "dave", "Davealt", new[] { 0, 99 }));
        Assert.Equal(before, AllJobs(db));
    }

    [Theory]
    [InlineData("", new[] { 1 })]
    [InlineData("   ", new[] { 1 })]
    [InlineData("Bob", new int[0])]
    public async Task ABlankNameOrNoLevels_IsABadRequest(string name, int[] levels)
    {
        await using var db = Seeded();

        Assert.IsType<BadRequestObjectResult>(await PostAsync(db, "bob", name, levels));
    }

    // ---- GET roster ----------------------------------------------------------------------------

    [Fact]
    public async Task AddonRoster_ReadsTheMainsJobsFromTheAccount()
    {
        await using var db = Seeded();
        // The roster row has no jobs of its own: the account's value is the answer.
        (await CharacterWriter.LoadAsync(db, "bob", CancellationToken.None)).SetJobs(Character.MainSlot, jobLevels: new[] { 0, 70 });
        db.SaveChanges();

        var ok = Assert.IsType<OkObjectResult>(await NewAddonController(db, "bob").GetRosterAsync(CancellationToken.None));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value));
        var bob = json.RootElement.GetProperty("roster").EnumerateArray()
            .Single(row => row.GetProperty("characterName").GetString() == "Bob");
        Assert.Equal(new[] { 0, 70 }, bob.GetProperty("jobLevels").EnumerateArray().Select(e => e.GetInt32()));
    }

    // ---- a placeholder claim -------------------------------------------------------------------

    [Theory]
    [InlineData(false, new[] { 0, 30 })] // the real account has none: it takes the placeholder's
    [InlineData(true, new[] { 0, 60 })]  // it has its own: never clobbered
    public async Task Claim_CarriesThePlaceholdersJobs_OnlyWhenTheRealAccountHasNone(bool realHasJobs, int[] expected)
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.AddRange(
            new AppUser { Id = "real", UserName = "real", CharacterName = "Bob", JobLevels = realHasJobs ? new[] { 0, 60 } : null },
            new AppUser { Id = "ph", UserName = "ph", CharacterName = "Bob", IsPlaceholder = true,
                JobLevels = new[] { 0, 30 }, StrongJobs = new[] { 0, 1 }, MeritJobs = new[] { "note" } });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 10, LinkshellId = Ls, AppUserId = "ph", CharacterName = "Bob" });
        db.SaveChanges();

        var result = await new PlaceholderClaimService(db, new UserMergeService(db))
            .ClaimPlaceholderAsync("ph", "real", Ls, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var real = db.Characters.AsNoTracking().Single(c => c.AppUserId == "real" && c.Slot == Character.MainSlot);
        Assert.Equal(expected, real.JobLevels);
        Assert.Equal(new[] { 0, 1 }, real.StrongJobs);
        Assert.Equal(new[] { "note" }, real.MeritJobs);
    }
}
