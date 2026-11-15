using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Xunit;

namespace LinkshellManager.Tests;

// The two C# helpers the save hook uses to fill Tod.SpawnKey / CooldownMinutes / IntervalMinutes
// (and the same columns on PendingTodSubmissions and HnmRecurringBoards). The Postgres suite checks
// the SQL copies in TodDerivedSql give the same answers (TodDerivedColumnsParityTests); these pin the answers.
public class TodDerivedValueTests
{
    [Theory]
    // Every monster name production holds on Tods and standing boards.
    [InlineData("Adamantoise", "adamantoise/aspidochelone")]
    [InlineData("Adamantoise/Aspidochelone", "adamantoise/aspidochelone")]
    [InlineData("Behemoth/King Behemoth", "behemoth/king behemoth")]
    [InlineData("Cerberus", "cerberus")]
    [InlineData("Fafnir/Nidhogg", "fafnir/nidhogg")]
    [InlineData("Khimaira", "khimaira")]
    [InlineData("Tiamat", "tiamat")]
    [InlineData("Hydra", "hydra")]
    [InlineData("Jormungand", "jormungand")]
    [InlineData("King Arthro", "king arthro")]
    [InlineData("Misc Monster", "misc monster")]
    [InlineData("Testing", "testing")]
    [InlineData("Vrtra", "vrtra")]
    // Either half of a pair is the same spawn as the combined label.
    [InlineData("Aspidochelone", "adamantoise/aspidochelone")]
    [InlineData("Behemoth", "behemoth/king behemoth")]
    [InlineData("King Behemoth", "behemoth/king behemoth")]
    [InlineData("Fafnir", "fafnir/nidhogg")]
    [InlineData("Nidhogg", "fafnir/nidhogg")]
    [InlineData(" Fafnir ", "fafnir/nidhogg")]
    [InlineData("fafnir/NIDHOGG", "fafnir/nidhogg")]
    [InlineData("Nidhogg/Fafnir", "fafnir/nidhogg")]
    // Other names: trimmed, lower-case pieces.
    [InlineData("Foo / Bar", "foo/bar")]
    [InlineData("  TIAMAT\t", "tiamat")]
    [InlineData("Foo//Bar/", "foo/bar")]
    [InlineData("/", "")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void SpawnKey_OneKeyPerSpawn(string? name, string? expected)
    {
        Assert.Equal(expected, HnmConfig.SpawnKey(name));
    }

    [Theory]
    // Every Cooldown production holds (a bare number is hours for a cooldown).
    [InlineData("22 Hour", 1320)]
    [InlineData("48 Hour", 2880)]
    [InlineData("72 Hour", 4320)]
    [InlineData("84 Hour", 5040)]
    [InlineData("22", 1320)]
    [InlineData(" 72 ", 4320)]
    [InlineData("1 Hour 30 Min", 90)]
    [InlineData("90 Min", 90)]
    [InlineData("1.5 h", 90)]
    [InlineData("2hrs", 120)]
    [InlineData("0.025 h", 2)]
    [InlineData("0 Min", null)]
    [InlineData("0", null)]
    [InlineData("1h30m", null)]
    [InlineData("99999999999 Hour", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void StoredMinutes_Cooldown(string? text, int? expected)
    {
        Assert.Equal(expected, TodDurationFormat.StoredMinutes(text, TodDurationFormat.HoursUnit));
    }

    [Theory]
    // Every Interval production holds (a bare number is minutes for an interval).
    [InlineData("10 Min", 10)]
    [InlineData("1 Hour", 60)]
    [InlineData("6 Hour", 360)]
    [InlineData("5 min", 5)]
    [InlineData("10", 10)]
    [InlineData("0.5", 1)]
    [InlineData("0.4", null)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("2147483648", null)]
    public void StoredMinutes_Interval(string? text, int? expected)
    {
        Assert.Equal(expected, TodDurationFormat.StoredMinutes(text, TodDurationFormat.MinutesUnit));
    }

    // A digit outside 0-9 is not a duration: it used to match the pattern and then throw in Parse.
    [Fact]
    public void NonAsciiDigits_AreNotADuration()
    {
        Assert.False(TodDurationFormat.TryParseMinutes("\u0662\u0662 Hour", TodDurationFormat.HoursUnit, out _));
        Assert.Null(TodDurationFormat.StoredMinutes("\u0662\u0662", TodDurationFormat.HoursUnit));
    }

    // Two copies of the merge pairs must agree: HnmConfig (the app) and the SpawnKey SQL, in the
    // same order (a name with halves of two pairs takes the first pair's key on both sides).
    [Fact]
    public void MergePairs_AgreeAcrossHnmConfigAndTheSpawnKeySql()
    {
        var fromSql = Regex.Matches(
                TodDerivedSql.SpawnKeySql("x"),
                @"WHEN '([^']+)' = ANY\(s\.l\) OR '([^']+)' = ANY\(s\.l\) THEN '([^']+)'")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value))
            .ToArray();
        var fromConfig = HnmConfig.MonsterMergePairs
            .Select(pair => (
                pair.Base.ToLowerInvariant(),
                pair.Stronger.ToLowerInvariant(),
                $"{pair.Base}/{pair.Stronger}".ToLowerInvariant()))
            .ToArray();

        Assert.Equal(fromConfig, fromSql);
        Assert.Equal(3, fromSql.Length);
    }

    // TodDerivedSql spells out .NET's whitespace for Postgres (whose own \s depends on the server's
    // locale). Its set must be exactly what string.Trim and the duration patterns' \s treat as
    // whitespace, or the SQL fill and the save hook would disagree on padded text.
    [Fact]
    public void SqlWhitespaceSet_IsExactlyDotNetWhitespace()
    {
        var spelledOut = new Regex("^[" + TodDerivedSql.WhitespaceChars + "]$");
        var dotNet = new Regex(@"^\s$");
        for (var code = 0; code <= char.MaxValue; code++)
        {
            var text = ((char)code).ToString();
            Assert.True(char.IsWhiteSpace((char)code) == spelledOut.IsMatch(text), $"U+{code:X4}");
            Assert.True(char.IsWhiteSpace((char)code) == dotNet.IsMatch(text), $"U+{code:X4}");
        }
    }

    // StoredMinutes agrees with TryParseMinutes wherever the minutes fit an integer column.
    [Theory]
    [InlineData("22 Hour")]
    [InlineData("1 Hour 30 Min")]
    [InlineData("0.5 Min")]
    [InlineData("0 Min")]
    [InlineData("abc")]
    public void StoredMinutes_MatchesTryParseMinutes(string text)
    {
        var parsed = TodDurationFormat.TryParseMinutes(text, TodDurationFormat.HoursUnit, out var minutes);
        Assert.Equal(parsed ? minutes : null, TodDurationFormat.StoredMinutes(text, TodDurationFormat.HoursUnit));
    }
}
