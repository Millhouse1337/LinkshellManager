using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5: Characters is the only source of names, jobs and crafts, and the save stamps exactly one
// copy of it: the account's CharacterName (the main's, slot 0, name). After EVERY writer path of
// this build every account's CharacterName is exactly its main's name (the "Mains" count of the
// R5.5 build's start-up check, AccountNameMirrorCounts.MainsAsync).
//
// (These replace MirrorParityTests, which checked the exact copy R5.5 made on every save. Until R5.7
// they also checked that the old account columns and the roster names were left unwritten; R5.7
// dropped those columns, and Round5ContractRoundTripTests checks the rollback SQL that brings them
// back filled.)
[Collection("postgres")]
public class AccountNameStampTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private int _linkshellId;

    public AccountNameStampTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        await _pg.ResetAsync();
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        _linkshellId = linkshell.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Profile_RenameSwapClearJobsAndCrafts()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await AddMemberAsync("other", "Other", null, null);

        // Names, jobs and crafts in one profile save (the Activity profile endpoint).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: " Newmain ", TimeZone: null, AltCharacterName1: "Altone", AltCharacterName2: "Alttwo",
            JobLevels: Levels(75), StrongJobs: new[] { true }, MeritJobs: new[] { "Axe" },
            Alt1JobLevels: Levels(50), Alt2JobLevels: Levels(30),
            CraftLevels: new[] { 1, 2 }, Alt1CraftLevels: new[] { 3 })));
        await AssertStampedAsync();
        Assert.Equal(("Newmain", "Altone", "Alttwo"), await NamesAsync("me"));
        // The profile stores each slot's full job list; each slot keeps its own level.
        var jobs = (await JobsAsync("me")).Split('|');
        Assert.Contains("75", jobs[0]);
        Assert.Contains("50", jobs[1]);
        Assert.Contains("30", jobs[2]);

        // The main and alt 1 swap names; alt 2 is cleared (its jobs go with it).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "Altone", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertStampedAsync();
        Assert.Equal(("Altone", "Newmain", null), await NamesAsync("me"));

        // A capitals-only rename.
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertStampedAsync();
        Assert.Equal(("ALTONE", "Newmain", null), await NamesAsync("me"));

        // A refused save (another member's name) changes nothing.
        Assert.IsType<BadRequestObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Other", AltCharacterName2: null)));
        await AssertStampedAsync();
        Assert.Equal(("ALTONE", "Newmain", null), await NamesAsync("me"));

    }

    [Fact]
    public async Task Addon_MainAndAltJobs()
    {
        await AddMemberAsync("me", "Mine", "Altone", "Alttwo");

        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", " mine ", Levels(75)));
        await AssertStampedAsync();
        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", "ALTTWO", Levels(40)));
        await AssertStampedAsync();
        Assert.IsType<ForbidResult>(await PostAddonJobsAsync("me", "Nobody", Levels(1)));
        await AssertStampedAsync();
        Assert.Equal("[75]|-|[40]", await JobsAsync("me"));

    }

    [Fact]
    public async Task PlaceholderCreate_AndAltFillIn_FromTheImportAndTheSignupForm()
    {
        string imported;
        await using (var db = _pg.CreateContext())
        {
            var service = NewManualMembers(db);
            var result = await service.FindOrCreateByNameAsync(_linkshellId, " Newbie ", "Newalt", "NEWBIE", CancellationToken.None);
            Assert.True(result.Success, result.Error);
            imported = result.AppUserId!;
        }
        await AssertStampedAsync();
        Assert.Equal(("Newbie", "Newalt", null), await NamesAsync(imported));

        // The import again: fills only an empty alt.
        await using (var db = _pg.CreateContext())
        {
            var result = await NewManualMembers(db).FindOrCreateByNameAsync(_linkshellId, "newbie", "Ignored", "Secondalt", CancellationToken.None);
            Assert.Equal(imported, result.AppUserId);
        }
        await AssertStampedAsync();
        Assert.Equal(("Newbie", "Newalt", "Secondalt"), await NamesAsync(imported));

        // The Discord sign-up form: a new placeholder for this Discord user, then its alts replaced.
        string formMember;
        await using (var db = _pg.CreateContext())
        {
            var result = await NewManualMembers(db).FindOrCreateForOutsideAsync(_linkshellId, "Formmain", "Formalt", null, "555", CancellationToken.None);
            Assert.True(result.Success, result.Error);
            formMember = result.AppUserId!;
        }
        await using (var db = _pg.CreateContext())
        {
            var result = await NewManualMembers(db).FindOrCreateForOutsideAsync(_linkshellId, "Formmain", "Newformalt", "Formtwo", "555", CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }
        await AssertStampedAsync();
        Assert.Equal(("Formmain", "Newformalt", "Formtwo"), await NamesAsync(formMember));

    }

    [Fact]
    public async Task PlaceholderClaim_MovesTheNamesAndTheRosterRow()
    {
        await AddMemberAsync("real", "Bob", null, null, linkshellId: null);
        await AddMemberAsync("ph", "Bobby", "Bobalt", null, placeholder: true);
        await using (var db = _pg.CreateContext())
        {
            var characters = await CharacterWriter.LoadAsync(db, "ph", CancellationToken.None);
            characters.SetJobs(Character.MainSlot, jobLevels: Levels(60));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var result = await new PlaceholderClaimService(db, new UserMergeService(db))
                .ClaimPlaceholderAsync("ph", "real", _linkshellId, CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        await AssertStampedAsync();
        Assert.Equal(("Bob", "Bobby", "Bobalt"), await NamesAsync("real"));
        await using (var check = _pg.CreateContext())
        {
            // The moved roster row now shows the real account's main.
            var moved = await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).SingleAsync();
            Assert.Equal(("real", "Bob"), (moved.AppUserId, moved.AppUser!.CharacterName));
            Assert.False(await check.Characters.AnyAsync(c => c.AppUserId == "ph"));
            Assert.False(await check.Users.AnyAsync(u => u.Id == "ph"));
        }

    }

    [Fact]
    public async Task DiscordSignIn_NewAccount_PromotedPlaceholder_AndABlankMain()
    {

        // A brand-new account.
        await using (var db = _pg.CreateContext())
        {
            var result = await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("100", "newuser", " New User ", null), null, CancellationToken.None);
            Assert.True(result.IsNewAppUser);
        }
        await AssertStampedAsync();

        // A placeholder made by the sign-up form for this Discord user, with no main name yet.
        await AddMemberAsync("ph", null, "Phalt", null, placeholder: true, discordUserId: "200");
        await using (var db = _pg.CreateContext())
        {
            var result = await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", null, null), null, CancellationToken.None);
            Assert.Equal("ph", result.AppUser.Id);
        }
        await AssertStampedAsync();
        Assert.Equal(("phuser", "Phalt", null), await NamesAsync("ph"));

        // An account linked already, whose main was cleared: the next sign-in fills it.
        await using (var db = _pg.CreateContext())
        {
            var characters = await CharacterWriter.LoadAsync(db, "ph", CancellationToken.None);
            characters.SetName(Character.MainSlot, null);
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync();
        await using (var db = _pg.CreateContext())
        {
            await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", "Ph Global", null), null, CancellationToken.None);
        }
        await AssertStampedAsync();
        Assert.Equal(("Ph Global", "Phalt", null), await NamesAsync("ph"));

    }

    [Fact]
    public async Task DataAdmin_EditOfACharactersName()
    {
        await AddMemberAsync("admin", "Admin", null, null, linkshellId: null);
        await AddMemberAsync("me", "Mine", "Altone", null);

        await using (var db = _pg.CreateContext())
        {
            var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
            var editor = new DataAdminEditor(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);
            var actor = await db.Users.SingleAsync(u => u.Id == "admin");
            var main = await db.Characters.AsNoTracking().SingleAsync(c => c.AppUserId == "me" && c.Slot == Character.MainSlot);
            var result = await editor.UpdateAsync(
                catalog.FindByClrType(typeof(Character))!,
                main.Id,
                new Dictionary<string, string?> { [nameof(Character.Name)] = " Renamed " },
                actor,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));

            // A name another slot of the account has is refused by the database, not half-saved.
            var refused = await editor.UpdateAsync(
                catalog.FindByClrType(typeof(Character))!,
                main.Id,
                new Dictionary<string, string?> { [nameof(Character.Name)] = "ALTONE" },
                actor,
                CancellationToken.None);
            Assert.False(refused.Succeeded);
        }

        await AssertStampedAsync();
        Assert.Equal(("Renamed", "Altone", null), await NamesAsync("me"));

    }

    // A change made straight on CharacterName (not through CharacterWriter) is undone by the save,
    // alone or in a save that also changes the account's characters.
    [Fact]
    public async Task AStraightChangeOfTheMainName_IsUndone()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync();

        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetJobs(Character.Alt1Slot, jobLevels: Levels(10));
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync();
        Assert.Equal(("Mine", "Altone", null), await NamesAsync("me"));

    }

    // A request that loaded the characters before another request renamed the main (the addon job
    // post has no lock) must stamp the database's main, not its own stale one, onto the account.
    [Fact]
    public async Task AStaleContext_StampsTheSavedMain_NotItsOwnOldOne()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await using var stale = _pg.CreateContext();
        var staleSet = await CharacterWriter.LoadAsync(stale, "me", CancellationToken.None);

        await using (var other = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(other, "me", CancellationToken.None);
            set.SetName(Character.MainSlot, "Newmain");
            await other.SaveChangesAsync();
        }

        Assert.True(staleSet.SetJobs(Character.Alt1Slot, jobLevels: Levels(75)));
        await stale.SaveChangesAsync();

        await AssertStampedAsync();
        Assert.Equal(("Newmain", "Altone", null), await NamesAsync("me"));

    }

    // Identity's UpdateAsync marks every column modified. An account loaded before another request
    // changed its characters must not write its old CharacterName back.
    [Fact]
    public async Task AnIdentityUpdateOfAStaleAccount_DoesNotWriteItsOldCopies()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await using var stale = _pg.CreateContext();
        var staleUser = await stale.Users.SingleAsync(u => u.Id == "me");

        await using (var other = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(other, "me", CancellationToken.None);
            set.SetName(Character.MainSlot, "Newmain");
            set.SetJobs(Character.Alt1Slot, jobLevels: Levels(50));
            await other.SaveChangesAsync();
        }

        staleUser.TimeZone = "America/New_York";
        Assert.True((await NewUserManager(stale).UpdateAsync(staleUser)).Succeeded);

        await AssertStampedAsync();
        Assert.Equal(("Newmain", "Altone", null), await NamesAsync("me"));

    }

    // The context that adds a new membership (which loaded the account before another request renamed
    // the main) does not write its old CharacterName back.
    [Fact]
    public async Task ANewMembership_FromAStaleContext_KeepsTheSavedMain()
    {
        await AddMemberAsync("me", "Mine", null, null, linkshellId: null);
        await using var stale = _pg.CreateContext();
        var staleUser = await stale.Users.SingleAsync(u => u.Id == "me");

        await using (var other = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(other, "me", CancellationToken.None);
            set.SetName(Character.MainSlot, "Newmain");
            await other.SaveChangesAsync();
        }

        stale.AppUserLinkshells.Add(LinkshellMembership.NewMember(staleUser, _linkshellId, DateTime.UtcNow));
        await stale.SaveChangesAsync();

        await AssertStampedAsync();
        await using (var check = _pg.CreateContext())
        {
            var link = await check.AppUserLinkshells.AsNoTracking().Include(l => l.AppUser).SingleAsync();
            Assert.Equal("Newmain", link.AppUser!.CharacterName);
        }

    }

    // ---- helpers -------------------------------------------------------------------------------

    private static int[] Levels(int level) => new[] { level };

    // An account with its characters (through the writer), and a membership in the test linkshell
    // unless linkshellId is null.
    private async Task AddMemberAsync(
        string id, string? main, string? alt1, string? alt2, bool placeholder = false, int? linkshellId = -1,
        string? discordUserId = null)
    {
        await using var db = _pg.CreateContext();
        var account = new AppUser { Id = id, UserName = id, IsPlaceholder = placeholder };
        db.Users.Add(account);
        CharacterWriter.ForNewAccount(db, account).SetNames(main, alt1, alt2);
        if (linkshellId is not null)
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = _linkshellId, AppUserId = id, Rank = LinkshellRanks.Member, Status = "Active",
                DiscordUserId = discordUserId, DiscordIdConfirmed = discordUserId is not null,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<(string?, string?, string?)> NamesAsync(string id)
    {
        await using var db = _pg.CreateContext();
        var rows = await db.Characters.AsNoTracking().Where(c => c.AppUserId == id).ToListAsync();
        return (rows.SingleOrDefault(r => r.Slot == 0)?.Name, rows.SingleOrDefault(r => r.Slot == 1)?.Name, rows.SingleOrDefault(r => r.Slot == 2)?.Name);
    }

    // The job levels of the three slots, "-" for none.
    private async Task<string> JobsAsync(string id)
    {
        await using var db = _pg.CreateContext();
        var rows = await db.Characters.AsNoTracking().Where(c => c.AppUserId == id).ToListAsync();
        string Show(short slot) => rows.SingleOrDefault(r => r.Slot == slot)?.JobLevels is { } levels
            ? "[" + string.Join(", ", levels) + "]"
            : "-";
        return string.Join("|", Show(0), Show(1), Show(2));
    }

    // Every account's CharacterName is its main's name.
    private async Task AssertStampedAsync()
    {
        await using var db = _pg.CreateContext();
        Assert.Equal(0, await AccountNameMirrorCounts.MainsAsync(db, CancellationToken.None));
    }

    private async Task<IActionResult> PostProfileAsync(string userId, ActivityUpdateProfileRequest request)
    {
        await using var db = _pg.CreateContext();
        var users = NewUserManager(db);
        var controller = new ActivityOverviewController(
            db, null!, new AppUserProfileService(db, users, new CharacterNameValidator(db)), users,
            null!, null!, NullLogger<ActivityOverviewController>.Instance, null!, null!, null!, null!, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test")),
                },
            },
        };
        return await controller.UpdateProfileAsync(request, CancellationToken.None);
    }

    private async Task<IActionResult> PostAddonJobsAsync(string owner, string name, int[] levels)
    {
        await using var db = _pg.CreateContext();
        var http = new DefaultHttpContext();
        http.Items[LinkshellManagerDiscordApp.Authorization.AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = _linkshellId, IssuedToAppUserId = owner, TokenPrefix = "att_1", TokenHash = "hash-1",
            CreatedAt = DateTime.UtcNow,
        };
        http.Items[LinkshellManagerDiscordApp.Authorization.AddonApiAuthAttribute.LinkshellIdContextKey] = _linkshellId;
        var controller = new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return await controller.PostCharacterJobsAsync(new AddonApiController.AddonPostJobLevelsRequest(name, levels), CancellationToken.None);
    }

    private static ManualMemberService NewManualMembers(ApplicationDbContext db)
    {
        var claims = new PlaceholderClaimService(db, new UserMergeService(db));
        return new ManualMemberService(db, NewUserManager(db), new PlaceholderClaimRequestService(db, claims));
    }

    private static DiscordIdentityService NewIdentity(ApplicationDbContext db) => new(
        null!,
        db,
        NewUserManager(db),
        Microsoft.Extensions.Options.Options.Create(new DiscordOAuthOptions { ClientId = "app", ClientSecret = "secret", BotToken = "bot" }),
        new MemoryCache(new MemoryCacheOptions()),
        NullLogger<DiscordIdentityService>.Instance);

    private static UserManager<AppUser> NewUserManager(ApplicationDbContext db) => new(
        new UserStore<AppUser>(db),
        Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
        new PasswordHasher<AppUser>(),
        Array.Empty<IUserValidator<AppUser>>(),
        Array.Empty<IPasswordValidator<AppUser>>(),
        new UpperInvariantLookupNormalizer(),
        new IdentityErrorDescriber(),
        null!,
        NullLogger<UserManager<AppUser>>.Instance);
}
