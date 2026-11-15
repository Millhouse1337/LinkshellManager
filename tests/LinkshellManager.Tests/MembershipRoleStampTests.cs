using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// A membership's role id (AppUserLinkshell.LinkshellRoleId) is the source of its role, and Rank is
// a copy of that role's name. Every save keeps the two in step (ApplicationDbContext
// .StampMembershipRoles):
//   - an id that was set wins, and Rank becomes the role's name;
//   - otherwise the id comes from Rank: the role whose Name equals the trimmed Rank exactly, else
//     Leader for "leader" in other capitals, else Member (the role permissions already gave it);
//   - a linkshell missing its built-in roles gets them in the same save.
// The Postgres copy of these tests also checks what the two-column foreign key refuses.
public class MembershipRoleStampTests
{
    private static readonly AppUser Actor = new() { Id = "admin-1", UserName = "millhouse", IsSuperAdmin = true };

    private static ApplicationDbContext NewInMemoryContext(string name, ILoggerFactory? logs = null)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseInMemoryDatabase(name);
        if (logs is not null)
        {
            options.UseLoggerFactory(logs);
        }
        return new ApplicationDbContext(options.Options);
    }

    // Linkshell 1 has all four built-in roles plus a custom "Raider"; linkshell 2 has its own four.
    private static async Task<string> SeededAsync()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewInMemoryContext(name);
        db.Linkshells.AddRange(
            new Linkshell { Id = 1, LinkshellName = "Kraken LS" },
            new Linkshell { Id = 2, LinkshellName = "Titans" });
        db.LinkshellRoles.AddRange(
            new LinkshellRole { Id = 9, LinkshellId = 1, Name = LinkshellRanks.Leader },
            new LinkshellRole { Id = 10, LinkshellId = 1, Name = LinkshellRanks.Officer },
            new LinkshellRole { Id = 11, LinkshellId = 1, Name = LinkshellRanks.Member },
            new LinkshellRole { Id = 12, LinkshellId = 1, Name = "Raider" },
            new LinkshellRole { Id = 13, LinkshellId = 1, Name = LinkshellRanks.Trial },
            new LinkshellRole { Id = 19, LinkshellId = 2, Name = LinkshellRanks.Leader },
            new LinkshellRole { Id = 20, LinkshellId = 2, Name = LinkshellRanks.Officer },
            new LinkshellRole { Id = 21, LinkshellId = 2, Name = LinkshellRanks.Member },
            new LinkshellRole { Id = 23, LinkshellId = 2, Name = LinkshellRanks.Trial });
        await db.SaveChangesAsync();
        return name;
    }

    private static async Task<AppUserLinkshell> MemberAsync(string name, int id = 1)
    {
        await using var check = NewInMemoryContext(name);
        return await check.AppUserLinkshells.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    [Theory]
    [InlineData("Officer", 10, "Officer")]
    [InlineData("  Officer\t", 10, "Officer")]
    [InlineData("Raider", 12, "Raider")]
    [InlineData("Leader", 9, "Leader")]
    [InlineData("leader", 9, "Leader")]      // permissions treat it as the Leader (IsLeader)
    [InlineData("officer", 11, "Member")]    // capitals only: permissions already gave it Member
    [InlineData("Auctionner", 11, "Member")] // names no role
    [InlineData("", 11, "Member")]
    [InlineData("   ", 11, "Member")]
    [InlineData(null, 11, "Member")]
    public async Task NewMember_IsGivenTheRoleItsRankNames_AndRankBecomesItsName(string? rank, int expectedId, string expectedRank)
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = rank });
            await db.SaveChangesAsync();
        }

        var member = await MemberAsync(name);
        Assert.Equal(expectedId, member.LinkshellRoleId);
        Assert.Equal(expectedRank, member.Rank);
    }

    [Fact]
    public async Task NewMember_NeverGetsAnotherLinkshellsRole_EvenOnTheSynchronousSave()
    {
        var name = await SeededAsync();
        using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { Id = 1, LinkshellId = 2, Rank = LinkshellRanks.Officer },
                new AppUserLinkshell { Id = 2, LinkshellId = 2, Rank = "Raider" });
            db.SaveChanges();
        }

        Assert.Equal(20, (await MemberAsync(name, 1)).LinkshellRoleId);
        var raider = await MemberAsync(name, 2);
        Assert.Equal(21, raider.LinkshellRoleId); // linkshell 2's Member, never linkshell 1's Raider
        Assert.Equal(LinkshellRanks.Member, raider.Rank);
    }

    [Fact]
    public async Task NewMember_WithARoleId_GetsThatRolesNameAsRank()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member, LinkshellRoleId = 12 });
            await db.SaveChangesAsync();
        }

        var member = await MemberAsync(name);
        Assert.Equal(12, member.LinkshellRoleId);
        Assert.Equal("Raider", member.Rank);
    }

    [Fact]
    public async Task RankChange_MovesTheRole_AndAnUnrelatedChangeLeavesItAlone()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var member = await db.AppUserLinkshells.SingleAsync();
            Assert.Equal(11, member.LinkshellRoleId);
            member.Rank = "Raider";
            await db.SaveChangesAsync();
            Assert.Equal(12, member.LinkshellRoleId);

            member.Rank = "Gone";
            await db.SaveChangesAsync();
            Assert.Equal(11, member.LinkshellRoleId);
            Assert.Equal(LinkshellRanks.Member, member.Rank);

            member.Status = "Active";
            await db.SaveChangesAsync();
            Assert.Equal(11, member.LinkshellRoleId);
            Assert.Equal(LinkshellRanks.Member, member.Rank);
        }
    }

    // The id is the source: changing only the id moves Rank with it. When both change in one save,
    // the id wins.
    [Fact]
    public async Task IdChange_CopiesTheRolesNameIntoRank_AndWinsOverARankWrittenAlongside()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var member = await db.AppUserLinkshells.SingleAsync();
            member.LinkshellRoleId = 10;
            await db.SaveChangesAsync();
            Assert.Equal(LinkshellRanks.Officer, member.Rank);

            member.LinkshellRoleId = 12;
            member.Rank = LinkshellRanks.Trial;
            await db.SaveChangesAsync();
            Assert.Equal(12, member.LinkshellRoleId);
            Assert.Equal("Raider", member.Rank);
        }

        Assert.Equal("Raider", (await MemberAsync(name)).Rank);
    }

    // Clearing the id is not "no role": the save looks it up from Rank again.
    [Fact]
    public async Task IdCleared_IsLookedUpFromRankAgain()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = "Raider" });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var member = await db.AppUserLinkshells.SingleAsync();
            member.LinkshellRoleId = null;
            await db.SaveChangesAsync();
        }

        Assert.Equal(12, (await MemberAsync(name)).LinkshellRoleId);
    }

    // A linkshell with no roles yet (the save that adds its first member): the default roles are
    // added in that same save, and the member is given the right one.
    [Fact]
    public async Task LinkshellWithNoRoles_GetsTheDefaultRoles_AndItsLeaderTheLeaderRole()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            db.Linkshells.Add(new Linkshell { Id = 5, LinkshellName = "Fresh" });
            await db.SaveChangesAsync();
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { Id = 1, LinkshellId = 5, Rank = LinkshellRanks.Leader },
                new AppUserLinkshell { Id = 2, LinkshellId = 5, Rank = "Nobody" });
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        var roles = await check.LinkshellRoles.AsNoTracking().Where(r => r.LinkshellId == 5).ToListAsync();
        Assert.Equal(LinkshellRoleDefaults.BuildDefaultRoles(5).Select(r => r.Name).OrderBy(n => n), roles.Select(r => r.Name).OrderBy(n => n));
        var leader = await MemberAsync(name, 1);
        Assert.Equal(roles.Single(r => r.Name == LinkshellRanks.Leader).Id, leader.LinkshellRoleId);
        Assert.Equal(LinkshellRanks.Leader, leader.Rank);
        var nobody = await MemberAsync(name, 2);
        Assert.Equal(roles.Single(r => r.Name == LinkshellRanks.Member).Id, nobody.LinkshellRoleId);
        Assert.Equal(LinkshellRanks.Member, nobody.Rank);
    }

    // The linkshell and its leader in one save: the roles are linked through the navigation.
    [Fact]
    public async Task LinkshellAndLeaderInOneSave_TheLeaderGetsTheNewLeaderRole()
    {
        var name = Guid.NewGuid().ToString();
        await using (var db = NewInMemoryContext(name))
        {
            var linkshell = new Linkshell { LinkshellName = "Fresh" };
            db.Linkshells.Add(linkshell);
            db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, Rank = LinkshellRanks.Leader });
            await db.SaveChangesAsync();
        }

        await using var check = NewInMemoryContext(name);
        var member = await check.AppUserLinkshells.AsNoTracking().SingleAsync();
        var role = await check.LinkshellRoles.AsNoTracking().SingleAsync(r => r.Id == member.LinkshellRoleId);
        Assert.Equal(member.LinkshellId, role.LinkshellId);
        Assert.Equal(LinkshellRanks.Leader, role.Name);
        Assert.Equal(4, await check.LinkshellRoles.CountAsync(r => r.LinkshellId == member.LinkshellId));
    }

    // A role added in the same save as the member who names it is found among the tracked rows.
    [Fact]
    public async Task RoleAddedInTheSameSave_IsFound()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.LinkshellRoles.Add(new LinkshellRole { Id = 14, LinkshellId = 1, Name = "Scout" });
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = "Scout" });
            await db.SaveChangesAsync();
        }

        Assert.Equal(14, (await MemberAsync(name)).LinkshellRoleId);
    }

    [Fact]
    public async Task RankThatNamesNoRole_IsLoggedAsAWarning()
    {
        var name = await SeededAsync();
        var provider = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(provider));
        await using (var db = NewInMemoryContext(name, logs))
        {
            db.AppUserLinkshells.AddRange(
                new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = "Auctionner" },
                new AppUserLinkshell { Id = 2, LinkshellId = 1, Rank = LinkshellRanks.Officer });
            await db.SaveChangesAsync();
        }

        var warning = Assert.Single(provider.Entries, e => e.Category == typeof(MembershipRoleMatch).FullName);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("'Auctionner'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("Member role", warning.Message, StringComparison.Ordinal);
    }

    // Data Admin writes through the same SaveChanges. Rank is read-only there; the role id is the
    // field, and the save copies the chosen role's name into Rank.
    [Fact]
    public async Task DataAdminRoleIdEdit_MovesTheRole_AndRankFollows()
    {
        var name = await SeededAsync();
        await using (var db = NewInMemoryContext(name))
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = 1, Rank = LinkshellRanks.Member });
            await db.SaveChangesAsync();
        }

        await using (var db = NewInMemoryContext(name))
        {
            var catalog = new DataAdminCatalog(db.Model, DataAdminPolicy.Default);
            var editor = new DataAdminEditor(db, new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions())), NullLogger<DataAdminEditor>.Instance);
            var memberships = catalog.FindByClrType(typeof(AppUserLinkshell))!;
            Assert.Contains(memberships.EditableColumns, c => c.Name == nameof(AppUserLinkshell.LinkshellRoleId));
            Assert.DoesNotContain(memberships.EditableColumns, c => c.Name == nameof(AppUserLinkshell.Rank));
            Assert.Contains(memberships.DetailColumns, c => c.Name == nameof(AppUserLinkshell.Rank));

            // A posted Rank is ignored: it is not an editable column.
            var result = await editor.UpdateAsync(
                memberships,
                1,
                new Dictionary<string, string?> { [nameof(AppUserLinkshell.Rank)] = LinkshellRanks.Officer, [nameof(AppUserLinkshell.LinkshellRoleId)] = "12" },
                Actor,
                CancellationToken.None);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        }

        var member = await MemberAsync(name);
        Assert.Equal(12, member.LinkshellRoleId);
        Assert.Equal("Raider", member.Rank);
    }

    [Theory]
    [InlineData("Officer", "Officer", false)]
    [InlineData(" Officer ", "Officer", false)]
    [InlineData("leader", "Leader", true)]
    [InlineData("LEADER", "Leader", true)]
    [InlineData(" leader ", "Member", true)] // IsLeader does not trim, so permissions gave it Member
    [InlineData("officer", "Member", true)]
    [InlineData(null, "Member", true)]
    public void Pick_GivesTheRolePermissionsWouldGive(string? rank, string expected, bool fellBack)
    {
        var roles = LinkshellRoleDefaults.BuildDefaultRoles(1).ToList();

        var (role, fell) = MembershipRoleMatch.Pick(rank, roles);

        Assert.Equal(expected, role?.Name);
        Assert.Equal(fellBack, fell);
    }

    [Fact]
    public void Pick_WithNoRoleToFallBackTo_IsNull()
    {
        var (role, fell) = MembershipRoleMatch.Pick("Raider", new[] { new LinkshellRole { Name = "Scout" } });

        Assert.Null(role);
        Assert.True(fell);
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly object _gate = new();
        private readonly List<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_gate)
                {
                    return _entries.ToList();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private void Add(LogEntry entry)
        {
            lock (_gate)
            {
                _entries.Add(entry);
            }
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Add(new LogEntry(category, logLevel, formatter(state, exception)));
        }
    }
}
