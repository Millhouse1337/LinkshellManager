using System;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// The End Camp modal's Time of Death, entered as two boxes: Date and Time.
///
/// The case that matters is a mob that died overnight and gets logged the next morning, or a
/// Friday kill entered on Monday. A single free-text box could already carry a date, but nothing
/// about it said so — an officer who did not know types a bare time, and a bare time silently
/// means today.
///
/// The Date box costs a modal row, and Discord caps a modal at 5. It was paid for by removing the
/// "Which window did it pop on?" picker: every HQ family is a 7-window king/dragon, so the camps
/// that most need a date were exactly the ones already full.
/// </summary>
public class EndCampTodDateEntryTests
{
    // A fixed, DST-free zone so "local" is arithmetic rather than a question. UTC-5 all year.
    private const string Zone = "America/Cancun";

    // A fixed "now", so a rule about times AHEAD of the clock doesn't pass or fail with the hour the
    // tests run: 9:10 AM on Nov 8 in Cancun.
    private static readonly DateTime Now = new(2026, 11, 8, 14, 10, 0, DateTimeKind.Utc);

    private static readonly TimeZoneConversionService TimeZones = new(
        DateTimeZoneProviders.Tzdb, NullLogger<TimeZoneConversionService>.Instance);

    private static DateTime? Parse(string? date, string? time, string? zone = Zone)
    {
        Assert.True(
            DiscordInteractionsController.TryParseCampTod(
                TimeZones, date, time, zone, out var todUtc, out var error, Now),
            $"date=\"{date}\" time=\"{time}\" was rejected: {error}");
        return todUtc;
    }

    private static string Reject(string? date, string? time, string? zone = Zone)
    {
        Assert.False(
            DiscordInteractionsController.TryParseCampTod(
                TimeZones, date, time, zone, out _, out var error, Now),
            $"date=\"{date}\" time=\"{time}\" was accepted; it should not parse.");
        Assert.False(string.IsNullOrWhiteSpace(error), "a rejection must explain itself.");
        return error!;
    }

    /// <summary>Both boxes blank stays "not entered" — no time, and no repop derived from one.</summary>
    [Fact]
    public void BothBlank_IsNotEntered()
    {
        Assert.Null(Parse(null, null));
        Assert.Null(Parse("", ""));
        Assert.Null(Parse("   ", "   "));
    }

    /// <summary>
    /// A date in its own box, in each spelling someone might reach for. This is the entry the
    /// split exists for: the kill was not today, and saying so takes no explaining.
    /// </summary>
    [Theory]
    [InlineData("9/5", "9:05 PM")]
    [InlineData("9/5", "21:05")]
    [InlineData("9/5/2026", "9:05 PM")]
    [InlineData("2026-09-05", "21:05")]
    [InlineData("9-5", "9:05 PM")]
    public void ADateAndATime_ResolveToThatEvening(string date, string time)
    {
        // 21:05 at UTC-5 is 02:05 the next day in UTC.
        Assert.Equal(new DateTime(2026, 9, 6, 2, 5, 0, DateTimeKind.Unspecified), Parse(date, time));
    }

    /// <summary>
    /// Seconds survive. A ToD drives the repop clock, so dropping them would shift every predicted
    /// window for the next pop by up to a minute.
    /// </summary>
    [Fact]
    public void SecondsAreKept()
    {
        Assert.Equal(
            new DateTime(2026, 9, 6, 2, 5, 15, DateTimeKind.Unspecified),
            Parse("9/5", "9:05:15 PM"));
    }

    /// <summary>"yesterday" and "today" are words, because that is how the overnight case is thought about.</summary>
    [Fact]
    public void YesterdayIsExactlyOneDayBeforeToday()
    {
        var yesterday = Parse("yesterday", "9:05 PM");
        var today = Parse("today", "9:05 PM");

        Assert.NotNull(yesterday);
        Assert.NotNull(today);
        Assert.Equal(TimeSpan.FromDays(1), today!.Value - yesterday!.Value);
    }

