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

// Two characters at one kill can both post its ToD. That used to save two rows, each with its own
// repop, and the Window frequency chart counted the kill twice.
public class TodSameKillTests
{
    private const int LinkshellId = 7;
    private const string Issuer = "user-1";
    private const string Monster = "Fafnir";

    private static readonly DateTime KilledAt = new(2026, 11, 1, 13, 11, 42, DateTimeKind.Utc);

    private static async Task<ApplicationDbContext> SeededAsync(bool canManageTods = true)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Test" });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = LinkshellId,
            Name = LinkshellRanks.Member,
            CanManageTods = canManageTods,
            CanSubmitTodForApproval = true,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = Issuer,
            LinkshellId = LinkshellId,
            Rank = LinkshellRanks.Member,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static SubmissionApprovalService Approvals(ApplicationDbContext db) =>
        new(db, TodCallers.Recorder(db), NullLogger<SubmissionApprovalService>.Instance);

    private static async Task<JsonElement> PostAsync(
        ApplicationDbContext db, string monster, DateTime killedAt, bool? claim = true, int? popWindow = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton(Approvals(db));
        services.AddSingleton(TodCallers.Recorder(db));

        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Items[AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
        {
            Id = 1,
            LinkshellId = LinkshellId,
            IssuedToAppUserId = Issuer,
            TokenPrefix = "att_1",
            TokenHash = "hash-1",
            CreatedAt = KilledAt,
        };
        http.Items[AddonApiAuthAttribute.LinkshellIdContextKey] = LinkshellId;

        var controller = new AddonApiController(
            db, null!, null!, null!, null!, null!, null!, null!, null!,
            new MonsterTimingResolver(db), null!, TestPermissions.For(db), NullLogger<AddonApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        var result = await controller.PostTodAsync(
            new AddonApiController.AddonPostTodRequest(monster, killedAt, null, claim, null, popWindow),
            CancellationToken.None);
        return JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    }

    // ---- one row per kill ----

    [Fact]
    public async Task TwoPostsOfOneKill_SaveOneTod_AndBothGetItsRepop()
    {
        await using var db = await SeededAsync();

        var first = await PostAsync(db, Monster, KilledAt);
        var second = await PostAsync(db, Monster, KilledAt.AddSeconds(40));

        var tod = await db.Tods.SingleAsync();
        // The first post's time stands, so the repop already showing everywhere doesn't move.
        Assert.Equal(KilledAt, tod.Time);
        Assert.Equal(first.GetProperty("todId").GetInt32(), second.GetProperty("todId").GetInt32());
        Assert.Equal(first.GetProperty("repopTimeUtc").GetDateTime(), second.GetProperty("repopTimeUtc").GetDateTime());
        Assert.False(first.GetProperty("alreadyPosted").GetBoolean());
        Assert.True(second.GetProperty("alreadyPosted").GetBoolean());
    }

    [Fact]
    public async Task AClockBehindTheFirstPost_IsStillTheSameKill()
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt);
        await PostAsync(db, Monster, KilledAt.AddSeconds(-40));

        Assert.Equal(KilledAt, (await db.Tods.SingleAsync()).Time);
    }

    // A kill logged under either half of a merge pair, or the combined label, is one spawn.
    [Theory]
    [InlineData("Fafnir", "Nidhogg")]
    [InlineData("Fafnir", "Fafnir/Nidhogg")]
    [InlineData("Fafnir/Nidhogg", "Nidhogg")]
    public async Task EverySpellingOfTheSpawn_IsTheSameKill(string firstName, string secondName)
    {
        await using var db = await SeededAsync();

        await PostAsync(db, firstName, KilledAt);
        await PostAsync(db, secondName, KilledAt.AddSeconds(5));

        Assert.Single(await db.Tods.ToListAsync());
    }

    // ---- what is not the same kill ----

