using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// WHO THE CAMP ROSTER IS ALLOWED TO CLAIM AS A MEMBER.
//
// The addon reports every character standing in the alliance, member or not, and the server decides
// which of them the linkshell actually knows. Everything that lists a capture — the Event System
// card, the Activity, the Discord embed, the addon's own post summary — asks this set, so the
// answer has to be the same four-name rule the DKP paths use to decide who gets paid. A name this
// set accepts but the ledger does not would show as credited and be paid nothing.
public class RosterNameSetTests
{
    private static RosterNameSet Roster(params RosterNameCandidate[] members)
        => RosterNameSet.From(members);

    private static RosterNameCandidate Member(
        string? membershipName, string? accountName = null, string? alt1 = null, string? alt2 = null)
        => new(membershipName, accountName, alt1, alt2);

    [Fact]
    public void RosterMainName_IsRegistered()
    {
        Assert.True(Roster(Member("Edicius")).Contains("Edicius"));
    }

    // The scan reads whatever the game shows, and FFXI names are capitalized inconsistently in
    // every surface that types one in by hand.
    [Fact]
    public void NameMatch_IgnoresCase()
    {
        Assert.True(Roster(Member("Edicius")).Contains("EDICIUS"));
    }

    // Party memory hands back a name with no padding, but a hand-typed one has whatever the officer
    // left on it.
    [Fact]
    public void NameMatch_IgnoresSurroundingWhitespace()
    {
        Assert.True(Roster(Member("Edicius")).Contains("  Edicius "));
    }

    // THE case that makes this worth sharing. A member at camp on an alt is a member — the ledger
    // credits them through the same alt index, so the roster listing must not tag them as a
    // stranger.
    [Fact]
    public void LinkedAlt_IsRegistered()
    {
        var roster = Roster(Member("Edicius", accountName: "Edicius", alt1: "Athmilk", alt2: "Millhouse"));
        Assert.True(roster.Contains("Athmilk"));
        Assert.True(roster.Contains("Millhouse"));
    }

    // A membership row created before anyone filled in a character name still carries the account's
    // name, and that is the one the player is standing as.
    [Fact]
    public void AccountCharacterName_CountsWhenTheMembershipHasNone()
    {
        Assert.True(Roster(Member(null, accountName: "Edicius")).Contains("Edicius"));
    }

    // The whole point: someone else's linkshell standing at the same camp.
    [Fact]
    public void StrangerAtTheCamp_IsNotRegistered()
    {
        Assert.False(Roster(Member("Edicius")).Contains("Randomguy"));
    }

    // An empty scan row is not a member. Answering "yes" here would walk a blank name past the one
    // check that exists to catch strangers.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsNeverRegistered(string? name)
    {
        Assert.False(Roster(Member("Edicius")).Contains(name));
    }

    // A linkshell with nobody on the roster yet answers "no" to everyone — which is different from
    // having no roster at all, below.
    [Fact]
    public void EmptyRoster_RegistersNobody()
    {
        Assert.False(Roster().Contains("Edicius"));
    }

    // THE SAFETY VALVE. A mapper called without a roster (an older caller, a surface with no
    // linkshell in hand) must tag NOBODY rather than tagging everybody — a wrong "not registered"
    // banner across a whole camp is far worse than no banner at all.
    [Fact]
    public void UnknownRoster_TreatsEveryoneAsRegistered()
    {
        Assert.False(RosterNameSet.Unknown.IsKnown);
        Assert.True(RosterNameSet.Unknown.Contains("Randomguy"));
    }

    [Fact]
    public void LoadedRoster_ReportsItselfAsKnown()
    {
        Assert.True(Roster(Member("Edicius")).IsKnown);
        Assert.True(Roster().IsKnown);
    }

    // ---- Knows: the account first, then the name ----
    //
    // The order matters and is not cosmetic: it is the order WindowEventDkpLedgerService resolves a
    // capture entry in. A camp handoff stamps the account onto the entry so credit does not depend
    // on the character name matching one of the four indexed above, and anything that reports on
    // who will be paid has to ask the same question the same way.

    private static RosterNameSet RosterWithAccount(string name, string appUserId)
        => RosterNameSet.From(new[] { new RosterNameCandidate(name, null, null, null, appUserId) });

    [Fact]
    public void Knows_AcceptsAKnownAccount_EvenWhenTheNameIsUnknown()
    {
        Assert.True(RosterWithAccount("Edicius", "user-1").Knows("Someothercharacter", "user-1"));
    }

    [Fact]
    public void Knows_AcceptsAKnownName_WhenNoAccountWasRecorded()
    {
        Assert.True(RosterWithAccount("Edicius", "user-1").Knows("Edicius", null));
    }

    // A kicked member's old capture still carries their account. It must not read as payable.
    [Fact]
    public void Knows_RejectsAnUnknownAccountAndAnUnknownName()
    {
        Assert.False(RosterWithAccount("Edicius", "user-1").Knows("Randomguy", "user-999"));
    }

    [Fact]
    public void Knows_WithNoRoster_AcceptsEverything()
    {
        Assert.True(RosterNameSet.Unknown.Knows("Randomguy", "user-999"));
        Assert.True(RosterNameSet.Unknown.Knows(null, null));
    }
}
