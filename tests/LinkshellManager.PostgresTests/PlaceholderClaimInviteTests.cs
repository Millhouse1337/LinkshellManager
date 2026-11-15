using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// A person has at most one open invite and one open join request per linkshell (unique indexes).
// When a placeholder is claimed and BOTH accounts already have one for the same linkshell, moving
// the placeholder's row onto the real account would break that, and the claim would fail. The
// real account's row stays, the placeholder's goes; anything only the placeholder had moves over.
[Collection("postgres")]
public class PlaceholderClaimInviteTests : IAsyncLifetime
{
    private const string Real = "real-account";
    private const string Ph = "placeholder-account";

    private readonly PostgresFixture _pg;

    public PlaceholderClaimInviteTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Both_accounts_invited_to_one_linkshell_leaves_one_open_invite()
    {
        int both, onlyPlaceholder, realInvite, realRequest;
        await using (var db = _pg.CreateContext())
        {
            var bothLinkshell = new Linkshell { LinkshellName = "Both LS" };
            var placeholderLinkshell = new Linkshell { LinkshellName = "Placeholder LS" };
            db.Linkshells.AddRange(bothLinkshell, placeholderLinkshell);
            db.Users.Add(new AppUser { Id = Real, UserName = "real", CharacterName = "Bob" });
            db.Users.Add(new AppUser { Id = Ph, UserName = "ph", CharacterName = "Bobalt", IsPlaceholder = true });
            await db.SaveChangesAsync();
            both = bothLinkshell.Id;
            onlyPlaceholder = placeholderLinkshell.Id;

            // Old website spelling on the placeholder, Activity spelling on the real account: both
            // are open invites. Each also has an open join request there, and the placeholder has
            // an answered one, which is not open and simply moves.
            var invite = new Invite { LinkshellId = both, AppUserId = Real, Status = "PendingInvite" };
            var request = new Invite { LinkshellId = both, AppUserId = Real, Status = JoinRequestService.PendingStatus };
            db.Invites.AddRange(
                invite,
                request,
                new Invite { LinkshellId = both, AppUserId = Ph, Status = "Pending" },
                new Invite { LinkshellId = both, AppUserId = Ph, Status = JoinRequestService.PendingStatus },
                new Invite { LinkshellId = both, AppUserId = Ph, Status = JoinRequestService.DeniedStatus },
                new Invite { LinkshellId = onlyPlaceholder, AppUserId = Ph, Status = "PendingInvite" });
            await db.SaveChangesAsync();
            realInvite = invite.Id;
            realRequest = request.Id;
        }

        await using (var db = _pg.CreateContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var left = await new UserMergeService(db).RepointAsync(Ph, Real, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Equal(0, left);
        }

        await using (var check = _pg.CreateContext())
        {
            var invites = await check.Invites.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
            Assert.All(invites, i => Assert.Equal(Real, i.AppUserId));

            var open = invites.Where(i => i.LinkshellId == both && i.Status is "Pending" or "PendingInvite").ToList();
            Assert.Equal(realInvite, Assert.Single(open).Id);
            var requests = invites.Where(i => i.LinkshellId == both && i.Status == JoinRequestService.PendingStatus).ToList();
            Assert.Equal(realRequest, Assert.Single(requests).Id);
            Assert.Single(invites, i => i.LinkshellId == both && i.Status == JoinRequestService.DeniedStatus);
            Assert.Single(invites, i => i.LinkshellId == onlyPlaceholder && i.Status == "PendingInvite");
        }
    }
}
