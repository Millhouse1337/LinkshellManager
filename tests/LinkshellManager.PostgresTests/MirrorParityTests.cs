using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using LinkshellManagerDiscordApp.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

#pragma warning disable CS0618 // These tests read the mirror columns and the roster name on purpose.

// Round 5 (R5.5): what makes rolling back to the build before the name switch (603439d) safe.
//
// Characters is the only written source of names, jobs and crafts now, and every save copies it
// onto the account's old columns and the roster names. The old build reads those copies, and at
// start-up (and on every account save) copies the account columns back INTO Characters. So after
// EVERY writer path of this build:
// - the pre-check's A7 query finds no account whose columns differ from Characters, CharacterName is
//   exactly the main's name, and every roster name is exactly its account's main
//   (AccountNameMirrorCounts, the R5.5 start-up check);
// - the account columns are an EXACT copy of Characters (not just equal after trimming);
// - the old build's own copy (PreviousBuildCharacterCopy, frozen from 603439d) changes no row.
//
// (These replace CharacterSyncHookTests, which tested the copy the other way, now removed.)
[Collection("postgres")]
public class MirrorParityTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private int _linkshellId;

    public MirrorParityTests(PostgresFixture pg) => _pg = pg;

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
        await AssertMirrorExactAsync();

        // Names, jobs and crafts in one profile save (the Activity profile endpoint).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: " Newmain ", TimeZone: null, AltCharacterName1: "Altone", AltCharacterName2: "Alttwo",
            JobLevels: Levels(75), StrongJobs: new[] { true }, MeritJobs: new[] { "Axe" },
            Alt1JobLevels: Levels(50), Alt2JobLevels: Levels(30),
            CraftLevels: new[] { 1, 2 }, Alt1CraftLevels: new[] { 3 })));
        await AssertMirrorExactAsync();
        Assert.Equal(("Newmain", "Altone", "Alttwo"), await NamesAsync("me"));

        // The main and alt 1 swap names; alt 2 is cleared (its jobs go with it).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "Altone", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertMirrorExactAsync();
        Assert.Equal(("Altone", "Newmain", null), await NamesAsync("me"));

        // A capitals-only rename.
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertMirrorExactAsync();

        // A refused save (another member's name) changes nothing.
        Assert.IsType<BadRequestObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Other", AltCharacterName2: null)));
        await AssertMirrorExactAsync();
    }

    [Fact]
    public async Task Addon_MainAndAltJobs()
    {
        await AddMemberAsync("me", "Mine", "Altone", "Alttwo");

        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", " mine ", Levels(75)));
        await AssertMirrorExactAsync();
        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", "ALTTWO", Levels(40)));
        await AssertMirrorExactAsync();
        Assert.IsType<ForbidResult>(await PostAddonJobsAsync("me", "Nobody", Levels(1)));
        await AssertMirrorExactAsync();
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
        await AssertMirrorExactAsync();
        Assert.Equal(("Newbie", "Newalt", null), await NamesAsync(imported));

        // The import again: fills only an empty alt.
        await using (var db = _pg.CreateContext())
        {
            var result = await NewManualMembers(db).FindOrCreateByNameAsync(_linkshellId, "newbie", "Ignored", "Secondalt", CancellationToken.None);
            Assert.Equal(imported, result.AppUserId);
        }
        await AssertMirrorExactAsync();
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
        await AssertMirrorExactAsync();
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

        await AssertMirrorExactAsync();
        Assert.Equal(("Bob", "Bobby", "Bobalt"), await NamesAsync("real"));
        await using var check = _pg.CreateContext();
        Assert.Equal("Bob", (await check.AppUserLinkshells.AsNoTracking().SingleAsync()).CharacterName);
        Assert.False(await check.Characters.AnyAsync(c => c.AppUserId == "ph"));
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
        await AssertMirrorExactAsync();

        // A placeholder made by the sign-up form for this Discord user, with no main name yet.
        await AddMemberAsync("ph", null, "Phalt", null, placeholder: true, discordUserId: "200");
        await using (var db = _pg.CreateContext())
        {
            var result = await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", null, null), null, CancellationToken.None);
            Assert.Equal("ph", result.AppUser.Id);
        }
        await AssertMirrorExactAsync();
        Assert.Equal(("phuser", "Phalt", null), await NamesAsync("ph"));

        // An account linked already, whose main was cleared: the next sign-in fills it.
        await using (var db = _pg.CreateContext())
        {
            var characters = await CharacterWriter.LoadAsync(db, "ph", CancellationToken.None);
            characters.SetName(Character.MainSlot, null);
            await db.SaveChangesAsync();
        }
        await AssertMirrorExactAsync();
        await using (var db = _pg.CreateContext())
        {
            await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", "Ph Global", null), null, CancellationToken.None);
        }
        await AssertMirrorExactAsync();
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

        await AssertMirrorExactAsync();
        Assert.Equal(("Renamed", "Altone", null), await NamesAsync("me"));
    }

    // A change made straight on the old columns (not through CharacterWriter) is undone by the save,
    // so the mirror can never drift.
    [Fact]
    public async Task AStraightChangeOfTheOldColumns_IsUndone()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            user.AltCharacterName2 = "Sneakyalt";
            user.Alt1JobLevels = Levels(99);
            await db.SaveChangesAsync();
        }

        await AssertMirrorExactAsync();
        Assert.Equal(("Mine", "Altone", null), await NamesAsync("me"));
    }

    // The start-up check counts what differs (here: drift made with raw SQL, as an older build or a
    // hand edit could leave it).
    [Fact]
    public async Task TheStartupCheck_CountsDrift()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await AddMemberAsync("you", "Yours", null, null);
        await using var db = _pg.CreateContext();
        Assert.True((await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None)).AllMatch);

        await db.Database.ExecuteSqlRawAsync("UPDATE \"AspNetUsers\" SET \"AltCharacterName1\" = 'Changed' WHERE \"Id\" = 'me'");
        await db.Database.ExecuteSqlRawAsync("UPDATE \"AspNetUsers\" SET \"CharacterName\" = ' Yours ' WHERE \"Id\" = 'you'");
        await db.Database.ExecuteSqlRawAsync("UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = 'Old' WHERE \"AppUserId\" = 'me'");

        // ' Yours ' still copies to the same row (A7 trims), but it is not exactly the main's name, and
        // its roster name no longer equals it either: two roster names differ.
        Assert.Equal(new AccountNameMirrorCounts.Result(1, 1, 2), await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None));
    }

    // A request that loaded the characters before another request renamed one (the addon job post
    // has no lock) must copy the database's rows, not its own stale ones, onto the account.
    [Fact]
    public async Task AStaleContext_CopiesTheSavedRows_NotItsOwnOldOnes()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await using var stale = _pg.CreateContext();
        var staleSet = await CharacterWriter.LoadAsync(stale, "me", CancellationToken.None);

        await using (var other = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(other, "me", CancellationToken.None);
            set.SetName(Character.Alt1Slot, "Newalt");
            await other.SaveChangesAsync();
        }

        Assert.True(staleSet.SetJobs(Character.MainSlot, jobLevels: Levels(75)));
        await stale.SaveChangesAsync();

        await AssertMirrorExactAsync();
        Assert.Equal(("Mine", "Newalt", null), await NamesAsync("me"));
    }

    // Identity's UpdateAsync marks every column modified. An account loaded before another request
    // changed its characters must not write its old copies back over the new ones.
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

        await AssertMirrorExactAsync();
        Assert.Equal(("Newmain", "Altone", null), await NamesAsync("me"));
    }

    // A new membership gets the account's main as saved in the database, even when the context that
    // adds it loaded the account before another request renamed the main.
    [Fact]
    public async Task ANewMembership_FromAStaleContext_GetsTheSavedMain()
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

        await AssertMirrorExactAsync();
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

    private async Task AssertMirrorExactAsync()
    {
        await using var db = _pg.CreateContext();

        // 1. The start-up check (A7, the main, the roster names).
        Assert.Equal(new AccountNameMirrorCounts.Result(0, 0, 0), await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None));

        // 2. The account columns are an exact copy of the three slots.
        var characters = await db.Characters.AsNoTracking().ToListAsync();
        foreach (var user in await db.Users.AsNoTracking().ToListAsync())
        {
            Character? Slot(short slot) => characters.SingleOrDefault(c => c.AppUserId == user.Id && c.Slot == slot);
            var (main, alt1, alt2) = (Slot(0), Slot(1), Slot(2));
            Assert.Equal(main?.Name, user.CharacterName);
            Assert.Equal(alt1?.Name, user.AltCharacterName1);
            Assert.Equal(alt2?.Name, user.AltCharacterName2);
            Assert.Equal(main?.JobLevels, user.JobLevels);
            Assert.Equal(main?.StrongJobs, user.StrongJobs);
            Assert.Equal(main?.MeritJobs, user.MeritJobs);
            Assert.Equal(main?.CraftLevels, user.CraftLevels);
            Assert.Equal(alt1?.JobLevels, user.Alt1JobLevels);
            Assert.Equal(alt1?.StrongJobs, user.Alt1StrongJobs);
            Assert.Equal(alt1?.MeritJobs, user.Alt1MeritJobs);
            Assert.Equal(alt1?.CraftLevels, user.Alt1CraftLevels);
            Assert.Equal(alt2?.JobLevels, user.Alt2JobLevels);
            Assert.Equal(alt2?.StrongJobs, user.Alt2StrongJobs);
            Assert.Equal(alt2?.MeritJobs, user.Alt2MeritJobs);
            Assert.Equal(alt2?.CraftLevels, user.Alt2CraftLevels);
        }

        // 3. The old build's copy into Characters changes nothing.
        var before = await SnapshotAsync(db);
        await PreviousBuildCharacterCopy.RunForAllAccountsAsync(db);
        Assert.Equal(before, await SnapshotAsync(db));
    }

    private static async Task<List<string>> SnapshotAsync(ApplicationDbContext db) =>
        (await db.Database.SqlQueryRaw<string>(
                "SELECT concat_ws('|', \"Id\", \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"JobLevels\"::text, " +
                "\"StrongJobs\"::text, \"MeritJobs\"::text, \"CraftLevels\"::text, \"UpdatedAt\"::text) AS \"Value\" " +
                "FROM \"Characters\" ORDER BY \"Id\"")
            .ToListAsync());

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
