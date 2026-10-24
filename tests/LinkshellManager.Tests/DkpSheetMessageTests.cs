using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The DKP sheet post: the totals, then the member table split into pages (one embed each), with
// the .xlsx attached to the first message. Discord rejects a message whose embeds add up to more
// than 6000 characters (about 95 members), so a big roster runs on into more messages.
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

    private static List<JsonElement> RenderAll(DkpSheetData data)
        => DkpSheetMessageBuilder.BuildPayloads(data, Xlsx)
            .Select(payload => JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire)).RootElement.Clone())
            .ToList();

    // The first message: the whole post, for a roster that fits in one.
    private static JsonElement Render(DkpSheetData data) => RenderAll(data)[0];

    private static List<JsonElement> Attachments(JsonElement payload)
        => payload.TryGetProperty("attachments", out var list) ? list.EnumerateArray().ToList() : new List<JsonElement>();

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

    // A roster past one message runs on into more, every member shown once, each message inside
    // Discord's limits. The spreadsheet rides on the first; the titles number the messages; the
    // legend and "Updated" footer close the last.
    [Fact]
    public void ABigRoster_RunsOnIntoMoreMessages_WithEveryMemberShown()
    {
        var payloads = RenderAll(Sheet(200));

        Assert.InRange(payloads.Count, 2, 4);
        foreach (var payload in payloads)
        {
            var embeds = Embeds(payload);
            Assert.True(TotalChars(payload) <= 6000, $"embeds total {TotalChars(payload)} chars");
            Assert.InRange(embeds.Count, 1, 10);
            Assert.All(embeds, embed => Assert.True(Text(embed, "description").Length <= 4096));
        }

        var all = string.Join("\n", payloads.SelectMany(Embeds).Select(embed => Text(embed, "description")));
        var shown = Regex.Matches(all, @"Member\d{3}").Select(match => match.Value).ToList();
        Assert.Equal(200, shown.Count);
        Assert.Equal(200, shown.Distinct().Count());
        Assert.DoesNotContain("more —", all);

        Assert.Equal(Xlsx, Assert.Single(Attachments(payloads[0])).GetProperty("filename").GetString());
        Assert.All(payloads.Skip(1), payload => Assert.Empty(Attachments(payload)));

        Assert.Equal($"📊 Sky — DKP (1/{payloads.Count})", Text(Embeds(payloads[0])[0], "title"));
        Assert.Equal($"📊 Sky — DKP (2/{payloads.Count})", Text(Embeds(payloads[1])[0], "title"));

        var lastEmbed = Embeds(payloads[^1])[^1];
        Assert.EndsWith(DkpSheetMessageBuilder.Legend, Text(lastEmbed, "description"));
        Assert.True(lastEmbed.TryGetProperty("footer", out _));
        Assert.Single(payloads.SelectMany(Embeds), embed => embed.TryGetProperty("footer", out _));
    }

    // Past MaxMessages the post stops and points at the spreadsheet rather than flooding the channel.
    [Fact]
    public void AHugeRoster_StopsAtTheMessageCap_AndPointsAtTheSpreadsheet()
    {
        var payloads = RenderAll(Sheet(3000));

        Assert.Equal(DkpSheetMessageBuilder.MaxMessages, payloads.Count);
        var all = string.Join("\n", payloads.SelectMany(Embeds).Select(embed => Text(embed, "description")));
        var shown = Regex.Matches(all, @"Member\d{3,4}").Count;
        Assert.InRange(shown, 1000, 2999);
        Assert.Contains($"_…and {3000 - shown} more — full list in the attached spreadsheet._",
            Text(Embeds(payloads[^1])[^1], "description"));
    }

    [Fact]
    public void ASmallRoster_FitsOnOnePage_WithTheLegend()
    {
        Assert.Single(RenderAll(Sheet(3)));
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
