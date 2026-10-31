using System;
using System.Text.Json;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The Discord board for an event with NO party setup (DiscordEventMessageBuilder.Build's ad-hoc
// roster): a "Sign up" button that starts the character / role / main / sub wizard, no
// "DKP / hour" on an HNM camp, and a roster that shows what each person is coming as.
public class DiscordAdHocBoardTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 21, 53, 14, DateTimeKind.Utc);

    private static Event Camp(string type, int? dkpPerHour) => new()
    {
        Id = 42,
        EventName = "caerb",
        EventType = type,
        EventLocation = "Mount Zhayolm",
        StartTime = Start,
        DkpPerHour = dkpPerHour,
        CreationSource = "App",
    };

    private static string Board(Event ev, params EventSignupLine[] signups) =>
        JsonSerializer.Serialize(DiscordEventMessageBuilder.Build(ev, signups));

    // One button opens the same wizard a party board's "Sign Up (No Slot)" runs -- the old single
    // "Pick your job" select is gone.
    [Fact]
    public void Board_OffersASignUpButton_InsteadOfAJobSelect()
    {
        var json = Board(Camp("HNM", 0));

        Assert.Contains("\"label\":\"Sign up\"", json);
        Assert.Contains($"{DiscordEventMessageBuilder.PartyJoinEventPrefix}42", json);
        Assert.Contains($"{DiscordEventMessageBuilder.WithdrawPrefix}42", json);
        Assert.DoesNotContain(DiscordEventMessageBuilder.JobSelectPrefix, json);
    }

    // An HNM camp pays by window, never by the hour.
    [Fact]
    public void HnmCamp_HasNoDkpPerHourField()
    {
        Assert.DoesNotContain("DKP / hour", Board(Camp("HNM", 0)));
    }

    // ...but an event that really is paid by the hour still says so.
    [Fact]
    public void TimedEvent_KeepsItsDkpPerHourField()
    {
        Assert.Contains("DKP / hour", Board(Camp("Dynamis", 2)));
    }

    // The wizard records the sub job and role, so the roster shows them.
    [Fact]
    public void Roster_ShowsMainSubAndRole()
    {
        var json = Board(Camp("HNM", null),
            new EventSignupLine("Millhouse", "PLD", "WAR", "Tank"),
            new EventSignupLine("Zexamir", "BLM"));

        Assert.Contains("PLD/WAR \\u00B7 Tank", json);
        Assert.Contains("**Zexamir** \\u2014 BLM", json);
    }
}
