using System.Reflection;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Covers the vocabulary layer: what a category IS is derived from its number, what can happen to the
// treasury is a fixed catalog, and none of the words on screen are bookkeeping jargon.
public class TreasuryVocabularyTests
{
    // Class derived from the number, so a category whose number and behaviour disagree is not a state
    // the schema can reach.
    [Theory]
    [InlineData(1000, LedgerAccountClasses.Holds)]
    [InlineData(1999, LedgerAccountClasses.Holds)]
    [InlineData(2000, LedgerAccountClasses.Owes)]
    [InlineData(2999, LedgerAccountClasses.Owes)]
    [InlineData(3000, LedgerAccountClasses.Worth)]
    [InlineData(3999, LedgerAccountClasses.Worth)]
    [InlineData(4000, LedgerAccountClasses.MoneyIn)]
    [InlineData(4999, LedgerAccountClasses.MoneyIn)]
    [InlineData(5000, LedgerAccountClasses.MoneyOut)]
    [InlineData(5999, LedgerAccountClasses.MoneyOut)]
    public void FromNumber_MapsEveryRangeBoundary(int accountNumber, string expected)
    {
        Assert.Equal(expected, LedgerAccountClasses.FromNumber(accountNumber));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(6000)]
    [InlineData(0)]
    [InlineData(-1)]
    public void FromNumber_RejectsNumbersOutsideTheRange(int accountNumber)
    {
        Assert.Null(LedgerAccountClasses.FromNumber(accountNumber));
        // The DB CHECK constraint enforces the same range, so this can never be persisted either.
        Assert.True(
            accountNumber < LedgerAccountClasses.MinAccountNumber
            || accountNumber > LedgerAccountClasses.MaxAccountNumber);
    }

    // What we hold and what we spend grow when gil is added to them; what we owe, what we're worth and
    // gil we took in grow the other way. This is what gross totals key on, and getting it wrong makes
    // a reversal inflate both totals instead of reducing one.
    [Theory]
    [InlineData(LedgerAccountClasses.Holds, 1)]
    [InlineData(LedgerAccountClasses.MoneyOut, 1)]
    [InlineData(LedgerAccountClasses.Owes, -1)]
    [InlineData(LedgerAccountClasses.Worth, -1)]
    [InlineData(LedgerAccountClasses.MoneyIn, -1)]
    public void NormalSign_IsTheSideACategoryGrowsOn(string accountClass, int expected)
    {
        Assert.Equal(expected, LedgerAccountClasses.NormalSign(accountClass));
    }

