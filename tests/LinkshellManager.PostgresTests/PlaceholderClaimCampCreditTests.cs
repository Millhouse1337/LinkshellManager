using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The one-payout-row-per-account index (OneCampCreditPerAccount) meets the placeholder claim. A
// person on the real account's main AND on an imported placeholder (their alt, as the old DKP sheet
// named it) can both be paid by one review card -- two accounts, two rows. The claim moves the
// placeholder's rows onto the real account, which would give that card two rows for one account; the
// index refused that and the whole claim failed. The claim now folds the pair into one row first.
[Collection("postgres")]
public class PlaceholderClaimCampCreditTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public PlaceholderClaimCampCreditTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static DkpLedgerEntry Row(int linkshellId, string appUserId, int cardId, string entryType, double amount) => new()
    {
        LinkshellId = linkshellId,
        AppUserId = appUserId,
        SourceWindowEventId = cardId,
        EntryType = entryType,
        Amount = amount,
        OccurredAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Theory]
    [InlineData(true)]   // the placeholder's payout row is the older one
    [InlineData(false)]  // the real account's payout row is the older one
    public async Task ClaimingAPlaceholderPaidOnTheSameCard_FoldsThePayoutsIntoOneRow(bool placeholderPaidFirst)
    {
        int linkshellId, olderId, newerId, auditId, otherCardId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "real", UserName = "real", CharacterName = "Bob" });
            db.Users.Add(new AppUser { Id = "ph", UserName = "ph", CharacterName = "Bobalt", IsPlaceholder = true });
            // Same balance on both memberships: the claim's merge overwrites the real balance with
            // the placeholder's (P2.12 changes that), and this test is only about the ledger rows.
            db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, AppUserId = "real", CharacterName = "Bob", LinkshellDkp = 10 });
            db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, AppUserId = "ph", CharacterName = "Bobalt", LinkshellDkp = 10 });
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;

            // Card 1 paid both accounts (Bob 10 on the real one, Bobalt 6 on the placeholder).
            var first = Row(linkshellId, placeholderPaidFirst ? "ph" : "real", 1, "SnapshotEarned", placeholderPaidFirst ? 6 : 10);
            db.DkpLedgerEntries.Add(first);
            await db.SaveChangesAsync();
            var second = Row(linkshellId, placeholderPaidFirst ? "real" : "ph", 1, "SnapshotEarned", placeholderPaidFirst ? 10 : 6);
            db.DkpLedgerEntries.Add(second);
            await db.SaveChangesAsync();

            // An officer corrected the NEWER row; the correction must end up on the kept row.
            var audit = Row(linkshellId, second.AppUserId!, 1, "AuditAdjustment", -1);
            audit.AuditRelatedLedgerEntryId = second.Id;
            // The placeholder alone was paid by card 2: a plain repoint, nothing to fold.
            var other = Row(linkshellId, "ph", 2, "SnapshotEarned", 2);
            db.DkpLedgerEntries.AddRange(audit, other);
            await db.SaveChangesAsync();

            olderId = first.Id;
            newerId = second.Id;
            auditId = audit.Id;
            otherCardId = other.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            var result = await new PlaceholderClaimService(db, null!)
                .ClaimPlaceholderAsync("ph", "real", linkshellId, CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        await using (var db = _pg.CreateContext())
        {
            var rows = await db.DkpLedgerEntries.AsNoTracking().OrderBy(e => e.Id).ToListAsync();

            // Nothing is left on the placeholder, and the account's ledger total is both accounts'
            // rows added up (10 + 6 - 1 + 2), exactly what a plain repoint would have given.
            Assert.DoesNotContain(rows, r => r.AppUserId == "ph");
            Assert.Equal(17d, rows.Where(r => r.AppUserId == "real").Sum(r => r.Amount));

            // Card 1: one payout row, the older one, holding both payouts; the audit points at it.
            var card1 = Assert.Single(rows, r => r.SourceWindowEventId == 1 && r.EntryType == "SnapshotEarned");
            Assert.Equal(olderId, card1.Id);
            Assert.Equal(16d, card1.Amount);
            Assert.DoesNotContain(rows, r => r.Id == newerId);
            Assert.Equal(olderId, rows.Single(r => r.Id == auditId).AuditRelatedLedgerEntryId);

            // Card 2 simply moved.
            Assert.Equal("real", rows.Single(r => r.Id == otherCardId).AppUserId);
            Assert.Null(await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == "ph"));
        }
    }
}
