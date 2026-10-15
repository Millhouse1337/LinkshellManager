using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The auction card is a text embed: a status line, one line per item with its bid, the item's
// type / notes beneath it, and a Bid button per item.
public class AuctionBoardMessageTests
{
    // Mirrors DiscordBotClient.JsonOptions so assertions see the on-the-wire JSON.
    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly DateTime Now = new(2026, 10, 15, 20, 0, 0, DateTimeKind.Utc);

    private static Auction LiveAuction(params AuctionItem[] items) => new()
    {
        Id = 5,
        AuctionTitle = "Sky drops",
        CreatedBy = "Millh",
        StartedAt = Now.AddHours(-1),
        StartTime = Now.AddHours(-1),
        EndTime = Now.AddHours(1),
        AuctionItems = items.ToList(),
    };

    private static JsonElement Render(object payload)
        => JsonDocument.Parse(JsonSerializer.Serialize(payload, Wire)).RootElement.Clone();

    private static string Description(object payload)
        => Render(payload).GetProperty("embeds")[0].GetProperty("description").GetString() ?? string.Empty;

    [Fact]
    public void LiveCard_ShowsStatusCountAndEachItemsBid()
    {
        var auction = LiveAuction(
            new AuctionItem { Id = 1, ItemName = "Ridill", CurrentHighestBid = 120, CurrentHighestBidder = "Alpha" },
            new AuctionItem { Id = 2, ItemName = "Kraken Club", StartingBidDkp = 50 },
            new AuctionItem { Id = 3, ItemName = "Haubergeon" });

        var text = Description(AuctionBoardMessageBuilder.BuildLivePayload(auction, null, Now));

        Assert.StartsWith("🟢 **Live** · 3 items", text);
        Assert.Contains("• **Ridill** — **120 DKP** · leading Alpha", text);
        Assert.Contains("• **Kraken Club** — starts at 50 DKP · no bids yet", text);
        Assert.Contains("• **Haubergeon** — no bids yet", text);
    }

    [Theory]
    [InlineData(-2, null, "Queued")]  // starts in 2h, not started by hand
    [InlineData(1, 1, "Live")]
    [InlineData(3, -1, "Ended")]      // the window has elapsed but nobody has closed it yet
    public void Status_FollowsTheAuctionsTimes(int startedHoursAgo, int? endsInHours, string expected)
    {
        var auction = new Auction
        {
            StartTime = Now.AddHours(-startedHoursAgo),
            EndTime = endsInHours is { } h ? Now.AddHours(h) : null,
        };

        Assert.Equal(expected, AuctionBoardMessageBuilder.Status(auction, Now));
    }

    [Fact]
    public void ItemTypeAndNotes_SitUnderTheItem()
    {
        var auction = LiveAuction(
            new AuctionItem { Id = 1, ItemName = "Ridill", ItemType = "Weapon", Notes = "From Ouryu" });

        var text = Description(AuctionBoardMessageBuilder.BuildLivePayload(auction, null, Now));

        Assert.Contains("• **Ridill** — no bids yet\n↳ _Weapon · From Ouryu_", text);
    }

    [Fact]
    public void ABigAuction_SaysHowManyItemsDidNotFit()
    {
        var items = Enumerable.Range(1, 60)
            .Select(i => new AuctionItem { Id = i, ItemName = $"Item {i}", Notes = new string('n', 100) })
            .ToArray();

        var payload = AuctionBoardMessageBuilder.BuildLivePayload(LiveAuction(items), null, Now);
        var text = Description(payload);

        Assert.True(text.Length <= 4096, $"description is {text.Length} chars");
        Assert.Matches(@"_…and \d+ more — see the app\._", text);
        Assert.Contains("• **Item 1** —", text);
        Assert.DoesNotContain("• **Item 60** —", text);
    }

    [Fact]
    public void MoreThanTwentyItems_SaysTheRestAreBidInTheApp()
    {
        var items = Enumerable.Range(1, 22)
            .Select(i => new AuctionItem { Id = i, ItemName = $"Item {i}" })
            .ToArray();

        var payload = Render(AuctionBoardMessageBuilder.BuildLivePayload(LiveAuction(items), "https://lsm.example/Auction", Now));

        Assert.Contains("have no button here — bid on them in the app", payload.GetProperty("embeds")[0].GetProperty("description").GetString());
        var rows = payload.GetProperty("components").EnumerateArray().ToList();
        Assert.Equal(5, rows.Count); // 4 rows of 5 Bid buttons + the app link row
        Assert.Equal(AuctionBoardMessageBuilder.MaxBidButtons,
            rows.Take(4).Sum(row => row.GetProperty("components").GetArrayLength()));
    }

    // A card first posted when it carried a rendered PNG keeps that file unless the edit says
    // otherwise, so every payload clears attachments explicitly.
    [Fact]
    public void EveryPayload_ClearsAttachments()
    {
        var live = Render(AuctionBoardMessageBuilder.BuildLivePayload(
            LiveAuction(new AuctionItem { Id = 1, ItemName = "Ridill" }), null, Now));
        var closed = Render(AuctionBoardMessageBuilder.BuildClosedPayload(
            new AuctionHistory { Id = 9, AuctionTitle = "Sky drops" }, null));

        Assert.Equal(0, live.GetProperty("attachments").GetArrayLength());
        Assert.Equal(0, closed.GetProperty("attachments").GetArrayLength());
        Assert.DoesNotContain("attachment://", live.ToString());
    }

    [Fact]
    public void ResultsCard_ShowsWinnersAndHowManySold()
    {
        var history = new AuctionHistory
        {
            Id = 9,
            AuctionTitle = "Sky drops",
            CreatedBy = "Millh",
            AuctionItems = new List<AuctionItem>
            {
                new() { Id = 1, ItemName = "Ridill", ItemType = "Weapon", CurrentHighestBidder = "Alpha", EndingBidDkp = 120 },
                new() { Id = 2, ItemName = "Haubergeon" },
            },
        };

        var payload = Render(AuctionBoardMessageBuilder.BuildClosedPayload(history, null));
        var text = payload.GetProperty("embeds")[0].GetProperty("description").GetString() ?? string.Empty;

        Assert.StartsWith("**2 items** · 1 sold", text);
        Assert.Contains("• **Ridill** — won by Alpha for **120 DKP**\n↳ _Weapon_", text);
        Assert.Contains("• **Haubergeon** — _no bids_", text);
        Assert.Contains("Created by", payload.GetProperty("embeds")[0].GetProperty("fields").ToString());
    }
}
