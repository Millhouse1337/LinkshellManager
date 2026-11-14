using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// P3.6: what TodRecorder changed on purpose, now that the app, the addon, approval and End Camp all
// record a ToD through it (backend-04, logic-10). What each path already did and still does is in
// TodRecordingCharacterizationTests; the two-posts-at-once race is in the Postgres tests.
public class TodRecorderTests
{
    private static readonly DateTime KilledAt = new(2026, 11, 5, 13, 0, 0, DateTimeKind.Utc);

    private static TodRecordInput Input(string monster, DateTime? time, bool? claim = true) =>
        new(TodCallers.LinkshellId, monster, Hq: null, time, AdditionalSeconds: null, PopWindow: null,
            claim, Killed: null, DayNumber: null, TodSources.App, TodCallers.User);

    // ---- logic-10: the app now checks for the same kill ----

    // The finding's example: the addon posts Fafnir's ToD, and an officer unsure it went through
    // logs it in the app a moment later. That used to be two ToDs, two repops, and the kill counted
    // twice in the Window chart.
    [Fact]
    public async Task App_LoggingAKillTheAddonAlreadyPosted_AddsToThatTod()
    {
        await using var db = await TodCallers.SeededAsync();
        var posted = await TodCallers.AddonPostAsync(db, "Fafnir", KilledAt.AddSeconds(10), claim: null);

        var logged = await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Fafnir", KilledAt, claim: true, popWindow: 3));

        var tod = await db.Tods.SingleAsync();
        Assert.Equal(KilledAt.AddSeconds(10), tod.Time); // the first post's time stands
        Assert.True(tod.Claim);                          // the blank claim takes the app's answer
        Assert.Equal(3, tod.PopWindow);
        Assert.True(logged.GetProperty("alreadyPosted").GetBoolean());
        Assert.Equal(posted.GetProperty("todId").GetInt32(), logged.GetProperty("tod").GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task App_AKillMinutesLater_IsItsOwnTod()
    {
        await using var db = await TodCallers.SeededAsync();
        await TodCallers.AddonPostAsync(db, "Fafnir", KilledAt);

        var logged = await TodCallers.AppPostAsync(db, TodCallers.AppRequest("Fafnir", KilledAt.AddMinutes(5)));

        Assert.Equal(2, await db.Tods.CountAsync());
        Assert.False(logged.TryGetProperty("alreadyPosted", out _));
    }

    // ---- one name per spawn, whoever posts it ----

    // The addon posts the half that died. It is stored like the app and the camp boards store it:
    // under the pair, with HQ ticked, so the day cycle and claim stats read it the same way.
    [Theory]
    [InlineData("Nidhogg", true)]
    [InlineData("Fafnir", false)]
    public async Task Addon_StoresAMergePairUnderItsCombinedName(string posted, bool expectedHq)
    {
        await using var db = await TodCallers.SeededAsync();

        await TodCallers.AddonPostAsync(db, posted, KilledAt);

        var tod = await db.Tods.SingleAsync();
        Assert.Equal("Fafnir/Nidhogg", tod.MonsterName);
        Assert.Equal(expectedHq, tod.Hq);
    }

    // ---- every path moves a parked board now, not only the app ----

    [Fact]
    public async Task Addon_MovesAParkedBoardToTheNewRepop()
    {
        await using var db = await TodCallers.SeededAsync();
        db.LinkshellMonsterTimings.Add(TodCallers.Setup("Cerberus", 48 * 60));
        var board = TodCallers.ParkedBoard(50, "Cerberus", KilledAt.AddHours(-10));
        db.Events.Add(board);
        await db.SaveChangesAsync();

        await TodCallers.AddonPostAsync(db, "Cerberus", KilledAt);

        Assert.Equal(KilledAt.AddHours(48), board.StartTime);
    }

    [Fact]
    public async Task Approval_MovesAParkedBoardToTheNewRepop()
    {
        await using var db = await TodCallers.SeededAsync();
        var board = TodCallers.ParkedBoard(50, "Cerberus", KilledAt.AddHours(-10));
        db.Events.Add(board);
        var pending = new PendingTodSubmission
        {
            LinkshellId = TodCallers.LinkshellId,
            SubmittedAtUtc = KilledAt,
            MonsterName = "Cerberus",
            Time = KilledAt,
            Cooldown = "48 Hour",
            RepopTime = KilledAt.AddHours(48),
        };
        db.PendingTodSubmissions.Add(pending);
        await db.SaveChangesAsync();

        await TodCallers.Approvals(db).ApproveTodAsync(pending.Id, CancellationToken.None);

        Assert.Equal(KilledAt.AddHours(48), board.StartTime);
    }

    // ---- End Camp ----

    // A kill the addon posted BEFORE the camp went live is outside End Camp's own adoption window,
    // so End Camp used to save a second ToD beside it. It is the same kill: one ToD.
    [Fact]
    public async Task EndCamp_AKillAlreadySavedOutsideTheCamp_IsNotSavedTwice()
    {
        await using var db = await TodCallers.SeededAsync();
        var earlier = new Tod
        {
            LinkshellId = TodCallers.LinkshellId,
            MonsterName = "Fafnir/Nidhogg",
            Time = KilledAt.AddSeconds(20),
            RepopTime = KilledAt.AddSeconds(20).AddHours(22),
            TimeStamp = KilledAt.AddHours(-3), // stamped before the camp went live
            PopWindow = 4,
        };
        db.Tods.Add(earlier);
        db.Events.Add(new Event
        {
            Id = 70,
            LinkshellId = TodCallers.LinkshellId,
            EventName = "Faf",
            EventType = "HNM",
            AssignedMonsterName = "Fafnir/Nidhogg",
            DayNumber = 2,
            StartTime = KilledAt.AddHours(-1),
            CommencementStartTime = KilledAt.AddHours(-1),
            HnmWindowNumber = 6,
        });
        await db.SaveChangesAsync();

        var result = await TodCallers.Pop(db).PopAsync(new HnmCampPopService.Request(
            EventId: 70, TodTimeUtc: KilledAt, Cooldown: null, Interval: null, DayNumber: null,
            Claimed: true, Killed: true, PopWindow: null), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var tod = await db.Tods.SingleAsync();
        Assert.Equal(earlier.Id, tod.Id);
        // Treated like an adopted ToD: the form's answers win, and its window reading is kept.
        Assert.Equal(KilledAt, tod.Time);
        Assert.True(tod.Killed);
        Assert.Equal(4, tod.PopWindow);
    }

    // ---- the recorder itself ----

    [Fact]
    public async Task ASecondPostOfTheKill_FillsAMissingScreenshot()
    {
        await using var db = await TodCallers.SeededAsync();
        var recorder = TodCallers.Recorder(db);
        await recorder.RecordAsync(Input("Cerberus", KilledAt), CancellationToken.None);

        var second = await recorder.RecordAsync(
            Input("Cerberus", KilledAt.AddSeconds(30)) with { ImagePath = "/uploads/tods/kill.png" },
            CancellationToken.None);

        Assert.True(second.MergedIntoExisting);
        Assert.Equal("/uploads/tods/kill.png", (await db.Tods.SingleAsync()).ImagePath);
    }

    // End Camp's "nobody saw it die": a row with no time and no repop, which nothing can match.
    [Fact]
    public async Task NoTime_SavesARowWithNoRepop_AndNeverMerges()
    {
        await using var db = await TodCallers.SeededAsync();
        var recorder = TodCallers.Recorder(db);

        var first = await recorder.RecordAsync(Input("Cerberus", null), CancellationToken.None);
        var second = await recorder.RecordAsync(Input("Cerberus", null), CancellationToken.None);

        Assert.False(first.MergedIntoExisting);
        Assert.False(second.MergedIntoExisting);
        Assert.Null(first.RepopUtc);
        Assert.Equal(2, await db.Tods.CountAsync());
    }

    [Fact]
    public async Task ATypedRepop_BeatsTheSum()
    {
        await using var db = await TodCallers.SeededAsync();

        var result = await TodCallers.Recorder(db).RecordAsync(
            Input("Cerberus", KilledAt) with { Cooldown = "48 Hour", RepopUtcOverride = KilledAt.AddHours(40) },
            CancellationToken.None);

        Assert.Equal(KilledAt.AddHours(40), result.Tod.RepopTime);
    }

    [Fact]
    public void RepopIsTimePlusCooldownPlusSeconds()
    {
        Assert.Equal(KilledAt.AddHours(22).AddSeconds(9), TodRecorder.RepopFor(KilledAt, "22 Hour", 9));
        Assert.Equal(KilledAt.AddMinutes(45), TodRecorder.RepopFor(KilledAt, "45 Min", -3));
    }

    // Every spelling of one spawn takes the same lock, and the key never changes between restarts
    // (string.GetHashCode would).
    [Fact]
    public void EverySpellingOfASpawn_TakesTheSameLock()
    {
        var keys = new[] { "Fafnir", "Nidhogg", "fafnir/nidhogg", " Fafnir/Nidhogg " }
            .Select(name => TodRecorder.LockKeys(7, TodRecorder.Normalize(name, false).Name))
            .Distinct()
            .ToList();

        Assert.Single(keys);
        Assert.Equal((7, unchecked((int)Fnv1a("fafnir/nidhogg"))), keys[0]);
        Assert.NotEqual(keys[0], TodRecorder.LockKeys(8, "Fafnir/Nidhogg"));
        Assert.NotEqual(keys[0], TodRecorder.LockKeys(7, "Behemoth/King Behemoth"));
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
        {
            hash = unchecked((hash ^ ch) * 16777619u);
        }
        return hash;
    }
}
