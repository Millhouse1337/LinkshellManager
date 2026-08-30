using System.Text;
using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// What one legacy RevenueEntries row becomes.
public sealed record TreasuryConversion(int AddTo, int TakeFrom, long Amount, string TransactionKind);

// THE mapping from the old single-column shape to the new two-halves shape.
//
// RevenueEntry.EntryType was an unconstrained varchar(16) that accumulated two incompatible
// vocabularies: direction words ("Income", "Expense") written by the Activity, source words
// ("AH Sale", "Mercenary Work", "Donation", "Other") written by the web form, and "Auction Payout"
// written by the gil-auction close with a NEGATIVE Value. Seven values, one column, no constraint.
//
// This class is the single source of that mapping. The C# Map() below and the SQL the
// ConvertRevenueEntriesToJournal migration runs are BOTH generated from the same Rules table, so
// the function the tests exercise is the function the migration executes. Asserting a hand-written
// SQL string against a separately-written C# function proves nothing about the migration; deriving
// both from one table does.
public static class RevenueEntryConversionMap
{
    // Order matters: first match wins, in both the C# and the generated SQL. Category == null
    // means "any category".
    private sealed record Rule(string EntryType, string? Category, int AddTo, int TakeFrom, string TransactionKind);

    private static readonly Rule[] Rules =
    {
        // Web form: an auction-house sale.
        new("AH Sale", null,
            TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales, TreasuryTransactionKinds.SoldAnItem),

        // Both mark-sold paths wrote this exact pair. Same economic event as "AH Sale" above — the
        // two vocabularies collapse here.
        new("Income", "Item Sale",
            TreasuryAccounts.GilOnHand, TreasuryAccounts.ItemSales, TreasuryTransactionKinds.SoldAnItem),

        new("Mercenary Work", null,
            TreasuryAccounts.GilOnHand, TreasuryAccounts.PaidWork, TreasuryTransactionKinds.GotPaidForWork),

        new("Donation", null,
            TreasuryAccounts.GilOnHand, TreasuryAccounts.MemberDonations, TreasuryTransactionKinds.GotADonation),

        // The officer explicitly chose "Other", so a catch-all is the faithful answer, not a guess.
        new("Other", null,
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn, TreasuryTransactionKinds.OtherMoneyIn),

        // Gil auction close: gil left the treasury for a member. The old row carried a negative
        // Value; the amount becomes positive and the direction moves into which half is which.
        new("Auction Payout", null,
            TreasuryAccounts.GilToMembers, TreasuryAccounts.GilOnHand, TreasuryTransactionKinds.PaidGilToMember),

        // Activity-recorded gil in, with a free-text category we cannot map to anything specific.
        // The original text is preserved on the line's note, so nothing is lost.
        new("Income", null,
            TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn, TreasuryTransactionKinds.OtherMoneyIn),

        // Activity-recorded gil out, same reasoning.
        new("Expense", null,
            TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand, TreasuryTransactionKinds.OtherMoneyOut),
    };

    public static TreasuryConversion Map(string? entryType, string? category, long value)
    {
        var type = Normalize(entryType);
        var cat = Normalize(category);
        // ABS, never -Value. [Range(0, long.MaxValue)] on ManageRevenueViewModel.Value is only a
        // model-binding check and ManageRevenueController.Edit assigns model.Value straight through,
        // so an already-negative Expense row can exist in production. Negating one would move gil
        // the wrong way; taking its magnitude and letting the rule decide the direction cannot.
        var amount = Math.Abs(value);

        foreach (var rule in Rules)
        {
            if (!string.Equals(type, Normalize(rule.EntryType), StringComparison.Ordinal))
            {
                continue;
            }
            if (rule.Category is not null
                && !string.Equals(cat, Normalize(rule.Category), StringComparison.Ordinal))
            {
                continue;
            }
            return new TreasuryConversion(rule.AddTo, rule.TakeFrom, amount, rule.TransactionKind);
        }

        // An EntryType nobody has seen. Fall back to the sign, which is the only signal left, and
        // file it in the catch-all — which is where every hand-recorded entry lands now anyway.
        return value >= 0
            ? new TreasuryConversion(
                TreasuryAccounts.GilOnHand, TreasuryAccounts.OtherMoneyIn, amount,
                TreasuryTransactionKinds.OtherMoneyIn)
            : new TreasuryConversion(
                TreasuryAccounts.OtherMoneyOut, TreasuryAccounts.GilOnHand, amount,
                TreasuryTransactionKinds.OtherMoneyOut);
    }

