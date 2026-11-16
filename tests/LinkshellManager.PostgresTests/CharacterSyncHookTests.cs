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

    // The addon posting an alt's job levels (POST character/jobs, Round 5) writes the account's
    // Alt1JobLevels, and the save copies them to that alt's row only.
    [Fact]
    public async Task AddonAltJobLevels_AreCopiedToTheAltsRow()
    {
        int linkshellId;
        await using (var db = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "LS" };
            db.Linkshells.Add(linkshell);
            db.Users.Add(new AppUser
            {
                Id = "u1", UserName = "u1", CharacterName = "Bob", JobLevels = new[] { 0, 1 },
                AltCharacterName1 = "Bobalt", AltCharacterName2 = "Bobtwo", Alt2JobLevels = new[] { 0, 22 },
            });
            await db.SaveChangesAsync();
            linkshellId = linkshell.Id;
            db.AppUserLinkshells.Add(new AppUserLinkshell { LinkshellId = linkshellId, AppUserId = "u1", CharacterName = "Bob" });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            http.Items[LinkshellManagerDiscordApp.Authorization.AddonApiAuthAttribute.TokenContextKey] = new AddonApiToken
            {
                Id = 1, LinkshellId = linkshellId, IssuedToAppUserId = "u1", TokenPrefix = "att_1", TokenHash = "hash-1",
                CreatedAt = DateTime.UtcNow,
            };
            http.Items[LinkshellManagerDiscordApp.Authorization.AddonApiAuthAttribute.LinkshellIdContextKey] = linkshellId;
            var controller = new LinkshellManagerDiscordApp.Controllers.AddonApiController(
                db, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<LinkshellManagerDiscordApp.Controllers.AddonApiController>.Instance)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http },
            };

            var result = await controller.PostCharacterJobsAsync(
                new LinkshellManagerDiscordApp.Controllers.AddonApiController.AddonPostJobLevelsRequest(" BOBALT ", new[] { 0, 75, 37 }),
                CancellationToken.None);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { "Bob", "Bobalt", "Bobtwo" }, rows.Select(r => r.Name));
        Assert.Equal(new[] { 0, 1 }, rows[0].JobLevels);
        Assert.Equal(new[] { 0, 75, 37 }, rows[1].JobLevels);
        Assert.Equal(new[] { 0, 22 }, rows[2].JobLevels);
        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(new[] { 0, 75, 37 }, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == "u1")).Alt1JobLevels);
        }
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
        // The placeholder's main takes the first empty alt slot, its alt the next (owner decision 2).
        Assert.Equal(new[] { ("real", (short)0, "Bob"), ("real", (short)1, "Bobby"), ("real", (short)2, "Bobalt") },
            rows.Select(r => (r.AppUserId, r.Slot, r.Name)));
        Assert.Equal(new[] { 75 }, rows[0].JobLevels);
        // Alt jobs are not part of the claim's carry-over.
        Assert.Null(rows[1].JobLevels);
        Assert.Null(rows[2].JobLevels);
        await AssertEqualToAccountsAsync();
    }

    // Since CharactersSwitch an account holds each name once (IX_Characters_AppUserId_NormalizedName).
    // Swapping the main and an alt in one save must still copy: the renamed rows are replaced, not
    // updated one by one (which would hit the index half-way).
    [Fact]
    public async Task SwappingMainAndAlt_InOneSave_IsCopied()
    {
        await using (var db = _pg.CreateContext())
        {
            await PostgresFixture.AssertIndexExistsAsync(db, "IX_Characters_AppUserId_NormalizedName");
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Ann", AltCharacterName1 = "Bea", AltCharacterName2 = "Cid" });
            await db.SaveChangesAsync();
        }
        var cid = (await RowsAsync()).Single(r => r.Slot == 2);

        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.CharacterName = "bea";
            user.AltCharacterName1 = "ANN";
            await db.SaveChangesAsync();
        }

        var rows = await RowsAsync();
        Assert.Equal(new[] { ((short)0, "bea"), ((short)1, "ANN"), ((short)2, "Cid") }, rows.Select(r => (r.Slot, r.Name)));
        // The untouched slot keeps its row.
        Assert.Equal(cid, rows[2]);
        await AssertEqualToAccountsAsync();
    }

    // A slot that repeats an earlier slot's name in the same account has no row (the earlier slot
    // already gives the account that name), and the save still works. Data Admin can save such an
    // account even though the profile refuses it.
    [Fact]
    public async Task ANameRepeatedInsideOneAccount_KeepsItsFirstSlotOnly()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser
            {
                Id = "u1", UserName = "u1", CharacterName = "Twin", AltCharacterName1 = " twin ", Alt1JobLevels = new[] { 5 },
                AltCharacterName2 = "Other",
            });
            await db.SaveChangesAsync();
        }
        Assert.Equal(new[] { ((short)0, "Twin"), ((short)2, "Other") }, (await RowsAsync()).Select(r => (r.Slot, r.Name)));

        // The main moves to alt 2's name: now alt 2 is the repeat, and alt 1 gets its row.
        await using (var db = _pg.CreateContext())
        {
            var user = await db.Users.SingleAsync(u => u.Id == "u1");
            user.CharacterName = "OTHER";
            db.SaveChanges();
        }
        var rows = await RowsAsync();
        Assert.Equal(new[] { ((short)0, "OTHER"), ((short)1, "twin") }, rows.Select(r => (r.Slot, r.Name)));
        Assert.Equal(new[] { 5 }, rows[1].JobLevels);
        await AssertEqualToAccountsAsync();
    }

    // Two copies of one account run one after the other (CharacterSync.SyncSql's advisory lock), and
    // the later one reads the account as saved by then. Here another connection holds the lock, the
    // save goes in and its copy waits; meanwhile the account is changed again (a save whose own copy
    // never ran); when the lock is let go the waiting copy writes that newest name, not its own.
    [Fact]
    public async Task ACopyThatWaitsForTheLock_WritesTheAccountAsSavedByThen()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob" });
            await db.SaveChangesAsync();
        }

        await using var holder = new Npgsql.NpgsqlConnection(_pg.ConnectionString);
        await holder.OpenAsync();
        await using var hold = await holder.BeginTransactionAsync();
        await using (var take = new Npgsql.NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock({CharacterSync.LockClassId}, hashtext('u1'))", holder, hold))
        {
            await take.ExecuteNonQueryAsync();
        }

        var save = Task.Run(async () =>
        {
            await using var db = _pg.CreateContext();
            (await db.Users.SingleAsync(u => u.Id == "u1")).CharacterName = "Rob";
            await db.SaveChangesAsync();
        });

        // Wait until the copy is queued behind the lock (the save itself is in by then).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        await using (var watcher = new Npgsql.NpgsqlConnection(_pg.ConnectionString))
        {
            await watcher.OpenAsync();
            while (true)
            {
                await using var waiting = new Npgsql.NpgsqlCommand(
                    "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted", watcher);
                if ((long)(await waiting.ExecuteScalarAsync())! > 0)
                {
                    break;
                }
                Assert.False(save.IsCompleted, "The copy did not wait for the lock.");
                Assert.True(DateTime.UtcNow < deadline, "The copy never queued behind the lock.");
                await Task.Delay(20);
            }
            Assert.Equal("Rob", (await _pg.CreateContext().Users.AsNoTracking().SingleAsync()).CharacterName);
            await using var later = new Npgsql.NpgsqlCommand(
                "UPDATE \"AspNetUsers\" SET \"CharacterName\" = 'Late' WHERE \"Id\" = 'u1'", watcher);
            await later.ExecuteNonQueryAsync();
        }

        await hold.CommitAsync();
        await save.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { ((short)0, "Late") }, (await RowsAsync()).Select(r => (r.Slot, r.Name)));
    }

    // Start-up runs the copy for every account, so rows a failed copy left behind are repaired.
    [Fact]
    public async Task ResyncAll_RepairsEveryAccount_AndChangesNothingWhenRight()
    {
        await using (var db = _pg.CreateContext())
        {
            db.Users.Add(new AppUser { Id = "u1", UserName = "u1", CharacterName = "Bob", AltCharacterName1 = "Bobalt" });
            db.Users.Add(new AppUser { Id = "u2", UserName = "u2", CharacterName = "Ann" });
            await db.SaveChangesAsync();
        }
        await using (var db = _pg.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"AspNetUsers\" SET \"CharacterName\" = 'Robert', \"AltCharacterName1\" = NULL WHERE \"Id\" = 'u1'");
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Characters\" WHERE \"AppUserId\" = 'u2'");
        }

        await using (var db = _pg.CreateContext())
        {
            Assert.Equal(2, await CharacterSync.ResyncAllAsync(db, CancellationToken.None));
        }
        await AssertEqualToAccountsAsync();
        var repaired = await RowsAsync();

        await using (var db = _pg.CreateContext())
        {
            await CharacterSync.ResyncAllAsync(db, CancellationToken.None);
        }
        Assert.Equal(repaired, await RowsAsync());
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

    // Every account's Characters rows are exactly what the C# Trim, the 256 limit and (since
    // CharactersSwitch) the one-name-per-account rule give.
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
                if (!string.IsNullOrEmpty(name) && name.EnumerateRunes().Count() <= Character.MaxNameLength
                    && !expected.Any(row => row.Item1 == user.Id && row.Item4 == name.ToLowerInvariant()))
                {
                    expected.Add((user.Id, slot, name, name.ToLowerInvariant()));
                }
            }
            Assert.Equal(
                CharacterSync.DesiredRows(user).Select(row => (user.Id, row.Slot, row.Name, row.NormalizedName)),
                expected.Where(row => row.Item1 == user.Id));
        }
        Assert.Equal(expected, (await RowsAsync()).Select(r => (r.AppUserId, r.Slot, r.Name, r.NormalizedName)));
    }
}
