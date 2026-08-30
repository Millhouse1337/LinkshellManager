using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the ONE definition of "how much gil does this linkshell have", and the two rules that make
// every number on the treasury card correct.
//
// Five call sites used to answer the balance question independently and four disagreed. One of those
// numbers gates whether a gil auction may be created, so a wrong answer here lets a linkshell auction
// gil it does not hold.
public class TreasuryBalanceServiceTests
{
    // A transaction, as its two halves. Line 1 adds to a category, line 2 takes the same amount from
    // another — exactly what TreasuryJournalWriter builds.
    private static IEnumerable<LedgerLineRow> Entry(int addTo, int takeFrom, long amount) =>
        new[] { new LedgerLineRow(addTo, amount), new LedgerLineRow(takeFrom, -amount) };

    private static IEnumerable<LedgerLineRow> SoldAnItem(long amount) =>
        Entry(TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales, amount);

    private static IEnumerable<LedgerLineRow> PaidAMember(long amount) =>
        Entry(TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand, amount);

    // Same categories and members, opposite amounts. The member is carried through because
    // MirrorAsync copies it — a reversal has to net against its original PER MEMBER, not land in
    // some anonymous bucket.
    private static IEnumerable<LedgerLineRow> Reversed(IEnumerable<LedgerLineRow> original) =>
        original.Select(line => new LedgerLineRow(
            line.AccountNumber, -line.Amount, line.CounterpartyCharacterName));

    // One lump sum shared between several members: the whole amount on one side, one line per member
    // on the other. Built the way TreasuryJournalWriter builds it, shares and all.
    private static IEnumerable<LedgerLineRow> SplitEntry(
        TreasuryTransactionKind kind, long amount, int members)
    {
        var splitAccount = kind.SplitAccount!.Value;
        var wholeAccount = splitAccount == kind.AddTo ? kind.TakeFrom : kind.AddTo;
        var wholeSign = wholeAccount == kind.AddTo ? 1 : -1;

        var recipients = Enumerable.Range(0, members)
            .Select(i => new TreasuryRecipient($"user-{i}", $"Member{i:D2}"))
            .ToList();

        yield return new LedgerLineRow(wholeAccount, wholeSign * amount);
        foreach (var (_, share) in TreasurySplit.Allocate(amount, recipients))
        {
            yield return new LedgerLineRow(splitAccount, -wholeSign * share);
        }
    }

    // A split is ONE payout, not N. Summing per line is right; treating each member's line as its own
    // transaction would report a 1,000,000 split across eight people as 8,000,000 of gil out.
    [Fact]
    public void Project_ASplitPayoutIsOneAmountOfGilOutNotOnePerMember()
    {
        var kind = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.SplitGilAmongMembers)!;

        var snapshot = TreasuryBalanceService.Project(SplitEntry(kind, 1_000_000, 8));

