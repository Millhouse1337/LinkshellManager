using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LinkshellManager.PostgresTests;

// The Characters table (AddCharacters) stays equal to the accounts' own name and job columns after
// every kind of account save: a new account, a profile-style edit, Data Admin, a placeholder made by
// an officer, and a placeholder claim. A name too long to copy is left out, and a copy that fails
// never fails the save it follows.
[Collection("postgres")]
public class CharacterSyncHookTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public CharacterSyncHookTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Row(int Id, string AppUserId, short Slot, string Name, string NormalizedName,
        int[]? JobLevels, int[]? StrongJobs, string[]? MeritJobs, int[]? CraftLevels, DateTime UpdatedAt);

    [Fact]
    public async Task NewAccount_IsCopied_TrimmedAndWithItsJobs()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser
            {
                Id = "u1", UserName = "u1",
                CharacterName = "  Bob ", JobLevels = new[] { 75, 0 }, StrongJobs = new[] { 1 }, MeritJobs = new[] { "m" }, CraftLevels = new[] { 100 },
                AltCharacterName1 = "Bobalt", Alt1JobLevels = new[] { 10 }, Alt1StrongJobs = new[] { 0 }, Alt1MeritJobs = new[] { "a" }, Alt1CraftLevels = new[] { 5 },
                AltCharacterName2 = "   ",
            });
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { ("u1", (short)0, "Bob", "bob"), ("u1", (short)1, "Bobalt", "bobalt") },
            rows.Select(r => (r.AppUserId, r.Slot, r.Name, r.NormalizedName)));
        Assert.Equal(new[] { 75, 0 }, rows[0].JobLevels);
        Assert.Equal(new[] { 1 }, rows[0].StrongJobs);
        Assert.Equal(new[] { "m" }, rows[0].MeritJobs);
        Assert.Equal(new[] { 100 }, rows[0].CraftLevels);
        Assert.Equal(new[] { 10 }, rows[1].JobLevels);
        Assert.Equal(new[] { 0 }, rows[1].StrongJobs);
        Assert.Equal(new[] { "a" }, rows[1].MeritJobs);
        Assert.Equal(new[] { 5 }, rows[1].CraftLevels);
    }

    [Fact]
    public async Task EditingTheAccount_UpdatesAddsAndDeletesSlots_AndLeavesTheRestAlone()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob", AltCharacterName1 = "Bobalt" });
            await db.SaveChangesAsync();
        }
        var before = await RowsAsync();

        // A save that changes nothing the copy reads does not run it: a row changed by hand stays.
        await _pg.CreateContext().Database.ExecuteSqlRawAsync(
            "UPDATE \"Characters\" SET \"Name\" = 'Tampered' WHERE \"AppUserId\" = 'u1' AND \"Slot\" = 1");
        await using (var db = _pg.CreateContext())
        {
            (await db.Users.SingleAsync(u => u.Id == "u1")).TimeZone = "UTC";
            await db.SaveChangesAsync();
        }
        Assert.Equal("Tampered", (await RowsAsync())[1].Name);

        // Another save got to slot 2 first: the copy updates that row instead of adding a second.
        await _pg.CreateContext().Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Characters\" (\"AppUserId\", \"Slot\", \"Name\", \"NormalizedName\", \"UpdatedAt\") " +
            "VALUES ('u1', 2, 'Old', 'old', now())");

        // A profile-style save: main jobs set, alt 1 cleared, alt 2 named. Synchronous on purpose.
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.JobLevels = new[] { 50 };
            user.AltCharacterName1 = null;
            user.AltCharacterName2 = " Bobthird ";
            db.SaveChanges();
        }

        var after = await RowsAsync();
        Assert.Equal(new[] { ((short)0, "Bob"), ((short)2, "Bobthird") }, after.Select(r => (r.Slot, r.Name)));
        Assert.Equal(new[] { 50 }, after[0].JobLevels);
        Assert.True(after[0].UpdatedAt > before[0].UpdatedAt);
        Assert.Equal(before[0].Id, after[0].Id);

        // Saving the same values again changes no row.
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.AltCharacterName2 = "Bobthird";
            await db.SaveChangesAsync();
        }
        Assert.Equal(after.Select(r => r.UpdatedAt), (await RowsAsync()).Select(r => r.UpdatedAt));
    }

    [Fact]
    public async Task NameLongerThan256_IsLeftOut_AndTheSaveStillWorks()
    {
        var longName = new string('L', 300);
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = longName, AltCharacterName1 = "Short" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(longName, (await _pg.CreateContext().Users.AsNoTracking().SingleAsync()).CharacterName);
        Assert.Equal(new[] { ((short)1, "Short") }, (await RowsAsync()).Select(r => (r.Slot, r.Name)));
    }

    // Data Admin edits an account through the same SaveChanges.
    [Fact]
    public async Task DataAdminEdit_IsCopied()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "admin", UserName = "admin", CharacterName = "Admin", IsSuperAdmin = true });
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob" });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
            var editor = new DataAdminEditor(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);
            var actor = await db.Users.SingleAsync(u => u.Id == "admin");
            var result = await editor.UpdateAsync(
                catalog.FindByClrType(typeof(AppUser))!,
                "u1",
                new Dictionary<string, string?>
                {
                    [nameof(AppUser.CharacterName)] = "Robert",
                    [nameof(AppUser.AltCharacterName1)] = "Bobalt",
                },
                actor,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        }

        Assert.Equal(new[] { ((short)0, "Robert"), ((short)1, "Bobalt") },
            (await RowsAsync()).Where(r => r.AppUserId == "u1").Select(r => (r.Slot, r.Name)));
        // Characters itself is read-only in Data Admin: it is a copy.
        await using var check = _pg.CreateContext();
        Assert.True(new DataAdminCatalog(check.Model, DataAdminPolicy.Default).FindByClrType(typeof(Character))!.IsReadOnly);
    }

    // An officer adding someone with no login makes a placeholder account (UserManager.CreateAsync).
    [Fact]
    public async Task PlaceholderCreatedByAnOfficer_IsCopied()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
        }

        string placeholderId;
        await using (var db = _pg.CreateContext())
        {
            var claims = new PlaceholderClaimService(db, new UserMergeService(db));
            var service = new ManualMemberService(db, NewUserManager(db), new PlaceholderClaimRequestService(db, claims));
            var result = await service.FindOrCreateByNameAsync(linkshellId, " Newbie ", "Newalt", null, CancellationToken.None);
            Assert.True(result.Success, result.Error);
            placeholderId = result.AppUserId!;
        }

        Assert.Equal(new[] { (placeholderId, (short)0, "Newbie"), (placeholderId, (short)1, "Newalt") },
            (await RowsAsync()).Select(r => (r.AppUserId, r.Slot, r.Name)));
    }

    // A claim copies missing names and jobs onto the real account, and deleting the placeholder
    // takes its rows with it (the foreign key cascades), so no Characters row keeps its id.
    [Fact]
    public async Task Claim_CopiesTheRealAccountAgain_AndThePlaceholderRowsGo()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser { Id = "real", UserName = "real", CharacterName = "Bob" });
            db.Users.Add(new AppUser
            {
                Id = "ph", UserName = "ph", CharacterName = "Bobby", IsPlaceholder = true,
                JobLevels = new[] { 75 }, AltCharacterName1 = "Bobalt", Alt1JobLevels = new[] { 30 },
            });
            db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, AppUserId = "ph", CharacterName = "Bobby" });
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
        }
        Assert.Equal(3, (await RowsAsync()).Count);

        await using (var db = _pg.CreateContext())
        {
            var result = await new PlaceholderClaimService(db, new UserMergeService(db))
                .ClaimPlaceholderAsync("ph", "real", linkshellId, CancellationToken.None);
            Assert.True(result.Success, result.Error);
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { ("real", (short)0, "Bob"), ("real", (short)1, "Bobalt") },
            rows.Select(r => (r.AppUserId, r.Slot, r.Name)));
        Assert.Equal(new[] { 75 }, rows[0].JobLevels);
        // The account's alt 1 jobs are not part of the claim's carry-over, so the copy has none.
        Assert.Null(rows[1].JobLevels);
        await AssertEqualToAccountsAsync();
    }

    // The copy runs after the save. Without a transaction the save is already in; inside the
    // caller's transaction it runs in a savepoint, so a failure leaves the transaction usable and
    // the caller's later work and commit go through.
    [Fact]
    public async Task AFailedCopy_NeverFailsTheSave_WithOrWithoutATransaction()
    {
        var breaker = new BreakCharacterCopy();

        await using (var db = _pg.CreateContext(breaker))
        {
            db.Users.Add(new AppUser { Id = "plain", UserName = "plain", CharacterName = "Plain" });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext(breaker))
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.Users.Add(new AppUser { Id = "async-tx", UserName = "async-tx", CharacterName = "AsyncTx" });
            await db.SaveChangesAsync();
            db.Linkshells.Add(new Linkshell { LinkshellName = "After async" });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        await using (var db = _pg.CreateContext(breaker))
        {
            using var tx = db.Database.BeginTransaction();
            db.Users.Add(new AppUser { Id = "sync-tx", UserName = "sync-tx", CharacterName = "SyncTx" });
            db.SaveChanges();
            db.Linkshells.Add(new Linkshell { LinkshellName = "After sync" });
            db.SaveChanges();
            tx.Commit();
        }

        Assert.Equal(3, breaker.Broken);
        await using var check = _pg.CreateContext();
        Assert.Equal(new[] { "async-tx", "plain", "sync-tx" },
            (await check.Users.Select(u => u.Id).ToListAsync()).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, await check.Linkshells.CountAsync());
        Assert.Empty(await RowsAsync());
    }

    // Turns the copy into a statement Postgres rejects (division by zero, still using both
    // parameters), so the failure is a real database error that would abort an open transaction.
    private sealed class BreakCharacterCopy : DbCommandInterceptor
    {
        public int Broken { get; private set; }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Break(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Break(command);
            return ValueTask.FromResult(result);
        }

        private void Break(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO \"Characters\"", StringComparison.Ordinal))
            {
                command.CommandText = "SELECT 1 / (array_length(@ids, 1) - array_length(@ids, 1)) WHERE @now IS NOT NULL";
                Broken++;
            }
        }
    }

    private static UserManager<AppUser> NewUserManager(ApplicationDbContext db) => new(
        new UserStore<AppUser>(db),
        Options.Create(new IdentityOptions()),
        new PasswordHasher<AppUser>(),
        Array.Empty<IUserValidator<AppUser>>(),
        Array.Empty<IPasswordValidator<AppUser>>(),
        new UpperInvariantLookupNormalizer(),
        new IdentityErrorDescriber(),
        null!,
        NullLogger<UserManager<AppUser>>.Instance);

    private async Task<List<Row>> RowsAsync()
    {
        await using var db = _pg.CreateContext();
        return (await db.Characters.AsNoTracking().ToListAsync())
            .OrderBy(c => c.AppUserId, StringComparer.Ordinal).ThenBy(c => c.Slot)
            .Select(c => new Row(c.Id, c.AppUserId, c.Slot, c.Name, c.NormalizedName, c.JobLevels, c.StrongJobs, c.MeritJobs, c.CraftLevels, c.UpdatedAt))
            .ToList();
    }

    // Every account's Characters rows are exactly what the C# Trim and the 256 limit give.
    private async Task AssertEqualToAccountsAsync()
    {
        await using var db = _pg.CreateContext();
        var expected = new List<(string, short, string, string)>();
        foreach (var user in (await db.Users.AsNoTracking().ToListAsync()).OrderBy(u => u.Id, StringComparer.Ordinal))
        {
            var names = new[] { user.CharacterName, user.AltCharacterName1, user.AltCharacterName2 };
            for (short slot = 0; slot < 3; slot++)
            {
                var name = names[slot]?.Trim();
                if (!string.IsNullOrEmpty(name) && name.EnumerateRunes().Count() <= Character.MaxNameLength)
                {
                    expected.Add((user.Id, slot, name, name.ToLowerInvariant()));
                }
            }
        }
        Assert.Equal(expected, (await RowsAsync()).Select(r => (r.AppUserId, r.Slot, r.Name, r.NormalizedName)));
    }
}