    /// <summary>
    /// A blank date means today — and a "today" more than 3 hours ahead of the officer's own clock
    /// means last night. This is the 11pm kill logged the next morning, the commonest entry there is.
    /// </summary>
    [Fact]
    public void ABlankDate_RollsBackWhenTheTimeIsStillAhead()
    {
        var todUtc = Parse(null, "11:59 PM");

        Assert.NotNull(todUtc);
        // 11:59 PM is almost 15 hours ahead of 9:10 AM: last night, Nov 7.
        Assert.Equal(new DateTime(2026, 11, 8, 4, 59, 0), todUtc);
    }

    /// <summary>
    /// An EXPLICIT date is never second-guessed. The roll-back above applies only to a blank date;
    /// applying it to a typed one would move an officer's deliberate answer by a day.
    /// </summary>
    [Fact]
    public void AnExplicitTodayIsNotRolledBack()
    {
        var explicitToday = Parse("today", "11:59 PM");
        var blank = Parse(null, "11:59 PM");

        Assert.NotNull(explicitToday);
        Assert.NotNull(blank);
        // Same wall clock, but the blank one may have been pushed back a day; the typed one never is.
        Assert.True(explicitToday!.Value >= blank!.Value);
    }

    /// <summary>
    /// A year-less date resolves to its MOST RECENT occurrence, which is what carries it across New
    /// Year: "12/31" entered on January 1st is days ago, not eleven months away. Asserted as "never
    /// in the future" so the test does not itself depend on today's date.
    /// </summary>
    [Fact]
    public void AYearLessDate_IsNeverInTheFuture()
    {
        foreach (var monthDay in new[] { "1/1", "6/15", "12/31" })
        {
            var todUtc = Parse(monthDay, "11:00 PM");

            Assert.NotNull(todUtc);
            Assert.True(
                todUtc!.Value <= Now,
                $"\"{monthDay}\" resolved to {todUtc:o}, which is in the future.");
            Assert.True(
                todUtc.Value > Now.AddYears(-1).AddDays(-2),
                $"\"{monthDay}\" resolved to {todUtc:o}, further back than the most recent one.");
        }
    }

    /// <summary>The clock forms that already worked keep working — this split the field, it did not move it.</summary>
    [Theory]
    [InlineData("21:05")]
    [InlineData("9:05 PM")]
    [InlineData("9:05:15 pm")]
    [InlineData("9:05 p.m.")]
    public void TheOriginalClockForms_StillParse(string time)
    {
        Assert.NotNull(Parse(null, time));
    }

    /// <summary>"now" is the explicit shortcut and stays exact rather than rounding to a minute.</summary>
    [Fact]
    public void Now_IsThisMoment()
    {
        var todUtc = Parse(null, "now");

        Assert.NotNull(todUtc);
        Assert.Equal(Now, todUtc);
    }

    // ---- logic-09: a typed ToD can no longer be saved 23 hours early ----

    /// <summary>A time already passed today is today, as typed.</summary>
    [Fact]
    public void APastTime_IsToday()
    {
        Assert.Equal(new DateTime(2026, 11, 8, 14, 5, 0), Parse(null, "9:05 AM"));
    }

    /// <summary>
    /// THE BUG. The officer's Profile says America/Chicago but they read an Eastern clock: the kill
    /// was 9:05 PM Eastern, typed at 9:20 PM Eastern, which is 8:20 PM by their LSM clock. "9:05 PM"
    /// looked 45 minutes in the future, was rolled back a day, and the repop and board were 23 hours
    /// early. Now it is refused, with both clocks shown so they can see which one is wrong.
    /// </summary>
    [Fact]
    public void ATimeAFewMinutesAhead_IsRefused_WithBothClocks()
    {
        var typedAt = new DateTime(2026, 11, 9, 2, 20, 0, DateTimeKind.Utc); // 8:20 PM Nov 8 in Chicago

        Assert.False(DiscordInteractionsController.TryParseCampTod(
            TimeZones, null, "9:05 PM", "America/Chicago", out var todUtc, out var error, typedAt));

        Assert.Null(todUtc);
        Assert.Contains("Nov 8, 9:05 PM", error);  // what they typed
        Assert.Contains("Nov 8, 8:20 PM", error);  // what their LSM clock says
        Assert.Contains("America/Chicago", error);
        Assert.Contains("Nothing was saved", error);
    }