    [Fact]
    public async Task KillsMinutesApart_StayTwoTods()
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt);
        await PostAsync(db, Monster, KilledAt.AddMinutes(3));

        Assert.Equal(2, await db.Tods.CountAsync());
    }

    [Fact]
    public async Task AnotherMonsterAtTheSameMoment_IsItsOwnKill()
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt);
        await PostAsync(db, "Behemoth", KilledAt);

        Assert.Equal(2, await db.Tods.CountAsync());
    }

    [Fact]
    public async Task AnotherLinkshellsTod_IsLeftAlone()
    {
        await using var db = await SeededAsync();
        db.Tods.Add(new Tod { LinkshellId = LinkshellId + 1, MonsterName = Monster, Time = KilledAt, RepopTime = KilledAt.AddHours(22) });
        await db.SaveChangesAsync();

        await PostAsync(db, Monster, KilledAt);

        Assert.Equal(2, await db.Tods.CountAsync());
    }

    // ---- what the second post adds ----

    // Each character's window is when IT first noticed the pop, so the earliest is the closest to
    // the truth, and a real reading beats none.
    [Theory]
    [InlineData(null, 2, 2)]
    [InlineData(2, null, 2)]
    [InlineData(2, 1, 1)]
    [InlineData(1, 2, 1)]
    public async Task TheEarliestWindowIsKept(int? first, int? second, int expected)
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt, popWindow: first);
        await PostAsync(db, Monster, KilledAt, popWindow: second);

        Assert.Equal(expected, (await db.Tods.SingleAsync()).PopWindow);
    }

    [Fact]
    public async Task ABlankClaim_TakesTheSecondPostsAnswer()
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt, claim: null);
        var second = await PostAsync(db, Monster, KilledAt, claim: true);

        var tod = await db.Tods.SingleAsync();
        Assert.True(tod.Claim);
        Assert.True(second.GetProperty("claim").GetBoolean());
    }

    // The second poster is told what is saved, so their panel can't show a different answer.
    [Fact]
    public async Task ADifferentClaim_LeavesTheFirstAnswer_AndReportsIt()
    {
        await using var db = await SeededAsync();

        await PostAsync(db, Monster, KilledAt, claim: true);
        var second = await PostAsync(db, Monster, KilledAt, claim: false);

        Assert.True((await db.Tods.SingleAsync()).Claim);
        Assert.True(second.GetProperty("claim").GetBoolean());
    }

    // ---- the approval queue ----

    [Fact]
    public async Task AMember_WhoseKillIsAlreadyPosted_QueuesNothing_AndChangesNothing()
    {
        await using var db = await SeededAsync(canManageTods: false);
        db.Tods.Add(new Tod
        {
            LinkshellId = LinkshellId, MonsterName = Monster, Time = KilledAt, RepopTime = KilledAt.AddHours(22), PopWindow = 3,
        });
        await db.SaveChangesAsync();

        var result = await PostAsync(db, Monster, KilledAt.AddSeconds(5), claim: true, popWindow: 1);

        Assert.Empty(await db.PendingTodSubmissions.ToListAsync());
        var tod = await db.Tods.SingleAsync();
        Assert.Equal(3, tod.PopWindow);
        Assert.Null(tod.Claim);
        Assert.True(result.GetProperty("alreadyPosted").GetBoolean());
    }

    [Fact]
    public async Task TwoMembers_PostingOneKill_QueueOneSubmission()
    {
        await using var db = await SeededAsync(canManageTods: false);

        var first = await PostAsync(db, Monster, KilledAt, claim: null, popWindow: 2);
        var second = await PostAsync(db, Monster, KilledAt.AddSeconds(5), claim: true, popWindow: 1);

        var pending = await db.PendingTodSubmissions.SingleAsync();
        Assert.Equal(1, pending.PopWindow);
        Assert.True(pending.Claim);
        Assert.Equal(first.GetProperty("submissionId").GetInt32(), second.GetProperty("submissionId").GetInt32());
    }

    [Fact]
    public async Task Approving_AKillThatAlreadyHasATod_AddsNoSecondOne()
    {
        await using var db = await SeededAsync();
        db.Tods.Add(new Tod { LinkshellId = LinkshellId, MonsterName = Monster, Time = KilledAt, RepopTime = KilledAt.AddHours(22) });
        var pending = new PendingTodSubmission
        {
            LinkshellId = LinkshellId,
            SubmittedByAppUserId = Issuer,
            SubmittedAtUtc = KilledAt,
            MonsterName = Monster,
            Time = KilledAt.AddSeconds(5),
            RepopTime = KilledAt.AddSeconds(5).AddHours(22),
            Claim = true,
            PopWindow = 2,
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        var result = await Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.Equal(ApprovalResult.Approved, result);
        Assert.Empty(await db.PendingTodSubmissions.ToListAsync());
        var tod = await db.Tods.SingleAsync();
        Assert.Equal(KilledAt, tod.Time);
        Assert.Equal(2, tod.PopWindow);
        Assert.True(tod.Claim);
    }

    [Fact]
    public async Task Approving_AKillWithNoTodYet_StillSavesOne()
    {
        await using var db = await SeededAsync();
        var pending = new PendingTodSubmission
        {
            LinkshellId = LinkshellId,
            SubmittedByAppUserId = Issuer,
            SubmittedAtUtc = KilledAt,
            MonsterName = Monster,
            Time = KilledAt,
            RepopTime = KilledAt.AddHours(22),
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        await Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.Equal(KilledAt, (await db.Tods.SingleAsync()).Time);
    }
}
