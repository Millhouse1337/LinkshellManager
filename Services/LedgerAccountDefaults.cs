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
    // (number, name), in AccountNumber order. Class and normal side are derived from the number.
    // The comment after each one says what it holds; no screen shows a description, so none is stored.
    private static readonly (int Number, string Name)[] Seed =
    {
        (TreasuryAccounts.GilOnHand, "Gil on hand"),             // what the linkshell actually has
        (TreasuryAccounts.OwedToUs, "Owed to us"),               // promised to us, not handed over yet

        (TreasuryAccounts.WeOwe, "We owe"),                      // promised to a member, not handed over yet

        // Gil on hand plus what we're owed, minus what we owe. Worked out, never recorded against:
        // no TreasuryTransactionKind names it.
        (TreasuryAccounts.NetWorth, "What we're worth"),
        (TreasuryAccounts.StartingBalance, "Starting balance"),  // what we had before tracking began

        (TreasuryAccounts.MemberDonations, "Member donations"),
        (TreasuryAccounts.ItemSales, "Item sales"),
        (TreasuryAccounts.PaidWork, "Paid work"),                // runs, escorts or crafts for others
        (TreasuryAccounts.OtherMoneyIn, "Other gil in"),

        (TreasuryAccounts.GilToMembers, "Gil paid to members"),  // gil auction wins, splits, reimbursements
        (TreasuryAccounts.Supplies, "Supplies"),                 // food, medicine, entry items, gear
        (TreasuryAccounts.OtherMoneyOut, "Other gil out"),
        (TreasuryAccounts.BalanceAdjustment, "Balance adjustment"), // a recount that does not match the books
    };

    public static IEnumerable<LedgerAccount> BuildDefaultAccounts(int linkshellId)
    {
        var createdAt = DateTime.UtcNow;
        foreach (var (number, name) in Seed)
        {
            yield return new LedgerAccount
            {
                LinkshellId = linkshellId,
                AccountNumber = number,
                Name = name,
                IsCash = number == TreasuryAccounts.GilOnHand,
                IsActive = true,
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
            .OrderBy(account => account.AccountNumber)
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
            .OrderBy(account => account.AccountNumber)
            .ToListAsync(cancellationToken);
    }
}
