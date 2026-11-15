using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// RosterSwitch re-fills every membership's role id from Rank and every key-item tick's account from
// its membership, deletes the ticks nobody can see, and then makes the tick's account required,
// with a foreign key and one tick per account per key item.
//
// Seeded at the migration before with the messy rows an older build or Data Admin can leave behind:
// a member with no role id, one whose id points at the role they had before an older build changed
// their Rank, one whose Rank names no role but still has an id, a case-only Rank with an id, a blank
// Rank on an empty-named role, a padded Rank, a linkshell with no roles. Ticks: a normal one, one
// with no account yet, one whose membership's account changed, one on a membership with no account,
// one whose membership is gone, one whose account is gone, and two pairs of duplicates for one
// account (one pair where the newer row is on the member's current membership, which must win).
// Running the SQL again changes nothing. The new keys are checked. Down keeps every row; an old build
// then adds the rows it can, and Up again repairs them.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class RosterSwitchRoundTripTests
{
    private const string Before = "20261115031344_AccountJobsAndKeyItemAccounts";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Switch = "20261115053404_RosterSwitch";

    private const string ForeignKey = "FK_ChartMemberKeyItems_AspNetUsers_AppUserId";
    private const string UniqueIndex = "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_AppUserId";

    private readonly PostgresFixture _pg;

    public RosterSwitchRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task RosterSwitch_RepairsRoleIdsAndTicks_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "roster_switch_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(_pg.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");

        var connectionString = new NpgsqlConnectionStringBuilder(_pg.ConnectionString)
        {
            Database = databaseName,
            // No pooled connections left behind, so DROP DATABASE at the end is not blocked.
            Pooling = false,
        }.ConnectionString;

        try
        {
            await MigrateAsync(connectionString, Before);
            var seed = await SeedAsync(connectionString);

            // Up: role ids match Rank exactly; ticks have their account; unseen ticks are gone.
            await MigrateAsync(connectionString, Switch);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertRoleIdsAsync(connectionString, seed);
                await AssertTicksAsync(c, seed.ExpectedTicks);
                // Of each duplicate pair: the tick on the current membership, else the older one.
                Assert.Equal(seed.DupCurrentKept, await ScalarAsync(c,
                    "SELECT \"Id\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'DupCurrent'"));
                Assert.Equal(seed.DupGoneKept, await ScalarAsync(c,
                    "SELECT \"Id\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'DupGone'"));

                // Running the SQL again changes nothing, and says so.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, RosterSwitch.RefillRoleIdsSql);
                await ScalarAsync(c, RosterSwitch.RefillKeyItemAccountsSql);
                Assert.Contains("RosterSwitch: set 0 membership role id(s) from Rank and cleared 0 stale one(s); 4 still matched by name.", notices);
                Assert.Contains("RosterSwitch: re-linked 0 key-item tick(s) to their account; deleted 0 with no account, 0 whose account is gone and 0 duplicate(s).", notices);
                await AssertRoleIdsAsync(connectionString, seed);
                await AssertTicksAsync(c, seed.ExpectedTicks);

                // The new keys: an account is required, must exist, and ticks a key item once.
                var noAccount = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    KeyItem(seed.Main, seed.Members["officer-stale"], "Hydra", null)));
                Assert.Equal(PostgresErrorCodes.NotNullViolation, noAccount.SqlState);
                var unknownAccount = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    KeyItem(seed.Main, seed.Members["officer-stale"], "Hydra", "nobody")));
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, unknownAccount.SqlState);
                var secondTick = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    KeyItem(seed.Main, 777777, "Normal", "u-raider")));
                Assert.Equal(PostgresErrorCodes.UniqueViolation, secondTick.SqlState);
            }

            // Down: the key and the index go, the column may be NULL again; every row stays.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                await AssertTicksAsync(c, seed.ExpectedTicks);
                Assert.Equal((long)seed.Members.Count, await ScalarAsync(c, "SELECT count(*) FROM \"AppUserLinkshells\""));

                // Rows a build without these keys could write: a Rank changed without its role
                // id, a tick with no account, and a second tick of a key item the same account
                // already has (on a membership that is gone).
                await ScalarAsync(c, $"UPDATE \"AppUserLinkshells\" SET \"Rank\" = 'Officer' WHERE \"Id\" = {seed.Members["raider"]}");
                await ScalarAsync(c, KeyItem(seed.Main, 999990, "LateNoAccount", null));
                await ScalarAsync(c, KeyItem(seed.Main, 777777, "Normal", "u-raider"));

                // The same SQL repairs them and counts what it did, deletes included.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, RosterSwitch.RefillRoleIdsSql);
                await ScalarAsync(c, RosterSwitch.RefillKeyItemAccountsSql);
                Assert.Contains("RosterSwitch: set 1 membership role id(s) from Rank and cleared 0 stale one(s); 4 still matched by name.", notices);
                Assert.Contains("RosterSwitch: re-linked 0 key-item tick(s) to their account; deleted 1 with no account, 0 whose account is gone and 1 duplicate(s).", notices);
                Assert.Equal(seed.Roles[(seed.Main, "Officer")], await RoleIdOfAsync(c, seed.Members["raider"]));
                await AssertTicksAsync(c, seed.ExpectedTicks);
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'LateNoAccount'"));

                // And one more the migration itself must repair on the way up.
                await ScalarAsync(c, KeyItem(seed.Main, seed.Members["padded"], "LateNullYet", null));
            }

            // Up again: the keys come back and the last rollback row has its account.
            await MigrateAsync(connectionString, Switch);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertTicksAsync(c, seed.ExpectedTicks);
                Assert.Equal("u-pad", await ScalarAsync(c,
                    "SELECT \"AppUserId\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = 'LateNullYet'"));

                // The app now reads roles by id. For every member, messy ones included, that gives
                // exactly the role the old read by Rank gives.
                await AssertRoleByIdMatchesRoleByRankAsync(connectionString);

                // A deleted account takes its ticks with it; deleting a linkshell still works.
                await ScalarAsync(c, "DELETE FROM \"AppUserLinkshells\" WHERE \"AppUserId\" = 'u-raider'");
                await ScalarAsync(c, "DELETE FROM \"AspNetUsers\" WHERE \"Id\" = 'u-raider'");
                Assert.Equal(0L, await ScalarAsync(c,
                    "SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"AppUserId\" = 'u-raider'"));
                await ScalarAsync(c, $"DELETE FROM \"Linkshells\" WHERE \"Id\" = {seed.Main}");
                Assert.Equal(0L, await ScalarAsync(c, "SELECT count(*) FROM \"ChartMemberKeyItems\""));
            }
        }
        finally
        {
            await ExecuteAsync(_pg.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private sealed record Seed(
        int Main,
        int Other,
        int Bare,
        Dictionary<string, int> Members,
        Dictionary<string, (int Linkshell, string? Role)> ExpectedRoles,
        Dictionary<(int Linkshell, string Name), int> Roles,
        Dictionary<string, string?> ExpectedTicks,
        int DupCurrentKept,
        int DupGoneKept);

    // Linkshells and roles through EF (they did not change); accounts, memberships and ticks by SQL,
    // because the EF model already says the tick's account is required.
    private static async Task<Seed> SeedAsync(string connectionString)
    {
        int main, other, bare;
        var roles = new Dictionary<(int, string), int>();
        await using (var db = NewContext(connectionString))
        {
            var mainLinkshell = new Linkshell { LinkshellName = "Main LS" };
            var otherLinkshell = new Linkshell { LinkshellName = "Other LS" };
            var bareLinkshell = new Linkshell { LinkshellName = "Bare LS" };
            db.Linkshells.AddRange(mainLinkshell, otherLinkshell, bareLinkshell);
            await db.SaveChangesAsync();
            main = mainLinkshell.Id;
            other = otherLinkshell.Id;
            bare = bareLinkshell.Id;

            var added = LinkshellRoleDefaults.BuildDefaultRoles(main)
                .Concat(LinkshellRoleDefaults.BuildDefaultRoles(other))
                .Append(new LinkshellRole { LinkshellId = main, Name = "Raider", SortOrder = 10 })
                .Append(new LinkshellRole { LinkshellId = main, Name = "", SortOrder = 11 })
                .ToList();
            db.LinkshellRoles.AddRange(added);
            await db.SaveChangesAsync();
            foreach (var role in added)
            {
                roles[(role.LinkshellId, role.Name)] = role.Id;
            }
        }

        await using var c = await OpenAsync(connectionString);
        foreach (var account in new[] { "u-old", "u-new", "u-raider", "u-dup", "u-none", "u-null", "u-blank", "u-case", "u-pad", "u-bare", "u-other" })
        {
            await ScalarAsync(c, Account(account));
        }

        var members = new Dictionary<string, int>();
        var expected = new Dictionary<string, (int, string?)>();
        async Task Member(string key, int linkshellId, string? account, string? rank, int? roleId, string? expectedRole)
        {
            members[key] = (int)(await ScalarAsync(c, Membership(linkshellId, account, rank, roleId) + " RETURNING \"Id\""))!;
            expected[key] = (linkshellId, expectedRole);
        }

        // An older build added it, so it never got an id.
        await Member("officer-null", main, "u-null", "Officer", null, "Officer");
        // An older build changed Rank from Member to Officer and left the old id.
        await Member("officer-stale", main, "u-old", "Officer", roles[(main, "Member")], "Officer");
        // Correct already.
        await Member("raider", main, "u-raider", "Raider", roles[(main, "Raider")], "Raider");
        // Rank names no role (renamed or deleted) but the id was left behind.
        await Member("gone-role", main, "u-none", "Gone Role", roles[(main, "Raider")], null);
        // Capitals only: today's lookup finds nothing, so no id, whatever was stored.
        await Member("case-only", main, "u-case", "officer", roles[(main, "Officer")], null);
        // A blank Rank never holds the empty-named role.
        await Member("blank", main, "u-blank", "", roles[(main, "")], null);
        // Padded: today's lookup trims.
        await Member("padded", main, "u-pad", " Officer ", null, "Officer");
        // A sheet-only row with no account.
        await Member("sheet", main, null, "Member", roles[(main, "Member")], "Member");
        // Another linkshell's member, and a linkshell with no role rows.
        await Member("other", other, "u-other", "Member", null, "Member");
        await Member("bare", bare, "u-bare", "Member", null, null);
        // A member whose ticks have duplicates, and a membership Data Admin moved to account u-new.
        await Member("dup", main, "u-dup", "Member", roles[(main, "Member")], "Member");
        await Member("moved", main, "u-new", "Member", roles[(main, "Member")], "Member");

        // Ticks: key item name -> the account it must end with (null = deleted).
        var ticks = new Dictionary<string, string?>
        {
            ["Normal"] = "u-raider",
            ["NullYet"] = "u-null",
            ["AccountChanged"] = "u-new",
            ["NoAccount"] = null,
            ["MembershipGone"] = null,
            ["AccountGone"] = null,
        };
        await ScalarAsync(c, KeyItem(main, members["raider"], "Normal", "u-raider"));
        await ScalarAsync(c, KeyItem(main, members["officer-null"], "NullYet", null));
        // Ticked while the membership was u-old's; Data Admin then gave the membership to u-new.
        await ScalarAsync(c, KeyItem(main, members["moved"], "AccountChanged", "u-old"));
        await ScalarAsync(c, KeyItem(main, members["sheet"], "NoAccount", null));
        await ScalarAsync(c, KeyItem(main, 999999, "MembershipGone", null));
        await ScalarAsync(c, KeyItem(main, 999998, "AccountGone", "deleted-account"));

        // Duplicates for u-dup. "DupCurrent": the OLDER row is on a membership that is gone, the
        // newer on the current membership -- the current one stays. "DupGone": both memberships are
        // gone -- the older row stays.
        var dupOld = await InsertTickAsync(c, KeyItem(main, 999997, "DupCurrent", "u-dup"));
        var dupCurrent = await InsertTickAsync(c, KeyItem(main, members["dup"], "DupCurrent", "u-dup"));
        var goneOlder = await InsertTickAsync(c, KeyItem(main, 999996, "DupGone", "u-dup"));
        var goneNewer = await InsertTickAsync(c, KeyItem(main, 999995, "DupGone", "u-dup"));
        Assert.True(dupOld < dupCurrent && goneOlder < goneNewer);
        ticks["DupCurrent"] = "u-dup";
        ticks["DupGone"] = "u-dup";

        return new Seed(main, other, bare, members, expected, roles, ticks, dupCurrent, goneOlder);
    }

    private static async Task<int> InsertTickAsync(NpgsqlConnection c, string insert) =>
        (int)(await ScalarAsync(c, insert + " RETURNING \"Id\""))!;

    private static string Account(string id) =>
        "INSERT INTO \"AspNetUsers\" (\"Id\", \"UserName\", \"EmailConfirmed\", \"PhoneNumberConfirmed\", " +
        "\"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"IsSuperAdmin\", \"IsPlaceholder\") " +
        $"VALUES ('{id}', '{id}', false, false, false, false, 0, false, false)";

    private static string Membership(int linkshellId, string? appUserId, string? rank, int? roleId) =>
        "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"AppUserId\", \"Rank\", \"LinkshellRoleId\", " +
        "\"DiscordIdConfirmed\", \"DkpPoolLedgerFromId\", \"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\") " +
        $"VALUES ({linkshellId}, {Text(appUserId)}, {Text(rank)}, {roleId?.ToString() ?? "NULL"}, false, 0, 0, 0, 0)";

    private static string KeyItem(int linkshellId, int membershipId, string name, string? appUserId) =>
        "INSERT INTO \"ChartMemberKeyItems\" (\"LinkshellId\", \"Board\", \"KeyItemName\", \"MembershipId\", \"AppUserId\", \"SetAt\") " +
        $"VALUES ({linkshellId}, 'Dynamis', '{name}', {membershipId}, {Text(appUserId)}, now())";

    private static string Text(string? value) => value is null ? "NULL" : $"'{value.Replace("'", "''")}'";

    // Each member holds exactly the role today's name lookup finds for its Rank, and the C# the save
    // hook uses (MembershipRoleMatch) gives the same id for every row in the database.
    private static async Task AssertRoleIdsAsync(string connectionString, Seed seed)
    {
        await using var c = await OpenAsync(connectionString);
        foreach (var (key, (linkshellId, role)) in seed.ExpectedRoles)
        {
            int? expected = role is null ? null : seed.Roles[(linkshellId, role)];
            var actual = await RoleIdOfAsync(c, seed.Members[key]);
            Assert.True(actual == expected, $"Member {key}: expected role {expected?.ToString() ?? "NULL"}, got {actual?.ToString() ?? "NULL"}.");
        }

        await using var db = NewContext(connectionString);
        var roles = await db.LinkshellRoles.AsNoTracking().ToListAsync();
        var members = await db.AppUserLinkshells.AsNoTracking()
            .Select(m => new { m.Id, m.LinkshellId, m.Rank, m.LinkshellRoleId })
            .ToListAsync();
        foreach (var member in members)
        {
            Assert.Equal(MembershipRoleMatch.RoleIdFor(member.Rank, member.LinkshellId, roles), member.LinkshellRoleId);
        }
    }

    private static async Task AssertRoleByIdMatchesRoleByRankAsync(string connectionString)
    {
        await using var db = NewContext(connectionString);
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var permissions = new LinkshellPermissions(db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
        var members = await db.AppUserLinkshells.AsNoTracking().ToListAsync();
        Assert.NotEmpty(members);
        foreach (var member in members)
        {
            var byRank = await permissions.GetEffectiveRoleAsync(member.Rank, member.LinkshellId, CancellationToken.None);
            var byId = await permissions.GetEffectiveRoleAsync(member, CancellationToken.None);
            Assert.True(byRank?.Id == byId?.Id, $"Member {member.Id} ({member.Rank}): by rank {byRank?.Id}, by id {byId?.Id}.");
            Assert.Equal(
                await AuctionBidService.CanRankBidAsync(db, member.LinkshellId, member.Rank, CancellationToken.None),
                await AuctionBidService.CanMembershipBidAsync(db, member, CancellationToken.None));
        }
    }

    private static async Task<int?> RoleIdOfAsync(NpgsqlConnection c, int memberId)
    {
        var value = await ScalarAsync(c, $"SELECT \"LinkshellRoleId\" FROM \"AppUserLinkshells\" WHERE \"Id\" = {memberId}");
        return value is DBNull ? null : (int?)value;
    }

    // Each kept tick has its expected account, exactly once; each deleted one is gone.
    private static async Task AssertTicksAsync(NpgsqlConnection c, Dictionary<string, string?> expected)
    {
        foreach (var (name, account) in expected)
        {
            var count = (long)(await ScalarAsync(c,
                $"SELECT count(*) FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = '{name}'"))!;
            if (account is null)
            {
                Assert.True(count == 0, $"Tick {name} should be deleted, found {count}.");
                continue;
            }
            Assert.True(count == 1, $"Tick {name}: expected one row, found {count}.");
            Assert.Equal(account, await ScalarAsync(c,
                $"SELECT \"AppUserId\" FROM \"ChartMemberKeyItems\" WHERE \"KeyItemName\" = '{name}'"));
        }
    }

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        var required = (long)(await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name = 'ChartMemberKeyItems' AND column_name = 'AppUserId' AND is_nullable = 'NO'"))!;
        Assert.True(required == (present ? 1L : 0L), $"ChartMemberKeyItems.AppUserId required={present}");

        // CASCADE ('c'): a deleted account takes its ticks.
        var foreignKey = (long)(await ScalarAsync(c,
            $"SELECT count(*) FROM pg_constraint WHERE conname = '{ForeignKey}' AND contype = 'f' AND confdeltype = 'c'"))!;
        Assert.True(foreignKey == (present ? 1L : 0L), $"{ForeignKey} present={present}");

        var unique = (long)(await ScalarAsync(c,
            $"SELECT count(*) FROM pg_indexes WHERE indexname = '{UniqueIndex}' AND indexdef LIKE 'CREATE UNIQUE INDEX%'"))!;
        Assert.True(unique == (present ? 1L : 0L), $"{UniqueIndex} present={present}");

        // The old per-membership unique index and the plain account index stay either way.
        Assert.Equal(true, await ScalarAsync(c,
            "SELECT to_regclass('public.\"IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~\"') IS NOT NULL"));
        Assert.Equal(true, await ScalarAsync(c,
            "SELECT to_regclass('public.\"IX_ChartMemberKeyItems_AppUserId\"') IS NOT NULL"));
    }

    private static ApplicationDbContext NewContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString)
            .UseApplicationServiceProvider(PostgresFixture.IdentityServices)
            .Options;
        return new ApplicationDbContext(options);
    }

    // Moves the schema of the given database to exactly this migration, forwards or backwards.
    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        await using var db = NewContext(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
