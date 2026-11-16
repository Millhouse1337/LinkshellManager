using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using LinkshellManager.TestSupport;

namespace LinkshellManager.PostgresTests;

// Join requests, through the app's own routes on real Postgres: a player finds a linkshell and
// asks to join; an officer who can manage invites approves or denies; the player can cancel while
// it waits. The one-open-request index and the search's ILIKE only exist on Postgres.
[Collection("postgres")]
public class JoinRequestTests : IAsyncLifetime
{
    private const string Leader = "leader";
    private const string Officer = "officer";
    private const string Member = "member";
    private const string Player = "player";
    private const string Other = "other";

    private readonly PostgresFixture _pg;

    public JoinRequestTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // "Wyrm Hunters" with a Leader, an Officer and a Member (with DKP). "Empty" has no members and
    // "Locked" is tied to another Discord server. Player and Other are in nothing.
    private async Task<(int Hunters, int Locked)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var hunters = new Linkshell { LinkshellName = "Wyrm Hunters", Details = "We camp wyrms." };
        var empty = new Linkshell { LinkshellName = "Wyrm Empty" };
        var locked = new Linkshell { LinkshellName = "Wyrm Locked", LockToDiscordGuild = true, DiscordGuildId = "999" };
        var unnamed = new Linkshell { LinkshellName = "  " };
        db.Linkshells.AddRange(hunters, empty, locked, unnamed);
        db.Users.AddRange(
            new AppUser { Id = Leader, UserName = Leader, CharacterName = "Boss" },
            new AppUser { Id = Officer, UserName = Officer, CharacterName = "Helper" },
            new AppUser { Id = Member, UserName = Member, CharacterName = "Secretname" },
            new AppUser { Id = Player, UserName = Player, CharacterName = "Newbie" },
            new AppUser { Id = Other, UserName = Other, CharacterName = "Stranger" });
        await db.SaveChangesAsync();
        db.AppUserLinkshells.AddRange(
            new AppUserLinkshell { LinkshellId = hunters.Id, AppUserId = Leader, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = hunters.Id, AppUserId = Officer, CharacterName = "Helper", Rank = LinkshellRanks.Officer, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = hunters.Id, AppUserId = Member, CharacterName = "Secretname", Rank = LinkshellRanks.Member, LinkshellDkp = 1234.5 },
            new AppUserLinkshell { LinkshellId = locked.Id, AppUserId = Leader, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 },
            new AppUserLinkshell { LinkshellId = unnamed.Id, AppUserId = Leader, CharacterName = "Boss", Rank = LinkshellRanks.Leader, LinkshellDkp = 0 });
        await db.SaveChangesAsync();
        return (hunters.Id, locked.Id);
    }

    private static ActivityRosterController Controller(ApplicationDbContext db, string userId, string? guildId = null)
    {
        var userManager = new UserManager<AppUser>(
            new UserStore<AppUser>(db),
            Options.Create(new IdentityOptions()),
            new PasswordHasher<AppUser>(),
            Array.Empty<IUserValidator<AppUser>>(),
            Array.Empty<IPasswordValidator<AppUser>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<AppUser>>.Instance);
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        var adminOverride = new AdminOverrideService(db, settings, NullLogger<AdminOverrideService>.Instance);
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, authenticationType: "Test")),
        };
        if (guildId is not null)
        {
            http.Request.Headers["X-Discord-Guild-Id"] = guildId;
        }
        return ActivityControllerFactory.Create<ActivityRosterController>(
            http, db, userManager, adminOverride, new LinkshellPermissions(db, adminOverride));
    }

    private async Task<IActionResult> AskAsync(string userId, int linkshellId, string? message = null)
    {
        await using var db = _pg.CreateContext();
        return await Controller(db, userId).RequestJoinLinkshellAsync(
            linkshellId, new ActivityJoinRequestInput(message), new JoinRequestService(db), CancellationToken.None);
    }

    private async Task<IActionResult> ApproveAsync(string userId, int requestId)
    {
        await using var db = _pg.CreateContext();
        return await Controller(db, userId).ApproveJoinRequestAsync(requestId, new JoinRequestService(db), CancellationToken.None);
    }

    private async Task<IActionResult> DenyAsync(string userId, int requestId)
    {
        await using var db = _pg.CreateContext();
        return await Controller(db, userId).DeclineJoinRequestAsync(requestId, new JoinRequestService(db), CancellationToken.None);
    }

    private async Task<IActionResult> CancelAsync(string userId, int requestId)
    {
        await using var db = _pg.CreateContext();
        return await Controller(db, userId).CancelJoinRequestAsync(requestId, new JoinRequestService(db), CancellationToken.None);
    }

    private async Task<List<ActivityLinkshellSearchResultDto>> SearchAsync(string userId, string query, string? guildId = null)
    {
        await using var db = _pg.CreateContext();
        var result = await Controller(db, userId, guildId).SearchLinkshellsAsync(query, new JoinRequestService(db), CancellationToken.None);
        return Assert.IsAssignableFrom<List<ActivityLinkshellSearchResultDto>>(Assert.IsType<OkObjectResult>(result).Value);
    }

    private async Task<Invite> RequestRowAsync(string userId, int linkshellId)
    {
        await using var db = _pg.CreateContext();
        return await db.Invites.AsNoTracking()
            .Where(i => i.AppUserId == userId && i.LinkshellId == linkshellId)
            .OrderByDescending(i => i.Id)
            .FirstAsync();
    }

    private async Task<List<AppUserLinkshell>> MembershipsAsync(string userId, int linkshellId)
    {
        await using var db = _pg.CreateContext();
        return await db.AppUserLinkshells.AsNoTracking()
            .Where(m => m.AppUserId == userId && m.LinkshellId == linkshellId)
            .ToListAsync();
    }

    // ------------------------------------------------------------------------- search ---

    [Fact]
    public async Task Search_ListsNamedLinkshellsWithMembers_AndNothingPrivate()
    {
        var (hunters, _) = await SeedAsync();

        var results = await SearchAsync(Player, "wyrm");

        // Not "Wyrm Empty" (no members), not "Wyrm Locked" (another Discord server), not the
        // nameless one.
        var hit = Assert.Single(results);
        Assert.Equal(hunters, hit.Id);
        Assert.Equal("Wyrm Hunters", hit.Name);
        Assert.Equal("We camp wyrms.", hit.Details);
        Assert.Equal(3, hit.MemberCount);

        // Only the name, description and member count: no member names, ranks or DKP.
        var json = JsonSerializer.Serialize(results);
        Assert.DoesNotContain("Secretname", json);
        Assert.DoesNotContain("1234", json);
        Assert.DoesNotContain("Boss", json);
        Assert.Equal(
            new[] { "Details", "Id", "MemberCount", "Name" },
            typeof(ActivityLinkshellSearchResultDto).GetProperties().Select(p => p.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Search_LeavesOutTheirOwnAndAskedLinkshells_AndNeedsTwoLetters()
    {
        var (hunters, locked) = await SeedAsync();

        Assert.Empty(await SearchAsync(Member, "wyrm hunters"));
        Assert.Empty(await SearchAsync(Player, "w"));
        // % and _ are matched as typed, not as wildcards.
        Assert.Empty(await SearchAsync(Player, "%%"));
        Assert.Empty(await SearchAsync(Player, "__"));

        // A locked linkshell shows inside its own Discord server.
        Assert.Contains(await SearchAsync(Player, "wyrm", guildId: "999"), r => r.Id == locked);

        Assert.IsType<OkObjectResult>(await AskAsync(Player, hunters));
        Assert.Empty(await SearchAsync(Player, "wyrm hunters"));
    }

    // ------------------------------------------------------------------------ request ---

    [Fact]
    public async Task Request_IsSavedWithItsMessage_AndASecondOneIsRefused()
    {
        var (hunters, _) = await SeedAsync();

        Assert.IsType<OkObjectResult>(await AskAsync(Player, hunters, "  Hi, I play WHM.  "));
        var row = await RequestRowAsync(Player, hunters);
        Assert.Equal(JoinRequestService.PendingStatus, row.Status);
        Assert.Equal("Hi, I play WHM.", row.Message);
        Assert.NotNull(row.CreatedAtUtc);
        Assert.Null(row.ResolvedAtUtc);

        var again = Assert.IsType<BadRequestObjectResult>(await AskAsync(Player, hunters));
        Assert.Contains("already asked", JsonSerializer.Serialize(again.Value));

        await using var db = _pg.CreateContext();
        Assert.Equal(1, await db.Invites.CountAsync(i => i.AppUserId == Player));
    }

    [Fact]
    public async Task TheDatabaseRefusesASecondOpenRequest_EvenIfTheCheckIsSkipped()
    {
        var (hunters, _) = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            db.Invites.Add(new Invite { AppUserId = Player, LinkshellId = hunters, Status = JoinRequestService.PendingStatus });
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            db.Invites.Add(new Invite { AppUserId = Player, LinkshellId = hunters, Status = JoinRequestService.PendingStatus });
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.True(UniqueRaceRetry.IsUniqueRejectionOn(ex, new[] { JoinRequestService.Table }));
        }

        // Answered requests and invites are not covered.
        await using (var db = _pg.CreateContext())
        {
            db.Invites.AddRange(
                new Invite { AppUserId = Player, LinkshellId = hunters, Status = JoinRequestService.DeniedStatus },
                new Invite { AppUserId = Player, LinkshellId = hunters, Status = JoinRequestService.CancelledStatus },
                new Invite { AppUserId = Player, LinkshellId = hunters, Status = "PendingInvite" });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task AMember_CannotAsk()
    {
        var (hunters, _) = await SeedAsync();

        var result = Assert.IsType<BadRequestObjectResult>(await AskAsync(Member, hunters));
        Assert.Contains("already in", JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task ALockedLinkshell_RefusesARequestFromOutsideItsDiscordServer()
    {
        var (_, locked) = await SeedAsync();

        var result = Assert.IsType<ObjectResult>(await AskAsync(Player, locked));
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        await using var db = _pg.CreateContext();
        Assert.False(await db.Invites.AnyAsync());
    }

    // ------------------------------------------------------------------------ answers ---

    [Fact]
    public async Task APlainMember_Gets403_OnApproveAndDeny()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);

        Assert.IsType<ForbidResult>(await ApproveAsync(Member, request.Id));
        Assert.IsType<ForbidResult>(await DenyAsync(Member, request.Id));
        // Someone outside the linkshell too.
        Assert.IsType<ForbidResult>(await ApproveAsync(Other, request.Id));

        Assert.Equal(JoinRequestService.PendingStatus, (await RequestRowAsync(Player, hunters)).Status);
        Assert.Empty(await MembershipsAsync(Player, hunters));
    }

    [Fact]
    public async Task Approve_JoinsThemAsAMember_AndRecordsWhoAndWhen()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);

        Assert.IsType<OkObjectResult>(await ApproveAsync(Officer, request.Id));

        var membership = Assert.Single(await MembershipsAsync(Player, hunters));
        Assert.Equal(LinkshellRanks.Member, membership.Rank);
        Assert.Equal("Active", membership.Status);
        Assert.Equal(0, membership.LinkshellDkp);
        // Since R5.6 a new membership has no roster name: the roster shows the account's main.
        Assert.Null(membership.CharacterName);

        var answered = await RequestRowAsync(Player, hunters);
        Assert.Equal(JoinRequestService.ApprovedStatus, answered.Status);
        Assert.Equal(Officer, answered.ResolvedByAppUserId);
        Assert.NotNull(answered.ResolvedAtUtc);

        await using var db = _pg.CreateContext();
        Assert.Equal(hunters, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == Player)).PrimaryLinkshellId);
    }

    [Fact]
    public async Task ApprovingTwice_JoinsThemOnce_AndTheSecondIsTold()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);

        Assert.IsType<OkObjectResult>(await ApproveAsync(Officer, request.Id));
        var second = Assert.IsType<BadRequestObjectResult>(await ApproveAsync(Leader, request.Id));
        Assert.Contains("already answered", JsonSerializer.Serialize(second.Value));
        // A deny after the approve does not undo it either.
        Assert.IsType<BadRequestObjectResult>(await DenyAsync(Leader, request.Id));

        Assert.Single(await MembershipsAsync(Player, hunters));
        var answered = await RequestRowAsync(Player, hunters);
        Assert.Equal(JoinRequestService.ApprovedStatus, answered.Status);
        Assert.Equal(Officer, answered.ResolvedByAppUserId);
    }

    // Two officers pressing at the same moment: an approve and a deny, or two approves. The
    // request row is locked while it is answered, so exactly one wins and the other is told it
    // was already answered. The status and the roster always agree.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoAnswersAtTheSameMoment_OnlyOneWins(bool bothApprove)
    {
        var (hunters, _) = await SeedAsync();
        const int rounds = 6;
        await using (var db = _pg.CreateContext())
        {
            for (var round = 0; round < rounds; round++)
            {
                db.Users.Add(new AppUser { Id = $"racer{round}", UserName = $"racer{round}", CharacterName = $"Racer{round}" });
            }
            await db.SaveChangesAsync();
        }

        for (var round = 0; round < rounds; round++)
        {
            var player = $"racer{round}";
            Assert.IsType<OkObjectResult>(await AskAsync(player, hunters));
            var request = await RequestRowAsync(player, hunters);

            var results = await Task.WhenAll(
                Task.Run(() => ApproveAsync(Officer, request.Id)),
                Task.Run(() => bothApprove ? ApproveAsync(Leader, request.Id) : DenyAsync(Leader, request.Id)));

            Assert.Single(results, r => r is OkObjectResult);
            var loser = Assert.Single(results, r => r is not OkObjectResult);
            Assert.Contains("already answered", JsonSerializer.Serialize(Assert.IsType<BadRequestObjectResult>(loser).Value));

            var answered = await RequestRowAsync(player, hunters);
            var memberships = await MembershipsAsync(player, hunters);
            if (answered.Status == JoinRequestService.ApprovedStatus)
            {
                Assert.Single(memberships);
            }
            else
            {
                Assert.Equal(JoinRequestService.DeniedStatus, answered.Status);
                Assert.Empty(memberships);
            }
        }
    }

    [Fact]
    public async Task ApprovingSomeoneWhoAlreadyJoined_MarksItApproved_AndAddsNoSecondRow()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);
        // They got in another way meanwhile (an invite, a Discord sign-up).
        await using (var db = _pg.CreateContext())
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell
            {
                LinkshellId = hunters, AppUserId = Player, CharacterName = "Newbie", Rank = LinkshellRanks.Member, LinkshellDkp = 7,
            });
            await db.SaveChangesAsync();
        }

        var ok = Assert.IsType<OkObjectResult>(await ApproveAsync(Officer, request.Id));
        Assert.Contains("alreadyMember", JsonSerializer.Serialize(ok.Value));

        var membership = Assert.Single(await MembershipsAsync(Player, hunters));
        Assert.Equal(7, membership.LinkshellDkp);
        Assert.Equal(JoinRequestService.ApprovedStatus, (await RequestRowAsync(Player, hunters)).Status);
    }

    [Fact]
    public async Task Deny_KeepsTheRequestAsDenied_AndTheyMayAskAgain()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);

        Assert.IsType<OkObjectResult>(await DenyAsync(Leader, request.Id));

        var denied = await RequestRowAsync(Player, hunters);
        Assert.Equal(JoinRequestService.DeniedStatus, denied.Status);
        Assert.Equal(Leader, denied.ResolvedByAppUserId);
        Assert.Empty(await MembershipsAsync(Player, hunters));

        Assert.IsType<OkObjectResult>(await AskAsync(Player, hunters));
    }

    [Fact]
    public async Task Cancel_OnlyByTheirOwnPlayer_AndOnlyWhileItWaits()
    {
        var (hunters, _) = await SeedAsync();
        await AskAsync(Player, hunters);
        var request = await RequestRowAsync(Player, hunters);

        // Someone else's request looks like no request at all.
        Assert.IsType<NotFoundObjectResult>(await CancelAsync(Other, request.Id));
        Assert.IsType<NotFoundObjectResult>(await CancelAsync(Officer, request.Id));

        Assert.IsType<OkObjectResult>(await CancelAsync(Player, request.Id));
        var cancelled = await RequestRowAsync(Player, hunters);
        Assert.Equal(JoinRequestService.CancelledStatus, cancelled.Status);
        Assert.Equal(Player, cancelled.ResolvedByAppUserId);

        // Cancelled is answered: it cannot be cancelled again or approved.
        Assert.IsType<BadRequestObjectResult>(await CancelAsync(Player, request.Id));
        Assert.IsType<BadRequestObjectResult>(await ApproveAsync(Officer, request.Id));
        Assert.Empty(await MembershipsAsync(Player, hunters));

        // They may ask again.
        Assert.IsType<OkObjectResult>(await AskAsync(Player, hunters));
    }

    [Fact]
    public async Task AnInviteId_IsNotAJoinRequest()
    {
        var (hunters, _) = await SeedAsync();
        int inviteId;
        await using (var db = _pg.CreateContext())
        {
            var invite = new Invite { AppUserId = Player, LinkshellId = hunters, Status = "PendingInvite" };
            db.Invites.Add(invite);
            await db.SaveChangesAsync();
            inviteId = invite.Id;
        }

        Assert.IsType<NotFoundObjectResult>(await ApproveAsync(Officer, inviteId));
        Assert.IsType<NotFoundObjectResult>(await CancelAsync(Player, inviteId));
        Assert.Empty(await MembershipsAsync(Player, hunters));
    }
}
