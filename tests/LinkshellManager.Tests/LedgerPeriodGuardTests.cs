using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The treasury lock. Two things matter:
//   * a linkshell that never touches the feature is never locked by it (absence means unlocked)
//   * the guard lives inside the writer, so no endpoint can post around it
public class LedgerPeriodGuardTests
{
    private const int Linkshell = 1;
    private static readonly DateTime JuneEnd = new(2026, 6, 30, 23, 59, 59, DateTimeKind.Utc);
    private static readonly DateTime InJune = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime InJuly = new(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<ApplicationDbContext> SeededContextAsync(DateTime? lockedThrough = null)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell { Id = Linkshell, LinkshellName = "Test LS" });
        if (lockedThrough.HasValue)
        {
            db.LedgerPeriods.Add(new LedgerPeriod
            {
                LinkshellId = Linkshell,
                LockedThroughUtc = lockedThrough.Value,
                LockedAt = DateTime.UtcNow,
                LockedByCharacterName = "Leader",
            });
        }
        await db.SaveChangesAsync();
        await new LedgerAccountProvisioner(db).EnsureAccountsAsync(Linkshell, CancellationToken.None);
        return db;
    }

    // No row at all — the state every linkshell is in until a leader locks something. Nothing may be
    // blocked, and nothing needed backfilling for this to be true.
    [Fact]
    public async Task NoRow_MeansNothingIsLocked()
    {
        using var db = await SeededContextAsync();
        var guard = new LedgerPeriodGuard(db);

        Assert.Null(await guard.GetLockedThroughAsync(Linkshell, CancellationToken.None));
        Assert.False(await guard.IsLockedAsync(Linkshell, InJune, CancellationToken.None));
        await guard.EnsureOpenAsync(Linkshell, InJune, CancellationToken.None);
    }

    // A row whose lock has been lifted is kept rather than deleted, so the fact it was ever locked
    // survives. A null LockedThroughUtc still means unlocked.
    [Fact]
    public async Task RowWithNoLockDate_MeansNothingIsLocked()
    {
        using var db = await SeededContextAsync();
        db.LedgerPeriods.Add(new LedgerPeriod
        {
            LinkshellId = Linkshell,
            LockedThroughUtc = null,
            UnlockedAt = DateTime.UtcNow,
            UnlockReason = "Needed to fix a June typo.",
        });
        await db.SaveChangesAsync();

        await new LedgerPeriodGuard(db).EnsureOpenAsync(Linkshell, InJune, CancellationToken.None);
    }

    [Fact]
    public async Task LockedThrough_BlocksOnOrBeforeAndAllowsAfter()
    {
        using var db = await SeededContextAsync(JuneEnd);
        var guard = new LedgerPeriodGuard(db);

        Assert.True(await guard.IsLockedAsync(Linkshell, InJune, CancellationToken.None));
        Assert.True(await guard.IsLockedAsync(Linkshell, JuneEnd, CancellationToken.None));
        Assert.False(await guard.IsLockedAsync(Linkshell, InJuly, CancellationToken.None));

        var blocked = await Assert.ThrowsAsync<TreasuryPeriodLockedException>(() =>
            guard.EnsureOpenAsync(Linkshell, InJune, CancellationToken.None));
        Assert.Equal(JuneEnd, blocked.LockedThroughUtc);

        await guard.EnsureOpenAsync(Linkshell, InJuly, CancellationToken.None);
    }

    [Fact]
    public async Task Lock_IsPerLinkshell()
    {
        using var db = await SeededContextAsync(JuneEnd);
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Other LS" });
        await db.SaveChangesAsync();

        await new LedgerPeriodGuard(db).EnsureOpenAsync(2, InJune, CancellationToken.None);
    }

    // The guard is called from inside TreasuryJournalWriter, not from controllers, so there is no
    // endpoint — present or future — that can record into a locked stretch by forgetting to check.
    [Fact]
    public async Task Writer_RefusesToConfirmIntoALockedPeriod()
    {
        using var db = await SeededContextAsync(JuneEnd);
        var writer = new TreasuryJournalWriter(db, new LedgerAccountProvisioner(db), new LedgerPeriodGuard(db));

        await Assert.ThrowsAsync<TreasuryPeriodLockedException>(() =>
            writer.RecordAsync(
                Linkshell,
                new TreasuryEntryRequest(TreasuryTransactionKinds.SoldAnItem, 1_000, InJune),
                new TreasuryActor("user-1", "Millhouse"),
                CancellationToken.None));
    }

    [Fact]
    public async Task Writer_AllowsAnEntryDatedAfterTheLock()
    {
        using var db = await SeededContextAsync(JuneEnd);
        var writer = new TreasuryJournalWriter(db, new LedgerAccountProvisioner(db), new LedgerPeriodGuard(db));

        var entry = await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.SoldAnItem, 1_000, InJuly),
            new TreasuryActor("user-1", "Millhouse"),
            CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(JournalEntryStatuses.Confirmed, entry.Status);
    }

    // Reversing carries the original's date, so a locked period cannot be altered through a reversal
    // either — which is the whole point of locking it.
    [Fact]
    public async Task Writer_RefusesToReverseAnEntryInsideALockedPeriod()
    {
        using var db = await SeededContextAsync();
        var guard = new LedgerPeriodGuard(db);
        var writer = new TreasuryJournalWriter(db, new LedgerAccountProvisioner(db), guard);
        var actor = new TreasuryActor("user-1", "Millhouse");

        var entry = await writer.RecordAsync(
            Linkshell,
            new TreasuryEntryRequest(TreasuryTransactionKinds.SoldAnItem, 1_000, InJune),
            actor,
            CancellationToken.None);
        await db.SaveChangesAsync();

        db.LedgerPeriods.Add(new LedgerPeriod { LinkshellId = Linkshell, LockedThroughUtc = JuneEnd });
        await db.SaveChangesAsync();
        guard.Forget(Linkshell);

        await Assert.ThrowsAsync<TreasuryPeriodLockedException>(() =>
            writer.ReverseAsync(entry, "changed my mind", actor, CancellationToken.None));
    }

    // A leader who unlocks and immediately records must not hit the request-scoped cache.
    [Fact]
    public async Task Forget_DropsTheCachedLock()
    {
        using var db = await SeededContextAsync(JuneEnd);
        var guard = new LedgerPeriodGuard(db);

        Assert.True(await guard.IsLockedAsync(Linkshell, InJune, CancellationToken.None));

        var period = await db.LedgerPeriods.SingleAsync();
        period.LockedThroughUtc = null;
        await db.SaveChangesAsync();

        // Still cached.
        Assert.True(await guard.IsLockedAsync(Linkshell, InJune, CancellationToken.None));

        guard.Forget(Linkshell);
        Assert.False(await guard.IsLockedAsync(Linkshell, InJune, CancellationToken.None));
    }
}
