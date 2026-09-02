using System;
using System.Collections.Generic;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The Claim Shield block at the foot of the event board.
//
// It is not decoration: both finalizers gate the claim bonus on being in this list, so the board is
// now showing part of the payout. The two things it has to get right are therefore the two things
// tested hardest here — who is named, and the bar, which is a claim about how much of the lottery
// was us.
public class ClaimShieldBoardSectionTests
{
    private static readonly DateTime When = new(2026, 8, 30, 20, 14, 0, DateTimeKind.Utc);

    private static ClaimShieldBoardCapture Capture(
        bool won = true, int total = 20, params (string Name, bool Matched)[] members)
        => new("Nidhogg", won, total, When,
            Array.ConvertAll(members, m => new ClaimShieldBoardMember(m.Name, m.Matched)));

    [Fact]
    public void NoCaptures_RendersNothing()
    {
        Assert.Null(ClaimShieldBoardSection.Build(null));
        Assert.Null(ClaimShieldBoardSection.Build(Array.Empty<ClaimShieldBoardCapture>()));
    }

    [Fact]
    public void AWonLottery_NamesWhoTaggedIt()
    {
        var text = ClaimShieldBoardSection.Build(new[]
        {
            Capture(members: new[] { ("Zexamir", true), ("Millhouse", true) }),
        })!;

        Assert.Contains("Claimed", text);
        Assert.Contains("Zexamir", text);
        Assert.Contains("Millhouse", text);
        Assert.Contains("**2** of **20** claimers were ours (10%)", text);
    }

    [Fact]
    public void ALostLotteryStillRenders()
        => Assert.Contains(
            "Lost",
            ClaimShieldBoardSection.Build(new[] { Capture(won: false, members: new[] { ("Alez", true) }) })!);

    // THE number people act on. Someone who acted but isn't on the roster earns nothing, and the
    // only place anyone would notice is here — so it is stated, not quietly dropped.
    [Fact]
    public void SomeoneNotOnTheRoster_IsNamedAsUnpaid()
    {
        var text = ClaimShieldBoardSection.Build(new[]
        {
            Capture(members: new[] { ("Zexamir", true), ("Nobody", false) }),
        })!;

        Assert.Contains("Not on the roster", text);
        Assert.Contains("Nobody", text);
    }

    // The tagged count is per MEMBER across every lottery, not the sum of the lotteries. A contested
    // pop produces several and one person can tag in all of them; the bonus is paid once.
    [Fact]
    public void TheHeaderCountsPeople_NotTags()
    {
        var text = ClaimShieldBoardSection.Build(new[]
        {
            Capture(members: new[] { ("Zexamir", true), ("Alez", true) }),
            Capture(won: false, members: new[] { ("Zexamir", true) }),
        })!;

        Assert.Contains("**2** members will earn the claim bonus", text);
    }

    [Fact]
    public void SeveralLotteries_AreCounted()
        => Assert.Contains(
            "2 lotteries",
            ClaimShieldBoardSection.Build(new[]
            {
                Capture(members: new[] { ("Zexamir", true) }),
                Capture(won: false, members: new[] { ("Alez", true) }),
            })!);

    // --------------------------------------------------------------------------- the bar ---

    private static string BarOf(string text)
    {
        var start = text.IndexOf('`');
        var end = text.IndexOf('`', start + 1);
        return text[(start + 1)..end];
    }

    [Fact]
    public void TheBarIsProportional()
    {
        var text = ClaimShieldBoardSection.Build(new[]
        {
            Capture(total: 20, members: new[]
            {
                ("A", true), ("B", true), ("C", true), ("D", true), ("E", true),
            }),
        })!;

        var bar = BarOf(text);
        Assert.Equal(20, bar.Length);
        Assert.Equal(5, bar.Split('█').Length - 1);   // 5 of 20 = a quarter
    }

    // "Some" and "none" are the two answers this bar exists to tell apart, and one tagger in a crowd
    // of forty rounds to zero cells.
    [Fact]
    public void OneTaggerInACrowd_StillShowsACell()
    {
        var bar = BarOf(ClaimShieldBoardSection.Build(new[]
        {
            Capture(total: 40, members: new[] { ("Zexamir", true) }),
        })!);

        Assert.Equal(1, bar.Split('█').Length - 1);
    }

    // TotalPlayers arrives as 0 from a capture the parser could not total. A full bar there would be
    // the page claiming we were the entire lottery — a statement about other linkshells, made by a
    // missing field.
    [Fact]
    public void AnUnknownTotal_DrawsAnEmptyBar_AndSaysLess()
    {
        var text = ClaimShieldBoardSection.Build(new[]
        {
            Capture(total: 0, members: new[] { ("Zexamir", true), ("Alez", true) }),
        })!;

        Assert.DoesNotContain('█', BarOf(text));
        Assert.Contains("**2** of ours landed an action", text);
        Assert.DoesNotContain(" of **0** ", text);
    }

    [Fact]
    public void NobodyTagged_SaysSo()
    {
        var text = ClaimShieldBoardSection.Build(new[] { Capture(won: false, total: 12) })!;

        Assert.Contains("Nobody landed an action", text);
        Assert.DoesNotContain('█', BarOf(text));
    }

    // The bar can never overflow its own width, whatever the addon reported. A capture claiming more
    // of ours than there were players in the lottery is bad data, not a reason to emit a 30-cell bar
    // that wraps on a phone.
    [Fact]
    public void MoreOfOursThanPlayers_DoesNotOverflowTheBar()
    {
        var bar = BarOf(ClaimShieldBoardSection.Build(new[]
        {
            Capture(total: 2, members: new[] { ("A", true), ("B", true), ("C", true), ("D", true) }),
        })!);

        Assert.Equal(20, bar.Length);
    }

    // --------------------------------------------------------------------------- limits ---

    // The board embed is shared with the also-attending roster, and Discord hard-caps it. A
    // contested pop can produce a dozen lotteries; the newest few are what is being talked about.
    [Fact]
    public void ManyLotteries_ShowTheNewestAndSayHowManyAreLeft()
    {
        var captures = new List<ClaimShieldBoardCapture>();
        for (var i = 0; i < 9; i++)
        {
            captures.Add(new ClaimShieldBoardCapture(
                "Nidhogg", i % 2 == 0, 20, When.AddMinutes(i),
                new[] { new ClaimShieldBoardMember($"Member{i}", true) }));
        }

        var text = ClaimShieldBoardSection.Build(captures)!;

        Assert.Contains("+5 earlier lotteries", text);
        // Newest first: the last capture recorded is the one at the top.
        Assert.Contains("Member8", text);
        Assert.DoesNotContain("Member0", text);
        Assert.True(text.Length <= 1800, $"section was {text.Length} characters");
    }

    // Character names are player-supplied and go into markdown. One containing an underscore would
    // otherwise italicise the rest of the line.
    [Fact]
    public void MarkdownInANameIsEscaped()
        => Assert.Contains(
            @"Zex\_amir",
            ClaimShieldBoardSection.Build(new[] { Capture(members: new[] { ("Zex_amir", true) }) })!);
}
