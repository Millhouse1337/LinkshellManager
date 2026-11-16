using System.Text.Json;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The addon's GET /tods keeps ONE row per spawn (SpawnKey), like the app's ToD tracker
// (LatestTodLookup, and the ToD tab's mergedMonsterName). It used to keep one row per monster NAME,
// so a merge pair arrived as two rows ("Fafnir" and "Nidhogg") where the app showed one.
//
// The row is the spawn's newest ToD (Time ?? TimeStamp, as before), labelled with the pair's
// combined name, which is what the app shows. The addon only displays and matches that name (it
// already collapses merge pairs itself and matches either half), and posts ToDs under the half the
// kill message named, never under a tracker row's name.
public class AddonTodListOnePerSpawnTests
{
    private const int LinkshellId = 51;
    private static readonly DateTime KilledAt = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    private static async Task<List<JsonElement>> ListAsync(ApplicationDbContext db)
    {
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;
        var controller = new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.ListTodsAsync(CancellationToken.None);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value)
            .GetProperty("tods").EnumerateArray().ToList();
    }

    private static async Task<ApplicationDbContext> SeededAsync(string hidden = "", params Tod[] tods)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test", HiddenTodMonsters = hidden });
        foreach (var tod in tods)
        {
            tod.LinkshellId = LinkshellId;
            tod.TimeStamp ??= tod.Time;
        }
        db.Tods.AddRange(tods);
        await db.SaveChangesAsync();
        return db;
    }

    private static Tod Kill(int id, string monster, int hoursAgo, int? day = null) => new()
    {
        Id = id, MonsterName = monster, DayNumber = day, Cooldown = "22 Hour",
        Time = KilledAt.AddHours(-hoursAgo), RepopTime = KilledAt.AddHours(22 - hoursAgo),
    };

    [Fact]
    public async Task AMergePair_IsOneRow_TheNewestKill_UnderTheCombinedName()
    {
        await using var db = await SeededAsync("",
            Kill(1, "Fafnir", hoursAgo: 100, day: 2),
            Kill(2, "Nidhogg", hoursAgo: 1, day: 6),
            Kill(3, "Fafnir/Nidhogg", hoursAgo: 200, day: 1),
            Kill(4, "King Behemoth", hoursAgo: 300),
            Kill(5, "behemoth", hoursAgo: 5, day: 3),
            Kill(6, "Aspidochelone", hoursAgo: 7),
            Kill(7, "Tiamat", hoursAgo: 2),
            Kill(8, "tiamat", hoursAgo: 90));

        var tods = await ListAsync(db);

        Assert.Equal(
            new[] { "Adamantoise/Aspidochelone", "Behemoth/King Behemoth", "Fafnir/Nidhogg", "Tiamat" },
            tods.Select(t => t.GetProperty("monsterName").GetString()).Order().ToArray());

        var wyrm = tods.Single(t => t.GetProperty("monsterName").GetString() == "Fafnir/Nidhogg");
        Assert.Equal(2, wyrm.GetProperty("id").GetInt32());
        Assert.Equal(6, wyrm.GetProperty("dayNumber").GetInt32());
        // The pair's grid from Monster Setups (built-in defaults here): seven 10-minute windows.
        Assert.True(wyrm.GetProperty("hasSpawnGrid").GetBoolean());
        Assert.Equal(7, wyrm.GetProperty("windowCount").GetInt32());
        Assert.Equal(600, wyrm.GetProperty("windowCadenceSeconds").GetInt32());

        Assert.Equal(5, tods.Single(t => t.GetProperty("monsterName").GetString() == "Behemoth/King Behemoth").GetProperty("id").GetInt32());
        Assert.Equal(7, tods.Single(t => t.GetProperty("monsterName").GetString() == "Tiamat").GetProperty("id").GetInt32());
    }

    // The same newest-ToD rule as the app's tracker (LatestTodLookup): one id per spawn.
    [Fact]
    public async Task TheRows_AreTheAppsLatestToDs()
    {
        await using var db = await SeededAsync("",
            Kill(1, "Fafnir", hoursAgo: 100, day: 2),
            Kill(2, "Nidhogg", hoursAgo: 1, day: 6),
            Kill(3, " Cerberus ", hoursAgo: 4),
            Kill(4, "CERBERUS", hoursAgo: 50),
            Kill(5, "Some Custom NM", hoursAgo: 9));

        var addonIds = (await ListAsync(db)).Select(t => t.GetProperty("id").GetInt32()).Order().ToArray();
        var appIds = (await LatestTodLookup.ForLinkshellAsync(db, LinkshellId, CancellationToken.None))
            .Select(entry => entry.TodId).Order().ToArray();

        Assert.Equal(new[] { 2, 3, 5 }, addonIds);
        Assert.Equal(appIds, addonIds);
    }

    // A ToD that was never entered (no Time) still wins its spawn when it is the newest by
    // TimeStamp, as before, so the addon shows "Not entered" rather than the superseded countdown.
    [Fact]
    public async Task ANotEnteredToD_StillWinsItsSpawn()
    {
        var notEntered = new Tod { Id = 9, MonsterName = "Fafnir/Nidhogg", TimeStamp = KilledAt };
        await using var db = await SeededAsync("", Kill(1, "Nidhogg", hoursAgo: 30), notEntered);

        var row = Assert.Single(await ListAsync(db));

        Assert.Equal(9, row.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("repopTimeUtc").ValueKind);
    }

    // Hidden monsters are still filtered by the name each ToD was stored under, before the rows are
    // merged, so the list shows what the addon showed before (it merged the pair itself).
    [Fact]
    public async Task HiddenMonsters_AreStillLeftOut()
    {
        await using var db = await SeededAsync("Tiamat|Nidhogg",
            Kill(1, "Fafnir", hoursAgo: 100, day: 2),
            Kill(2, "Nidhogg", hoursAgo: 1, day: 6),
            Kill(3, "Tiamat", hoursAgo: 2),
            Kill(4, "Vrtra", hoursAgo: 2));

        var tods = await ListAsync(db);

        Assert.Equal(new[] { "Fafnir/Nidhogg", "Vrtra" }, tods.Select(t => t.GetProperty("monsterName").GetString()).Order().ToArray());
        Assert.Equal(1, tods.Single(t => t.GetProperty("monsterName").GetString() == "Fafnir/Nidhogg").GetProperty("id").GetInt32());
    }

    // A ToD with a blank name answers for no spawn, the same as in the app.
    [Fact]
    public async Task ABlankName_IsLeftOut()
    {
        await using var db = await SeededAsync("", Kill(1, "  ", hoursAgo: 1), Kill(2, "Vrtra", hoursAgo: 2));

        var row = Assert.Single(await ListAsync(db));

        Assert.Equal("Vrtra", row.GetProperty("monsterName").GetString());
    }
}
