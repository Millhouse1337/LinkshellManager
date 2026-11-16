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

#pragma warning disable CS0618 // These tests read the old account columns and the roster name on purpose.

// Round 5 (R5.6): Characters is the only written source of names, jobs and crafts, and the save
// stamps exactly one copy of it: the account's CharacterName (the main's, slot 0, name). After EVERY
// writer path of this build:
// - every account's CharacterName is exactly its main's name, and every roster name its account's
//   main (the R5.5 start-up check's counts, AccountNameMirrorCounts);
// - the old account alt, job and craft columns are NOT written: an account keeps what the R5.5 build
//   last copied there (each test starts from that), and a new account has none.
// Rolling back to R5.5 is safe without SQL: R5.5 reads none of the old columns.
//
// Going back further (to R5.4, which reads the old columns and copies them INTO Characters) needs
// the "mirror refill" SQL from docs/round5-drop-manifest.md first. Each test ends by running it
// (MirrorRefillSql, word for word) and checking that the copies are then exact and that the R5.4
// build's own copy (PreviousBuildCharacterCopy) changes no Characters row.
//
// (These replace MirrorParityTests, which checked the exact copy R5.5 made on every save.)
[Collection("postgres")]
public class AccountNameStampTests : IAsyncLifetime
{
    // docs/round5-drop-manifest.md, "Mirror refill", word for word. Safe to run twice.
    internal const string MirrorRefillSql = """
        UPDATE "AspNetUsers" u SET
          "CharacterName" = m."Name",
          "JobLevels" = m."JobLevels", "StrongJobs" = m."StrongJobs", "MeritJobs" = m."MeritJobs", "CraftLevels" = m."CraftLevels",
          "AltCharacterName1" = a1."Name", "Alt1JobLevels" = a1."JobLevels", "Alt1StrongJobs" = a1."StrongJobs",
          "Alt1MeritJobs" = a1."MeritJobs", "Alt1CraftLevels" = a1."CraftLevels",
          "AltCharacterName2" = a2."Name", "Alt2JobLevels" = a2."JobLevels", "Alt2StrongJobs" = a2."StrongJobs",
          "Alt2MeritJobs" = a2."MeritJobs", "Alt2CraftLevels" = a2."CraftLevels"
        FROM "AspNetUsers" x
        LEFT JOIN "Characters" m  ON m."AppUserId"  = x."Id" AND m."Slot"  = 0
        LEFT JOIN "Characters" a1 ON a1."AppUserId" = x."Id" AND a1."Slot" = 1
        LEFT JOIN "Characters" a2 ON a2."AppUserId" = x."Id" AND a2."Slot" = 2
        WHERE x."Id" = u."Id";
        UPDATE "AppUserLinkshells" l SET "CharacterName" = u."CharacterName"
          FROM "AspNetUsers" u WHERE u."Id" = l."AppUserId" AND l."CharacterName" IS DISTINCT FROM u."CharacterName";
        """;

    // The 14 old columns of every account, NULL shown as "-", so a test can say "not written".
    private const string OldColumnsSql = """
        SELECT u."Id" || '=' || concat_ws('|',
            coalesce(u."AltCharacterName1", '-'), coalesce(u."AltCharacterName2", '-'),
            coalesce(u."JobLevels"::text, '-'), coalesce(u."StrongJobs"::text, '-'), coalesce(u."MeritJobs"::text, '-'),
            coalesce(u."CraftLevels"::text, '-'),
            coalesce(u."Alt1JobLevels"::text, '-'), coalesce(u."Alt1StrongJobs"::text, '-'),
            coalesce(u."Alt1MeritJobs"::text, '-'), coalesce(u."Alt1CraftLevels"::text, '-'),
            coalesce(u."Alt2JobLevels"::text, '-'), coalesce(u."Alt2StrongJobs"::text, '-'),
            coalesce(u."Alt2MeritJobs"::text, '-'), coalesce(u."Alt2CraftLevels"::text, '-')) AS "Value"
        FROM "AspNetUsers" u ORDER BY u."Id"
        """;

    private const string NoOldColumns = "-|-|-|-|-|-|-|-|-|-|-|-|-|-";

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
        var before = await AsTheR55BuildLeftItAsync();

