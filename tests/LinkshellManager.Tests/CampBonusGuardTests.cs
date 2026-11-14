using System.Security.Claims;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.Tests;

// The live camp card's open / close / kill bonus cells edit the camp only while it is running.
//
// They used to refuse on `Event.EndTime is not null`, which never happens on an HNM board: End Camp
// parks the board (WdFinalizedAt, CommencementStartTime cleared) and leaves EndTime null. So the
// "closed event" refusal could never fire, and a parked or queued board took the edit.
public class CampBonusGuardTests
{
    private const int Ls = 7;
    private const string User = "officer";
    private const int EventId = 20;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public enum Board { Running, Parked, Queued }

    private static async Task<ApplicationDbContext> SeededAsync(Board board)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "Test", HnmStandardCloseBonus = 1 });
        db.Users.Add(new AppUser { Id = User, UserName = User, CharacterName = "Officer" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = User, LinkshellId = Ls, CharacterName = "Officer", Rank = LinkshellRanks.Leader,
        });
        var now = DateTime.UtcNow;
        db.Events.Add(new Event
        {
            Id = EventId,
            LinkshellId = Ls,
            EventName = "Fafnir",
            EventType = "HNM",
            StartTime = now.AddHours(-1),
            CommencementStartTime = board == Board.Running ? now.AddHours(-1) : null,
            WdFinalizedAt = board == Board.Parked ? now.AddMinutes(-5) : null,
        });
        await db.SaveChangesAsync();
        return db;
    }

    // The web / Activity route: a signed-in officer.
    private static AddonApiController Management(ApplicationDbContext db)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, User) }, authenticationType: "Test")),
        };
        return new AddonApiController(
            db, null!, userManager, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static async Task<Event> CampAsync(ApplicationDbContext db) =>
        await db.Events.AsNoTracking().SingleAsync(e => e.Id == EventId);

    // ---- a running camp: saved, as before ----

    [Fact]
    public async Task Running_CloseBonus_IsSaved()
    {
        await using var db = await SeededAsync(Board.Running);

        var result = await Management(db).SetCampCloseBonusManagementAsync(
            EventId, new AddonApiController.AddonSetCloseBonusRequest(3), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(3, (await CampAsync(db)).HnmCloseBonusOverride);
    }

    [Fact]
    public async Task Running_OpenBonus_IsSaved()
    {
        await using var db = await SeededAsync(Board.Running);

        var result = await Management(db).SetCampOpenBonusManagementAsync(
            EventId, new AddonApiController.AddonSetOpenBonusRequest(2), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(2, (await CampAsync(db)).HnmOpenBonusOverride);
    }

    [Fact]
    public async Task Running_KillBonus_IsSaved()
    {
        await using var db = await SeededAsync(Board.Running);

        var result = await Management(db).SetCampKillBonusManagementAsync(
            EventId, new AddonApiController.AddonSetKillBonusRequest(5), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(5, (await CampAsync(db)).HnmKillBonusOverride);
    }

    // ---- after End Camp, or before the camp starts: refused, nothing written ----

    [Theory]
    [InlineData(Board.Parked)]
    [InlineData(Board.Queued)]
    public async Task NotRunning_CloseBonus_IsRefused(Board board)
    {
        await using var db = await SeededAsync(board);

        var result = await Management(db).SetCampCloseBonusManagementAsync(
            EventId, new AddonApiController.AddonSetCloseBonusRequest(3), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await CampAsync(db)).HnmCloseBonusOverride);
    }

    [Theory]
    [InlineData(Board.Parked)]
    [InlineData(Board.Queued)]
    public async Task NotRunning_OpenBonus_IsRefused(Board board)
    {
        await using var db = await SeededAsync(board);

        var result = await Management(db).SetCampOpenBonusManagementAsync(
            EventId, new AddonApiController.AddonSetOpenBonusRequest(2), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await CampAsync(db)).HnmOpenBonusOverride);
    }

    [Theory]
    [InlineData(Board.Parked)]
    [InlineData(Board.Queued)]
    public async Task NotRunning_KillBonus_IsRefused(Board board)
    {
        await using var db = await SeededAsync(board);

        var result = await Management(db).SetCampKillBonusManagementAsync(
            EventId, new AddonApiController.AddonSetKillBonusRequest(5), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null((await CampAsync(db)).HnmKillBonusOverride);
    }
}
