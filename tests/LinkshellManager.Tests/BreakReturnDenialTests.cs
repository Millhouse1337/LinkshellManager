using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Deny on a member's own "I'm back" takes the time back. It used to only stamp the request as
// denied, so the member's clock kept running and they were paid for the time anyway.
public class BreakReturnDenialTests
{
    private static readonly DateTime Start = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BreakAt = Start.AddHours(1);
    private static readonly DateTime ReturnAt = Start.AddHours(2);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Event TimedEvent() => new()
    {
        Id = 10,
        LinkshellId = TodCallers.LinkshellId,
        EventName = "Sky",
        EventType = "Sky",
        CommencementStartTime = Start,
        DkpPerHour = 10,
    };

    private static Event WindowedEvent() => new()
    {
        Id = 10,
        LinkshellId = TodCallers.LinkshellId,
        EventName = "Fafnir",
        EventType = "HNM",
        CommencementStartTime = Start,
    };

    // Joined at Start, broke at +1h (1h banked), self-returned at +2h, still back.
    private static (AppUserEvent Participation, List<AppUserEventStatusLedger> Ledger) BackFromBreak()
    {
        var participation = new AppUserEvent
        {
            Id = 1,
            EventId = 10,
            AppUserId = TodCallers.User,
            CharacterName = "Tester",
            StartTime = Start,
            Duration = 1,
            IsOnBreak = false,
            ResumeTime = ReturnAt,
        };
        var ledger = new List<AppUserEventStatusLedger>
        {
            new() { Id = 1, AppUserEventId = 1, EventId = 10, ActionType = "BreakStart", OccurredAt = BreakAt },
            new()
            {
                Id = 2, AppUserEventId = 1, EventId = 10, ActionType = "BreakReturn", OccurredAt = ReturnAt,
                RequiresVerification = true,
            },
        };
        return (participation, ledger);
    }

    [Fact]
    public void StillBack_GoesBackOnBreak_AsOfTheReturnTime()
    {
        var (participation, ledger) = BackFromBreak();
        var now = ReturnAt.AddMinutes(45);

        BreakReturnDenial.Apply(TimedEvent(), participation, ledger[1], ledger, now);

        Assert.True(participation.IsOnBreak);
        Assert.Null(participation.ResumeTime);
        Assert.Equal(1, participation.Duration);
        // The 45 minutes since the return no longer count.
        Assert.Equal(1, EventEndService.CalculateAccumulatedDurationHours(participation, now, Start));

        Assert.Equal(now, ledger[1].DeniedAt);
        Assert.False(ledger[1].RequiresVerification);
    }

    [Fact]
    public void TookAnotherBreakSince_TheBankedTimeComesBackOut()
    {
        var (participation, ledger) = BackFromBreak();
        // Second break 30 minutes after the return: those 30 minutes were banked into Duration.
        var secondBreak = ReturnAt.AddMinutes(30);
        participation.Duration = 1.5;
        participation.IsOnBreak = true;
        participation.ResumeTime = null;
        ledger.Add(new AppUserEventStatusLedger
        {
            Id = 3, AppUserEventId = 1, EventId = 10, ActionType = "BreakStart", OccurredAt = secondBreak,
        });

        BreakReturnDenial.Apply(TimedEvent(), participation, ledger[1], ledger, secondBreak.AddHours(1));

        Assert.Equal(1, participation.Duration!.Value, 6);
        // Their current break is left as it was.
        Assert.True(participation.IsOnBreak);
        Assert.NotNull(ledger[1].DeniedAt);
    }

    [Fact]
    public void BackAgainAfterASecondBreak_OnlyTheDeniedSegmentIsRemoved()
    {
        var (participation, ledger) = BackFromBreak();
        var secondBreak = ReturnAt.AddMinutes(30);
        var secondReturn = secondBreak.AddMinutes(20);
        participation.Duration = 1.5;
        participation.IsOnBreak = false;
        participation.ResumeTime = secondReturn;
        ledger.Add(new AppUserEventStatusLedger
        {
            Id = 3, AppUserEventId = 1, EventId = 10, ActionType = "BreakStart", OccurredAt = secondBreak,
        });
        ledger.Add(new AppUserEventStatusLedger
        {
            Id = 4, AppUserEventId = 1, EventId = 10, ActionType = "BreakReturn", OccurredAt = secondReturn,
            RequiresVerification = true,
        });

        BreakReturnDenial.Apply(TimedEvent(), participation, ledger[1], ledger, secondReturn.AddHours(1));

        Assert.Equal(1, participation.Duration!.Value, 6);
        Assert.False(participation.IsOnBreak);
        Assert.Equal(secondReturn, participation.ResumeTime);
        // The later return is still waiting for its own Verify / Deny.
        Assert.True(ledger[3].RequiresVerification);
    }

    [Fact]
    public void WindowedEvent_OnlyMarksTheRequest()
    {
        var (participation, ledger) = BackFromBreak();

        BreakReturnDenial.Apply(WindowedEvent(), participation, ledger[1], ledger, ReturnAt.AddHours(1));

        Assert.False(participation.IsOnBreak);
        Assert.Equal(ReturnAt, participation.ResumeTime);
        Assert.Equal(1, participation.Duration);
        Assert.NotNull(ledger[1].DeniedAt);
        Assert.False(ledger[1].RequiresVerification);
    }

    [Fact]
    public void OnBreakWithNoBreakRowAfterTheReturn_NothingIsGuessed()
    {
        var (participation, ledger) = BackFromBreak();
        participation.IsOnBreak = true;
        participation.ResumeTime = null;

        BreakReturnDenial.Apply(TimedEvent(), participation, ledger[1], ledger, ReturnAt.AddHours(1));

        Assert.Equal(1, participation.Duration);
        Assert.NotNull(ledger[1].DeniedAt);
    }

    // ---- through both endpoints ----

    private static async Task<ApplicationDbContext> SeededAsync()
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = TodCallers.LinkshellId, LinkshellName = "Test" });
        db.Users.Add(new AppUser { Id = TodCallers.User, UserName = TodCallers.User, CharacterName = "Tester" });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = TodCallers.User,
            LinkshellId = TodCallers.LinkshellId,
            CharacterName = "Tester",
            Rank = LinkshellRanks.Leader,
        });
        db.Events.Add(TimedEvent());
        var (participation, ledger) = BackFromBreak();
        db.AppUserEvents.Add(participation);
        db.AppUserEventStatusLedgers.AddRange(ledger);
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task ActivityDeny_PutsTheMemberBackOnBreak()
    {
        await using var db = await SeededAsync();

        var result = await TodCallers.App(db).DenyReturnAsync(
            10, new ActivityVerifyReturnRequest(2), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var participation = await db.AppUserEvents.SingleAsync();
        Assert.True(participation.IsOnBreak);
        Assert.Equal(1, participation.Duration);
        var denied = await db.AppUserEventStatusLedgers.SingleAsync(entry => entry.Id == 2);
        Assert.NotNull(denied.DeniedAt);
        Assert.False(denied.RequiresVerification);
    }

    [Fact]
    public async Task AddonDeny_PutsTheMemberBackOnBreak()
    {
        await using var db = await SeededAsync();

        var result = await TodCallers.Addon(db).DenyReturnAsync(
            10, new AddonApiController.AddonVerifyReturnRequest(2), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var participation = await db.AppUserEvents.SingleAsync();
        Assert.True(participation.IsOnBreak);
        Assert.Equal(1, participation.Duration);
        Assert.NotNull((await db.AppUserEventStatusLedgers.SingleAsync(entry => entry.Id == 2)).DeniedAt);
    }
}
