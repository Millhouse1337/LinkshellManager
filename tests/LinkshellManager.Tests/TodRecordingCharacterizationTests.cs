using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// P3.6 (backend-04): recording a ToD was written four times -- the app's Log ToD, the addon's ToD
// post, approving a member's submission, and End Camp. They now all go through TodRecorder.
//
// Written against the four copies BEFORE they were replaced, so they pin what each path already did
// and must keep doing: the name and HQ a kill is stored under, the repop sum, which day it records,
// the party sign-up reset, and moving a parked board. What the recorder changed on purpose is in
// TodRecorderTests.
public class TodRecordingCharacterizationTests
{
    private static readonly DateTime KilledAt = new(2026, 11, 5, 13, 0, 0, DateTimeKind.Utc);

    // ---- the app's Log ToD ----

    // Picking the HQ half is an HQ answer, and the kill is stored under the pair's combined name,
    // so it matches one logged from the addon or a camp board.
    [Theory]
    [InlineData("Nidhogg", false, true)]
    [InlineData("Fafnir", false, false)]
    [InlineData("Fafnir/Nidhogg", true, true)]
    [InlineData("Fafnir/Nidhogg", false, false)]
    public async Task App_StoresAMergePairUnderItsCombinedName(string picked, bool hqToggle, bool expectedHq)
    {
        await using var db = await TodCallers.SeededAsync();

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest(picked, KilledAt, hq: hqToggle));

