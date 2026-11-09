using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The invite card's "In a Linkshell" option: the linkshells to pick from, then the picked
// one's roster with each row marked for the linkshell doing the inviting. No Discord server
// in these (guild id null), so the Discord lookup is never reached and every flag is null.
public class InviteRosterTests
{
    private const int TargetId = 1;   // the linkshell sending invites
    private const int SourceId = 2;   // the linkshell whose roster is shown
    private const int RetiredId = 3;  // not Active
    private const string CallerId = "caller";

    private static async Task<(ApplicationDbContext Db, InviteCandidateService Service)> SeedAsync()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        db.Linkshells.AddRange(
            new Linkshell { Id = TargetId, LinkshellName = "Target", Status = "Active" },
            new Linkshell { Id = SourceId, LinkshellName = "Source", Status = "Active" },
            new Linkshell { Id = RetiredId, LinkshellName = "Retired", Status = "Inactive" });

        db.Users.AddRange(
            new AppUser { Id = CallerId, UserName = "caller", CharacterName = "Caller" },
            new AppUser { Id = "free", UserName = "free", CharacterName = "Free" },
            new AppUser { Id = "shared", UserName = "shared", CharacterName = "Shared" },
            new AppUser { Id = "invited", UserName = "invited", CharacterName = "Invited" },
            new AppUser { Id = "asker", UserName = "asker", CharacterName = "Asker" },
            new AppUser { Id = "ghost", UserName = "ghost", CharacterName = "Ghost", IsPlaceholder = true });

        foreach (var id in new[] { CallerId, "free", "shared", "invited", "asker", "ghost" })
        {
            db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = id, LinkshellId = SourceId, Status = "Active" });
        }
        // A roster row with no user behind it at all, only a name.
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = null, CharacterName = "Nameonly", LinkshellId = SourceId, Status = "Active" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = CallerId, LinkshellId = TargetId, Status = "Active" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { AppUserId = "shared", LinkshellId = TargetId, Status = "Active" });

        db.Invites.Add(new Invite { AppUserId = "invited", LinkshellId = TargetId, Status = "PendingInvite" });
        db.Invites.Add(new Invite { AppUserId = "asker", LinkshellId = TargetId, Status = "PendingJoinRequest" });

        await db.SaveChangesAsync();
        return (db, new InviteCandidateService(db, null!));
    }

    [Fact]
    public async Task Sources_ListEveryOtherActiveLinkshell()
    {
        var (_, service) = await SeedAsync();

        var sources = await service.ListInviteSourcesAsync(TargetId, CancellationToken.None);

        Assert.Equal(new[] { SourceId }, sources.Select(source => source.Id));
    }

    [Fact]
    public async Task Roster_ListsEveryoneMarkedForTheInvitingLinkshell()
    {
        var (_, service) = await SeedAsync();

        var rows = await service.GetLinkshellRosterAsync(TargetId, SourceId, CallerId, null, CancellationToken.None);

        Assert.NotNull(rows);
        var states = rows!.ToDictionary(row => row.DisplayName, row => row.State);
        Assert.Equal(InviteRosterStates.Invitable, states["Free"]);
        Assert.Equal(InviteRosterStates.Member, states["Shared"]);
        Assert.Equal(InviteRosterStates.Invited, states["Invited"]);
        Assert.Equal(InviteRosterStates.Requested, states["Asker"]);
        Assert.Equal(InviteRosterStates.Self, states["Caller"]);
        Assert.Equal(InviteRosterStates.NoAccount, states["Ghost"]);
        Assert.Equal(InviteRosterStates.NoAccount, states["Nameonly"]);
        Assert.All(rows!, row => Assert.Null(row.InDiscordServer));
    }

    [Fact]
    public async Task Roster_NeverHandsOutAPlaceholdersId()
    {
        var (_, service) = await SeedAsync();

        var rows = await service.GetLinkshellRosterAsync(TargetId, SourceId, CallerId, null, CancellationToken.None);

        Assert.Null(rows!.Single(row => row.DisplayName == "Ghost").AppUserId);
    }

    [Theory]
    [InlineData(RetiredId)] // not Active
    [InlineData(TargetId)]  // its own roster
    [InlineData(99)]        // no such linkshell
    public async Task Roster_IsNullForALinkshellThatCannotBePicked(int sourceId)
    {
        var (_, service) = await SeedAsync();

        var rows = await service.GetLinkshellRosterAsync(TargetId, sourceId, CallerId, null, CancellationToken.None);

        Assert.Null(rows);
    }
}
