using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// The board's officer "Add Member" find: a typed name is looked up among the people already on
// the roster, and nothing else. It used to create a member whenever the name missed by so much as
// an emoji, which is how "Comte🧀" (self-registered) and "Comte" (typed by an officer) both ended
// up on one roster. These pin that the find reaches the person the officer meant.
public class RosterMemberSearchTests
{
    private static RosterNameCandidate Member(string id, string roster, string? alt1 = null, string? alt2 = null)
        => new(roster, roster, alt1, alt2, id);

    private static readonly RosterNameCandidate[] Roster =
    {
        Member("u-comte", "Comte🧀"),
        Member("u-sinvera", "Sinvera", "Sinabun", "Sinthra"),
        Member("u-mill", "Millhouse"),
        Member("u-milltwo", "Milltwo"),
        Member("u-rhyen", "Rhyen", "Tatemcrae"),
        Member("u-tate", "Tate"),
    };

    private static string[] Ids(IEnumerable<RosterNameCandidate> found) => found.Select(m => m.AppUserId!).ToArray();

    [Theory]
    [InlineData("Millhouse")]
    [InlineData("millhouse")]
    [InlineData("  MILLHOUSE  ")]
    public void AnExactName_FindsThatMember_AnyCapitals(string typed)
    {
        Assert.Equal(new[] { "u-mill" }, Ids(RosterMemberSearch.Find(Roster, typed)));
    }

    [Fact]
    public void AnExactName_BeatsMembersWhoseNamesOnlyContainIt()
    {
        // "Tate" is inside Rhyen's alt Tatemcrae too, but "Tate" typed in full means Tate.
        Assert.Equal(new[] { "u-tate" }, Ids(RosterMemberSearch.Find(Roster, "Tate")));
    }

    [Theory]
    [InlineData("Sinabun")]
    [InlineData("sinthra")]
    public void AnAltName_FindsTheMemberWhoOwnsIt(string typed)
    {
        Assert.Equal(new[] { "u-sinvera" }, Ids(RosterMemberSearch.Find(Roster, typed)));
    }

    [Fact]
    public void PartOfAName_FindsAMessyEntry()
    {
        // The exact case that made a duplicate: the officer typed the clean name of someone whose
        // roster entry carried an emoji.
        Assert.Equal(new[] { "u-comte" }, Ids(RosterMemberSearch.Find(Roster, "comte")));
    }

    [Fact]
    public void PartOfAName_ReturnsEveryMemberItCouldMean()
    {
        Assert.Equal(new[] { "u-mill", "u-milltwo" }, Ids(RosterMemberSearch.Find(Roster, "mill")).OrderBy(id => id));
    }

    [Theory]
    [InlineData("Nobodyhere")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoMatch_OrNothingTyped_FindsNobody(string? typed)
    {
        Assert.Empty(RosterMemberSearch.Find(Roster, typed));
    }

    [Fact]
    public void BlankNamesOnAMember_NeverMatch()
    {
        var roster = new[] { new RosterNameCandidate(null, "  ", null, "", "u-blank") };

        Assert.Empty(RosterMemberSearch.Find(roster, " "));
        Assert.Empty(RosterMemberSearch.Find(roster, "a"));
    }
}
