using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Migrations;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// AddMembershipRoleId gives every membership the id of the role its Rank names, with a two-column
// foreign key so the role must be in the member's own linkshell.
//
// Seeded at the migration before with every kind of Rank the app could hold: exact, padded with
// spaces or tabs, different capitals only, unknown, NULL, empty and blank, and a linkshell with two
// custom roles that differ only in capitals (today's case-sensitive unique index allows that) and a
// role with an empty name (Data Admin can make one; an empty or blank Rank must never land on it),
// plus a linkshell with no role rows at all. Up must link exactly the rows today's name lookup finds and
// leave every other row NULL (so nobody gets more rights), and the SQL must agree with the C# the
// save hook uses (MembershipRoleMatch). Then the key is checked: another linkshell's role is
// refused, a held role cannot be deleted, and deleting a whole linkshell still works. Down drops it
// all; Up again fills rows an old build added in between.
//
// Its OWN database inside the test container, like DropRedundantJobRatingsIndexRoundTripTests.
// Deleted at the end.
[Collection("postgres")]
public class MembershipRoleIdBackfillTests
{
    private const string Before = "20261115003851_OneCloseOneRouteOneInvite";

    // Named, not "latest", so a later migration's Down never joins this test.
    private const string AddRoleId = "20261115011931_AddMembershipRoleId";

    private const string ForeignKey = "FK_AppUserLinkshells_LinkshellRoles_LinkshellId_LinkshellRoleId";
    private const string AlternateKey = "AK_LinkshellRoles_LinkshellId_Id";
    private const string Index = "IX_AppUserLinkshells_LinkshellId_LinkshellRoleId";

    // Every Rank shape, with the role name today's lookup finds for it (null = none, so Member).
    private static readonly (string? Rank, string? Role)[] MainRanks =
    {
        ("Officer", "Officer"),
        (" Officer ", "Officer"),
        ("\tMember\n", "Member"),
        ("officer", null),
        ("Leader", "Leader"),
        ("leader", null),
        ("Trial", "Trial"),
        ("Auctionner", null),
        (null, null),
        ("", null),
        ("   ", null),
        ("Raider", "Raider"),
        ("raider", "raider"),
        ("RAIDER", null),
    };

    private readonly PostgresFixture _pg;

    public MembershipRoleIdBackfillTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task AddMembershipRoleId_FillsOnlyExactMatches_AndGoesUpDownAndUpAgain()
    {
        var databaseName = "membership_roleid_" + Guid.NewGuid().ToString("N")[..8];
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

            // Up: exactly the rows today's lookup finds are linked; the rest stay NULL.
            await MigrateAsync(connectionString, AddRoleId);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                await AssertLinkedAsExpectedAsync(connectionString, seed);
                await AssertSqlAgreesWithCSharpAsync(connectionString);

                // Running the fill again changes nothing, and says so.
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ScalarAsync(c, AddMembershipRoleId.FillRoleIdsSql);
                var unmatched = MainRanks.Count(r => r.Role is null) + 1; // + the role-less linkshell's member
                Assert.Contains($"AddMembershipRoleId: linked 0 membership(s) to their role; {unmatched} still matched by name.", notices);

                // Another linkshell's role is refused, even one with the same name.
                var otherOfficer = seed.Roles[(seed.Other, "Officer")];
                var crossLinkshell = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    $"UPDATE \"AppUserLinkshells\" SET \"LinkshellRoleId\" = {otherOfficer} WHERE \"Id\" = {seed.Members[0]}"));
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, crossLinkshell.SqlState);

                // A role someone holds cannot be deleted; one nobody holds can.
                var raider = seed.Roles[(seed.Main, "Raider")];
                var held = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(c,
                    $"DELETE FROM \"LinkshellRoles\" WHERE \"Id\" = {raider}"));
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, held.SqlState);
                await ScalarAsync(c, $"DELETE FROM \"LinkshellRoles\" WHERE \"Id\" = {seed.Roles[(seed.Main, "Unused")]}");
            }

            // Down: the column, the keys and the index go; the memberships stay.
            await MigrateAsync(connectionString, Before);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: false);
                Assert.Equal((long)seed.Members.Count, await ScalarAsync(c, "SELECT count(*) FROM \"AppUserLinkshells\""));

                // An old build running after a rollback adds a member it cannot stamp.
                await ScalarAsync(c, Membership(seed.Main, "Officer"));
            }

            // Up again: everything is filled again, the new row included.
            await MigrateAsync(connectionString, AddRoleId);
            await using (var c = await OpenAsync(connectionString))
            {
                await AssertSchemaAsync(c, present: true);
                Assert.Equal(1L, await ScalarAsync(c,
                    $"SELECT count(*) FROM \"AppUserLinkshells\" WHERE \"Id\" > {seed.Members.Max()} " +
                    $"AND \"LinkshellRoleId\" = {seed.Roles[(seed.Main, "Officer")]}"));
                await AssertLinkedAsExpectedAsync(connectionString, seed);
                await AssertSqlAgreesWithCSharpAsync(connectionString);

                // Deleting a linkshell takes its members and roles with it in one statement.
                await ScalarAsync(c, $"DELETE FROM \"Linkshells\" WHERE \"Id\" = {seed.Main}");
                Assert.Equal(0L, await ScalarAsync(c, $"SELECT count(*) FROM \"AppUserLinkshells\" WHERE \"LinkshellId\" = {seed.Main}"));
                Assert.Equal(0L, await ScalarAsync(c, $"SELECT count(*) FROM \"LinkshellRoles\" WHERE \"LinkshellId\" = {seed.Main}"));
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
        List<int> Members,
        Dictionary<int, string?> ExpectedRoleByMember,
        Dictionary<(int Linkshell, string Name), int> Roles);

    // Three linkshells: one with the default roles plus custom roles, one with the default roles,
    // one with no roles at all. Linkshells and roles through EF (they did not change); memberships
    // by SQL, because this schema has no LinkshellRoleId column yet.
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
                .Append(new LinkshellRole { LinkshellId = main, Name = "raider", SortOrder = 11 })
                .Append(new LinkshellRole { LinkshellId = main, Name = "Unused", SortOrder = 12 })
                .Append(new LinkshellRole { LinkshellId = main, Name = "", SortOrder = 13 })
                .ToList();
            db.LinkshellRoles.AddRange(added);
            await db.SaveChangesAsync();
            foreach (var role in added)
            {
                roles[(role.LinkshellId, role.Name)] = role.Id;
            }
        }

        var members = new List<int>();
        var expected = new Dictionary<int, string?>();
        await using var c = await OpenAsync(connectionString);
        foreach (var (rank, role) in MainRanks)
        {
            var id = (int)(await ScalarAsync(c, Membership(main, rank) + " RETURNING \"Id\""))!;
            members.Add(id);
            expected[id] = role;
        }
        var otherMember = (int)(await ScalarAsync(c, Membership(other, "Member") + " RETURNING \"Id\""))!;
        members.Add(otherMember);
        expected[otherMember] = "Member";
        var bareMember = (int)(await ScalarAsync(c, Membership(bare, "Member") + " RETURNING \"Id\""))!;
        members.Add(bareMember);
        expected[bareMember] = null;

        return new Seed(main, other, bare, members, expected, roles);
    }

    private static string Membership(int linkshellId, string? rank) =>
        "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"Rank\", \"DiscordIdConfirmed\", " +
        "\"DkpPoolLedgerFromId\", \"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\") " +
        $"VALUES ({linkshellId}, {SqlText(rank)}, false, 0, 0, 0, 0)";

    private static string SqlText(string? value) =>
        value is null ? "NULL" : "E'" + value.Replace("\\", "\\\\").Replace("'", "''").Replace("\t", "\\t").Replace("\n", "\\n") + "'";

    // Each seeded member holds exactly the role the table above says, by id, in its own linkshell.
    private static async Task AssertLinkedAsExpectedAsync(string connectionString, Seed seed)
    {
        var linked = await LinkedRoleIdsAsync(connectionString);
        foreach (var (memberId, roleName) in seed.ExpectedRoleByMember)
        {
            int? expected = null;
            if (roleName is not null)
            {
                var linkshellId = await LinkshellOfAsync(connectionString, memberId);
                expected = seed.Roles[(linkshellId, roleName)];
            }
            Assert.True(linked[memberId] == expected, $"Member {memberId}: expected role {expected?.ToString() ?? "NULL"}, got {linked[memberId]?.ToString() ?? "NULL"}.");
        }
    }

    // The SQL fill and the C# the save hook uses give the same id for every row in the database.
    private static async Task AssertSqlAgreesWithCSharpAsync(string connectionString)
    {
        var linked = await LinkedRoleIdsAsync(connectionString);
        await using var db = NewContext(connectionString);
        var roles = await db.LinkshellRoles.AsNoTracking().ToListAsync();
        var members = await db.AppUserLinkshells.AsNoTracking()
            .Select(m => new { m.Id, m.LinkshellId, m.Rank })
            .ToListAsync();
        Assert.Equal(linked.Count, members.Count);
        foreach (var member in members)
        {
            Assert.Equal(MembershipRoleMatch.RoleIdFor(member.Rank, member.LinkshellId, roles), linked[member.Id]);
        }
    }

    private static async Task<Dictionary<int, int?>> LinkedRoleIdsAsync(string connectionString)
    {
        var result = new Dictionary<int, int?>();
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText = "SELECT \"Id\", \"LinkshellRoleId\" FROM \"AppUserLinkshells\"";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        }
        return result;
    }

    private static async Task<int> LinkshellOfAsync(string connectionString, int memberId)
    {
        await using var c = await OpenAsync(connectionString);
        return (int)(await ScalarAsync(c, $"SELECT \"LinkshellId\" FROM \"AppUserLinkshells\" WHERE \"Id\" = {memberId}"))!;
    }

    private static async Task AssertSchemaAsync(NpgsqlConnection c, bool present)
    {
        var column = (long)(await ScalarAsync(c,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_name = 'AppUserLinkshells' AND column_name = 'LinkshellRoleId' AND is_nullable = 'YES'"))!;
        Assert.True(column == (present ? 1L : 0L), $"LinkshellRoleId present={present}");

        // NO ACTION ('a'), so a linkshell delete that reaches both tables in one statement works.
        var foreignKey = (long)(await ScalarAsync(c,
            $"SELECT count(*) FROM pg_constraint WHERE conname = '{ForeignKey}' AND contype = 'f' AND confdeltype = 'a'"))!;
        Assert.True(foreignKey == (present ? 1L : 0L), $"{ForeignKey} present={present}");

        var alternateKey = (long)(await ScalarAsync(c,
            $"SELECT count(*) FROM pg_constraint WHERE conname = '{AlternateKey}' AND contype = 'u'"))!;
        Assert.True(alternateKey == (present ? 1L : 0L), $"{AlternateKey} present={present}");

        var index = (bool)(await ScalarAsync(c, $"SELECT to_regclass('public.\"{Index}\"') IS NOT NULL"))!;
        Assert.True(index == present, $"{Index} present={present}");
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
