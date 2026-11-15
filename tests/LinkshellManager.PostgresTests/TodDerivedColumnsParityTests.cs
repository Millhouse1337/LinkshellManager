using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LinkshellManager.PostgresTests;

// TodDerivedSql holds SQL copies of the C# the save hook uses to stamp CooldownMinutes /
// IntervalMinutes / SpawnKey (TodDurationFormat.StoredMinutes, HnmConfig.SpawnKey). The
// AddTodDurationsAndSpawnKeys migration filled old rows with that SQL (before the squash), and any
// later SQL fill uses it too, so the two must agree on every value. Each input runs through the SQL
// expressions on real Postgres and is compared with the C#.
//
// The lists hold every distinct Cooldown, Interval and monster name production holds (from the
// pre-deploy check), plus the edge cases: bare numbers, mixed units, padding, capitals, zero,
// overflow, words, blank and NULL.
[Collection("postgres")]
public class TodDerivedColumnsParityTests
{
    public static readonly string?[] Durations =
    {
        // Production: every Cooldown and Interval value.
        "22 Hour", "48 Hour", "72 Hour", "84 Hour", "10 Min", "1 Hour", "6 Hour", null,
        // The plan's edge cases.
        "22", "1 Hour 30 Min", "90 Min", "0 Min", "5 min", " 72 ", "1.5 h", "2hrs",
        "99999999999 Hour", "abc", "",
        // More: rounding, overflow edges, spacing, capitals, unit spellings, things that are not durations.
        "0", "0.5", "0.4", "0.025 h", "0.5 Min", "1.25 Min", "2.5 Min",
        // Exactly half a minute in decimal but just under it as a double, which is what the C# rounds.
        "1.025 h", "1.025", "16.775 Hour", "4.225 h 0.5 m", "32.175 Hour", "0.000000000000000000000000000001",
        "2147483647.4", "2147483647.5", "35791394.1 Hour",
        "2147483647", "2147483648", "35791394 Hour", "35791395 Hour", "35791394 Hour 7 Min", "35791394 Hour 8 Min",
        "HOURS", "1 HOURS 5 MINUTES", "1 Hr", "3 hrs 2 mins", "1h 30m", "1h30m", "90Min", "90 M", "2 H",
        "\t22\n", "   ", "1.", ".5", "1e3", "-5", "22 Hours ago", "Min", "1 Hour 30", "30 Min 1 Hour",
        "1 Hour 1 Hour", "22 Hourly", "1 h m", "007", "1.50 h", "\u0662\u0662", "22\u00A0Hour",
        new string('9', 40), new string('9', 40) + " Min",
        // Whitespace .NET trims but Postgres's locale-dependent \s may not.
        "\u00A0", "22\u2009Hour", "\u300010 Min\u2028", "1 Hour\u0085", "1\u000B Hour",
    };

    public static readonly string?[] MonsterNames =
    {
        // Production: every monster name on Tods and on standing boards.
        "Adamantoise", "Adamantoise/Aspidochelone", "Behemoth/King Behemoth", "Cerberus", "Fafnir/Nidhogg",
        "Khimaira", "Tiamat", "Hydra", "Jormungand", "King Arthro", "Misc Monster", "Testing", "Vrtra",
        // The plan's edge cases.
        " Fafnir ", "fafnir/NIDHOGG", "King Behemoth", "Foo / Bar", "/", "Tiamat",
        // More: each half alone, reversed and padded pairs, two pairs in one name, empty pieces, blank.
        "Aspidochelone", "Behemoth", "Fafnir", "Nidhogg", "Nidhogg/Fafnir", "\tKing Behemoth\n",
        "KING BEHEMOTH/behemoth", "Fafnir/Behemoth", "Nidhogg/Aspidochelone", "Foo//Bar/", "//", " / ",
        "Behemoth King", "Fafnirs", "King  Behemoth", "", "   ", null,
        // Whitespace .NET trims but Postgres's locale-dependent \s may not, and a non-ASCII name.
        "\u00A0Fafnir\u00A0", "\u2003Tiamat\u3000", "Foo\u00A0/\u00A0Bar", "\u00A0", "\u00A0/\u2009", "\u00C9l\u00E9phant",
    };

    private readonly PostgresFixture _pg;

    public TodDerivedColumnsParityTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task DurationMinutesSql_MatchesStoredMinutes_ForEveryInput()
    {
        await using var c = await OpenAsync();
        var mismatches = new List<string>();
        foreach (var text in Durations)
        {
            var (cooldown, interval, _) = await DeriveAsync(c, text);
            var expectedCooldown = TodDurationFormat.StoredMinutes(text, TodDurationFormat.HoursUnit);
            var expectedInterval = TodDurationFormat.StoredMinutes(text, TodDurationFormat.MinutesUnit);
            if (cooldown != expectedCooldown || interval != expectedInterval)
            {
                mismatches.Add($"{Show(text)}: SQL ({cooldown}, {interval}) vs C# ({expectedCooldown}, {expectedInterval})");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    // Free-form cooldowns are accepted, so a typed "1.025" is possible. Of every thousandth of an
    // hour up to 200, this takes each one whose double times 60 rounds differently from the exact
    // decimal (the case the SQL once got wrong) plus every 97th as a sample, bare, as hours and plus
    // half a minute, in one query: the SQL must round the same double the C# rounds.
    [Fact]
    public async Task DurationMinutesSql_MatchesStoredMinutes_ForThousandthsOfAnHour()
    {
        var texts = new List<string>();
        var doubleDiffers = 0;
        for (var i = 1; i <= 200_000; i++)
        {
            var exact = i / 1000m;
            var differs = Math.Round((double)exact * 60d, MidpointRounding.AwayFromZero)
                != (double)Math.Round(exact * 60m, MidpointRounding.AwayFromZero);
            if (!differs && i % 97 != 0)
            {
                continue;
            }
            doubleDiffers += differs ? 1 : 0;
            var number = exact.ToString(System.Globalization.CultureInfo.InvariantCulture);
            texts.Add(number);
            texts.Add(number + " Hour");
            texts.Add(number + " h 0.5 m");
        }
        Assert.True(doubleDiffers > 10, "the sweep holds values where the double and the decimal round apart");

        await using var c = await OpenAsync();
        await using var command = c.CreateCommand();
        command.CommandText =
            $"SELECT q.v, {TodDerivedSql.DurationMinutesSql("q.v", 60)}, " +
            $"{TodDerivedSql.DurationMinutesSql("q.v", 1)} " +
            "FROM unnest(@v::text[]) AS q(v)";
        command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = texts.ToArray() });
        await using var reader = await command.ExecuteReaderAsync();
        var mismatches = new List<string>();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            var text = reader.GetString(0);
            int? cooldown = reader.IsDBNull(1) ? null : reader.GetInt32(1);
            int? interval = reader.IsDBNull(2) ? null : reader.GetInt32(2);
            var expectedCooldown = TodDurationFormat.StoredMinutes(text, TodDurationFormat.HoursUnit);
            var expectedInterval = TodDurationFormat.StoredMinutes(text, TodDurationFormat.MinutesUnit);
            if (cooldown != expectedCooldown || interval != expectedInterval)
            {
                mismatches.Add($"{Show(text)}: SQL ({cooldown}, {interval}) vs C# ({expectedCooldown}, {expectedInterval})");
            }
        }
        Assert.Equal(texts.Count, rows);
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches.Take(20)));
    }

    // The production values come out as the minutes the ToD board shows.
    [Theory]
    [InlineData("22 Hour", 1320)]
    [InlineData("48 Hour", 2880)]
    [InlineData("72 Hour", 4320)]
    [InlineData("84 Hour", 5040)]
    public async Task ProductionCooldowns_FillTheirMinutes(string text, int minutes)
    {
        await using var c = await OpenAsync();
        Assert.Equal(minutes, (await DeriveAsync(c, text)).Cooldown);
    }

    [Theory]
    [InlineData("10 Min", 10)]
    [InlineData("1 Hour", 60)]
    [InlineData("6 Hour", 360)]
    public async Task ProductionIntervals_FillTheirMinutes(string text, int minutes)
    {
        await using var c = await OpenAsync();
        Assert.Equal(minutes, (await DeriveAsync(c, text)).Interval);
    }

    [Fact]
    public async Task SpawnKeySql_MatchesSpawnKey_ForEveryInput()
    {
        await using var c = await OpenAsync();
        var mismatches = new List<string>();
        foreach (var name in MonsterNames)
        {
            var key = (await DeriveAsync(c, name)).SpawnKey;
            var expected = HnmConfig.SpawnKey(name);
            if (key != expected)
            {
                mismatches.Add($"{Show(name)}: SQL {Show(key)} vs C# {Show(expected)}");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join("\n", mismatches));
    }

    // Runs the three SQL expressions on one value, the way a fill runs them on a column.
    private static async Task<(int? Cooldown, int? Interval, string? SpawnKey)> DeriveAsync(NpgsqlConnection c, string? value)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            $"SELECT {TodDerivedSql.DurationMinutesSql("q.v", 60)}, " +
            $"{TodDerivedSql.DurationMinutesSql("q.v", 1)}, " +
            $"{TodDerivedSql.SpawnKeySql("q.v")} " +
            "FROM (SELECT @v::text AS v) q";
        command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (
            reader.IsDBNull(0) ? null : reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static string Show(string? value) =>
        value is null ? "NULL" : "\"" + value.Replace("\t", "\\t").Replace("\n", "\\n") + "\"";

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_pg.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
