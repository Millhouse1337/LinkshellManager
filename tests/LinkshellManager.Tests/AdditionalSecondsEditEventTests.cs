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

// What Edit Event's "Starts" hint reads: the seconds the predicted pop already includes.
public class AdditionalSecondsEditEventTests
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

    // ---- Edit Event's predicted-pop hint ----

    [Fact]
    public async Task UpcomingRepop_ReportsTheSecondsItsToDRecorded()
    {
        await using var db = NewInMemoryContext();
        db.Tods.Add(new Tod
        {
            Id = 70, LinkshellId = LinkshellId, MonsterName = "Fafnir/Nidhogg",
            AdditionalSeconds = Seconds, Time = KilledAt, RepopTime = ExpectedRepop,
        });
        await db.SaveChangesAsync();

        var entries = await UpcomingRepopLookup.ForLinkshellAsync(db, LinkshellId, KilledAt, CancellationToken.None);

        Assert.Equal(Seconds, Assert.Single(entries).AdditionalSeconds);
    }
}