    [Fact]
    public void SeededCategories_AreCoherent()
    {
        var accounts = LedgerAccountDefaults.BuildDefaultAccounts(7).ToList();

        Assert.All(accounts, account => Assert.Equal(7, account.LinkshellId));
        Assert.All(accounts, account => Assert.True(account.IsSystem));
        Assert.All(accounts, account => Assert.NotNull(account.AccountClass));
        Assert.All(accounts, account => Assert.False(string.IsNullOrWhiteSpace(account.Name)));
        // Names are what the UI shows, so a duplicate would be genuinely ambiguous on screen.
        Assert.Equal(accounts.Count, accounts.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(accounts.Count, accounts.Select(a => a.AccountNumber).Distinct().Count());
        Assert.Equal(accounts.Count, accounts.Select(a => a.SortOrder).Distinct().Count());

        // Exactly one gil-on-hand category: its balance IS the treasury balance, so two would make
        // "how much gil do we have" ambiguous again. A partial unique index enforces the same thing.
        var cash = Assert.Single(accounts, account => account.IsCash);
        Assert.Equal(TreasuryAccounts.GilOnHand, cash.AccountNumber);

        // "What we're worth" is worked out, never recorded against — that is what removes the whole
        // "did the close run, and did it run twice?" class of failure.
        var notPostable = Assert.Single(accounts, account => !account.IsPostable);
        Assert.Equal(TreasuryAccounts.NetWorth, notPostable.AccountNumber);
    }

    // Every number in TreasuryAccounts has to be seeded, or a call site can name a category that does
    // not exist and the writer throws at runtime.
    [Fact]
    public void EveryNamedAccountNumberIsSeeded()
    {
        var seeded = LedgerAccountDefaults.BuildDefaultAccounts(1).Select(a => a.AccountNumber).ToHashSet();
        var named = typeof(TreasuryAccounts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(int))
            .Select(field => (Name: field.Name, Number: (int)field.GetRawConstantValue()!));

        foreach (var (name, number) in named)
        {
            Assert.True(seeded.Contains(number), $"TreasuryAccounts.{name} ({number}) is not seeded");
        }
    }

    [Fact]
    public void EveryTransactionKindNamesRealPostableCategories()
    {
        var byNumber = LedgerAccountDefaults.BuildDefaultAccounts(1).ToDictionary(a => a.AccountNumber);

        foreach (var kind in TreasuryTransactionKinds.All)
        {
            Assert.True(byNumber.ContainsKey(kind.AddTo), $"{kind.Key} adds to a category that is not seeded");
            Assert.True(byNumber.ContainsKey(kind.TakeFrom), $"{kind.Key} takes from a category that is not seeded");
            // Nothing may post to "what we're worth" — it is derived.
            Assert.True(byNumber[kind.AddTo].IsPostable, $"{kind.Key} posts to a derived category");
            Assert.True(byNumber[kind.TakeFrom].IsPostable, $"{kind.Key} posts to a derived category");
            Assert.NotEqual(kind.AddTo, kind.TakeFrom);
            Assert.False(string.IsNullOrWhiteSpace(kind.Label));
            Assert.False(string.IsNullOrWhiteSpace(kind.Help));
            // The preview sentence has to have somewhere to put the amount.
            Assert.Contains("{0}", kind.PreviewTemplate);
            // A split has to land on one of the kind's own two categories. Anything else would
            // build an entry whose halves are not the pair the kind promises.
            Assert.True(
                kind.SplitAccount is null
                    || kind.SplitAccount == kind.AddTo
                    || kind.SplitAccount == kind.TakeFrom,
                $"{kind.Key} splits a category that is not one of its own two");
        }
    }

    // A split asks for several members, so the surfaces have to know to show a member field at all.
    [Fact]
    public void EverySplittableKindAsksForMembers()
    {
        Assert.All(
            TreasuryTransactionKinds.All.Where(kind => kind.IsSplittable),
            kind => Assert.True(kind.ShowsMember, $"{kind.Key} splits but does not show a member"));
    }

    // The balance sheet calls what-we-owe "Owed to members", and expands it into a list of people.
    // That is only honest while every kind touching the category names a member. The day someone
    // adds "We owe a shop", this fails instead of the label quietly becoming a lie.
    [Fact]
    public void EveryKindThatTouchesWhatWeOweNamesAMember()
    {
        var touching = TreasuryTransactionKinds.All
            .Where(kind => kind.AddTo == TreasuryAccounts.WeOwe || kind.TakeFrom == TreasuryAccounts.WeOwe)
            .ToList();

        Assert.NotEmpty(touching);
        Assert.All(touching, kind =>
            Assert.True(kind.ShowsMember, $"{kind.Key} moves what we owe but does not name a member"));
    }

    [Fact]
    public void TransactionKinds_HaveUniqueKeysAndLabels()
    {
        var all = TreasuryTransactionKinds.All;

        Assert.Equal(all.Count, all.Select(k => k.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(all.Count, all.Select(k => k.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, kind => Assert.NotNull(TreasuryTransactionKinds.Find(kind.Key)));
        Assert.Null(TreasuryTransactionKinds.Find("NotAThing"));
        Assert.Null(TreasuryTransactionKinds.Find(null));
    }

    // The two adjustment kinds are chosen by the app after someone counts the mule, not by an officer
    // from a list — so they must not appear in the picker.
    [Fact]
    public void Pickable_HidesTheKindsTheAppChoosesItself()
    {
        var pickable = TreasuryTransactionKinds.Pickable().Select(kind => kind.Key).ToList();

        Assert.DoesNotContain(TreasuryTransactionKinds.FoundExtraGil, pickable);
        Assert.DoesNotContain(TreasuryTransactionKinds.MissingGil, pickable);
        Assert.Contains(TreasuryTransactionKinds.SoldAnItem, pickable);
        Assert.Contains(TreasuryTransactionKinds.PaidGilToMember, pickable);
        Assert.Contains(TreasuryTransactionKinds.SplitGilAmongMembers, pickable);
        Assert.Contains(TreasuryTransactionKinds.WeOweSeveralMembers, pickable);
    }

    // An item merely LISTED on the auction house is not a sale: no buyer, no agreed price, nothing
    // owed. It must not be possible to record one, because there is nothing to record until it sells.
    [Fact]
    public void NothingRecordsAListingThatHasNotSold()
    {
        Assert.DoesNotContain(
            TreasuryTransactionKinds.All,
            kind => kind.Label.Contains("list", StringComparison.OrdinalIgnoreCase)
                || kind.Help.Contains("not sold", StringComparison.OrdinalIgnoreCase));
    }

    // THE test that keeps jargon off the screen. Every string a user sees comes from TreasuryLabels or
    // TreasuryTransactionKinds, and none of it may contain a bookkeeping term — the structure
    // underneath is rigorous, but the words are the words an officer would actually say.
    [Fact]
    public void NoUserVisibleStringUsesBookkeepingJargon()
    {
        var strings = typeof(TreasuryLabels)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (Source: $"TreasuryLabels.{field.Name}", Text: (string)field.GetRawConstantValue()!))
            .Concat(TreasuryTransactionKinds.All.SelectMany(kind => new[]
            {
                (Source: $"{kind.Key}.Label", Text: kind.Label),
                (Source: $"{kind.Key}.Help", Text: kind.Help),
                (Source: $"{kind.Key}.PreviewTemplate", Text: kind.PreviewTemplate),
            }))
            .Concat(LedgerAccountDefaults.BuildDefaultAccounts(1).SelectMany(account => new[]
            {
                (Source: $"category {account.AccountNumber}.Name", Text: account.Name),
                (Source: $"category {account.AccountNumber}.Description", Text: account.Description ?? string.Empty),
            }))
            .ToList();

        // Sanity: this only proves anything if it is actually looking at the strings.
        Assert.True(strings.Count > 60, $"expected to scan the whole vocabulary, scanned {strings.Count}");

        foreach (var (source, text) in strings)
        {
            // The one exemption: the label for the collapsed panel that deliberately DOES show the two
            // halves, for the one officer who wants to see them.
            if (source == $"TreasuryLabels.{nameof(TreasuryLabels.ShowDetails)}")
            {
                continue;
            }
            foreach (var forbidden in TreasuryLabels.Forbidden)
            {
                Assert.False(
                    text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{source} says \"{text}\" — '{forbidden}' is bookkeeping jargon, use plain English");
            }
        }
    }

    [Fact]
    public void ClassLabels_ArePlainEnglishForEveryClass()
    {
        foreach (var accountClass in new[]
                 {
                     LedgerAccountClasses.Holds, LedgerAccountClasses.Owes, LedgerAccountClasses.Worth,
                     LedgerAccountClasses.MoneyIn, LedgerAccountClasses.MoneyOut,
                 })
        {
            var label = TreasuryLabels.ClassLabel(accountClass);
            Assert.False(string.IsNullOrWhiteSpace(label));
            Assert.NotEqual(accountClass, label);
        }

        Assert.Equal("Uncategorized", TreasuryLabels.ClassLabel("something else"));
    }

    [Fact]
    public void EntryReference_ReadsAsAnOfficerWouldQuoteIt()
    {
        // Padding is a storage detail, not something an officer quotes.
        Assert.Equal("#142", TreasuryLabels.EntryReference("000142"));
        Assert.Equal("#8", TreasuryLabels.EntryReference("000008"));
        Assert.Equal("#142", TreasuryLabels.EntryReference("142"));
        Assert.Equal("#", TreasuryLabels.EntryReference(null));
        // Sequence starts at 1, so this is unreachable — it just must not read as "#".
        Assert.Equal("#0", TreasuryLabels.EntryReference("000000"));
    }

    [Theory]
    [InlineData(JournalEntryKinds.Reversal, true)]
    [InlineData(JournalEntryKinds.Correction, true)]
    [InlineData(JournalEntryKinds.Standard, false)]
    [InlineData(JournalEntryKinds.Opening, false)]
    [InlineData(JournalEntryKinds.Adjustment, false)]
    [InlineData(JournalEntryKinds.Migration, false)]
    public void OnlyFixesRequireAReason(string kind, bool expected)
    {
        // Mirrored by the CK_JournalEntries_ReasonRequiredForFixes constraint, so an entry claiming an
        // earlier one was wrong cannot be stored without saying why.
        Assert.Equal(expected, JournalEntryKinds.RequiresReason(kind));
    }

    [Fact]
    public void StatusHelpers_AreCaseInsensitive()
    {
        Assert.True(JournalEntryStatuses.IsDraft("draft"));
        Assert.True(JournalEntryStatuses.IsConfirmed("CONFIRMED"));
        Assert.False(JournalEntryStatuses.IsDraft(null));
        Assert.False(JournalEntryStatuses.IsConfirmed("Draft"));
    }
}
