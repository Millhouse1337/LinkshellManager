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

// The main character's jobs live on the account (AccountJobsAndKeyItemAccounts) and are read from
// there; every writer also copies them onto each membership, which the previous build reads, so a
// rollback still shows them. The addon may write only its token owner's own main character.
public class MainCharacterJobsTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // Bob (account "bob", alt Bobalt) is in both linkshells, spelled "Bob" on one roster and
    // " bob " on the other. Carol is in linkshell 1. Dave's account is only in the other linkshell.
    private static ApplicationDbContext Seeded()
    {
        var db = NewDb();
        db.Linkshells.AddRange(
            new Linkshell { Id = Ls, LinkshellName = "LS" },
            new Linkshell { Id = OtherLs, LinkshellName = "Other" });
        db.Users.AddRange(
            new AppUser { Id = "bob", UserName = "bob", CharacterName = "Bob", AltCharacterName1 = "Bobalt", JobLevels = new[] { 0, 1 } },
            new AppUser { Id = "carol", UserName = "carol", CharacterName = "Carol", JobLevels = new[] { 0, 50 } },
            new AppUser { Id = "dave", UserName = "dave", CharacterName = "Dave" });
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob", JobLevels = new[] { 0, 1 } },
            new AppUserLinkshell { Id = 2, LinkshellId = OtherLs, AppUserId = "bob", CharacterName = " bob ", JobLevels = new[] { 0, 1 } },
            new AppUserLinkshell { Id = 3, LinkshellId = Ls, AppUserId = "carol", CharacterName = "Carol", JobLevels = new[] { 0, 50 } },
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

    // ---- Write -----------------------------------------------------------------------------

    [Fact]
    public void Write_SetsTheAccountAndEveryMembership_AndLeavesNullArgumentsAlone()
    {
        var account = new AppUser { StrongJobs = new[] { 0, 1 }, MeritJobs = new[] { "kept" } };
        var memberships = new[]
        {
            new AppUserLinkshell { StrongJobs = new[] { 0, 1 }, MeritJobs = new[] { "kept" } },
            new AppUserLinkshell(),
        };

        MainCharacterJobs.Write(account, memberships, jobLevels: new[] { 0, 75 });

        Assert.Equal(new[] { 0, 75 }, account.JobLevels);
        Assert.All(memberships, m => Assert.Equal(new[] { 0, 75 }, m.JobLevels));
        // Each row holds its own copy, so changing one can never change another.
        Assert.NotSame(account.JobLevels, memberships[0].JobLevels);
        Assert.NotSame(memberships[0].JobLevels, memberships[1].JobLevels);
        // Untouched columns stay as they were, everywhere.
        Assert.Equal(new[] { 0, 1 }, account.StrongJobs);
        Assert.Equal(new[] { "kept" }, memberships[0].MeritJobs);
        Assert.Null(memberships[1].StrongJobs);
    }

    // ---- MatchOwnMain ----------------------------------------------------------------------

    [Theory]
    [InlineData("Bob", "Bob")]       // the roster spelling in the token's linkshell
    [InlineData("  BOB ", "Bob")]    // capitals and spaces do not matter
    [InlineData("Bobalt", null)]     // an alt: its jobs are its own, the addon does not write them
    [InlineData("Carol", null)]      // somebody else on the same roster
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MatchOwnMain_AcceptsOnlyTheOwnersMainCharacter(string? typed, string? expected)
    {
        using var db = Seeded();
        var owner = db.Users.Single(u => u.Id == "bob");
        var memberships = db.AppUserLinkshells.Where(m => m.AppUserId == "bob").ToList();

        Assert.Equal(expected, MainCharacterJobs.MatchOwnMain(typed, owner, memberships, Ls));
    }

    [Fact]
    public void MatchOwnMain_FallsBackToTheAccountName_ThenAnotherRoster()
    {
        var owner = new AppUser { CharacterName = " Mainname " };
        var memberships = new[]
        {
            new AppUserLinkshell { LinkshellId = Ls, CharacterName = "Rostername" },
            new AppUserLinkshell { LinkshellId = OtherLs, CharacterName = "Elsewhere" },
        };

        Assert.Equal("Mainname", MainCharacterJobs.MatchOwnMain("mainname", owner, memberships, Ls));
        Assert.Equal("Elsewhere", MainCharacterJobs.MatchOwnMain("ELSEWHERE", owner, memberships, Ls));
    }

    // ---- the addon routes --------------------------------------------------------------------

    [Fact]
    public async Task AddonPost_OwnMain_WritesTheAccountAndEveryMembership()
    {
        await using var db = Seeded();

        var result = await NewAddonController(db, "bob").PostCharacterJobsAsync(
            new AddonApiController.AddonPostJobLevelsRequest(" bob ", new[] { 0, 75, 37 }), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Contains("\"characterName\":\"Bob\"", JsonSerializer.Serialize(ok.Value));
        Assert.Equal(new[] { 0, 75, 37 }, db.Users.Single(u => u.Id == "bob").JobLevels);
        // Both of Bob's memberships, the other linkshell's too, for the previous build.
        Assert.All(db.AppUserLinkshells.Where(m => m.AppUserId == "bob"), m => Assert.Equal(new[] { 0, 75, 37 }, m.JobLevels));
        // Nobody else's.
        Assert.Equal(new[] { 0, 50 }, db.Users.Single(u => u.Id == "carol").JobLevels);
        Assert.Equal(new[] { 0, 50 }, db.AppUserLinkshells.Single(m => m.Id == 3).JobLevels);
    }

    // What the old guard let through: any name on the token's roster.
    [Theory]
    [InlineData("bob", "Carol")]     // another member of the same linkshell
    [InlineData("bob", "Bobalt")]    // the owner's alt (refused before too)
    [InlineData("bob", "Nobody")]
    [InlineData("dave", "Dave")]     // own main, but the owner is not on the token's linkshell
    [InlineData(null, "Bob")]        // a token with no owner
    [InlineData("ghost", "Bob")]     // an owner account that no longer exists
    public async Task AddonPost_AnythingButTheOwnersMain_IsRefusedAndChangesNothing(string? issuer, string name)
    {
        await using var db = Seeded();

        var result = await NewAddonController(db, issuer).PostCharacterJobsAsync(
            new AddonApiController.AddonPostJobLevelsRequest(name, new[] { 0, 99 }), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.DoesNotContain(db.Users.ToList(), u => u.JobLevels is { Length: 2 } levels && levels[1] == 99);
        Assert.DoesNotContain(db.AppUserLinkshells.ToList(), m => m.JobLevels is { Length: 2 } levels && levels[1] == 99);
    }

    [Fact]
    public async Task AddonRoster_ReadsTheMainsJobsFromTheAccount()
    {
        await using var db = Seeded();
        // A membership that drifted (an old build wrote it): the account's value is the answer.
        db.AppUserLinkshells.Single(m => m.Id == 1).JobLevels = new[] { 0, 9 };
        db.Users.Single(u => u.Id == "bob").JobLevels = new[] { 0, 70 };
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
        var real = db.Users.Single(u => u.Id == "real");
        Assert.Equal(expected, real.JobLevels);
        Assert.Equal(new[] { 0, 1 }, real.StrongJobs);
        Assert.Equal(new[] { "note" }, real.MeritJobs);
    }
}