    /// <summary>Up to three hours ahead is refused; the message offers `yesterday` for a real overnight kill.</summary>
    [Theory]
    [InlineData("9:11 AM")]   // a minute ahead
    [InlineData("12:10 PM")]  // exactly three hours ahead
    public void UpToThreeHoursAhead_IsRefused(string time)
    {
        Assert.Contains("yesterday", Reject(null, time));
    }

    /// <summary>More than three hours ahead still means last night.</summary>
    [Fact]
    public void MoreThanThreeHoursAhead_IsLastNight()
    {
        Assert.Equal(new DateTime(2026, 11, 7, 17, 11, 0), Parse(null, "12:11 PM"));
    }

    /// <summary>
    /// With no time zone on the Profile a typed time used to be read as UTC, about five hours off for
    /// an officer in the US. It is refused, with what to do about it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Not/AZone")]
    public void NoProfileZone_RefusesATypedTime(string? zone)
    {
        Assert.Contains("time zone", Reject(null, "9:05 AM", zone));
        Assert.Contains("time zone", Reject("9/5", "9:05 PM", zone));
    }

    /// <summary>`now` and both-blank need no zone, so an officer without one can still end a camp.</summary>
    [Fact]
    public void NoProfileZone_NowAndBlankStillWork()
    {
        Assert.Equal(Now, Parse(null, "now", zone: null));
        Assert.Null(Parse(null, null, zone: null));
    }

    /// <summary>
    /// A date beside "now" is a contradiction, not extra information. Silently honouring one of the
    /// two would record a time the officer did not ask for.
    /// </summary>
    [Fact]
    public void ADateBesideNow_IsRefused()
    {
        Assert.Contains("now", Reject("9/5", "now"), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A date with no time is a half-filled form, not a Time of Death. Guessing midnight would look
    /// like a real answer forever after, and the repop clock is counted from it.
    /// </summary>
    [Fact]
    public void ADateWithNoTime_IsRefused()
    {
        var error = Reject("9/5", null);

        Assert.Contains("Time of Death", error);
        Assert.Contains("blank", error, StringComparison.OrdinalIgnoreCase); // points at the "nobody saw it" path
    }

    /// <summary>Unparseable input is REFUSED, never guessed — a wrong ToD sets every window for the next pop.</summary>
    [Fact]
    public void GarbageIsRefused()
    {
        Assert.Contains("Time of Death", Reject(null, "sometime last night"));
        Assert.Contains("Date", Reject("last tuesday", "9:05 PM"));

        Reject(null, "25:99");
        Reject("13/45", "9:05 PM");
    }

    /// <summary>
    /// The modal has to actually SHOW both boxes, and must no longer ask for the pop window — the
    /// Date row is the one the window picker paid for.
    /// </summary>
    [Fact]
    public void TheModalHasADateBox_AndNoWindowPicker()
    {
        // A 7-window HQ family: the camp that used to fill all five rows.
        var json = System.Text.Json.JsonSerializer.Serialize(
            DiscordInteractionsController.BuildWdPopModalFields(
                new LinkshellManagerDiscordApp.Models.Event
                {
                    Id = 1,
                    AssignedMonsterName = "Fafnir",
                    HnmWindowNumber = 3,
                },
                null));

        Assert.Contains("wdpop_tod_date", json);
        Assert.Contains("wdpop_tod", json);
        Assert.DoesNotContain("wdpop_window", json);
        Assert.DoesNotContain("Which window did it pop on?", json);
    }
}
