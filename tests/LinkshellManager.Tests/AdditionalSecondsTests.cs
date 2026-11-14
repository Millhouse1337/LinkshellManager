using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A monster's "Additional Seconds" (Monster Setups): a repop is ToD + cooldown + those seconds, and a
// reposted board starts then. The addon left them off, so the same kill gave a different start time
// depending on where it was logged. These pin that it no longer does, and that the surfaces which take
// a ToD say so.
public class AdditionalSecondsTests
{
    private const int LinkshellId = 7;
    private const string Officer = "user-officer";
    private const string Monster = "Cerberus";
    private const int Seconds = 16;

    private static readonly DateTime KilledAt = new(2026, 11, 5, 13, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpectedRepop = KilledAt.AddHours(48).AddSeconds(Seconds);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static LinkshellMonsterTiming Setup(string monster = Monster, int seconds = Seconds, int cooldownMinutes = 48 * 60) => new()
    {
        LinkshellId = LinkshellId,
        MonsterName = monster,
        NormalizedMonsterName = MonsterTimingDefaults.Normalize(monster),
        CooldownMinutes = cooldownMinutes,
        AdditionalSeconds = seconds,
    };

    // ---- The addon's ToD post ----

    private static async Task<ApplicationDbContext> SeededForAddonAsync(bool canManageTods, int seconds = Seconds)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.LinkshellMonsterTimings.Add(Setup(seconds: seconds));
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = LinkshellId,
            Name = LinkshellRanks.Member,
            CanManageTods = canManageTods,
            CanSubmitTodForApproval = true,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = Officer,
            LinkshellId = LinkshellId,
            Rank = LinkshellRanks.Member,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static AddonApiController NewAddonController(ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(new SubmissionApprovalService(db, TodCallers.Recorder(db), NullLogger<SubmissionApprovalService>.Instance));
        services.AddSingleton(TodCallers.Recorder(db));

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1,
            LinkshellId = LinkshellId,
            IssuedToAppUserId = Officer,
            TokenPrefix = "att_1",
            TokenHash = "hash-1",
            CreatedAt = KilledAt,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;

        return new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
    }

    private static AddonApiController.AddonPostTodRequest PostRequest() =>
        new(Monster, KilledAt, null, true, null, null);

    [Fact]
    public async Task AddonToD_IncludesTheMonstersAdditionalSeconds()
    {
        await using var db = await SeededForAddonAsync(canManageTods: true);

        var result = await NewAddonController(db).PostTodAsync(PostRequest(), CancellationToken.None);

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(ExpectedRepop, tod.RepopTime);
        // On the row too, or editing this ToD later recomputes its repop without them.
        Assert.Equal(Seconds, tod.AdditionalSeconds);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(Seconds, body.GetProperty("additionalSeconds").GetInt32());
    }

    [Fact]
    public async Task AddonToD_WithNoAdditionalSeconds_IsToDPlusCooldown()
    {
        await using var db = await SeededForAddonAsync(canManageTods: true, seconds: 0);

        await NewAddonController(db).PostTodAsync(PostRequest(), CancellationToken.None);

        Assert.Equal(KilledAt.AddHours(48), (await db.Tods.SingleAsync()).RepopTime);
    }

    // The approval queue holds the repop the officer will approve, so it has to be the right one.
    [Fact]
    public async Task AddonToD_QueuedForApproval_IncludesThemToo()
    {
        await using var db = await SeededForAddonAsync(canManageTods: false);

        await NewAddonController(db).PostTodAsync(PostRequest(), CancellationToken.None);

        Assert.Equal(ExpectedRepop, (await db.PendingTodSubmissions.SingleAsync()).RepopTime);
    }
}
