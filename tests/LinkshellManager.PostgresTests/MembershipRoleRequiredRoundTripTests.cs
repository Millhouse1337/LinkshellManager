using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Round 5, R5.2: MembershipRoleRequired fills any empty role id from Rank, makes Rank the role's
// exact name, and then makes AppUserLinkshells.LinkshellRoleId NOT NULL (guarded).
//
// - Up, Down and Up again from AddCampSpawnKeys on a messy roster: every empty id gets the role
//   MembershipRoleMatch (the save hook's rule) gives its Rank, Rank becomes the role's name, the
//   column refuses NULL, nothing else changes, and each step says in a NOTICE what it wrote.
// - A linkshell with no Member role leaves a member with no role: the guard skips NOT NULL and
//   says so in a WARNING that reaches the app log, instead of failing start-up.
//
// Its OWN database inside the test container, dropped at the end.
[Collection("postgres")]
public class MembershipRoleRequiredRoundTripTests
{
    // Named, not "latest", so a later migration's Down never joins this test.
    private const string Before = "20261116013920_AddCampSpawnKeys";
    private const string MembershipRoleRequired = "20261116021841_MembershipRoleRequired";

    private const int Ls = 1;
    private const int OtherLs = 2;

    // Every kind of Rank a member with an empty id can carry. Each gets the role
    // MembershipRoleMatch.Pick gives it among its linkshell's roles.
    private static readonly string?[] EmptyIdRanks =
    {
        " Officer ",   // trimmed: the Officer role, Rank becomes "Officer"
        "officer",     // capitals differ: the Member role (what permissions gave it)
        "Auctionner",  // names no role: Member
        "LEADER",      // "leader" in any capitals: the Leader role
        "leader ",     // not IsLeader (untrimmed) and no exact role: Member
        "Raider",      // a custom role of this linkshell
        "Trial",
        "",
        "   ",
        null,
    };

    private readonly PostgresFixture _pg;

    public MembershipRoleRequiredRoundTripTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task MembershipRoleRequired_GoesUpDownAndUpAgain()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "role_required_");
        try
        {
            await MigrateAsync(connectionString, Before);
            var roles = await SeedRolesAsync(connectionString, Ls, includeMember: true, extra: "Raider");
            var otherRoles = await SeedRolesAsync(connectionString, OtherLs, includeMember: true, extra: null);

            await using (var c = await OpenAsync(connectionString))
            {
                for (var i = 0; i < EmptyIdRanks.Length; i++)
                {
                    await InsertMemberAsync(c, Ls, EmptyIdRanks[i], roleId: null, dkp: 10 + i);
                }
                // An id already set, with a Rank that drifted from it: the id stays, Rank follows it.
                await InsertMemberAsync(c, Ls, "Member ", roles["Officer"], dkp: 50);
                // An id already set and in step: untouched.
                await InsertMemberAsync(c, Ls, "Leader", roles["Leader"], dkp: 60);
                // Another linkshell's member naming a role only linkshell 1 has: its own Member role.
                await InsertMemberAsync(c, OtherLs, "Raider", roleId: null, dkp: 70);
            }
            var before = await MembersAsync(connectionString);

            // What the save hook would give each empty id, worked out from the same role lists.
            var expected = before.ToDictionary(
                m => m.Id,
                m => m.RoleId ?? MembershipRoleMatch.Pick(m.Rank, Roles(m.LinkshellId == Ls ? roles : otherRoles, m.LinkshellId)).Role!.Id);

            // Up.
            var log = await MigrateAsync(connectionString, MembershipRoleRequired);
            await AssertRequiredAsync(connectionString, before, expected, roles, otherRoles);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 3 role id(s) filled from Rank.", log);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 1 membership(s) given the Leader role.", log);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 7 membership(s) given the Member role.", log);
            // " Officer ", "officer", "Auctionner", "LEADER", "leader ", "", "   ", NULL, "Member " and
            // linkshell 2's "Raider".
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 10 Rank text(s) set to the role name.", log);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: LinkshellRoleId is now NOT NULL.", log);
            Assert.DoesNotContain(log, line => line.Contains("WARNING"));
            var afterUp = await MembersAsync(connectionString);

            // Down: the column takes NULL again; the filled ids and Rank texts stay.
            await MigrateAsync(connectionString, Before);
            Assert.True(await IsNullableAsync(connectionString));
            Assert.Equal(afterUp, await MembersAsync(connectionString));

            // An older build's row saved while rolled back, with no id: the next Up fills it.
            await using (var c = await OpenAsync(connectionString))
            {
                await InsertMemberAsync(c, Ls, "Officer", roleId: null, dkp: 80);
            }
            var rolledBack = (await MembersAsync(connectionString)).Single(m => m.Dkp == 80);

            // Up again.
            log = await MigrateAsync(connectionString, MembershipRoleRequired);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 1 role id(s) filled from Rank.", log);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 0 Rank text(s) set to the role name.", log);
            expected[rolledBack.Id] = roles["Officer"];
            await AssertRequiredAsync(connectionString, before.Append(rolledBack).ToList(), expected, roles, otherRoles);

            // Run once more by hand: nothing left to write.
            await using (var c = await OpenAsync(connectionString))
            {
                var notices = new List<string>();
                c.Notice += (_, e) => notices.Add(e.Notice.MessageText);
                await ExecuteAsync(c, LinkshellManagerDiscordApp.Migrations.MembershipRoleRequired.UpSql);
                Assert.Contains("MembershipRoleRequired: 0 role id(s) filled from Rank.", notices);
                Assert.Contains("MembershipRoleRequired: 0 membership(s) given the Leader role.", notices);
                Assert.Contains("MembershipRoleRequired: 0 membership(s) given the Member role.", notices);
                Assert.Contains("MembershipRoleRequired: 0 Rank text(s) set to the role name.", notices);
            }
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    // A linkshell with no Member role: a member whose Rank names no role is left with no role, so
    // NOT NULL is skipped with a WARNING (logged as a Warning, as start-up logs it). The rest of the
    // roster is still filled, and Down and Up again work on the half-done column.
    [Fact]
    public async Task MembershipRoleRequired_SkipsNotNull_WithAWarning_WhenAMemberHasNoRole()
    {
        var connectionString = await SchemaTestDatabase.CreateAsync(_pg, "role_required_skip_");
        try
        {
            await MigrateAsync(connectionString, Before);
            var roles = await SeedRolesAsync(connectionString, Ls, includeMember: false, extra: null);
            await using (var c = await OpenAsync(connectionString))
            {
                await InsertMemberAsync(c, Ls, "Nobody", roleId: null, dkp: 1);
                await InsertMemberAsync(c, Ls, " Officer ", roleId: null, dkp: 2);
            }

            var log = new MigrationNoticeLoggingTests.CapturingLogger();
            await MigrateAsync(connectionString, MembershipRoleRequired, log);

            var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
            Assert.Equal(
                "Postgres WARNING during migration: MembershipRoleRequired: LinkshellRoleId left nullable: " +
                "1 membership(s) have no role. Run pre-check C3/C4, fix them, and a later migration sets NOT NULL.",
                warning.Message);
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("is now NOT NULL"));
            Assert.True(await IsNullableAsync(connectionString));

            var members = await MembersAsync(connectionString);
            Assert.Null(members.Single(m => m.Dkp == 1).RoleId);
            Assert.Equal("Nobody", members.Single(m => m.Dkp == 1).Rank);
            Assert.Equal(roles["Officer"], members.Single(m => m.Dkp == 2).RoleId);
            Assert.Equal("Officer", members.Single(m => m.Dkp == 2).Rank);

            // The migration is recorded as applied (so start-up does not retry it every time).
            await using (var db = SchemaTestDatabase.CreateContext(connectionString))
            {
                Assert.Contains(MembershipRoleRequired, await db.Database.GetAppliedMigrationsAsync());
                // The member with no role still loads (the EF model keeps the id optional for this),
                // and permissions give it the Member role's rights: the missing built-in role is
                // added, as a permission check always did for a linkshell missing one.
                var loaded = await db.AppUserLinkshells.AsNoTracking().Where(m => m.LinkshellId == Ls).ToListAsync();
                var nobody = loaded.Single(m => m.Rank == "Nobody");
                Assert.Null(nobody.LinkshellRoleId);
                var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
                var permissions = new LinkshellPermissions(
                    db, new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance));
                Assert.Equal(LinkshellRanks.Member, (await permissions.GetEffectiveRoleAsync(nobody, default))?.Name);
                Assert.False(await permissions.CanAsync(nobody, r => r.CanManageEvents, default));
            }

            // Down and Up again. The check above added the missing Member role, so this time the
            // member gets it and NOT NULL goes in: fixing the cause and re-running is the cure.
            await MigrateAsync(connectionString, Before);
            Assert.True(await IsNullableAsync(connectionString));
            var again = await MigrateAsync(connectionString, MembershipRoleRequired);
            Assert.Contains("Postgres NOTICE during migration: MembershipRoleRequired: 1 membership(s) given the Member role.", again);
            Assert.False(await IsNullableAsync(connectionString));
            Assert.Equal(LinkshellRanks.Member, (await MembersAsync(connectionString)).Single(m => m.Dkp == 1).Rank);
        }
        finally
        {
            await SchemaTestDatabase.DropAsync(_pg, connectionString);
        }
    }

    private static async Task AssertRequiredAsync(
        string connectionString,
        List<Member> before,
        Dictionary<int, int> expected,
        Dictionary<string, int> roles,
        Dictionary<string, int> otherRoles)
    {
        Assert.False(await IsNullableAsync(connectionString));

        var names = roles.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var kv in otherRoles)
        {
            names[kv.Value] = kv.Key;
        }

        var after = await MembersAsync(connectionString);
        Assert.Equal(before.Select(m => m.Id).OrderBy(id => id), after.Select(m => m.Id).OrderBy(id => id));
        foreach (var member in after)
        {
            var old = before.Single(m => m.Id == member.Id);
            Assert.True(expected[member.Id] == member.RoleId,
                $"Rank {Show(old.Rank)} in linkshell {old.LinkshellId}: role {member.RoleId}, the save hook gives {expected[member.Id]}");
            Assert.Equal(names[expected[member.Id]], member.Rank);
            // Nothing else on the row moved.
            Assert.Equal(old.LinkshellId, member.LinkshellId);
            Assert.Equal(old.Dkp, member.Dkp);
        }

        // The column now refuses a member with no role.
        await using var c = await OpenAsync(connectionString);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertMemberAsync(c, Ls, "Member", roleId: null, dkp: 0));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, refused.SqlState);
    }

    // The four built-in roles (or three, without Member) plus an optional custom one, inserted with
    // raw SQL: the EF model is ahead of this schema. Returns name -> id.
    private static async Task<Dictionary<string, int>> SeedRolesAsync(
        string connectionString, int linkshellId, bool includeMember, string? extra)
    {
        await using var c = await OpenAsync(connectionString);
        await ExecuteAsync(c,
            "INSERT INTO \"Linkshells\" (\"Id\", \"LinkshellName\", \"AuctionsLocked\", \"DkpRoundingIncrement\", " +
            "\"EnableAuctions\", \"EnableDkp\", \"EnableEndgame\", \"EnableEvents\", \"EnableHnmSection\", \"EnableToDs\", " +
            "\"EnableTreasury\", \"HiddenTodMonsters\", \"LootStructure\") " +
            $"VALUES ({linkshellId}, 'LS{linkshellId}', false, '0.25', false, true, false, true, true, true, false, '', 'Dkp')");

        var names = new List<string> { LinkshellRanks.Leader, LinkshellRanks.Officer };
        if (includeMember)
        {
            names.Add(LinkshellRanks.Member);
        }
        names.Add(LinkshellRanks.Trial);
        if (extra is not null)
        {
            names.Add(extra);
        }

        var ids = new Dictionary<string, int>();
        foreach (var name in names)
        {
            await using var command = c.CreateCommand();
            command.CommandText =
                "INSERT INTO \"LinkshellRoles\" (\"LinkshellId\", \"Name\", \"IsSystem\", \"SortOrder\", " +
                "\"CanAddLoot\", \"CanAuditDkp\", \"CanCustomizeLinkshell\", \"CanLockAuctions\", \"CanManageAnnouncements\", " +
                "\"CanManageAuctions\", \"CanManageEvents\", \"CanManageInventory\", \"CanManageInvites\", \"CanManageMembers\", " +
                "\"CanManageParties\", \"CanManageRoles\", \"CanManageRules\", \"CanManageTods\", \"CanManageTreasury\", " +
                "\"CanModerateLiveEvent\", \"CanSubmitTodForApproval\") " +
                $"VALUES ({linkshellId}, @name, false, 0, false, false, false, false, false, false, false, false, false, " +
                "false, false, false, false, false, false, false, false) RETURNING \"Id\"";
            command.Parameters.AddWithValue("name", name);
            ids[name] = (int)(await command.ExecuteScalarAsync())!;
        }
        return ids;
    }

    private static List<LinkshellRole> Roles(Dictionary<string, int> roles, int linkshellId) =>
        roles.Select(kv => new LinkshellRole { Id = kv.Value, LinkshellId = linkshellId, Name = kv.Key }).ToList();

    private static async Task InsertMemberAsync(NpgsqlConnection c, int linkshellId, string? rank, int? roleId, double dkp)
    {
        await using var command = c.CreateCommand();
        command.CommandText =
            "INSERT INTO \"AppUserLinkshells\" (\"LinkshellId\", \"Rank\", \"LinkshellRoleId\", \"LinkshellDkp\", " +
            "\"DkpSeedLedgerId\", \"SeededDkpEarned\", \"SeededDkpSpent\") VALUES (@ls, @rank, @role, @dkp, 0, 0, 0)";
        command.Parameters.AddWithValue("ls", linkshellId);
        command.Parameters.AddWithValue("rank", (object?)rank ?? DBNull.Value);
        command.Parameters.AddWithValue("role", (object?)roleId ?? DBNull.Value);
        command.Parameters.AddWithValue("dkp", dkp);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Member(int Id, int LinkshellId, string? Rank, int? RoleId, double? Dkp);

    private static async Task<List<Member>> MembersAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT \"Id\", \"LinkshellId\", \"Rank\", \"LinkshellRoleId\", \"LinkshellDkp\" FROM \"AppUserLinkshells\" ORDER BY \"Id\"";
        await using var reader = await command.ExecuteReaderAsync();
        var members = new List<Member>();
        while (await reader.ReadAsync())
        {
            members.Add(new Member(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }
        return members;
    }

    private static async Task<bool> IsNullableAsync(string connectionString)
    {
        await using var c = await OpenAsync(connectionString);
        await using var command = c.CreateCommand();
        command.CommandText =
            "SELECT is_nullable = 'YES' FROM information_schema.columns " +
            "WHERE table_schema = 'public' AND table_name = 'AppUserLinkshells' AND column_name = 'LinkshellRoleId'";
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    // Moves the database to exactly this migration, forwards or backwards, with the notices logged
    // the way start-up logs them. Returns the logged lines.
    private static async Task<IReadOnlyList<string>> MigrateAsync(
        string connectionString, string targetMigration, MigrationNoticeLoggingTests.CapturingLogger? log = null)
    {
        log ??= new MigrationNoticeLoggingTests.CapturingLogger();
        await using var db = SchemaTestDatabase.CreateContext(connectionString);
        await MigrationNotices.RunWithNoticesLoggedAsync(db, log, async () =>
        {
            await db.GetService<IMigrator>().MigrateAsync(targetMigration);
            return 0;
        });
        return log.Entries.Select(e => e.Message).ToList();
    }

    private static string Show(string? value) => value is null ? "NULL" : "\"" + value + "\"";

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection c, string sql)
    {
        await using var command = c.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
