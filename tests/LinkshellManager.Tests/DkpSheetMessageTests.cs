using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The DKP sheet is one message: the totals, then the member table split into pages (one embed
// each), with the .xlsx attached. Discord rejects a message whose embeds add up to more than
// 6000 characters, so a big roster has to stop short and point at the spreadsheet.
public class DkpSheetMessageTests
{
    // Mirrors DiscordBotClient.JsonOptions so assertions see the on-the-wire JSON.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string Xlsx = "Sky-dkp.xlsx";

    private static DkpSheetData Sheet(int members, double current = 123.45)
    {
        var rows = Enumerable.Range(1, members)
            .Select(i => new DkpSheetMemberRow(i, $"Member{i:000}", "", "", current, 100, 500, 50))
            .ToList();
        return new DkpSheetData(1, "Sky", rows, members, 5000, 4000, 1000);
    }

    private static JsonElement Render(DkpSheetData data)
        => JsonDocument.Parse(JsonSerializer.Serialize(DkpSheetMessageBuilder.BuildPayload(data, Xlsx), Wire))
            .RootElement.Clone();

    private static List<JsonElement> Embeds(JsonElement payload)
        => payload.GetProperty("embeds").EnumerateArray().ToList();

    private static string Text(JsonElement embed, string property)
        => embed.TryGetProperty(property, out var value) ? value.GetString() ?? string.Empty : string.Empty;

    // Discord's 6000 counts titles, descriptions and footer text across every embed.
    private static int TotalChars(JsonElement payload)
        => Embeds(payload).Sum(embed =>
            Text(embed, "title").Length
            + Text(embed, "description").Length
            + (embed.TryGetProperty("footer", out var footer) ? Text(footer, "text").Length : 0));

    [Fact]
    public void ABigRoster_StaysInsideDiscordsLimits_AndPointsAtTheSpreadsheet()
    {
        var payload = Render(Sheet(200));
        var embeds = Embeds(payload);
        var all = string.Join("\n", embeds.Select(embed => Text(embed, "description")));

        Assert.True(TotalChars(payload) <= 6000, $"embeds total {TotalChars(payload)} chars");
        Assert.InRange(embeds.Count, 2, 10);
        Assert.All(embeds, embed => Assert.True(Text(embed, "description").Length <= 4096));

        var shown = Regex.Matches(all, @"Member\d{3}").Count;
        Assert.InRange(shown, 80, 199);
        Assert.Contains($"_…and {200 - shown} more — full list in the attached spreadsheet._", Text(embeds[^1], "description"));

        var attachments = payload.GetProperty("attachments").EnumerateArray().ToList();
        Assert.Equal(Xlsx, Assert.Single(attachments).GetProperty("filename").GetString());
    }

    [Fact]
    public void ASmallRoster_FitsOnOnePage_WithTheLegend()
    {
        var embeds = Embeds(Render(Sheet(3)));

        var embed = Assert.Single(embeds);
        var text = Text(embed, "description");
        Assert.StartsWith("**Members** 3", text);
        Assert.Contains("Member003", text);
        Assert.EndsWith(DkpSheetMessageBuilder.Legend, text);
        Assert.DoesNotContain("more —", text);
        Assert.Equal("📊 Sky — DKP", Text(embed, "title"));
    }

    // Columns size to their widest value, so a seven-figure balance can't shear the table.
    [Fact]
    public void BigNumbers_KeepTheColumnsAligned()
    {
        var data = Sheet(3);
        var rows = data.Members.ToList();
        rows[1] = rows[1] with { Current = 1_234_567.89 };
        var text = Text(Embeds(Render(data with { Members = rows }))[0], "description");

        var tableLines = text.Split('\n')
            .SkipWhile(line => line != "```").Skip(1)
            .TakeWhile(line => line != "```")
            .ToList();
        Assert.Equal(4, tableLines.Count); // header + 3 members
        Assert.Single(tableLines.Select(line => line.Length).Distinct());
        Assert.Contains("1,234,567.89", text);
    }

    [Fact]
    public void SeveralPools_GetTheirTotalsInTheSummary()
    {
        var data = Sheet(3) with
        {
            Pools = new[] { new DkpSheetPoolColumn(1, "Sky", "blue"), new DkpSheetPoolColumn(2, "Sea", "green") },
            PoolTotals = new[] { 3000.0, 2000.0 },
        };

        var text = Text(Embeds(Render(data))[0], "description");

        Assert.Contains("**Pools** Sky 3,000.00  ·  Sea 2,000.00", text);
    }

    [Fact]
    public void NoMembers_SaysSo()
    {
        var text = Text(Assert.Single(Embeds(Render(Sheet(0)))), "description");

        Assert.Contains("_No members._", text);
        Assert.DoesNotContain("```", text);
    }
}
