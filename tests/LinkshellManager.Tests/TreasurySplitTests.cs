using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

public class TreasurySplitTests
{
    private static IReadOnlyList<TreasuryRecipient> Members(params string[] names) =>
        names.Select(name => new TreasuryRecipient($"user-{name}", name)).ToList();

    [Fact]
    public void Allocate_DividesEvenlyWhenItDividesEvenly()
    {
        var shares = TreasurySplit.Allocate(900_000, Members("Ashira", "Millhouse", "Zeid"));

        Assert.Equal(3, shares.Count);
        Assert.All(shares, share => Assert.Equal(300_000, share.Share));
    }

    // 1,000,000 across three cannot be even. The leftover gil goes to the first members by name
    // rather than vanishing, because the shares have to add back up to what left the treasury.
    [Fact]
    public void Allocate_GivesTheLeftoverToTheFirstMembersByName()
    {
        var shares = TreasurySplit.Allocate(1_000_000, Members("Zeid", "Ashira", "Millhouse"));

        Assert.Equal(
            new[] { ("Ashira", 333_334L), ("Millhouse", 333_333L), ("Zeid", 333_333L) },
            shares.Select(share => (share.Recipient.CharacterName, share.Share)).ToArray());
    }

    // The property the whole class exists for. If the shares ever failed to add up to the total, the
    // entry's halves would not sum to zero and INV-2 would fire on save.
    [Fact]
    public void Allocate_AlwaysAddsUpToExactlyTheTotal()
    {
        var random = new Random(20260730);

        for (var iteration = 0; iteration < 5_000; iteration++)
        {
            var total = (long)random.Next(0, 50_000_000);
            var count = random.Next(1, 41);
            var members = Members(Enumerable.Range(0, count).Select(i => $"Member{i:D3}").ToArray());

            var shares = TreasurySplit.Allocate(total, members);

            Assert.Equal(count, shares.Count);
            Assert.Equal(total, shares.Sum(share => share.Share));
            // "Evenly" means nobody is off by more than the one gil that could not be divided.
            Assert.True(shares.Max(s => s.Share) - shares.Min(s => s.Share) <= 1);
        }
    }

    // Fixing a payout rebuilds it from a fresh request. If the order the members arrived in changed
    // who got the extra gil, a Fix that changed nothing would still look like it changed something.
    [Fact]
    public void Allocate_IgnoresTheOrderTheMembersArrivedIn()
    {
        var forwards = TreasurySplit.Allocate(1_000_000, Members("Ashira", "Millhouse", "Zeid"));
        var backwards = TreasurySplit.Allocate(1_000_000, Members("Zeid", "Millhouse", "Ashira"));

        Assert.Equal(
            forwards.Select(s => (s.Recipient.CharacterName, s.Share)),
            backwards.Select(s => (s.Recipient.CharacterName, s.Share)));
    }

    [Fact]
    public void Allocate_SortsByNameWithoutCaringAboutCase()
    {
        var shares = TreasurySplit.Allocate(3, Members("zeid", "Ashira", "millhouse"));

        Assert.Equal(
            new[] { "Ashira", "millhouse", "zeid" },
            shares.Select(share => share.Recipient.CharacterName).ToArray());
    }

    [Fact]
    public void Allocate_HandlesOnePersonAndNobody()
    {
        Assert.Equal(1_000_000, Assert.Single(TreasurySplit.Allocate(1_000_000, Members("Ashira"))).Share);
        Assert.Empty(TreasurySplit.Allocate(1_000_000, Array.Empty<TreasuryRecipient>()));
    }

    // Direction is carried by the categories a kind names, never by the sign of an amount — the same
    // rule the writer follows.
    [Fact]
    public void Allocate_TreatsTheTotalAsAMagnitude()
    {
        var shares = TreasurySplit.Allocate(-900_000, Members("Ashira", "Millhouse", "Zeid"));

        Assert.All(shares, share => Assert.Equal(300_000, share.Share));
    }

    // Fewer gil than members is a real thing to ask for and must not throw here; the surfaces reject
    // it with a plain-English message before it reaches the writer.
    [Fact]
    public void Allocate_StillAddsUpWhenThereIsNotEnoughToGoAround()
    {
        var shares = TreasurySplit.Allocate(2, Members("Ashira", "Millhouse", "Zeid"));

        Assert.Equal(2, shares.Sum(share => share.Share));
        Assert.Equal(new[] { 1L, 1L, 0L }, shares.Select(share => share.Share).ToArray());
    }
}
