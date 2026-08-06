using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// The categories every linkshell starts with. Thirteen of them, covering every way gil moves in
// this app today, in the words an officer would use.
//
// Names here are DEFAULTS, not labels: officers can rename their own categories, and
// LedgerAccount.Name is what the UI shows. This class is the one home for the seeded wording so a
// fresh linkshell and a converted one read the same.
//
// Mirrors LinkshellRoleDefaults + DkpPoolDefaults.
public static class LedgerAccountDefaults
{
    // (number, name, description, sortOrder). Class and normal side are derived from the number.
    private static readonly (int Number, string Name, string? Description)[] Seed =
    {
        (TreasuryAccounts.GilOnHand, "Gil on hand",
            "The gil the linkshell actually has. Every other category feeds this one."),
        (TreasuryAccounts.OwedToUs, "Owed to us",
            "Gil someone has agreed to pay the linkshell but has not handed over yet."),

        (TreasuryAccounts.WeOwe, "We owe",
            "Gil the linkshell has promised to a member but has not handed over yet."),

        (TreasuryAccounts.NetWorth, "What we're worth",
            "Gil on hand plus what we're owed, minus what we owe. Worked out automatically — "
                + "nothing is ever recorded against it."),
        (TreasuryAccounts.StartingBalance, "Starting balance",
            "The gil the linkshell already had before it started tracking any of this."),

        (TreasuryAccounts.MemberDonations, "Member donations", "Gil members gave to the linkshell."),
        (TreasuryAccounts.ItemSales, "Item sales", "Gil from selling items the linkshell owned."),
        (TreasuryAccounts.PaidWork, "Paid work", "Gil earned doing runs, escorts or crafts for others."),
        (TreasuryAccounts.OtherMoneyIn, "Other gil in", "Gil in that does not fit another category yet."),

        (TreasuryAccounts.GilToMembers, "Gil paid to members",
            "Gil handed out to members — gil auction wins, splits, reimbursements."),
        (TreasuryAccounts.Supplies, "Supplies",
            "Food, medicine, entry items and gear bought out of the treasury."),
        (TreasuryAccounts.OtherMoneyOut, "Other gil out", "Gil out that does not fit another category yet."),
        (TreasuryAccounts.BalanceAdjustment, "Balance adjustment",
            "The difference when someone counts the linkshell's gil and it does not match the books."),
    };

    public static IEnumerable<LedgerAccount> BuildDefaultAccounts(int linkshellId)
    {
        var createdAt = DateTime.UtcNow;
        var sortOrder = 0;
        foreach (var (number, name, description) in Seed)
        {
            yield return new LedgerAccount
            {
                LinkshellId = linkshellId,
                AccountNumber = number,
                Name = name,
                Description = description,
                IsCash = number == TreasuryAccounts.GilOnHand,
                // "What we're worth" is worked out, not recorded against. Leaving it unpostable is
                // what removes the whole "did the year-end close run, and did it run twice?"
                // failure class.
                IsPostable = number != TreasuryAccounts.NetWorth,
                IsSystem = true,
                IsActive = true,
                SortOrder = sortOrder++,
                CreatedAt = createdAt,
            };
        }
    }
}

// Guarantees a linkshell has its categories. Called lazily from every treasury read and write, so
// no creation path — present or future — can leave a linkshell without them, and so the
// AddTreasuryLedger migration does not have to seed anything at deploy time.
//
// That last point is deliberate: AddDkpPools refused an in-migration backfill because it would
// mean minutes of ACCESS EXCLUSIVE lock inside Database.Migrate() at startup — a 502 window on
// deploy. Seeding lazily costs one indexed lookup per request and nothing on deploy.
//
// Idempotent: the unique (LinkshellId, AccountNumber) index means a race loses at the DB and we
// re-read. Mirrors DkpPoolProvisioner.EnsureDefaultPoolAsync.
public sealed class LedgerAccountProvisioner
{
    private readonly ApplicationDbContext _db;

    public LedgerAccountProvisioner(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<List<LedgerAccount>> EnsureAccountsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var existing = await _db.LedgerAccounts
            .Where(account => account.LinkshellId == linkshellId)
            .OrderBy(account => account.SortOrder)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            // A linkshell seeded before a new default category was added is topped up rather than
            // left short, so the picker never references a category that does not exist.
            var missing = LedgerAccountDefaults.BuildDefaultAccounts(linkshellId)
                .Where(seed => existing.All(account => account.AccountNumber != seed.AccountNumber))
                .ToList();
            if (missing.Count == 0)
            {
                return existing;
            }
            _db.LedgerAccounts.AddRange(missing);
            return await SaveAndRereadAsync(linkshellId, missing, cancellationToken);
        }

        var seeded = LedgerAccountDefaults.BuildDefaultAccounts(linkshellId).ToList();
        _db.LedgerAccounts.AddRange(seeded);
        return await SaveAndRereadAsync(linkshellId, seeded, cancellationToken);
    }

    // The category a linkshell's gil lives in. Every balance is this one's.
    public async Task<LedgerAccount> EnsureCashAccountAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var accounts = await EnsureAccountsAsync(linkshellId, cancellationToken);
        return accounts.First(account => account.IsCash);
    }

    private async Task<List<LedgerAccount>> SaveAndRereadAsync(
        int linkshellId, List<LedgerAccount> added, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request seeded first — the unique index did its job. Drop our attempts and
            // take theirs.
            foreach (var account in added)
            {
                _db.Entry(account).State = EntityState.Detached;
            }
        }

        return await _db.LedgerAccounts
            .Where(account => account.LinkshellId == linkshellId)
            .OrderBy(account => account.SortOrder)
            .ToListAsync(cancellationToken);
    }
}
