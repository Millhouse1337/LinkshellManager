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

    private static readonly TimeZoneConversionService TimeZones = new(
        DateTimeZoneProviders.Tzdb, NullLogger<TimeZoneConversionService>.Instance);

    private static DateTime? Parse(string? date, string? time)
    {
        Assert.True(
            DiscordInteractionsController.TryParseCampTod(
                TimeZones, date, time, Zone, out var todUtc, out var error),
            $"date=\"{date}\" time=\"{time}\" was rejected: {error}");
        return todUtc;
    }

    private static string Reject(string? date, string? time)
    {
        Assert.False(
            DiscordInteractionsController.TryParseCampTod(
                TimeZones, date, time, Zone, out _, out var error),
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
    /// A blank date means today — and a "today" still ahead of the officer's own clock means last
    /// night. This is the 2am kill logged at 9am, the commonest entry there is.
    /// </summary>
    [Fact]
    public void ABlankDate_RollsBackWhenTheTimeIsStillAhead()
    {
        var todUtc = Parse(null, "11:59 PM");

        Assert.NotNull(todUtc);
        Assert.True(todUtc!.Value <= DateTime.UtcNow, $"{todUtc:o} is in the future.");
        Assert.True(todUtc.Value > DateTime.UtcNow.AddDays(-2), "it should be last night, not last week.");
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
                todUtc!.Value <= DateTime.UtcNow,
                $"\"{monthDay}\" resolved to {todUtc:o}, which is in the future.");
            Assert.True(
                todUtc.Value > DateTime.UtcNow.AddYears(-1).AddDays(-2),
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
        Assert.True(Math.Abs((DateTime.UtcNow - todUtc!.Value).TotalMinutes) < 1);
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