        Assert.Equal(-1_000_000, snapshot.CashOnHand);
        Assert.Equal(1_000_000, snapshot.MoneyOut);
        Assert.Equal(0, snapshot.MoneyIn);
        Assert.True(snapshot.Balances);
    }

    // The owed split is the same shape with no gil moving: everyone's share sits in what we owe until
    // it is handed over one member at a time.
    [Fact]
    public void Project_AnOwedSplitRaisesWhatWeOweWithoutTouchingGilOnHand()
    {
        var kind = TreasuryTransactionKinds.Find(TreasuryTransactionKinds.WeOweSeveralMembers)!;

        var snapshot = TreasuryBalanceService.Project(SplitEntry(kind, 1_000_000, 8));

        Assert.Equal(0, snapshot.CashOnHand);
        Assert.Equal(1_000_000, snapshot.WeOwe);
        Assert.Equal(1_000_000, snapshot.MoneyOut);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_NothingRecordedIsZero()
    {
        var snapshot = TreasuryBalanceService.Project(Array.Empty<LedgerLineRow>());

        Assert.Equal(0, snapshot.CashOnHand);
        Assert.Equal(0, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.MoneyOut);
        Assert.Equal(0, snapshot.NetChange);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_ASaleAddsToGilOnHandAndToGilIn()
    {
        var snapshot = TreasuryBalanceService.Project(SoldAnItem(4_000_000));

        Assert.Equal(4_000_000, snapshot.CashOnHand);
        Assert.Equal(4_000_000, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.MoneyOut);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_APayoutTakesFromGilOnHandAndAddsToGilOut()
    {
        var snapshot = TreasuryBalanceService.Project(SoldAnItem(4_000_000).Concat(PaidAMember(800_000)));

        Assert.Equal(3_200_000, snapshot.CashOnHand);
        Assert.Equal(4_000_000, snapshot.MoneyIn);
        Assert.Equal(800_000, snapshot.MoneyOut);
        Assert.Equal(3_200_000, snapshot.NetChange);
        Assert.True(snapshot.Balances);
    }

    // THE trap. Reversing a sale puts a POSITIVE amount on a gil-in category. Keying gross totals on
    // the category's normal side makes that REDUCE gil in, which is right. Keying them on the sign of
    // the amount would instead count it as gil out — the entry would still net to zero while both
    // gross figures lied about what happened.
    [Fact]
    public void Project_ReversingASaleReducesGilInRatherThanInflatingGilOut()
    {
        var sale = SoldAnItem(4_000_000).ToList();
        var snapshot = TreasuryBalanceService.Project(sale.Concat(Reversed(sale)));

        Assert.Equal(0, snapshot.CashOnHand);
        Assert.Equal(0, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.MoneyOut);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_ReversingAPayoutReducesGilOut()
    {
        var payout = PaidAMember(800_000).ToList();
        var snapshot = TreasuryBalanceService.Project(
            SoldAnItem(4_000_000).Concat(payout).Concat(Reversed(payout)));

        Assert.Equal(4_000_000, snapshot.CashOnHand);
        Assert.Equal(4_000_000, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.MoneyOut);
    }

    // A reversal is an ordinary entry that nets against its original. Nothing filters reversed entries
    // out, and nothing may: excluding an original while keeping its reversal would read a reversed 8M
    // sale as MINUS 8M gil.
    [Fact]
    public void Project_ReversalsNetRatherThanExcluding()
    {
        var sale = SoldAnItem(8_000_000).ToList();
        var both = sale.Concat(Reversed(sale)).ToList();

        Assert.Equal(0, TreasuryBalanceService.Project(both).CashOnHand);
        // If the original were dropped and only the reversal kept — the shape of a status filter —
        // this is the number an officer would be shown.
        Assert.Equal(-8_000_000, TreasuryBalanceService.Project(Reversed(sale)).CashOnHand);
    }

    // Gil promised but not yet handed over does not touch gil on hand.
    [Fact]
    public void Project_OwingAMemberDoesNotMoveGilOnHand()
    {
        var owed = Entry(TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe, 500_000);
        var snapshot = TreasuryBalanceService.Project(SoldAnItem(1_000_000).Concat(owed));

        Assert.Equal(1_000_000, snapshot.CashOnHand);
        Assert.Equal(500_000, snapshot.WeOwe);
        Assert.Equal(500_000, snapshot.MoneyOut);
        Assert.Equal(500_000, snapshot.NetWorth);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_PayingWhatWeOwedMovesGilAndClearsTheObligation()
    {
        var snapshot = TreasuryBalanceService.Project(
            SoldAnItem(1_000_000)
                .Concat(Entry(TreasuryAccounts.GilToMembers, TreasuryAccounts.WeOwe, 500_000))
                .Concat(Entry(TreasuryAccounts.WeOwe, TreasuryAccounts.GilOnHand, 500_000)));

        Assert.Equal(500_000, snapshot.CashOnHand);
        Assert.Equal(0, snapshot.WeOwe);
        // Recorded once as gil out, when the obligation arose — not twice.
        Assert.Equal(500_000, snapshot.MoneyOut);
        Assert.True(snapshot.Balances);
    }

    [Fact]
    public void Project_WorkDoneButNotYetPaidForIsOwedToUsNotGilOnHand()
    {
        var snapshot = TreasuryBalanceService.Project(
            Entry(TreasuryAccounts.OwedToUs, TreasuryAccounts.PaidWork, 300_000));

        Assert.Equal(0, snapshot.CashOnHand);
        Assert.Equal(300_000, snapshot.OwedToUs);
        Assert.Equal(300_000, snapshot.MoneyIn);
        Assert.Equal(300_000, snapshot.NetWorth);
        Assert.True(snapshot.Balances);
    }

    // An adopting linkshell's existing gil is a starting balance, not gil it earned. Without this it
    // would have to be recorded as gil in and overstate everything the linkshell ever took in.
    [Fact]
    public void Project_StartingGilIsNotCountedAsGilComingIn()
    {
        var snapshot = TreasuryBalanceService.Project(
            Entry(TreasuryAccounts.GilOnHand, TreasuryAccounts.StartingBalance, 40_000_000));

        Assert.Equal(40_000_000, snapshot.CashOnHand);
        Assert.Equal(40_000_000, snapshot.StartingBalance);
        Assert.Equal(0, snapshot.MoneyIn);
        Assert.Equal(0, snapshot.NetChange);
        Assert.True(snapshot.Balances);
    }

    // A sale recorded at 0 gil is real: both mark-sold paths clamp a negative price to 0.
    [Fact]
    public void Project_ZeroAmountEntryChangesNothingAndStillBalances()
    {
        var snapshot = TreasuryBalanceService.Project(SoldAnItem(0));

        Assert.Equal(0, snapshot.CashOnHand);
        Assert.Equal(0, snapshot.MoneyIn);
        Assert.True(snapshot.Balances);
    }

    // The identity behind the "Adds up" chip:
    //     what we hold - what we owe == what we started with + (gil in - gil out)
    // It follows from every entry's halves summing to zero, but it is not circular — the two sides are
    // built from independent per-category sums, so one unbalanced entry anywhere makes it false.
    [Fact]
    public void Project_BooksAddUpAcrossEveryKindOfEntry()
    {
        var kinds = TreasuryTransactionKinds.All;
        var random = new Random(20260730);

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var lines = new List<LedgerLineRow>();
            var recorded = new List<List<LedgerLineRow>>();

            for (var n = random.Next(0, 20); n > 0; n--)
            {
                var kind = kinds[random.Next(kinds.Count)];
                var amount = random.Next(0, 5_000_000);
                var entry = kind.IsSplittable
                    ? SplitEntry(kind, amount, random.Next(1, 9)).ToList()
                    : Entry(kind.AddTo, kind.TakeFrom, amount).ToList();
                recorded.Add(entry);
                lines.AddRange(entry);
            }
            // Reverse a few of them, the way a real book has corrections in it.
            foreach (var entry in recorded.Where(_ => random.Next(4) == 0))
            {
                lines.AddRange(Reversed(entry));
            }

            var snapshot = TreasuryBalanceService.Project(lines);

            Assert.True(
                snapshot.Balances,
                $"books did not add up: hold {snapshot.CashOnHand} + owed {snapshot.OwedToUs} "
                + $"- owe {snapshot.WeOwe} != start {snapshot.StartingBalance} + net {snapshot.NetChange}");
            Assert.Equal(snapshot.MoneyIn - snapshot.MoneyOut, snapshot.NetChange);
        }
    }

    // A single unbalanced entry must be visible, or the chip is decoration.
    [Fact]
    public void Project_DetectsAnEntryWhoseHalvesDoNotMatch()
    {
        var broken = new[]
        {
            new LedgerLineRow(TreasuryAccounts.GilOnHand, 4_000_000),
            new LedgerLineRow(TreasuryAccounts.ItemSales, -3_999_500),
        };

        Assert.False(TreasuryBalanceService.Project(broken).Balances);
    }

    // Every seeded category has to land in exactly one bucket of the snapshot, or a category's gil
    // would silently vanish from the summary.
    [Fact]
    public void Project_EverySeededCategoryIsClassified()
    {
        foreach (var account in LedgerAccountDefaults.BuildDefaultAccounts(1))
        {
            Assert.NotNull(LedgerAccountClasses.FromNumber(account.AccountNumber));

            var snapshot = TreasuryBalanceService.Project(
                new[] { new LedgerLineRow(account.AccountNumber, 1_000) });
            var buckets = new[]
            {
                snapshot.CashOnHand, snapshot.OwedToUs, snapshot.WeOwe,
                snapshot.MoneyIn, snapshot.MoneyOut, snapshot.StartingBalance,
            };
            Assert.Equal(1, buckets.Count(value => value != 0));
        }
    }

    // --- who we still owe ---

    // "Gil Owed to Member" / "We paid a member what we owed", as the writer builds them: the member
    // lands on the what-we-owe half, never on gil on hand.
    private static IEnumerable<LedgerLineRow> OweAMember(string? who, long amount) => new[]
    {
        new LedgerLineRow(TreasuryAccounts.GilToMembers, amount, who),
        new LedgerLineRow(TreasuryAccounts.WeOwe, -amount, who),
    };

    private static IEnumerable<LedgerLineRow> PaidWhatWeOwed(string? who, long amount) => new[]
    {
        new LedgerLineRow(TreasuryAccounts.WeOwe, amount, who),
        new LedgerLineRow(TreasuryAccounts.GilOnHand, -amount, null),
    };

    // An owed split: the whole amount on gil-paid with NOBODY named, one what-we-owe line per member.
    private static IEnumerable<LedgerLineRow> OweSeveralMembers(long amount, params string[] members)
    {
        yield return new LedgerLineRow(TreasuryAccounts.GilToMembers, amount, null);
        var recipients = members.Select(name => new TreasuryRecipient($"user-{name}", name)).ToList();
        foreach (var (recipient, share) in TreasurySplit.Allocate(amount, recipients))
        {
            yield return new LedgerLineRow(TreasuryAccounts.WeOwe, -share, recipient.CharacterName);
        }
    }

    // THE regression. A split stores each recipient's account id; a settlement stores null and only
    // the name. Keying on "id if present, else name" would file these under two different keys and
    // show Millhouse twice — once owed 333,334 and once owed -333,334 — while the total still came
    // out right. Asserting the COUNT is what catches it; the sum passes either way.
    [Fact]
    public void ProjectByMember_SettlingASplitClearsTheSamePersonRatherThanAddingASecondRow()
    {
        var lines = OweSeveralMembers(1_000_000, "Ashira", "Millhouse", "Zeid").ToList();
        var owedToMillhouse = TreasuryBalanceService.ProjectByMember(lines)
            .Single(row => row.CharacterName == "Millhouse").Amount;

        lines.AddRange(PaidWhatWeOwed("Millhouse", owedToMillhouse));

        var rows = TreasuryBalanceService.ProjectByMember(lines);

        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, row => row.CharacterName == "Millhouse");
        Assert.Equal(
            TreasuryBalanceService.Project(lines).WeOwe,
            rows.Sum(row => row.Amount));
    }

    // The invariant the expanded list rests on: what is shown per member always adds up to the
    // figure it sits under. A breakdown that does not is worse than none.
    [Fact]
    public void ProjectByMember_AlwaysAddsUpToWhatWeOwe()
    {
        var random = new Random(20260731);
        var names = new[] { "Ashira", "Millhouse", "Zeid", "Bruno", "Lilkinuuki" };

        for (var iteration = 0; iteration < 500; iteration++)
        {
            var lines = new List<LedgerLineRow>();
            var recorded = new List<List<LedgerLineRow>>();

            for (var n = random.Next(0, 12); n > 0; n--)
            {
                var amount = random.Next(0, 5_000_000);
                var who = names[random.Next(names.Length)];
                var entry = random.Next(3) switch
                {
                    0 => OweAMember(who, amount).ToList(),
                    1 => PaidWhatWeOwed(who, amount).ToList(),
                    _ => OweSeveralMembers(
                        amount, names.OrderBy(_ => random.Next()).Take(random.Next(1, 4)).ToArray()).ToList(),
                };
                recorded.Add(entry);
                lines.AddRange(entry);
            }
            foreach (var entry in recorded.Where(_ => random.Next(4) == 0))
            {
                lines.AddRange(Reversed(entry));
            }

            Assert.Equal(
                TreasuryBalanceService.Project(lines).WeOwe,
                TreasuryBalanceService.ProjectByMember(lines).Sum(row => row.Amount));
        }
    }

    // Recorded before a member was required. It still has to appear, or the rows stop adding up.
    [Fact]
    public void ProjectByMember_KeepsAnObligationThatNamesNobody()
    {
        var rows = TreasuryBalanceService.ProjectByMember(OweAMember(null, 500_000).ToList());

        Assert.Null(Assert.Single(rows).CharacterName);
        Assert.Equal(500_000, rows[0].Amount);
    }

    [Fact]
    public void ProjectByMember_TreatsTheSameNameInAnyCaseAsOnePerson()
    {
        var lines = OweAMember("Millhouse", 300_000).Concat(OweAMember("millhouse", 200_000)).ToList();

        var row = Assert.Single(TreasuryBalanceService.ProjectByMember(lines));

        Assert.Equal(500_000, row.Amount);
        // The first spelling seen wins, so the name does not flicker between renders.
        Assert.Equal("Millhouse", row.CharacterName);
    }

    // A reversal copies the counterparty through, so it nets against its original per member.
    [Fact]
    public void ProjectByMember_ReversingAnObligationClearsThatMember()
    {
        var owed = OweAMember("Ashira", 400_000).ToList();
        var lines = owed.Concat(Reversed(owed)).ToList();

        Assert.Empty(TreasuryBalanceService.ProjectByMember(lines));
    }

    // Nothing stops an officer settling more than was ever recorded. Showing the negative is the
    // only evidence that happened; hiding it would break the add-up as well.
    [Fact]
    public void ProjectByMember_ShowsAMemberWhoWasPaidMoreThanWasRecorded()
    {
        var lines = OweAMember("Zeid", 100_000).Concat(PaidWhatWeOwed("Zeid", 250_000)).ToList();

        var row = Assert.Single(TreasuryBalanceService.ProjectByMember(lines));

        Assert.Equal(-150_000, row.Amount);
        Assert.Equal(TreasuryBalanceService.Project(lines).WeOwe, row.Amount);
    }

    // Only what-we-owe lines are member obligations. Gil paid out and gil on hand are not.
    [Fact]
    public void ProjectByMember_IgnoresEveryCategoryButWhatWeOwe()
    {
        var lines = SoldAnItem(4_000_000)
            .Concat(PaidAMember(1_000_000))
            .Concat(new[] { new LedgerLineRow(TreasuryAccounts.GilToMembers, 900_000, "Ashira") })
            .ToList();

        Assert.Empty(TreasuryBalanceService.ProjectByMember(lines));
    }

    [Fact]
    public void ProjectByMember_ListsTheLargestObligationFirst()
    {
        var lines = OweAMember("Ashira", 100_000)
            .Concat(OweAMember("Zeid", 900_000))
            .Concat(OweAMember("Millhouse", 500_000))
            .ToList();

        Assert.Equal(
            new[] { "Zeid", "Millhouse", "Ashira" },
            TreasuryBalanceService.ProjectByMember(lines).Select(row => row.CharacterName).ToArray());
    }

    // ---- who's holding the gil ---------------------------------------------------------------
    //
    // A linkshell has no bank: gil on hand is the sum of what sits on members' mules. The holder
    // lives on the SIGNED gil-on-hand line for one reason, and these pin it — the breakdown adds up
    // to the figure it sits under by construction rather than by anyone remembering to keep a
    // separate "who has what" list in step.

    // Gil arriving on a named mule.
    private static IEnumerable<LedgerLineRow> SoldAnItemHeldBy(string holder, long amount) =>
        new[]
        {
            new LedgerLineRow(TreasuryAccounts.GilOnHand, amount, null, holder),
            new LedgerLineRow(TreasuryAccounts.ItemSales, -amount),
        };

    // Gil leaving one.
    private static IEnumerable<LedgerLineRow> SpentFrom(string holder, long amount) =>
        new[]
        {
            new LedgerLineRow(TreasuryAccounts.OtherMoneyOut, amount),
            new LedgerLineRow(TreasuryAccounts.GilOnHand, -amount, null, holder),
        };

    [Fact]
    public void ProjectByHolder_AddsUpToGilOnHand()
    {
        var lines = SoldAnItemHeldBy("Edicius", 8_000_000)
            .Concat(SoldAnItemHeldBy("Ukiya", 2_000_000))
            .Concat(SpentFrom("Edicius", 3_000_000))
            .ToList();

        var holders = TreasuryBalanceService.ProjectByHolder(lines);

        // The whole claim of the panel, in one assertion.
        Assert.Equal(
            TreasuryBalanceService.Project(lines).CashOnHand,
            holders.Sum(holder => holder.Amount));
    }

    [Fact]
    public void ProjectByHolder_NetsSpendingAgainstTheMuleItLeft()
    {
        var lines = SoldAnItemHeldBy("Edicius", 8_000_000)
            .Concat(SpentFrom("Edicius", 3_000_000))
            .ToList();

        var holder = Assert.Single(TreasuryBalanceService.ProjectByHolder(lines));
        Assert.Equal("Edicius", holder.CharacterName);
        Assert.Equal(5_000_000, holder.Amount);
    }

    // The bug this shape exists to prevent: both halves of an entry carry the same magnitude, so a
    // holder written onto the second half too would double every arrival.
    [Fact]
    public void ProjectByHolder_IgnoresEveryLineThatIsNotGilOnHand()
    {
        var lines = SoldAnItemHeldBy("Edicius", 8_000_000)
            .Append(new LedgerLineRow(TreasuryAccounts.ItemSales, -1_000_000, null, "Edicius"))
            .ToList();

        var holder = Assert.Single(TreasuryBalanceService.ProjectByHolder(lines));
        Assert.Equal(8_000_000, holder.Amount);
    }

    // Every line recorded before holders existed has no name, and neither does a gil-auction payout.
    // Dropping that bucket would make the rows visibly fail to add up to the figure above them.
    [Fact]
    public void ProjectByHolder_KeepsGilNobodyWasNamedFor()
    {
        var lines = SoldAnItem(5_000_000)
            .Concat(SoldAnItemHeldBy("Edicius", 1_000_000))
            .ToList();

        var holders = TreasuryBalanceService.ProjectByHolder(lines);

        Assert.Equal(2, holders.Count);
        var unnamed = Assert.Single(holders, holder => holder.CharacterName is null);
        Assert.Equal(5_000_000, unnamed.Amount);
        Assert.Equal(
            TreasuryBalanceService.Project(lines).CashOnHand,
            holders.Sum(holder => holder.Amount));
    }

    // Same reason ProjectByCounterparty keys on the name: the two write paths populate the account
    // id differently, so casing must not split one person into two rows.
    [Fact]
    public void ProjectByHolder_TreatsTheSameNameAsOnePersonWhateverTheCasing()
    {
        var lines = SoldAnItemHeldBy("Edicius", 4_000_000)
            .Concat(SoldAnItemHeldBy("edicius", 1_000_000))
            .ToList();

        var holder = Assert.Single(TreasuryBalanceService.ProjectByHolder(lines));
        Assert.Equal(5_000_000, holder.Amount);
    }

    // A mule that has handed everything on drops off; one spent past zero is KEPT, because hiding it
    // would both break the add-up and bury the only evidence of the mistake.
    [Fact]
    public void ProjectByHolder_DropsSettledMulesAndKeepsOverspentOnes()
    {
        var lines = SoldAnItemHeldBy("Edicius", 4_000_000)
            .Concat(SpentFrom("Edicius", 4_000_000))
            .Concat(SpentFrom("Ukiya", 1_000_000))
            .ToList();

        var holders = TreasuryBalanceService.ProjectByHolder(lines);

        var holder = Assert.Single(holders);
        Assert.Equal("Ukiya", holder.CharacterName);
        Assert.Equal(-1_000_000, holder.Amount);
    }

    // A reversal copies the original's holder, so undoing a sale takes the gil back off the SAME
    // mule rather than stranding it and pushing the difference into the unattributed bucket.
    [Fact]
    public void ProjectByHolder_ReversalTakesTheGilBackOffTheSameMule()
    {
        var sale = SoldAnItemHeldBy("Edicius", 8_000_000).ToList();
        var lines = sale
            .Concat(sale.Select(line => new LedgerLineRow(
                line.AccountNumber, -line.Amount, line.CounterpartyCharacterName, line.HolderCharacterName)))
            .ToList();

        Assert.Empty(TreasuryBalanceService.ProjectByHolder(lines));
    }
}