    // The signed movement the OLD code computed for this row, so the conversion can be checked
    // against the balance every pre-migration read site agreed on.
    public static long LegacySignedAmount(string? entryType, long value) =>
        string.Equals(Normalize(entryType), "expense", StringComparison.Ordinal) ? -value : value;

    // The signed movement the NEW shape produces for gil on hand. Must equal LegacySignedAmount for
    // every row, or a linkshell's displayed balance moves because of the conversion.
    public static long ConvertedCashDelta(string? entryType, string? category, long value)
    {
        var conversion = Map(entryType, category, value);
        if (conversion.AddTo == TreasuryAccounts.GilOnHand)
        {
            return conversion.Amount;
        }
        if (conversion.TakeFrom == TreasuryAccounts.GilOnHand)
        {
            return -conversion.Amount;
        }
        return 0L;
    }

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    // ---- SQL generation: the migration's CASE expressions come from the table above ----

    private static string SqlLiteral(string value) => $"'{value.Replace("'", "''")}'";

    // e."EntryType"/"Category" normalized the same way Normalize() does.
    private const string SqlType = "lower(btrim(coalesce(src.\"EntryType\", '')))";
    private const string SqlCategory = "lower(btrim(coalesce(src.\"Category\", '')))";

    private static string BuildCase(Func<Rule, string> resultOf, string fallbackPositive, string fallbackNegative)
    {
        var sql = new StringBuilder("CASE");
        foreach (var rule in Rules)
        {
            sql.Append($"\n                       WHEN {SqlType} = {SqlLiteral(Normalize(rule.EntryType))}");
            if (rule.Category is not null)
            {
                sql.Append($" AND {SqlCategory} = {SqlLiteral(Normalize(rule.Category))}");
            }
            sql.Append($" THEN {resultOf(rule)}");
        }
        sql.Append($"\n                       ELSE CASE WHEN src.\"Value\" >= 0 THEN {fallbackPositive} ELSE {fallbackNegative} END");
        sql.Append("\n                  END");
        return sql.ToString();
    }

    public static string AddToCaseSql() => BuildCase(
        rule => rule.AddTo.ToString(),
        TreasuryAccounts.GilOnHand.ToString(),
        TreasuryAccounts.OtherMoneyOut.ToString());

    public static string TakeFromCaseSql() => BuildCase(
        rule => rule.TakeFrom.ToString(),
        TreasuryAccounts.OtherMoneyIn.ToString(),
        TreasuryAccounts.GilOnHand.ToString());

    public static string TransactionKindCaseSql() => BuildCase(
        rule => SqlLiteral(rule.TransactionKind),
        SqlLiteral(TreasuryTransactionKinds.OtherMoneyIn),
        SqlLiteral(TreasuryTransactionKinds.OtherMoneyOut));

    // The seeded categories as a VALUES list, generated from LedgerAccountDefaults so a converted
    // linkshell and a fresh one get identical wording. The migration needs this because it has to
    // resolve category ids before it can write any lines, and the lazy provisioner has not run yet.
    public static string SeedAccountsValuesSql()
    {
        var rows = LedgerAccountDefaults.BuildDefaultAccounts(0)
            .Select(account => "("
                + $"{account.AccountNumber}, "
                + $"{SqlLiteral(account.Name)}, "
                + $"{(account.Description is null ? "NULL" : SqlLiteral(account.Description))}, "
                + $"{(account.IsCash ? "TRUE" : "FALSE")}, "
                + $"{(account.IsPostable ? "TRUE" : "FALSE")}, "
                + $"{account.SortOrder}"
                + ")");
        return string.Join(",\n                        ", rows);
    }
}