        // Names, jobs and crafts in one profile save (the Activity profile endpoint).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: " Newmain ", TimeZone: null, AltCharacterName1: "Altone", AltCharacterName2: "Alttwo",
            JobLevels: Levels(75), StrongJobs: new[] { true }, MeritJobs: new[] { "Axe" },
            Alt1JobLevels: Levels(50), Alt2JobLevels: Levels(30),
            CraftLevels: new[] { 1, 2 }, Alt1CraftLevels: new[] { 3 })));
        await AssertStampedAsync(before);
        Assert.Equal(("Newmain", "Altone", "Alttwo"), await NamesAsync("me"));
        // The profile stores each slot's full job list; each slot keeps its own level.
        var jobs = (await JobsAsync("me")).Split('|');
        Assert.Contains("75", jobs[0]);
        Assert.Contains("50", jobs[1]);
        Assert.Contains("30", jobs[2]);

        // The main and alt 1 swap names; alt 2 is cleared (its jobs go with it).
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "Altone", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertStampedAsync(before);
        Assert.Equal(("Altone", "Newmain", null), await NamesAsync("me"));

        // A capitals-only rename.
        Assert.IsType<OkObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Newmain", AltCharacterName2: null)));
        await AssertStampedAsync(before);
        Assert.Equal(("ALTONE", "Newmain", null), await NamesAsync("me"));

        // A refused save (another member's name) changes nothing.
        Assert.IsType<BadRequestObjectResult>(await PostProfileAsync("me", new ActivityUpdateProfileRequest(
            CharacterName: "ALTONE", TimeZone: null, AltCharacterName1: "Other", AltCharacterName2: null)));
        await AssertStampedAsync(before);
        Assert.Equal(("ALTONE", "Newmain", null), await NamesAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    [Fact]
    public async Task Addon_MainAndAltJobs()
    {
        await AddMemberAsync("me", "Mine", "Altone", "Alttwo");
        var before = await AsTheR55BuildLeftItAsync();

        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", " mine ", Levels(75)));
        await AssertStampedAsync(before);
        Assert.IsType<OkObjectResult>(await PostAddonJobsAsync("me", "ALTTWO", Levels(40)));
        await AssertStampedAsync(before);
        Assert.IsType<ForbidResult>(await PostAddonJobsAsync("me", "Nobody", Levels(1)));
        await AssertStampedAsync(before);
        Assert.Equal("[75]|-|[40]", await JobsAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    [Fact]
    public async Task PlaceholderCreate_AndAltFillIn_FromTheImportAndTheSignupForm()
    {
        var before = await AsTheR55BuildLeftItAsync();
        string imported;
        await using (var db = _pg.CreateContext())
        {
            var service = NewManualMembers(db);
            var result = await service.FindOrCreateByNameAsync(_linkshellId, " Newbie ", "Newalt", "NEWBIE", CancellationToken.None);
            Assert.True(result.Success, result.Error);
            imported = result.AppUserId!;
        }
        await AssertStampedAsync(before);
        Assert.Equal(("Newbie", "Newalt", null), await NamesAsync(imported));

        // The import again: fills only an empty alt.
        await using (var db = _pg.CreateContext())
        {
            var result = await NewManualMembers(db).FindOrCreateByNameAsync(_linkshellId, "newbie", "Ignored", "Secondalt", CancellationToken.None);
            Assert.Equal(imported, result.AppUserId);
        }
        await AssertStampedAsync(before);
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
        await AssertStampedAsync(before);
        Assert.Equal(("Formmain", "Newformalt", "Formtwo"), await NamesAsync(formMember));

        await AssertTheRefillMakesTheCopiesExactAsync();
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
        var before = await AsTheR55BuildLeftItAsync();

        await using (var db = _pg.CreateContext())
        {
            var result = await new PlaceholderClaimService(db, new UserMergeService(db))
                .ClaimPlaceholderAsync("ph", "real", _linkshellId, CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        await AssertStampedAsync(before);
        Assert.Equal(("Bob", "Bobby", "Bobalt"), await NamesAsync("real"));
        await using (var check = _pg.CreateContext())
        {
            Assert.Equal("Bob", (await check.AppUserLinkshells.AsNoTracking().SingleAsync()).CharacterName);
            Assert.False(await check.Characters.AnyAsync(c => c.AppUserId == "ph"));
            Assert.False(await check.Users.AnyAsync(u => u.Id == "ph"));
        }

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    [Fact]
    public async Task DiscordSignIn_NewAccount_PromotedPlaceholder_AndABlankMain()
    {
        var before = await AsTheR55BuildLeftItAsync();

        // A brand-new account.
        await using (var db = _pg.CreateContext())
        {
            var result = await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("100", "newuser", " New User ", null), null, CancellationToken.None);
            Assert.True(result.IsNewAppUser);
        }
        await AssertStampedAsync(before);

        // A placeholder made by the sign-up form for this Discord user, with no main name yet.
        await AddMemberAsync("ph", null, "Phalt", null, placeholder: true, discordUserId: "200");
        before = await AsTheR55BuildLeftItAsync();
        await using (var db = _pg.CreateContext())
        {
            var result = await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", null, null), null, CancellationToken.None);
            Assert.Equal("ph", result.AppUser.Id);
        }
        await AssertStampedAsync(before);
        Assert.Equal(("phuser", "Phalt", null), await NamesAsync("ph"));

        // An account linked already, whose main was cleared: the next sign-in fills it.
        await using (var db = _pg.CreateContext())
        {
            var characters = await CharacterWriter.LoadAsync(db, "ph", CancellationToken.None);
            characters.SetName(Character.MainSlot, null);
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync(before);
        await using (var db = _pg.CreateContext())
        {
            await NewIdentity(db).ResolveWebsiteSignInAsync(
                new DiscordIdentityProfile("200", "phuser", "Ph Global", null), null, CancellationToken.None);
        }
        await AssertStampedAsync(before);
        Assert.Equal(("Ph Global", "Phalt", null), await NamesAsync("ph"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    [Fact]
    public async Task DataAdmin_EditOfACharactersName()
    {
        await AddMemberAsync("admin", "Admin", null, null, linkshellId: null);
        await AddMemberAsync("me", "Mine", "Altone", null);
        var before = await AsTheR55BuildLeftItAsync();

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

        await AssertStampedAsync(before);
        Assert.Equal(("Renamed", "Altone", null), await NamesAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    // A change made straight on CharacterName or the old columns (not through CharacterWriter) is
    // undone by the save, alone or in a save that also changes the account's characters.
    [Fact]
    public async Task AStraightChangeOfTheMainNameOrTheOldColumns_IsUndone()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        var before = await AsTheR55BuildLeftItAsync();
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            user.AltCharacterName2 = "Sneakyalt";
            user.Alt1JobLevels = Levels(99);
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync(before);

        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "me");
            user.CharacterName = "Sneaky";
            user.AltCharacterName1 = "Sneakyalt";
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetJobs(Character.Alt1Slot, jobLevels: Levels(10));
            await db.SaveChangesAsync();
        }
        await AssertStampedAsync(before);
        Assert.Equal(("Mine", "Altone", null), await NamesAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    // The refill fixes any difference (here: drift made with raw SQL, as a hand edit could leave it),
    // and running it twice changes nothing more.
    [Fact]
    public async Task TheMirrorRefill_FixesDrift_AndIsSafeToRunTwice()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        await AddMemberAsync("you", "Yours", null, null);
        await using (var db = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(db, "me", CancellationToken.None);
            set.SetJobs(Character.MainSlot, jobLevels: Levels(75), strongJobs: Levels(1));
            set.SetCrafts(Character.Alt1Slot, new[] { 3 });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"AspNetUsers\" SET \"AltCharacterName1\" = 'Changed' WHERE \"Id\" = 'me'");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"AspNetUsers\" SET \"CharacterName\" = ' Yours ' WHERE \"Id\" = 'you'");
            await db.Database.ExecuteSqlRawAsync("UPDATE \"AppUserLinkshells\" SET \"CharacterName\" = 'Old' WHERE \"AppUserId\" = 'me'");
            Assert.False((await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None)).AllMatch);
        }

        await AssertTheRefillMakesTheCopiesExactAsync();
        await AssertTheRefillMakesTheCopiesExactAsync();
        await using var check = _pg.CreateContext();
        Assert.Equal(new[] { "me=Altone|-|[75]|[1]|-|-|-|-|-|[3]|-|-|-|-", "you=" + NoOldColumns },
            await check.Database.SqlQueryRaw<string>(OldColumnsSql).ToListAsync());
    }

    // A request that loaded the characters before another request renamed the main (the addon job
    // post has no lock) must stamp the database's main, not its own stale one, onto the account.
    [Fact]
    public async Task AStaleContext_StampsTheSavedMain_NotItsOwnOldOne()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        var before = await AsTheR55BuildLeftItAsync();
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

        await AssertStampedAsync(before);
        Assert.Equal(("Newmain", "Altone", null), await NamesAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    // Identity's UpdateAsync marks every column modified. An account loaded before another request
    // changed its characters must not write its old CharacterName (or any old column) back.
    [Fact]
    public async Task AnIdentityUpdateOfAStaleAccount_DoesNotWriteItsOldCopies()
    {
        await AddMemberAsync("me", "Mine", "Altone", null);
        var before = await AsTheR55BuildLeftItAsync();
        await using var stale = _pg.CreateContext();
        var staleUser = await stale.Users.SingleAsync(u => u.Id == "me");

        await using (var other = _pg.CreateContext())
        {
            var set = await CharacterWriter.LoadAsync(other, "me", CancellationToken.None);
            set.SetName(Character.MainSlot, "Newmain");
            set.SetJobs(Character.Alt1Slot, jobLevels: Levels(50));
            await other.SaveChangesAsync();
        }
        // The old columns as if R5.5 had made that save (so they differ from what staleUser holds).
        before = await AsTheR55BuildLeftItAsync();

        staleUser.TimeZone = "America/New_York";
        Assert.True((await NewUserManager(stale).UpdateAsync(staleUser)).Succeeded);

        await AssertStampedAsync(before);
        Assert.Equal(("Newmain", "Altone", null), await NamesAsync("me"));

        await AssertTheRefillMakesTheCopiesExactAsync();
    }

    // A new membership gets the account's main as saved in the database, even when the context that
    // adds it loaded the account before another request renamed the main.
    [Fact]
    public async Task ANewMembership_FromAStaleContext_GetsTheSavedMain()
    {
        await AddMemberAsync("me", "Mine", null, null, linkshellId: null);
        var before = await AsTheR55BuildLeftItAsync();
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

        await AssertStampedAsync(before);

        await AssertTheRefillMakesTheCopiesExactAsync();
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

    // The old columns as the R5.5 build leaves them (an exact copy of Characters, which is what the
    // refill writes), returned so the test can check nothing writes them after.
    private async Task<List<string>> AsTheR55BuildLeftItAsync()
    {
        await using (var db = _pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(MirrorRefillSql);
            Assert.True((await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None)).AllMatch);
        }
        return await OldColumnsAsync();
    }

    private async Task<List<string>> OldColumnsAsync()
    {
        await using var db = _pg.CreateContext();
        return await db.Database.SqlQueryRaw<string>(OldColumnsSql).ToListAsync();
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

    // CharacterName is the main's name and every roster name the account's main; the old columns of
    // every account in `before` are as they were, and an account made since has none.
    private async Task AssertStampedAsync(List<string> before)
    {
        await using var db = _pg.CreateContext();
        var counts = await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None);
        Assert.Equal(0, counts.Mains);
        Assert.Equal(0, counts.RosterNames);

        var previous = before.ToDictionary(line => line[..line.IndexOf('=')], StringComparer.Ordinal);
        foreach (var line in await db.Database.SqlQueryRaw<string>(OldColumnsSql).ToListAsync())
        {
            var id = line[..line.IndexOf('=')];
            Assert.Equal(previous.TryGetValue(id, out var was) ? was : id + "=" + NoOldColumns, line);
        }
    }

    // After the refill the copies are exact (the R5.5 start-up check's counts are all 0) and the
    // R5.4 build's own copy into Characters changes nothing.
    private async Task AssertTheRefillMakesTheCopiesExactAsync()
    {
        await using var db = _pg.CreateContext();
        await db.Database.ExecuteSqlRawAsync(MirrorRefillSql);
        Assert.Equal(new AccountNameMirrorCounts.Result(0, 0, 0), await AccountNameMirrorCounts.CountAsync(db, CancellationToken.None));

        var before = await CharactersSnapshotAsync(db);
        await PreviousBuildCharacterCopy.RunForAllAccountsAsync(db);
        Assert.Equal(before, await CharactersSnapshotAsync(db));
    }

    private static async Task<List<string>> CharactersSnapshotAsync(ApplicationDbContext db) =>
        await db.Database.SqlQueryRaw<string>(
                "SELECT concat_ws('|', \"Id\", \"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"JobLevels\"::text, " +
                "\"StrongJobs\"::text, \"MeritJobs\"::text, \"CraftLevels\"::text, \"UpdatedAt\"::text) AS \"Value\" " +
                "FROM \"Characters\" ORDER BY \"Id\"")
            .ToListAsync();

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
