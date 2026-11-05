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

// What Discord says about a repop: the note on End Camp's Time of Death box, and the "defeated"
// message's next day and formula. See AdditionalSecondsTests for the seconds themselves.
public class AdditionalSecondsDiscordTests
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

    // ---- The Discord End Camp modal ----

    private static readonly string Formula = TodRepopNote.Formula("22 Hour", Seconds)!;

    private static JsonElement ModalRow(string monster, string? formula, string fieldId) =>
        JsonSerializer.SerializeToElement(
                DiscordInteractionsController.BuildWdPopModalFields(
                    new Event { Id = 1, AssignedMonsterName = monster, HnmWindowNumber = 1 }, null, formula))
            .EnumerateArray()
            .Single(row => row.TryGetProperty("component", out var labeled)
                ? labeled.GetProperty("custom_id").GetString() == fieldId
                : row.GetProperty("components")[0].GetProperty("custom_id").GetString() == fieldId);

    [Fact]
    public void Formula_SaysHowTheRepopIsWorkedOut()
    {
        Assert.Equal("ToD + 22 Hour + 16 additional seconds", Formula);
        Assert.Equal("ToD + 22 Hour + 1 additional second", TodRepopNote.Formula("22 Hour", 1));
        Assert.Null(TodRepopNote.Formula("22 Hour", 0));
    }

    [Fact]
    public void Modal_ShowsTheNoteOnTheTimeOfDeathBox()
    {
        var row = ModalRow(Monster, Formula, "wdpop_tod");

        Assert.Equal(18, row.GetProperty("type").GetInt32());
        Assert.Equal("Reposted event starts at ToD + 22 Hour + 16 additional seconds.",
            row.GetProperty("description").GetString());
        // Discord refuses a text input that carries a label of its own inside a Label.
        Assert.False(row.GetProperty("component").TryGetProperty("label", out _));
    }

    // The box the note moves is the same box: the same id and the same one row of the five.
    [Fact]
    public void Modal_WithTheNote_StaysWithinFiveRows()
    {
        var rows = JsonSerializer.SerializeToElement(
            DiscordInteractionsController.BuildWdPopModalFields(
                new Event { Id = 1, AssignedMonsterName = "Fafnir/Nidhogg", HnmWindowNumber = 1 }, null, Formula));

        Assert.Equal(5, rows.GetArrayLength());
    }

    // Discord's limit on a Label's description. The longest cooldown label and the editor's largest
    // seconds value still fit, because going over is a rejected modal, not a truncated line.
    [Fact]
    public void Modal_NoteFitsDiscordsDescriptionLimit()
    {
        var row = ModalRow(Monster, TodRepopNote.Formula("2400 Hour 59 Min", 86400), "wdpop_tod");

        Assert.InRange(row.GetProperty("description").GetString()!.Length, 1, 100);
    }

    [Fact]
    public void Modal_WithNoAdditionalSeconds_KeepsThePlainBox()
    {
        var row = ModalRow(Monster, null, "wdpop_tod");

        Assert.Equal(1, row.GetProperty("type").GetInt32());
        Assert.Equal("Time of Death (blank = not entered)",
            row.GetProperty("components")[0].GetProperty("label").GetString());
    }

    // ---- End Camp ----

    private static readonly DateTime Repop = KilledAt.AddHours(22).AddSeconds(Seconds);

    private static Tod TodFor(string monster, int? day, bool hq = false, int seconds = Seconds, DateTime? repop = null) => new()
    {
        Id = 70,
        LinkshellId = LinkshellId,
        MonsterName = monster,
        DayNumber = day,
        Hq = hq,
        Cooldown = "22 Hour",
        AdditionalSeconds = seconds,
        Time = KilledAt,
        RepopTime = repop ?? Repop,
    };

    private static Event Parked(string monster, int? day) => new()
    {
        Id = 600,
        LinkshellId = LinkshellId,
        EventName = monster,
        EventType = "HNM",
        AssignedMonsterName = monster,
        DayNumber = day,
        StartTime = Repop,
        SourceTodId = 70,
        HnmDefeatedAt = KilledAt.AddMinutes(5),
    };

    private static async Task<DefeatedNoticeDetails> DetailsAsync(Event ev, Tod tod)
    {
        await using var db = NewInMemoryContext();
        db.Tods.Add(tod);
        await db.SaveChangesAsync();
        return await HnmBoardNoticeService.ResolveDetailsAsync(db, ev, CancellationToken.None);
    }

    // ---- The "defeated" note ----

    [Fact]
    public async Task DefeatedNote_AnNqKill_SaysTheNextDay()
    {
        var details = await DetailsAsync(Parked("Adamantoise/Aspidochelone", 3), TodFor("Adamantoise/Aspidochelone", 3));

        Assert.Equal(4, details.NextDay);
    }

    [Fact]
    public async Task DefeatedNote_AnHqKill_SaysDayOne()
    {
        var details = await DetailsAsync(Parked("Fafnir/Nidhogg", 6), TodFor("Fafnir/Nidhogg", 6, hq: true));

        Assert.Equal(1, details.NextDay);
    }

    // A ToD left with no day falls back to the board's, the same as the board coming back does.
    [Fact]
    public async Task DefeatedNote_AToDWithNoDay_MovesOnFromTheBoardsDay()
    {
        var details = await DetailsAsync(Parked("Behemoth/King Behemoth", 3), TodFor("Behemoth/King Behemoth", null));

        Assert.Equal(4, details.NextDay);
    }

    [Fact]
    public async Task DefeatedNote_AMonsterWithNoDayCycle_SaysNoDay()
    {
        var details = await DetailsAsync(Parked("Jormungand", 3), TodFor("Jormungand", 3));

        Assert.Null(details.NextDay);
    }

    // Ended with no ToD: the board still points at the ToD it was made from, whose repop has passed.
    // That ToD's next day is the day this camp was on, so printing it would be wrong.
    [Fact]
    public async Task DefeatedNote_ATodFromBeforeThisKill_SaysNothing()
    {
        var details = await DetailsAsync(
            Parked("Adamantoise/Aspidochelone", 4),
            TodFor("Adamantoise/Aspidochelone", 3, repop: KilledAt.AddHours(-1)));

        Assert.Null(details.NextDay);
        Assert.Null(details.RepopFormula);
    }

    [Fact]
    public void DefeatedNote_PrintsTheNextDayAndTheFormula()
    {
        var ev = Parked("Adamantoise/Aspidochelone", 3);
        ev.HnmRepostAt = Repop.AddHours(-8);

        var text = HnmBoardNoticeService.BuildDefeatedDescription(
            ev, new DefeatedNoticeDetails(4, Formula));

        Assert.Contains("Next pop: **Day 4**.", text);
        Assert.Contains("Repop = ToD + 22 Hour + 16 additional seconds.", text);
        Assert.Contains("will automatically re-post", text);
    }

    [Fact]
    public void DefeatedNote_WithNothingToAdd_ReadsAsItAlwaysDid()
    {
        var text = HnmBoardNoticeService.BuildDefeatedDescription(
            Parked("Jormungand", null), new DefeatedNoticeDetails(null, null));

        Assert.StartsWith("Will repop ", text);
        Assert.DoesNotContain("Next pop:", text);
        Assert.DoesNotContain("Repop =", text);
    }

    // ---- The reposted board ----

    // The start has seconds in it, so the board's Starts field has to show them.
    [Fact]
    public void Board_StartsField_ShowsSecondsOnAWindowedCamp()
    {
        var ev = Parked("Fafnir/Nidhogg", 3);
        ev.HnmDefeatedAt = null;

        var json = JsonSerializer.Serialize(
            DiscordEventMessageBuilder.Build(ev, Array.Empty<EventSignupLine>()));

        var unix = ((DateTimeOffset)Repop).ToUnixTimeSeconds();
        Assert.Contains($"\\u003Ct:{unix}:D\\u003E \\u003Ct:{unix}:T\\u003E (\\u003Ct:{unix}:R\\u003E)", json);
    }
}
