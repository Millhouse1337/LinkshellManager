using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Pins the one-way conversion of real gil from the old single-column shape to two balanced halves.
//
// The function exercised here is literally the function ConvertRevenueEntriesToJournal executes: the
// migration's CASE expressions are generated from the same Rules table Map() walks, so there is no
// gap between "the mapping we tested" and "the mapping that ran".
public class RevenueEntryConversionMapTests
{
    // Every EntryType/Category combination that exists in the wild.
    [Theory]
    // Web form values.
    [InlineData("AH Sale", null, 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales)]
    [InlineData("Mercenary Work", null, 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.PaidWork)]
    [InlineData("Donation", null, 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.MemberDonations)]
    [InlineData("Other", null, 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    // Both mark-sold paths — same economic event as "AH Sale", so the two vocabularies collapse.
    [InlineData("Income", "Item Sale", 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales)]
    // Activity values with free text we cannot map.
    [InlineData("Income", "Sales", 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    [InlineData("Income", null, 1_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    [InlineData("Expense", "Fees", 1_000, TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand)]
    // Gil auction close: negative Value, becomes a positive amount flowing the other way.
    [InlineData("Auction Payout", "Gil", -1_000, TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand)]
    public void Map_RoutesEveryKnownShape(
        string entryType, string? category, long value, int expectedAddTo, int expectedTakeFrom)
    {
        var result = RevenueEntryConversionMap.Map(entryType, category, value);

        Assert.Equal(expectedAddTo, result.AddTo);
        Assert.Equal(expectedTakeFrom, result.TakeFrom);
        Assert.Equal(Math.Abs(value), result.Amount);
    }

    [Theory]
    [InlineData("ah sale")]
    [InlineData("AH SALE")]
    [InlineData("  AH Sale  ")]
    public void Map_IsCaseAndWhitespaceInsensitive(string entryType)
    {
        var result = RevenueEntryConversionMap.Map(entryType, null, 500);

        Assert.Equal(TreasuryAccounts.ItemSales, result.TakeFrom);
        Assert.Equal(TreasuryTransactionKinds.SoldAnItem, result.TransactionKind);
    }

    // "Income" + "Item Sale" must win over the bare "Income" rule, which sits after it.
    [Fact]
    public void Map_MoreSpecificRuleWinsRegardlessOfCategoryCasing()
    {
        Assert.Equal(
            TreasuryAccounts.ItemSales,
            RevenueEntryConversionMap.Map("Income", "item sale", 1).TakeFrom);
        Assert.Equal(
            TreasuryAccounts.OtherMoneyIn,
            RevenueEntryConversionMap.Map("Income", "item sales!", 1).TakeFrom);
    }

    // An EntryType nobody has seen. The sign is the only signal left, and it must not vanish.
    [Theory]
    [InlineData("Tribute", 5_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    [InlineData("Tribute", -5_000, TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand)]
    [InlineData("", 5_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    [InlineData(null, 5_000, TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn)]
    public void Map_UnknownEntryTypeFallsBackToTheSign(
        string? entryType, long value, int expectedAddTo, int expectedTakeFrom)
    {
        var result = RevenueEntryConversionMap.Map(entryType, "whatever", value);

        Assert.Equal(expectedAddTo, result.AddTo);
        Assert.Equal(expectedTakeFrom, result.TakeFrom);
        Assert.Equal(Math.Abs(value), result.Amount);
    }

    // A sale genuinely recorded at 0 gil exists: both mark-sold paths clamp a negative price to 0.
    // It converts to two zero halves rather than being dropped, because the fact that a sale
    // happened is itself information.
    [Fact]
    public void Map_ZeroAmountSurvives()
    {
        var result = RevenueEntryConversionMap.Map("Income", "Item Sale", 0);

        Assert.Equal(0, result.Amount);
        Assert.Equal(TreasuryAccounts.GilOnHand, result.AddTo);
    }

    // The amount is always a magnitude and the direction always lives in which category is which.
    [Theory]
    [InlineData("Expense", 1_000)]
    [InlineData("Expense", -1_000)]
    [InlineData("Auction Payout", -1_000)]
    [InlineData("AH Sale", 1_000)]
    public void Map_AmountIsNeverNegative(string entryType, long value)
    {
        Assert.True(RevenueEntryConversionMap.Map(entryType, null, value).Amount >= 0);
    }

    // THE balance-preservation property: for every row that is not malformed, the gil-on-hand
    // movement after conversion equals what all four pre-migration read sites computed. A linkshell's
    // displayed balance must not move because of a schema change.
    [Theory]
    [InlineData("Income", 4_000_000)]
    [InlineData("AH Sale", 1_500_000)]
    [InlineData("Donation", 250_000)]
    [InlineData("Mercenary Work", 100_000)]
    [InlineData("Other", 50_000)]
    [InlineData("Expense", 300_000)]
    [InlineData("Auction Payout", -820_000)]
    [InlineData("Income", 0)]
    public void ConvertedCashDelta_MatchesTheLegacyBalanceFormula(string entryType, long value)
    {
        Assert.Equal(
            RevenueEntryConversionMap.LegacySignedAmount(entryType, value),
            RevenueEntryConversionMap.ConvertedCashDelta(entryType, null, value));
    }

    [Fact]
    public void ConvertedCashDelta_PreservesAWholeMixedTreasury()
    {
        var rows = new (string EntryType, string? Category, long Value)[]
        {
            ("Income", "Item Sale", 4_000_000),
            ("AH Sale", "Kirin's Osode", 1_500_000),
            ("Donation", null, 250_000),
            ("Mercenary Work", "Sky runs", 100_000),
            ("Other", "misc", 50_000),
            ("Expense", "Fees", 300_000),
            ("Auction Payout", "Gil", -820_000),
        };

        var legacy = rows.Sum(row => RevenueEntryConversionMap.LegacySignedAmount(row.EntryType, row.Value));
        var converted = rows.Sum(row =>
            RevenueEntryConversionMap.ConvertedCashDelta(row.EntryType, row.Category, row.Value));

        Assert.Equal(4_780_000, legacy);
        Assert.Equal(legacy, converted);
    }

    // The one deliberate exception, pinned so it is a decision rather than a surprise.
    //
    // An "Expense" row with a negative Value can exist: [Range(0, long.MaxValue)] on
    // ManageRevenueViewModel.Value is only a model-binding check, and ManageRevenueController.Edit
    // assigns model.Value straight through. The old formula read such a row as gil coming IN
    // (-(-1000) = +1000), which is almost certainly not what whoever typed it meant. The conversion
    // reads it as gil going out.
    //
    // That is a fix, not a preservation, and it moves that linkshell's balance — which is exactly why
    // the pre-flight query in the migration's docs groups by sign("Value"): it tells you whether any
    // such row exists BEFORE the conversion ships. LegacyEntryType is kept on the converted entry
    // either way, so the original is recoverable.
    [Fact]
    public void ConvertedCashDelta_NegativeExpenseIsDeliberatelyReclassifiedAsGilGoingOut()
    {
        const string entryType = "Expense";
        const long value = -1_000;

        Assert.Equal(1_000, RevenueEntryConversionMap.LegacySignedAmount(entryType, value));
        Assert.Equal(-1_000, RevenueEntryConversionMap.ConvertedCashDelta(entryType, null, value));
    }

    // Every rule must name a real seeded category and a real picker option, or the migration writes
    // rows pointing at categories that do not exist.
    [Fact]
    public void Map_OnlyEverTargetsSeededCategories()
    {
        var seeded = LedgerAccountDefaults.BuildDefaultAccounts(1)
            .Select(account => account.AccountNumber)
            .ToHashSet();

        var samples = new (string? EntryType, string? Category, long Value)[]
        {
            ("AH Sale", null, 1), ("Income", "Item Sale", 1), ("Mercenary Work", null, 1),
            ("Donation", null, 1), ("Other", null, 1), ("Auction Payout", "Gil", -1),
            ("Income", null, 1), ("Expense", null, 1), ("Tribute", null, 1), ("Tribute", null, -1),
            (null, null, 0),
        };

        foreach (var (entryType, category, value) in samples)
        {
            var result = RevenueEntryConversionMap.Map(entryType, category, value);
            Assert.Contains(result.AddTo, seeded);
            Assert.Contains(result.TakeFrom, seeded);
            Assert.NotNull(TreasuryTransactionKinds.Find(result.TransactionKind));
            // One half always has to be gil on hand: every legacy row was a cash movement.
            Assert.True(
                result.AddTo == TreasuryAccounts.GilOnHand || result.TakeFrom == TreasuryAccounts.GilOnHand,
                $"neither half of {entryType}/{category} touches gil on hand");
        }
    }

    // The generated SQL and the C# come from one table; these guard the generator itself.
    [Fact]
    public void GeneratedSql_CoversEveryRuleAndHasAFallback()
    {
        foreach (var sql in new[]
                 {
                     RevenueEntryConversionMap.AddToCaseSql(),
                     RevenueEntryConversionMap.TakeFromCaseSql(),
                     RevenueEntryConversionMap.TransactionKindCaseSql(),
                 })
        {
            // Eight rules, each its own WHEN, plus a sign-based ELSE so no row is left unmapped.
            Assert.Equal(8, sql.Split("WHEN lower(btrim(coalesce(src.\"EntryType\"").Length - 1);
            Assert.Contains("ELSE CASE WHEN src.\"Value\" >= 0", sql);
            Assert.StartsWith("CASE", sql);
            Assert.EndsWith("END", sql.TrimEnd());
        }
    }

    [Fact]
    public void SeedAccountsValuesSql_EscapesApostrophesAndCoversEveryCategory()
    {
        var sql = RevenueEntryConversionMap.SeedAccountsValuesSql();
        var expected = LedgerAccountDefaults.BuildDefaultAccounts(1).ToList();

        Assert.Equal(expected.Count, sql.Split("),").Length);
        // "What we're worth" would break the statement if the apostrophe were not doubled.
        Assert.Contains("What we''re worth", sql);
        Assert.DoesNotContain("What we're worth", sql);
        foreach (var account in expected)
        {
            Assert.Contains($"({account.AccountNumber}, ", sql);
        }
    }
}
