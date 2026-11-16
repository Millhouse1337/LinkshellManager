using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// LinkshellPermissions is the one copy of "may this member do X in this linkshell?".
// These tests pin its rule order:
//   no membership -> false; admin override -> true; Leader -> true; role flag, with an
//   unknown rank falling back to the Member role.
// Later steps point the addon and Discord copies at this service; if one of these tests
// has to change, a permission changed for every surface at once — that must be on purpose.
public class LinkshellPermissionsTests
{
    private const int Ls = 1;
    private const int OtherLs = 2;
    private const string AdminId = "admin-1";
    private const string UserId = "user-1";

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<LinkshellPermissions> NewServiceAsync(ApplicationDbContext db, bool overrideOn = false)
    {
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        if (overrideOn)
        {
            await settings.SetAdminOverrideEnabledAsync(true);
        }
        var adminOverride = new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance);
        return new LinkshellPermissions(db, adminOverride);
    }

    private static async Task<AppUserLinkshell> SeedMemberAsync(
        ApplicationDbContext db, string userId, string? rank, int linkshellId = Ls, bool superAdmin = false)
    {
        if (!await db.Linkshells.AnyAsync(l => l.Id == linkshellId))
        {
            db.Linkshells.Add(new Linkshell { Id = linkshellId, LinkshellName = $"LS{linkshellId}", LootStructure = "Dkp" });
        }
        if (await db.Users.FindAsync(userId) is null)
        {
            db.Users.Add(new AppUser { Id = userId, UserName = userId, IsSuperAdmin = superAdmin });
        }
        var membership = new AppUserLinkshell { AppUserId = userId, LinkshellId = linkshellId, Rank = rank };
        db.AppUserLinkshells.Add(membership);
        await db.SaveChangesAsync();
        return membership;
    }

    [Fact]
    public async Task NullMembership_IsDenied_EvenWithOverrideOn()
    {
        await using var db = NewDb();
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.False(await permissions.CanAsync((AppUserLinkshell?)null, r => true, CancellationToken.None));
    }

    [Fact]
    public async Task ByIds_NonMemberAndBlankId_AreDenied()
    {
        await using var db = NewDb();
        // The super admin is a member of another linkshell only: the override must not reach
        // a linkshell they never joined.
        await SeedMemberAsync(db, AdminId, LinkshellRanks.Leader, linkshellId: OtherLs, superAdmin: true);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        await db.SaveChangesAsync();
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.False(await permissions.CanAsync(AdminId, Ls, r => true, CancellationToken.None));
        Assert.False(await permissions.CanAsync((string?)null, Ls, r => true, CancellationToken.None));
        Assert.False(await permissions.CanAsync("  ", Ls, r => true, CancellationToken.None));
        Assert.True(await permissions.CanAsync(AdminId, OtherLs, r => r.CanManageTreasury, CancellationToken.None));
    }

    [Fact]
    public async Task AdminOverride_GrantsEverything_ToAMemberSuperAdmin()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, AdminId, LinkshellRanks.Member, superAdmin: true);
        var permissions = await NewServiceAsync(db, overrideOn: true);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => false, CancellationToken.None));
    }

    [Fact]
    public async Task AdminOverrideOff_SuperAdminGetsOnlyTheirRank()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, AdminId, LinkshellRanks.Member, superAdmin: true);
        var permissions = await NewServiceAsync(db, overrideOn: false);

        Assert.False(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
    }

    [Fact]
    public async Task Leader_AlwaysAllowed_EvenWhenTheLeaderRoleRowSaysNo()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, "leader"); // rank match is case-insensitive here
        var permissions = await NewServiceAsync(db);
        // A mis-edited Leader role row with every flag off.
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = LinkshellRanks.Leader, IsSystem = true });
        await db.SaveChangesAsync();

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageRoles, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => false, CancellationToken.None));
    }

    [Fact]
    public async Task CustomRole_UsesItsOwnFlags()
    {
        await using var db = NewDb();
        // The role first: a rank saved before its role exists is given the Member role.
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = "Raid Lead", CanManageEvents = true });
        await db.SaveChangesAsync();
        var membership = await SeedMemberAsync(db, UserId, "Raid Lead");
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanAddLoot, CancellationToken.None));
        // The custom role does not inherit Member's flags: it is used as-is.
        Assert.False(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownRank_FallsBackToTheMemberRole()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, "Deleted Role");
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));

        // The save gave it the Member role (and Rank says so); the id is what is read.
        var role = await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None);
        Assert.Equal(LinkshellRanks.Member, role?.Name);
        Assert.Equal(LinkshellRanks.Member, membership.Rank);
    }

    [Fact]
    public async Task BlankRank_IsTreatedAsMember()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, rank: null);
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanBid, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageMembers, CancellationToken.None));
        Assert.Equal(LinkshellRanks.Member, (await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None))?.Name);
    }

    [Fact]
    public async Task Officer_UsesTheSeededOfficerDefaults()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, LinkshellRanks.Officer);
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
    }

    // ---- R5.2: the role is read by id only (AppUserLinkshell.LinkshellRoleId) -------------------

    // Permission parity. For every kind of Rank a writer can save, the id the save hook picks gives
    // exactly the rights the old name path gave that Rank (OldRoleByRank and OldCanRankBid below are
    // copies of the code R5.2 deleted), on every surface: LinkshellPermissions, the overview and
    // the bid check. So reading the id only changed nobody's rights. The hook also makes Rank that
    // role's exact name ("officer" becomes "Member", which is what it already got).
    [Theory]
    [InlineData("Leader")]
    [InlineData("leader")]
    [InlineData("LEADER")]
    [InlineData("Officer")]
    [InlineData(" Officer ")]
    [InlineData("officer")]
    [InlineData("Member")]
    [InlineData("Trial")]
    [InlineData("Trial ")]
    [InlineData("Raid Lead")]
    [InlineData(" Raid Lead")]
    [InlineData("raid lead")]
    [InlineData("Deleted Role")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task RoleById_GivesTheSameRightsAsTheOldNamePath(string? rank)
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls));
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = "Raid Lead", CanManageEvents = true, CanAddLoot = true, CanBid = false });
        await db.SaveChangesAsync();
        var roles = await db.LinkshellRoles.AsNoTracking().Where(r => r.LinkshellId == Ls).ToListAsync();
        // Worked out from the Rank as written, before the save rewrites it.
        var oldRole = OldRoleByRank(rank, roles);
        var oldCanBid = OldCanRankBid(rank, roles);

        var membership = await SeedMemberAsync(db, UserId, rank);
        var permissions = await NewServiceAsync(db);

        var byId = await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None);
        Assert.NotNull(byId);
        Assert.Equal(LinkshellRanks.IsLeader(rank), LinkshellRanks.IsLeader(membership.Rank));
        if (!LinkshellRanks.IsLeader(rank))
        {
            Assert.Equal(oldRole.Id, byId!.Id);
            Assert.Equal(oldRole.Name, membership.Rank);
        }

        var byIdMap = new Dictionary<int, Dictionary<int, LinkshellRole>> { [Ls] = roles.ToDictionary(r => r.Id) };
        var overview = LinkshellManagerDiscordApp.Controllers.ActivityMappers.ResolvePermissionsFor(
            Ls, membership.LinkshellRoleId, byIdMap, adminOverrideActive: false);
        Assert.NotNull(overview);
        foreach (var flag in AllFlags)
        {
            var before = LinkshellRanks.IsLeader(rank) || flag(oldRole);
            Assert.Equal(before, await permissions.CanAsync(membership, flag, CancellationToken.None));
        }
        // The overview shows the role the id names (the Leader rule is applied on top by its
        // callers, as AnswersJoinRequests does).
        if (!LinkshellRanks.IsLeader(rank))
        {
            Assert.Equal(oldRole.CanManageInvites, overview!.CanManageInvites);
            Assert.Equal(oldRole.CanManageEvents, overview.CanManageEvents);
            Assert.Equal(oldRole.CanBid, overview.CanBid);
        }
        Assert.Equal(
            LinkshellRanks.IsLeader(rank) || oldRole.CanManageInvites,
            LinkshellManagerDiscordApp.Controllers.ActivityMappers.AnswersJoinRequests(
                Ls, membership.Rank, membership.LinkshellRoleId, byIdMap, adminOverrideActive: false));

        // And the bid check gives what the rank gave before the save. (Bidding has no Leader
        // rule: it reads the role's CanBid, and the Leader role can bid.)
        Assert.Equal(oldCanBid, await AuctionBidService.CanMembershipBidAsync(db, membership, CancellationToken.None));
    }

    // A messy roster in one linkshell, all saved together: every member keeps exactly the rights the
    // old name path gave their Rank, and nobody's id points outside the linkshell.
    [Fact]
    public async Task MessyRoster_EveryMemberKeepsTheirRights()
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = OtherLs, LinkshellName = "LS2", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls));
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(OtherLs));
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = Ls, Name = "Raid Lead", CanManageEvents = true, CanBid = false });
        db.LinkshellRoles.Add(new LinkshellRole { LinkshellId = OtherLs, Name = "Banker", CanManageTreasury = true });
        await db.SaveChangesAsync();
        var allRoles = await db.LinkshellRoles.AsNoTracking().ToListAsync();

        var ranks = new (int Linkshell, string? Rank)[]
        {
            (Ls, "Leader"), (Ls, "Officer"), (Ls, " Officer"), (Ls, "officer"), (Ls, "Raid Lead"), (Ls, "Banker"),
            (Ls, "Trial"), (Ls, "Auctionner"), (Ls, null), (Ls, ""),
            (OtherLs, "LEADER"), (OtherLs, "Banker"), (OtherLs, "Raid Lead"), (OtherLs, "Member"), (OtherLs, " Trial "),
        };
        var members = new List<(AppUserLinkshell Membership, string? Rank)>();
        for (var i = 0; i < ranks.Length; i++)
        {
            db.Users.Add(new AppUser { Id = $"u{i}", UserName = $"u{i}" });
            var membership = new AppUserLinkshell { AppUserId = $"u{i}", LinkshellId = ranks[i].Linkshell, Rank = ranks[i].Rank };
            db.AppUserLinkshells.Add(membership);
            members.Add((membership, ranks[i].Rank));
        }
        await db.SaveChangesAsync();
        var permissions = await NewServiceAsync(db);

        foreach (var (membership, rank) in members)
        {
            var roles = allRoles.Where(r => r.LinkshellId == membership.LinkshellId).ToList();
            var oldRole = OldRoleByRank(rank, roles);
            Assert.Contains(roles, r => r.Id == membership.LinkshellRoleId);
            foreach (var flag in AllFlags)
            {
                Assert.True(
                    (LinkshellRanks.IsLeader(rank) || flag(oldRole)) == await permissions.CanAsync(membership, flag, CancellationToken.None),
                    $"Rank {rank ?? "NULL"} in linkshell {membership.LinkshellId}");
            }
            Assert.Equal(OldCanRankBid(rank, roles), await AuctionBidService.CanMembershipBidAsync(db, membership, CancellationToken.None));
        }
    }

    // The Rank text no longer picks the role. A member read with a Rank that does not match their
    // id (only possible in memory, or by a hand edit in SQL: every save re-stamps Rank) gets the
    // rights of the role their id names. "Leader" is the one exception: the Leader rule reads Rank.
    [Fact]
    public async Task StaleRankText_DoesNotPickTheRole()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, LinkshellRanks.Member);
        db.ChangeTracker.Clear();
        membership.Rank = LinkshellRanks.Officer;
        var permissions = await NewServiceAsync(db);

        Assert.False(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.Equal(LinkshellRanks.Member, (await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None))?.Name);
    }

    // No id, or an id that is none of the linkshell's roles (should never happen: the column is
    // NOT NULL and the foreign key keeps it in the linkshell): Member rights, never the rights the
    // Rank text names, and a warning in the log. The bid check does the same.
    [Theory]
    [InlineData(null)]
    [InlineData(9999)]
    public async Task MissingOrUnknownRoleId_GetsMemberRights_AndIsLogged(int? roleId)
    {
        var provider = new CapturingLoggerProvider();
        using var logs = LoggerFactory.Create(builder => builder.AddProvider(provider));
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .UseLoggerFactory(logs)
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls));
        await db.SaveChangesAsync();
        db.LinkshellRoles.Single(r => r.Name == LinkshellRanks.Member).CanBid = false;
        await db.SaveChangesAsync();
        var membership = new AppUserLinkshell { Id = 77, AppUserId = UserId, LinkshellId = Ls, Rank = LinkshellRanks.Officer, LinkshellRoleId = roleId };
        var permissions = await NewServiceAsync(db);

        Assert.Equal(LinkshellRanks.Member, (await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None))?.Name);
        Assert.False(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.False(await AuctionBidService.CanMembershipBidAsync(db, membership, CancellationToken.None));

        var warnings = provider.Entries
            .Where(e => e.Category == typeof(LinkshellPermissions).FullName && e.Level == LogLevel.Warning)
            .ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, w => Assert.Contains("Member role", w.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trial_CannotBid_Member_Can()
    {
        await using var db = NewDb();
        var trial = await SeedMemberAsync(db, UserId, LinkshellRanks.Trial);
        var member = await SeedMemberAsync(db, "user-2", LinkshellRanks.Member);

        Assert.False(await AuctionBidService.CanMembershipBidAsync(db, trial, CancellationToken.None));
        Assert.True(await AuctionBidService.CanMembershipBidAsync(db, member, CancellationToken.None));
    }

    // The deleted name path, kept here only as the yardstick for the parity tests above:
    // LinkshellPermissions.GetEffectiveRoleAsync(rank, ...) -- the trimmed Rank's role, exact
    // capitals, else Member -- and AuctionBidService.CanRankBidAsync -- that role's CanBid, true
    // when the role was missing.
    private static LinkshellRole OldRoleByRank(string? rank, List<LinkshellRole> roles)
    {
        var name = string.IsNullOrWhiteSpace(rank) ? LinkshellRanks.Member : rank.Trim();
        return roles.FirstOrDefault(r => r.Name == name)
            ?? roles.Single(r => r.Name == LinkshellRanks.Member);
    }

    private static bool OldCanRankBid(string? rank, List<LinkshellRole> roles)
    {
        var name = string.IsNullOrWhiteSpace(rank) ? LinkshellRanks.Member : rank.Trim();
        return roles.FirstOrDefault(r => r.Name == name)?.CanBid != false;
    }

    // A custom role renamed (Data Admin edits only the role row): its holders keep it by id, so they
    // keep its rights, where the name lookup alone would have dropped them to Member.
    [Fact]
    public async Task RenamedCustomRole_KeepsItsHoldersRights()
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        var raidLead = new LinkshellRole { LinkshellId = Ls, Name = "Raid Lead", CanManageEvents = true, CanBid = false };
        db.LinkshellRoles.Add(raidLead);
        await db.SaveChangesAsync();
        var membership = await SeedMemberAsync(db, UserId, "Raid Lead");
        Assert.Equal(raidLead.Id, membership.LinkshellRoleId);

        raidLead.Name = "Strike Lead";
        await db.SaveChangesAsync();
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageEvents, CancellationToken.None));
        Assert.Equal("Strike Lead", (await permissions.GetEffectiveRoleAsync(membership, CancellationToken.None))?.Name);
        Assert.False(await AuctionBidService.CanMembershipBidAsync(db, membership, CancellationToken.None));
    }

    // The Leader rule still comes first: a Leader whose role id points at a role with no rights
    // (edited straight in the database, or stale) keeps every permission. A save would now copy
    // that role's name into Rank, so the stale pair is made in memory only, the way such a row
    // would be read.
    [Fact]
    public async Task Leader_IsAllowed_EvenWhenTheirRoleIdNamesARoleWithNoRights()
    {
        await using var db = NewDb();
        var membership = await SeedMemberAsync(db, UserId, LinkshellRanks.Leader);
        var empty = new LinkshellRole { LinkshellId = Ls, Name = "Nothing" };
        db.LinkshellRoles.Add(empty);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        membership.LinkshellRoleId = empty.Id;
        Assert.Equal(LinkshellRanks.Leader, membership.Rank);
        var permissions = await NewServiceAsync(db);

        Assert.True(await permissions.CanAsync(membership, r => r.CanManageRoles, CancellationToken.None));
        Assert.True(await permissions.CanAsync(membership, r => false, CancellationToken.None));
    }

    // The admin override still comes before the role, whatever the id says.
    [Fact]
    public async Task AdminOverride_StillGrantsEverything_WithARoleId()
    {
        await using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS1", LootStructure = "Dkp" });
        db.LinkshellRoles.AddRange(LinkshellRoleDefaults.BuildDefaultRoles(Ls));
        await db.SaveChangesAsync();
        var membership = await SeedMemberAsync(db, AdminId, LinkshellRanks.Member, superAdmin: true);
        Assert.NotNull(membership.LinkshellRoleId);

        // Off first: switching it on is saved in the database.
        Assert.False(await (await NewServiceAsync(db, overrideOn: false)).CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
        Assert.True(await (await NewServiceAsync(db, overrideOn: true)).CanAsync(membership, r => r.CanManageTreasury, CancellationToken.None));
    }

    private static readonly Func<LinkshellRole, bool>[] AllFlags =
    {
        r => r.CanManageRoles, r => r.CanManageMembers, r => r.CanManageEvents, r => r.CanModerateLiveEvent,
        r => r.CanAddLoot, r => r.CanManageInventory, r => r.CanManageCharts, r => r.CanManageTreasury,
        r => r.CanManageRules, r => r.CanManageAnnouncements, r => r.CanManageTods, r => r.CanAuditDkp,
        r => r.CanManageAuctions, r => r.CanLockAuctions, r => r.CanCustomizeLinkshell, r => r.CanManageParties,
        r => r.CanManageInvites, r => r.CanBid, r => r.CanUseAddon, r => r.CanManageAttendanceWithAddon,
        r => r.CanSubmitTodForApproval,
    };

    [Fact]
    public async Task EnsureDefaultRoles_SeedsMissingRolesOnce()
    {
        await using var db = NewDb();
        await SeedMemberAsync(db, UserId, LinkshellRanks.Member);
        var permissions = await NewServiceAsync(db);

        var first = await permissions.EnsureDefaultRolesAsync(Ls, CancellationToken.None);
        var second = await permissions.EnsureDefaultRolesAsync(Ls, CancellationToken.None);

        var expected = LinkshellRoleDefaults.BuildDefaultRoles(Ls).Count();
        Assert.Equal(expected, first.Count);
        Assert.Equal(expected, second.Count);
        Assert.Equal(expected, await db.LinkshellRoles.CountAsync(r => r.LinkshellId == Ls));
    }

    [Fact]
    public async Task GetMembership_LoadsTheLinkshell_AndIgnoresOtherLinkshells()
    {
        await using var db = NewDb();
        await SeedMemberAsync(db, UserId, LinkshellRanks.Member, linkshellId: OtherLs);
        var permissions = await NewServiceAsync(db);

        Assert.Null(await permissions.GetMembershipAsync(UserId, Ls, CancellationToken.None));
        var membership = await permissions.GetMembershipAsync(UserId, OtherLs, CancellationToken.None);
        Assert.NotNull(membership?.Linkshell);
    }

    // The controller keeps thin wrappers so its many call sites stay unchanged. Guard against
    // the old body growing back there (two copies is how the rules drifted in the first place).
    [Fact]
    public void ActivityController_DelegatesToTheService()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "Activity", "ActivityControllerBase.cs"));

        Assert.Contains("_permissions.CanAsync(", text);
        Assert.Contains("_permissions.GetMembershipAsync(", text);
        Assert.DoesNotContain("LinkshellRanks.IsLeader(", text);
        Assert.DoesNotContain("BuildDefaultRoles(", text);
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    // Collects what the context's logger factory is given (UseLoggerFactory), with its category.
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

    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
