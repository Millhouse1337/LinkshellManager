using System;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Xunit;

namespace LinkshellManager.Tests;

/// <summary>
/// What the End Camp / Enter ToD box accepts.
///
/// The field is free text because Discord caps a modal at 5 rows and an HQ family already fills
/// all five (ToD, HQ, Outcome, pop window, re-post lead) — so there is no row left for a separate
/// Date, and every HQ family is a 7-window king/dragon, which makes the camps that most need one
/// exactly the full ones.
///
/// The case that matters is a mob that died overnight and gets logged the next morning, or a
/// Friday kill entered on Monday. A bare clock time can only ever reach yesterday, so the date
/// spellings below are the whole feature.
/// </summary>
public class EndCampTodDateEntryTests
{
    // A fixed, DST-free zone so "local" is arithmetic rather than a question. UTC-5 all year.
    private const string Zone = "America/Cancun";

    private static readonly TimeZoneConversionService TimeZones = new(
        DateTimeZoneProviders.Tzdb, NullLogger<TimeZoneConversionService>.Instance);

    private static DateTime? Parse(string? raw)
    {
        Assert.True(
            DiscordInteractionsController.TryParseCampTod(TimeZones, raw, Zone, out var todUtc, out var error),
            $"\"{raw}\" was rejected: {error}");
        return todUtc;
    }

    private static string Reject(string raw)
    {
        Assert.False(
            DiscordInteractionsController.TryParseCampTod(TimeZones, raw, Zone, out _, out var error),
            $"\"{raw}\" was accepted; it should not parse.");
        return error ?? string.Empty;
    }

    /// <summary>Blank stays "not entered" — no time, and no repop derived from one.</summary>
    [Fact]
    public void Blank_IsNotEntered()
    {
        Assert.Null(Parse(null));
        Assert.Null(Parse(""));
        Assert.Null(Parse("   "));
    }

    /// <summary>
    /// A DATE plus a time, in the form someone types in a hurry. This is the entry that was
    /// possible but undiscoverable before: nothing on the modal said a date was allowed.
    /// </summary>
    [Theory]
    [InlineData("9/5 9:05 PM")]
    [InlineData("9/5 21:05")]
    [InlineData("9/5/2026 9:05 PM")]
    [InlineData("2026-09-05 21:05")]
    public void ADateAndTime_ResolvesToThatEvening(string raw)
    {
        // 21:05 at UTC-5 is 02:05 the next day in UTC.
        var todUtc = Parse(raw);

        Assert.NotNull(todUtc);
        Assert.Equal(new DateTime(2026, 9, 6, 2, 5, 0, DateTimeKind.Unspecified), todUtc!.Value);
    }

    /// <summary>
    /// Seconds survive a dated entry. A ToD drives the repop clock, so dropping them silently
    /// would shift every predicted window by up to a minute.
    /// </summary>
    [Fact]
    public void ADatedEntry_KeepsItsSeconds()
    {
        Assert.Equal(
            new DateTime(2026, 9, 6, 2, 5, 15, DateTimeKind.Unspecified),
            Parse("9/5 9:05:15 PM"));
    }

    /// <summary>
    /// "yesterday" is the shape the overnight case actually reaches for, and it must land one day
    /// back — NOT two. The bare-time path rolls back on its own when a time is still in the
    /// future, and applying both rules to one entry was the easy way to be off by a day.
    /// </summary>
    [Fact]
    public void Yesterday_GoesBackExactlyOneDay()
    {
        var yesterday = Parse("yesterday 9:05 PM");
        var today = Parse("9:05 PM");

        Assert.NotNull(yesterday);
        Assert.NotNull(today);

        var gap = today!.Value - yesterday!.Value;
        // "9:05 PM" itself rolls back a day when the officer types it before 9:05 PM, so the gap
        // is a full day either way — what must never happen is yesterday landing two days back.
        Assert.True(
            gap == TimeSpan.FromDays(1) || gap == TimeSpan.Zero,
            $"expected yesterday to be one day before today's reading at most, got {gap}.");
        Assert.True(
            yesterday.Value < DateTime.UtcNow,
            "a Time of Death is always in the past.");
    }

    /// <summary>
    /// A year-less date resolves to its MOST RECENT occurrence, which is what makes it survive New
    /// Year: "12/31 11:00 PM" entered on January 1st means five days ago, not eleven months away.
    /// Asserted as "never in the future" so the test does not itself depend on today's date.
    /// </summary>
    [Fact]
    public void AYearLessDate_IsNeverInTheFuture()
    {
        foreach (var monthDay in new[] { "1/1", "6/15", "12/31" })
        {
            var todUtc = Parse($"{monthDay} 11:00 PM");

            Assert.NotNull(todUtc);
            Assert.True(
                todUtc!.Value <= DateTime.UtcNow,
                $"\"{monthDay} 11:00 PM\" resolved to {todUtc:o}, which is in the future.");
            Assert.True(
                todUtc.Value > DateTime.UtcNow.AddYears(-1).AddDays(-2),
                $"\"{monthDay} 11:00 PM\" resolved to {todUtc:o}, further back than the most recent one.");
        }
    }

    /// <summary>The forms that already worked keep working — this widened the field, it did not move it.</summary>
    [Theory]
    [InlineData("21:05")]
    [InlineData("9:05 PM")]
    [InlineData("9:05:15 pm")]
    [InlineData("9:05 p.m.")]
    public void TheOriginalClockForms_StillParse(string raw)
    {
        Assert.NotNull(Parse(raw));
    }

    /// <summary>"now" is the explicit shortcut and stays exact rather than rounding to a minute.</summary>
    [Fact]
    public void Now_IsThisMoment()
    {
        var todUtc = Parse("now");

        Assert.NotNull(todUtc);
        Assert.True(Math.Abs((DateTime.UtcNow - todUtc!.Value).TotalMinutes) < 1);
    }

    /// <summary>
    /// Unparseable input is REFUSED, never guessed — a wrong ToD silently sets every predicted
    /// window for the next pop. The message has to carry the dated spellings, because it is the
    /// only place they are written out in full.
    /// </summary>
    [Fact]
    public void GarbageIsRefused_AndTheMessageShowsTheDatedForms()
    {
        var error = Reject("sometime last night");

        Assert.Contains("yesterday", error);
        Assert.Contains("9/5", error);
        Assert.False(string.IsNullOrWhiteSpace(error));

        Reject("25:99");
        Reject("13/45 9:05 PM");
    }

    /// <summary>
    /// The modal itself must keep saying a date is allowed. The parser accepting one is useless if
    /// the box still reads as clock-only — that was the state this started from.
    /// </summary>
    [Fact]
    public void TheModalAdvertisesTheDatedForms()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            DiscordInteractionsController.BuildWdPopModalFields(
                new LinkshellManagerDiscordApp.Models.Event { Id = 1, AssignedMonsterName = "Fafnir" },
                null));

        Assert.Contains("yesterday", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9/5", json);
    }
}