        var tod = await db.Tods.SingleAsync();
        Assert.Equal("Fafnir/Nidhogg", tod.MonsterName);
        Assert.Equal(expectedHq, tod.Hq);
    }

    [Fact]
    public async Task App_RepopIsTimePlusTheSetupsCooldownAndSeconds()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Cerberus", 48 * 60, additionalSeconds: 16));
        await db.SaveChangesAsync();

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Cerberus", KilledAt, dayNumber: 2, popWindow: 4));

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(KilledAt, tod.Time);
        Assert.Equal(KilledAt.AddHours(48).AddSeconds(16), tod.RepopTime);
        Assert.Equal("48 Hour", tod.Cooldown);
        Assert.Equal(16, tod.AdditionalSeconds);
        Assert.Equal(2, tod.DayNumber);
        Assert.Equal(4, tod.PopWindow);
        Assert.True(tod.Claim);
        Assert.Null(tod.Killed);
    }

    // Seconds typed on the form (an older client) beat the setup's.
    [Fact]
    public async Task App_TypedSecondsBeatTheSetups()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Cerberus", 48 * 60, additionalSeconds: 16));
        await db.SaveChangesAsync();

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Cerberus", KilledAt, additionalSeconds: 5));

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(5, tod.AdditionalSeconds);
        Assert.Equal(KilledAt.AddHours(48).AddSeconds(5), tod.RepopTime);
    }

    [Fact]
    public async Task App_ResetsThePartySignUpsForThatMonster()
    {
        await using var db = await TodCallers.SeededAsync();
        var slot = TodCallers.SignedUpSetup(db, "Cerberus");
        var other = TodCallers.SignedUpSetup(db, "Tiamat");
        await db.SaveChangesAsync();

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Cerberus", KilledAt));

        Assert.Null(slot.SignedUpAppUserId);
        Assert.Equal(TodCallers.User, other.SignedUpAppUserId);
    }

    [Fact]
    public async Task App_MovesAParkedBoardToTheNewRepop()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Cerberus", 48 * 60));
        var board = TodCallers.ParkedBoard(50, "Cerberus", KilledAt.AddHours(-10));
        db.Events.Add(board);
        await db.SaveChangesAsync();

        await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Cerberus", KilledAt));

        Assert.Equal(KilledAt.AddHours(48), board.StartTime);
    }

    // ---- the addon's ToD post ----

    [Fact]
    public async Task Addon_RepopIsTimePlusTheSetupsCooldownAndSeconds()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Cerberus", 48 * 60, additionalSeconds: 16));
        await db.SaveChangesAsync();

        var body = await TodCallers.AddonPostAsync(db, "Cerberus", KilledAt, claim: null, popWindow: 2);

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(KilledAt.AddHours(48).AddSeconds(16), tod.RepopTime);
        Assert.Equal(16, tod.AdditionalSeconds);
        Assert.Equal(2, tod.PopWindow);
        Assert.Null(tod.Claim);
        Assert.False(body.GetProperty("alreadyPosted").GetBoolean());
        Assert.Equal(tod.Id, body.GetProperty("todId").GetInt32());
    }

    // The addon sends the previous ToD's day; a live camp for the spawn knows the real one.
    [Fact]
    public async Task Addon_ALiveCampDecidesTheDay()
    {
        await using var db = await TodCallers.SeededAsync();
        db.Events.Add(new Event
        {
            Id = 60,
            LinkshellId = TodCallers.LinkshellId,
            EventName = "Faf",
            EventType = "HNM",
            AssignedMonsterName = "Fafnir/Nidhogg",
            DayNumber = 3,
            StartTime = KilledAt.AddHours(-1),
            CommencementStartTime = KilledAt.AddHours(-1),
        });
        await db.SaveChangesAsync();

        await TodCallers.AddonPostAsync(db, "Fafnir", KilledAt, dayNumber: 1);

        Assert.Equal(3, (await db.Tods.SingleAsync()).DayNumber);
    }

    [Fact]
    public async Task Addon_WithNoLiveCamp_KeepsTheDayItSent()
    {
        await using var db = await TodCallers.SeededAsync();

        await TodCallers.AddonPostAsync(db, "Fafnir", KilledAt, dayNumber: 2);

        Assert.Equal(2, (await db.Tods.SingleAsync()).DayNumber);
    }

    [Fact]
    public async Task Addon_ResetsThePartySignUpsForThatMonster()
    {
        await using var db = await TodCallers.SeededAsync();
        var slot = TodCallers.SignedUpSetup(db, "Cerberus");
        await db.SaveChangesAsync();

        await TodCallers.AddonPostAsync(db, "Cerberus", KilledAt);

        Assert.Null(slot.SignedUpAppUserId);
    }

    // ---- approving a member's submission ----

    // The repop the member submitted (or the reviewer typed) is the one saved.
    [Fact]
    public async Task Approval_SavesTheSubmittedRepop()
    {
        await using var db = await TodCallers.SeededAsync();
        var pending = new PendingTodSubmission
        {
            LinkshellId = TodCallers.LinkshellId,
            SubmittedByAppUserId = TodCallers.User,
            SubmittedAtUtc = KilledAt,
            MonsterName = "Cerberus",
            DayNumber = 4,
            Time = KilledAt,
            Cooldown = "48 Hour",
            Interval = "10 Min",
            RepopTime = KilledAt.AddHours(47),
            Claim = false,
            ImagePath = "/uploads/tods/kill.png",
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        var tod = await db.Tods.SingleAsync();
        Assert.Equal("Cerberus", tod.MonsterName);
        Assert.Equal(4, tod.DayNumber);
        Assert.Equal(KilledAt.AddHours(47), tod.RepopTime);
        Assert.Equal("48 Hour", tod.Cooldown);
        Assert.Equal("10 Min", tod.Interval);
        Assert.False(tod.Claim);
        Assert.Equal("/uploads/tods/kill.png", tod.ImagePath);
        Assert.Empty(await db.PendingTodSubmissions.ToListAsync());
    }

    [Fact]
    public async Task Approval_ResetsThePartySignUpsForThatMonster()
    {
        await using var db = await TodCallers.SeededAsync();
        var slot = TodCallers.SignedUpSetup(db, "Cerberus");
        var pending = new PendingTodSubmission
        {
            LinkshellId = TodCallers.LinkshellId,
            SubmittedAtUtc = KilledAt,
            MonsterName = "Cerberus",
            Time = KilledAt,
            Cooldown = "48 Hour",
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.Null(slot.SignedUpAppUserId);
    }

    // ---- End Camp ----

    // End Camp writes every answer on the form onto ONE ToD and points the camp at it.
    [Fact]
    public async Task EndCamp_RecordsOneTodWithTheFormsAnswers()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Fafnir/Nidhogg", 22 * 60, additionalSeconds: 9));
        var camp = new Event
        {
            Id = 70,
            LinkshellId = TodCallers.LinkshellId,
            EventName = "Faf",
            EventType = "HNM",
            AssignedMonsterName = "Fafnir/Nidhogg",
            DayNumber = 2,
            StartTime = KilledAt.AddHours(-1),
            CommencementStartTime = KilledAt.AddHours(-1),
            HnmWindowNumber = 3,
        };
        db.Events.Add(camp);
        await db.SaveChangesAsync();

        var result = await TodCallers.Pop(db).PopAsync(new HnmCampPopService.Request(
            EventId: 70, TodTimeUtc: KilledAt, Cooldown: null, Interval: null, DayNumber: null,
            Claimed: true, Killed: true, PopWindow: 2, Hq: true), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var tod = await db.Tods.SingleAsync();
        Assert.Equal("Fafnir/Nidhogg", tod.MonsterName);
        Assert.True(tod.Hq);
        Assert.True(tod.Claim);
        Assert.True(tod.Killed);
        Assert.Equal(2, tod.DayNumber);
        Assert.Equal(2, tod.PopWindow);
        Assert.Equal(9, tod.AdditionalSeconds);
        Assert.Equal(KilledAt.AddHours(22).AddSeconds(9), tod.RepopTime);
        Assert.Equal(tod.Id, camp.SourceTodId);
        Assert.Equal(tod.RepopTime, camp.StartTime);
    }
}
