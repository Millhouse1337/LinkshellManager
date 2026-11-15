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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The ToD rows the Activity and the addon read gain cooldownMinutes / intervalMinutes: the stored
// whole minutes beside the texts, which stay exactly as they were. Additive, so an addon that only
// reads the texts sees no change.
public class TodMinutesInResponsesTests
{
    private const int LinkshellId = 31;
    private static readonly DateTime KilledAt = new(2026, 11, 12, 3, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheActivityTodRow_CarriesTheStoredMinutes_BesideTheTexts()
    {
        await using var db = await SeededAsync();
        var tods = await db.Tods.OrderBy(t => t.Id).ToListAsync();

        var fafnir = ActivityMappers.MapTodDto(tods[0]);
        Assert.Equal("84 Hour", fafnir.Cooldown);
        Assert.Equal("1 Hour", fafnir.Interval);
        Assert.Equal(5040, fafnir.CooldownMinutes);
        Assert.Equal(60, fafnir.IntervalMinutes);

        var unreadable = ActivityMappers.MapTodDto(tods[1]);
        Assert.Equal("soon", unreadable.Cooldown);
        Assert.Null(unreadable.CooldownMinutes);
        Assert.Null(unreadable.IntervalMinutes);

        // camelCase on the wire, like every other field.
        var json = JsonSerializer.SerializeToElement(fafnir, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(5040, json.GetProperty("cooldownMinutes").GetInt32());
        Assert.Equal(60, json.GetProperty("intervalMinutes").GetInt32());
    }

    [Fact]
    public async Task TheAddonTodList_CarriesTheStoredMinutes_AndKeepsTheTexts()
    {
        await using var db = await SeededAsync();
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;
        var controller = new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.ListTodsAsync(CancellationToken.None);
        var tods = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value)
            .GetProperty("tods").EnumerateArray().ToList();

        var fafnir = tods.Single(t => t.GetProperty("monsterName").GetString() == "Fafnir/Nidhogg");
        Assert.Equal("84 Hour", fafnir.GetProperty("cooldown").GetString());
        Assert.Equal("1 Hour", fafnir.GetProperty("interval").GetString());
        Assert.Equal(5040, fafnir.GetProperty("cooldownMinutes").GetInt32());
        Assert.Equal(60, fafnir.GetProperty("intervalMinutes").GetInt32());

        var tiamat = tods.Single(t => t.GetProperty("monsterName").GetString() == "Tiamat");
        Assert.Equal(JsonValueKind.Null, tiamat.GetProperty("cooldownMinutes").ValueKind);
        Assert.Equal(JsonValueKind.Null, tiamat.GetProperty("intervalMinutes").ValueKind);
    }

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.Tods.AddRange(
            new Tod
            {
                Id = 1, LinkshellId = LinkshellId, MonsterName = "Fafnir/Nidhogg", Cooldown = "84 Hour",
                Interval = "1 Hour", Time = KilledAt, TimeStamp = KilledAt, RepopTime = KilledAt.AddHours(84),
            },
            new Tod
            {
                Id = 2, LinkshellId = LinkshellId, MonsterName = "Tiamat", Cooldown = "soon",
                Time = KilledAt, TimeStamp = KilledAt, RepopTime = KilledAt.AddHours(22),
            });
        // The save hook stamps the minutes from the texts.
        await db.SaveChangesAsync();
        return db;
    }
}
