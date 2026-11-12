using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Every officer's addon reads the same lottery off chat and posts it. A camp keeps ONE capture:
// the first post makes it, each later post adds only the people it is missing.
public class ClaimShieldOneCapturePerCampTests
{
    private const int LinkshellId = 7;
    private const int CampId = 50;
    private const string OfficerA = "user-a";
    private const string OfficerB = "user-b";

    private static readonly DateTime PoppedAt = DateTime.UtcNow.AddMinutes(-5);

    private static async Task<string> SeededDatabaseAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = Db(name);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = LinkshellId,
            Name = LinkshellRanks.Member,
            CanManageTods = true,
        });
        foreach (var (id, character, alt) in new[]
                 {
                     (OfficerA, "Alpha", (string?)null),
                     (OfficerB, "Bravo", null),
                     ("user-c", "Charlie", null),
                     ("user-d", "Delta", "Deltalt"),
                 })
        {
            db.Users.Add(new AppUser { Id = id, UserName = character, CharacterName = character, AltCharacterName1 = alt });
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                AppUserId = id,
                LinkshellId = LinkshellId,
                CharacterName = character,
                Rank = LinkshellRanks.Member,
            });
        }
        db.Events.Add(new Event
        {
            Id = CampId,
            LinkshellId = LinkshellId,
            EventName = "Fafnir",
            EventType = "HNM",
            CommencementStartTime = PoppedAt.AddHours(-1),
        });
        await db.SaveChangesAsync();
        return name;
    }

    private static ApplicationDbContext Db(string name) => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name).Options);

    // One officer's addon posting what it read. Each post gets its own context, as each request does.
    private static async Task<JsonElement> PostAsync(
        string databaseName, string officer, string[] names,
        string monster = "Fafnir", bool won = true, int totalPlayers = 10)
    {
        await using var db = Db(databaseName);
        var http = new DefaultHttpContext();
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1,
            LinkshellId = LinkshellId,
            IssuedToAppUserId = officer,
            TokenPrefix = "att_1",
            TokenHash = "hash-1",
            CreatedAt = PoppedAt,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;

        var controller = new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.PostClaimShieldCaptureAsync(
            new AddonApiController.AddonClaimShieldRequest(
                monster, won, totalPlayers, PoppedAt, "result line", names),
            CancellationToken.None);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    }

    private static async Task<ClaimShieldCapture> TheOnlyCaptureAsync(string databaseName)
    {
        await using var db = Db(databaseName);
        return await db.ClaimShieldCaptures.Include(c => c.Members).SingleAsync();
    }

    [Fact]
    public async Task TwoOfficers_MakeOneCapture_WithEveryoneEitherOfThemSaw()
    {
        var name = await SeededDatabaseAsync();

        var first = await PostAsync(name, OfficerA, ["Alpha", "Bravo", "Charlie"]);
        var second = await PostAsync(name, OfficerB, ["Bravo", "Charlie", "Delta"]);

        var capture = await TheOnlyCaptureAsync(name);
        Assert.Equal(CampId, capture.EventId);
        Assert.Equal(
            ["Alpha", "Bravo", "Charlie", "Delta"],
            capture.Members.Select(m => m.CharacterName).OrderBy(n => n));
        Assert.Equal(4, capture.MemberCount);
        Assert.Equal(4, capture.MatchedCount);

        Assert.False(first.GetProperty("joined").GetBoolean());
        Assert.True(second.GetProperty("joined").GetBoolean());
        Assert.Equal(1, second.GetProperty("added").GetInt32());
        Assert.Equal(first.GetProperty("captureId").GetInt32(), second.GetProperty("captureId").GetInt32());
        // The addon prints this as "N matched": the whole capture, not just what this post added.
        Assert.Equal(4, second.GetProperty("matchedCount").GetInt32());
    }

    [Fact]
    public async Task SomeoneSeenOnTheirAlt_IsNotAddedASecondTime()
    {
        var name = await SeededDatabaseAsync();

        await PostAsync(name, OfficerA, ["Delta"]);
        await PostAsync(name, OfficerB, ["Deltalt"]);

        Assert.Equal("Delta", Assert.Single((await TheOnlyCaptureAsync(name)).Members).CharacterName);
    }

    [Fact]
    public async Task UnmatchedNames_AreMergedByName()
    {
        var name = await SeededDatabaseAsync();

        await PostAsync(name, OfficerA, ["Stranger"]);
        await PostAsync(name, OfficerB, ["stranger", "Alpha"]);

        var capture = await TheOnlyCaptureAsync(name);
        Assert.Equal(2, capture.MemberCount);
        Assert.Equal(1, capture.MatchedCount);
    }

    [Fact]
    public async Task TheFullestReadOfTheLottery_Stands()
    {
        var name = await SeededDatabaseAsync();

        await PostAsync(name, OfficerA, ["Alpha"], won: false, totalPlayers: 10);
        await PostAsync(name, OfficerB, ["Bravo"], won: true, totalPlayers: 12);

        var capture = await TheOnlyCaptureAsync(name);
        Assert.True(capture.Won);
        Assert.Equal(12, capture.TotalPlayers);
        // The first post made it, so it is still "captured by" that officer.
        Assert.Equal("Alpha", capture.CapturedByCharacterName);
    }

    [Fact]
    public async Task WithNoCamp_EachPostKeepsItsOwnCapture()
    {
        var name = await SeededDatabaseAsync();

        // No live camp is named for this monster, so neither post resolves to an event.
        await PostAsync(name, OfficerA, ["Alpha"], monster: "Cerberus");
        await PostAsync(name, OfficerB, ["Bravo"], monster: "Cerberus");

        await using var db = Db(name);
        Assert.Equal(2, await db.ClaimShieldCaptures.CountAsync(c => c.EventId == null));
    }

    [Fact]
    public async Task AnArchivedCapture_IsNotJoinedByTheNextPop()
    {
        var name = await SeededDatabaseAsync();
        await using (var db = Db(name))
        {
            // Last pop's lottery, handed to its past event at End Camp. The board was recycled.
            db.ClaimShieldCaptures.Add(new ClaimShieldCapture
            {
                LinkshellId = LinkshellId,
                EventHistoryId = 900,
                MonsterName = "Fafnir",
                CapturedAtUtc = PoppedAt.AddDays(-1),
            });
            await db.SaveChangesAsync();
        }

        await PostAsync(name, OfficerA, ["Alpha"]);

        await using var check = Db(name);
        var live = await check.ClaimShieldCaptures.Include(c => c.Members).SingleAsync(c => c.EventId == CampId);
        Assert.Equal("Alpha", Assert.Single(live.Members).CharacterName);
        Assert.Equal(2, await check.ClaimShieldCaptures.CountAsync());
    }
}
