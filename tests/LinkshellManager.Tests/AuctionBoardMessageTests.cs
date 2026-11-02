using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The auction card is a text embed: the details (status, times, host) and then one numbered line
// per item with its bid and its type / notes, an item picker that opens the bid modal, and
// My bids / Open in app buttons.
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

        Assert.StartsWith("🟢 **Live** · ends <t:", text);
        Assert.Contains("Host **Millh** · 3 items", text);
        Assert.Contains("`1` **Ridill** · 120 DKP, Alpha", text);
        Assert.Contains("`2` **Kraken Club** · opens 50 DKP", text);
        Assert.Contains("`3` **Haubergeon** · no bids yet", text);
    }

    // The items sit UNDER the details, which is why both live in the description: Discord draws
    // fields after it, so Starts / Ends as fields landed below the list.
    [Fact]
    public void Items_SitUnderTheDetails()
    {
        var payload = Render(AuctionBoardMessageBuilder.BuildLivePayload(
            LiveAuction(new AuctionItem { Id = 1, ItemName = "Ridill" }), null, Now));
        var text = payload.GetProperty("embeds")[0].GetProperty("description").GetString()!;

        Assert.True(text.IndexOf("Starts <t:", StringComparison.Ordinal) < text.IndexOf("`1`", StringComparison.Ordinal));
        Assert.True(text.IndexOf("Host **Millh**", StringComparison.Ordinal) < text.IndexOf("`1`", StringComparison.Ordinal));
        Assert.False(payload.GetProperty("embeds")[0].TryGetProperty("fields", out _));
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
    public void ItemTypeAndNotes_SitOnTheItemsLine()
    {
        var auction = LiveAuction(
            new AuctionItem { Id = 1, ItemName = "Ridill", ItemType = "Weapon", Notes = "From Ouryu" });

        var text = Description(AuctionBoardMessageBuilder.BuildLivePayload(auction, null, Now));

        Assert.Contains("`1` **Ridill** · no bids yet · _Weapon · From Ouryu_", text);
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
        Assert.Contains("`1` **Item 1** ·", text);
        Assert.DoesNotContain("`60` **Item 60**", text);
    }

    // One picker instead of a button per item, then My bids and Open in app.
    [Fact]
    public void LiveCard_HasAnItemPickerAndMyBids()
    {
        var auction = LiveAuction(
            new AuctionItem { Id = 11, ItemName = "Ridill", CurrentHighestBid = 120, CurrentHighestBidder = "Alpha" },
            new AuctionItem { Id = 12, ItemName = "Kraken Club", StartingBidDkp = 50 });

        var rows = Render(AuctionBoardMessageBuilder.BuildLivePayload(auction, "https://lsm.example/Auction", Now))
            .GetProperty("components").EnumerateArray().ToList();

        Assert.Equal(2, rows.Count);
        var select = rows[0].GetProperty("components")[0];
        Assert.Equal(3, select.GetProperty("type").GetInt32());
        Assert.Equal("auc:pick:5", select.GetProperty("custom_id").GetString());
        var options = select.GetProperty("options").EnumerateArray().ToList();
        Assert.Equal("1. Ridill", options[0].GetProperty("label").GetString());
        Assert.Equal("11", options[0].GetProperty("value").GetString());
        Assert.Equal("120 DKP, Alpha", options[0].GetProperty("description").GetString());
        Assert.Equal("opens 50 DKP", options[1].GetProperty("description").GetString());

        var buttons = rows[1].GetProperty("components").EnumerateArray().ToList();
        Assert.Equal("My bids", buttons[0].GetProperty("label").GetString());
        Assert.Equal("auc:mybids:5", buttons[0].GetProperty("custom_id").GetString());
        Assert.Equal("Open in app", buttons[1].GetProperty("label").GetString());
    }

    [Fact]
    public void MoreThan25Items_SaysTheRestAreBidInTheApp()
    {
        var items = Enumerable.Range(1, 27)
            .Select(i => new AuctionItem { Id = i, ItemName = $"Item {i}" })
            .ToArray();

        var payload = Render(AuctionBoardMessageBuilder.BuildLivePayload(LiveAuction(items), null, Now));

        Assert.Contains("aren't in the picker — bid on them in the app", payload.GetProperty("embeds")[0].GetProperty("description").GetString());
        var select = payload.GetProperty("components")[0].GetProperty("components")[0];
        Assert.Equal(AuctionBoardMessageBuilder.MaxPickerItems, select.GetProperty("options").GetArrayLength());
    }

    [Fact]
    public void AnEndedAuction_HasNoPicker()
    {
        var auction = LiveAuction(new AuctionItem { Id = 1, ItemName = "Ridill" });
        auction.EndTime = Now.AddMinutes(-5);

        var rows = Render(AuctionBoardMessageBuilder.BuildLivePayload(auction, null, Now))
            .GetProperty("components").EnumerateArray().ToList();

        Assert.Single(rows);
        Assert.Equal("My bids", rows[0].GetProperty("components")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void MyBids_ListsOnlyTheMembersBidsNumberedLikeTheCard()
    {
        var items = new List<AuctionItem>
        {
            new() { Id = 1, ItemName = "Ridill", CurrentHighestBid = 120, CurrentHighestBidderAppUserId = "me",
                Bids = { new Bid { AppUserId = "me", BidAmount = 100 }, new Bid { AppUserId = "me", BidAmount = 120 } } },
            new() { Id = 2, ItemName = "Kraken Club" },
            new() { Id = 3, ItemName = "Haubergeon", CurrentHighestBid = 60, CurrentHighestBidderAppUserId = "other",
                Bids = { new Bid { AppUserId = "me", BidAmount = 40 } } },
        };

        var text = AuctionBoardMessageBuilder.BuildMyBidsText(items, "me");

        Assert.Contains("`1` **Ridill** · your bid 120 DKP · ✅ leading", text);
        Assert.Contains("`3` **Haubergeon** · your bid 40 DKP · outbid — 60 DKP now", text);
        Assert.DoesNotContain("Kraken Club", text);
        Assert.Contains("haven't bid", AuctionBoardMessageBuilder.BuildMyBidsText(items, "nobody"));
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
