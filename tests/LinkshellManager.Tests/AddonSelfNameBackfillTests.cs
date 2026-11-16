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

// Round 5: the addon's self-name backfill (an attendance post that says which character the token
// owner is logged in as) writes the owner's main through CharacterWriter when the account has none.
// The save copies it onto the account's CharacterName, exactly as every other writer
// (AccountNameStampTests covers the others on Postgres).
public class AddonSelfNameBackfillTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    // The token owner "me" has no main yet; alt is the name of its one alt (or none).
    private static ApplicationDbContext Seeded(string? alt)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = Ls, Name = LinkshellRanks.Member, CanManageTods = true, CanManageAttendanceWithAddon = true,
        });
        var me = new AppUser { Id = "me", UserName = "login" };
        db.Users.Add(me);
        CharacterWriter.ForNewAccount(db, me).SetName(Character.Alt1Slot, alt);
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "me", Rank = LinkshellRanks.Member });
        db.Events.Add(new Event
        {
            Id = 100, LinkshellId = Ls, EventName = "Sky", EventType = "Sky",
            StartTime = DateTime.UtcNow.AddHours(-1), CommencementStartTime = DateTime.UtcNow.AddHours(-1),
        });
        db.SaveChanges();
        return db;
    }

    private static AddonApiController NewAddonController(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1, LinkshellId = Ls, IssuedToAppUserId = "me", TokenPrefix = "att_1", TokenHash = "hash-1",
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

    private static Task<IActionResult> PostAsync(ApplicationDbContext db, string selfName) =>
        NewAddonController(db).PostAttendanceAsync(
            100,
            new AddonApiController.AddonAttendanceRequest(
                DateTime.UtcNow, [new AddonApiController.AddonAttendanceEntry(selfName, null, null, null)], SelfCharacterName: selfName),
            CancellationToken.None);

    [Fact]
    public async Task AnOwnerWithNoMain_GetsTheLoggedInCharacterAsMain_CopiedToTheAccount()
    {
        await using var db = Seeded(alt: "Myalt");

        Assert.IsType<OkObjectResult>(await PostAsync(db, " Selfy "));

        var rows = await db.Characters.AsNoTracking().Where(c => c.AppUserId == "me").OrderBy(c => c.Slot).ToListAsync();
        Assert.Equal(new[] { ((short)0, "Selfy"), ((short)1, "Myalt") }, rows.Select(c => (c.Slot, c.Name)));
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Equal("Selfy", me.CharacterName);
        // The attendance went to the owner.
        Assert.Equal("me", (await db.AppUserEvents.AsNoTracking().SingleAsync()).AppUserId);
    }

    // The logged-in character is already one of the owner's alts: it stays the alt, and the account
    // keeps having no main (one account never holds a name twice).
    [Fact]
    public async Task ANameTheOwnerHasAsAnAlt_IsNotCopiedToTheMain()
    {
        await using var db = Seeded(alt: "Selfy");

        Assert.IsType<OkObjectResult>(await PostAsync(db, "selfy"));

        var rows = await db.Characters.AsNoTracking().Where(c => c.AppUserId == "me").ToListAsync();
        Assert.Equal(new[] { ((short)1, "Selfy") }, rows.Select(c => (c.Slot, c.Name)));
        var me = await db.Users.AsNoTracking().SingleAsync(u => u.Id == "me");
        Assert.Null(me.CharacterName);
    }
}
