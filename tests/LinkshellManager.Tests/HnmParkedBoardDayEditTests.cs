using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Changing the Day box in the edit form on a board parked from its last camp.
//
// The Discord note works its "Next pop: Day N" out from the ToD, and so does the board when it comes
// back, so a day typed on the form reached neither. The form also opened on the day the camp ended
// on, one less than the note said. Now the form opens on the note's day, and a changed day is what
// the note says and what the board comes back on.
public class HnmParkedBoardDayEditTests
{
    private const int LinkshellId = 12;
    private const string Monster = "Behemoth/King Behemoth";

    private static readonly DateTime Now = new(2026, 11, 6, 20, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Repop = Now.AddHours(19);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod KillOnDay3(bool hq = false) => new()
    {
        Id = 7,
        LinkshellId = LinkshellId,
        MonsterName = Monster,
        DayNumber = 3,
        Hq = hq,
        Cooldown = "22 Hour",
        Time = Now.AddHours(-3),
        RepopTime = Repop,
    };

    // Parked after its day-3 kill, re-posting 8 hours before the pop.
    private static Event Parked() => new()
    {
        Id = 400,
        LinkshellId = LinkshellId,
        EventName = "Behemoth",
        EventType = "HNM",
        AssignedMonsterName = Monster,
        DayNumber = 3,
        StartTime = Repop,
        SourceTodId = 7,
        HnmDefeatedAt = Now.AddHours(-3),
        HnmRepostAt = Repop.AddHours(-8),
        WdFinalizedAt = Now.AddHours(-3),
    };

    private static async Task<ApplicationDbContext> SeededAsync(Event board, Tod? tod = null)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "TestLinkshell", EnableHnmSection = true });
        db.Tods.Add(tod ?? KillOnDay3());
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 1,
            LinkshellId = LinkshellId,
            MonsterName = Monster,
            Enabled = true,
            LeadHours = 8,
            CreatedAt = Now.AddDays(-7),
        });
        db.Events.Add(board);
        await db.SaveChangesAsync();
        return db;
    }

    // ---- what the form opens on ----

    [Fact]
    public async Task TheFormOpensOnTheDayTheNoteSays()
    {
        using var db = await SeededAsync(Parked());
        var ev = await db.Events.SingleAsync();

        Assert.Equal(4, await HnmRecurringBoardService.DayShownInEditFormAsync(db, ev, CancellationToken.None));
    }

    [Fact]
    public async Task ABoardThatIsUp_OpensOnItsOwnDay()
    {
        var up = Parked();
        up.HnmDefeatedAt = null;
        up.HnmRepostAt = null;
        using var db = await SeededAsync(up);
        var ev = await db.Events.SingleAsync();

        Assert.Equal(3, await HnmRecurringBoardService.DayShownInEditFormAsync(db, ev, CancellationToken.None));
    }

    // Ended with no ToD: the note says no day, so the box has only the board's own to show.
    [Fact]
    public async Task AParkedBoardWithNoTodOfThisKill_OpensOnItsOwnDay()
    {
        var parked = Parked();
        parked.SourceTodId = null;
        using var db = await SeededAsync(parked);
        var ev = await db.Events.SingleAsync();

        Assert.Equal(3, await HnmRecurringBoardService.DayShownInEditFormAsync(db, ev, CancellationToken.None));
    }

    // ---- what saving does ----

    [Fact]
    public void AChangedDay_IsWhatTheNoteSays()
    {
        var ev = Parked();

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 6);

        var details = HnmBoardNoticeService.ResolveDetails(ev, KillOnDay3());
        Assert.Equal(6, details.NextDay);
        Assert.Contains("Next pop: **Day 6**.", HnmBoardNoticeService.BuildDefeatedDescription(ev, details));
    }

    // The cycle counts on from this, and the ToD page still says the kill was on day 3.
    [Fact]
    public void AChangedDay_LeavesTheDayTheCampEndedOn()
    {
        var ev = Parked();

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 6);

        Assert.Equal(3, ev.DayNumber);
    }

    // Saving a different field sends the box straight back. Writing it as the board's day would have
    // the board come back a day too far on.
    [Fact]
    public void AnUntouchedDay_ChangesNothing()
    {
        var ev = Parked();

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 4);

        Assert.Equal(3, ev.DayNumber);
        Assert.Null(ev.HnmNextDayOverride);
        Assert.Equal(4, HnmBoardNoticeService.ResolveDetails(ev, KillOnDay3()).NextDay);
    }

    [Fact]
    public void AnEarlierTypedDay_StandsUntilItIsChangedAgain()
    {
        var ev = Parked();
        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 6);

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 6, typedDay: 6);
        Assert.Equal(6, ev.HnmNextDayOverride);

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 6, typedDay: 2);
        Assert.Equal(2, ev.HnmNextDayOverride);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ABlankDay_GoesBackToTheWorkedOutDay(int? typed)
    {
        var ev = Parked();
        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 6);

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 6, typedDay: typed);

        Assert.Null(ev.HnmNextDayOverride);
        Assert.Equal(4, HnmBoardNoticeService.ResolveDetails(ev, KillOnDay3()).NextDay);
    }

    // After an HQ the cycle says Day 1, and the officer can still say otherwise.
    [Fact]
    public void AChangedDay_BeatsTheResetAfterAnHq()
    {
        var ev = Parked();
        Assert.Equal(1, HnmBoardNoticeService.ResolveDetails(ev, KillOnDay3(hq: true)).NextDay);

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 1, typedDay: 3);

        Assert.Equal(3, HnmBoardNoticeService.ResolveDetails(ev, KillOnDay3(hq: true)).NextDay);
    }

    [Fact]
    public void ADayTypedWithNoTodOfThisKill_IsStillSaid()
    {
        var ev = Parked();
        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 3, typedDay: 5);

        Assert.Equal(5, HnmBoardNoticeService.ResolveDetails(ev, tod: null).NextDay);
    }

    [Fact]
    public void ABoardThatIsUp_JustTakesTheDay()
    {
        var ev = Parked();
        ev.HnmDefeatedAt = null;

        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 3, typedDay: 6);

        Assert.Equal(6, ev.DayNumber);
        Assert.Null(ev.HnmNextDayOverride);
    }

    // ---- the board coming back says the same day ----

    private static HnmRecurringBoardBackgroundService Poller() =>
        new(null!, NullLogger<HnmRecurringBoardBackgroundService>.Instance);

    [Fact]
    public async Task ThePoller_BringsTheBoardBackOnTheTypedDay()
    {
        var parked = Parked();
        parked.HnmNextDayOverride = 6;
        using var db = await SeededAsync(parked);
        var board = await db.HnmRecurringBoards.SingleAsync();

        await Poller().ProcessBoardAsync(db, board, Repop.AddHours(-8), CancellationToken.None);

        var ev = await db.Events.SingleAsync();
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(6, ev.DayNumber);
        Assert.Null(ev.HnmNextDayOverride);
    }

    [Fact]
    public async Task ThePoller_WithNoTypedDay_StillMovesOnADay()
    {
        using var db = await SeededAsync(Parked());
        var board = await db.HnmRecurringBoards.SingleAsync();

        await Poller().ProcessBoardAsync(db, board, Repop.AddHours(-8), CancellationToken.None);

        Assert.Equal(4, (await db.Events.SingleAsync()).DayNumber);
    }

    [Fact]
    public async Task StartingABoardByHand_ComesBackOnTheTypedDay()
    {
        var parked = Parked();
        parked.HnmNextDayOverride = 6;
        using var db = await SeededAsync(parked);
        var ev = await db.Events.SingleAsync();

        Assert.True(await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None));

        Assert.Equal(6, ev.DayNumber);
        Assert.Null(ev.HnmNextDayOverride);
    }

    [Fact]
    public async Task StartingABoardWithNoTod_ComesBackOnTheTypedDay()
    {
        var parked = Parked();
        parked.HnmNextDayOverride = 6;
        var db = await SeededAsync(parked);
        using (db)
        {
            db.Tods.Remove(await db.Tods.SingleAsync());
            await db.SaveChangesAsync();
            var ev = await db.Events.SingleAsync();

            Assert.True(await HnmRecurringBoardService.ReviveParkedBoardForStartAsync(db, ev, CancellationToken.None));

            Assert.Equal(6, ev.DayNumber);
            Assert.Null(ev.HnmNextDayOverride);
        }
    }

    // The Update button, once the re-post time has come: the same day, by the edit's own route.
    [Fact]
    public async Task UpdateOpeningADueBoard_KeepsTheTypedDay()
    {
        var parked = Parked();
        parked.HnmRepostAt = Now.AddHours(-1);
        using var db = await SeededAsync(parked);
        var ev = await db.Events.SingleAsync();
        HnmRecurringBoardService.ApplyEditedDay(ev, dayFormOpenedWith: 4, typedDay: 6);

        var revived = await HnmRecurringBoardService.ReviveDueParkedBoardAfterEditAsync(
            db, ev, Now, editedStartUtc: null, editedDayNumber: 6, CancellationToken.None);

        Assert.True(revived);
        Assert.Null(ev.HnmDefeatedAt);
        Assert.Equal(6, ev.DayNumber);
        Assert.Null(ev.HnmNextDayOverride);
    }
}
