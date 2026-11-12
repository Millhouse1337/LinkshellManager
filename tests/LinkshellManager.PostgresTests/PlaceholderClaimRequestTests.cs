using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.PostgresTests;

// One OPEN claim request per person per placeholder (the filtered unique indexes from
// AddPlaceholderClaimRequests). Two clicks at the same moment both pass the service's "already
// asked?" check; the index turns the later one away and the service reports "already asked"
// instead of failing.
[Collection("postgres")]
public class PlaceholderClaimRequestTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public PlaceholderClaimRequestTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        db.Users.Add(new AppUser { Id = "real", UserName = "real", CharacterName = "Bobalt" });
        db.Users.Add(new AppUser { Id = "ph", UserName = "ph", CharacterName = "Bobalt", IsPlaceholder = true });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Linkshell = linkshell, AppUserId = "ph", CharacterName = "Bobalt" });
        await db.SaveChangesAsync();
        return linkshell.Id;
    }

    private static PlaceholderClaimRequest Open(int linkshellId, string? appUserId, string? discordUserId) => new()
    {
        LinkshellId = linkshellId,
        PlaceholderAppUserId = "ph",
        RequesterAppUserId = appUserId,
        RequesterDiscordUserId = discordUserId,
        CreatedAtUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task ASecondOpenRequestFromTheSamePerson_IsRefused_ButAfterResolvingTheyMayAskAgain()
    {
        var linkshellId = await SeedAsync();
        await using var db = _pg.CreateContext();
        db.PlaceholderClaimRequests.AddRange(Open(linkshellId, "real", null), Open(linkshellId, null, "555"));
        await db.SaveChangesAsync();

        await using (var dup = _pg.CreateContext())
        {
            dup.PlaceholderClaimRequests.Add(Open(linkshellId, "real", null));
            await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync());
        }
        await using (var dup = _pg.CreateContext())
        {
            dup.PlaceholderClaimRequests.Add(Open(linkshellId, null, "555"));
            await Assert.ThrowsAsync<DbUpdateException>(() => dup.SaveChangesAsync());
        }

        foreach (var row in db.PlaceholderClaimRequests) row.ResolvedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.PlaceholderClaimRequests.AddRange(Open(linkshellId, "real", null), Open(linkshellId, null, "555"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TwoClicksAtOnce_FileOneRequest_AndNeitherFails()
    {
        var linkshellId = await SeedAsync();

        // The other click saves its request right after this one checked "already asked?".
        var otherClick = new BeforeFirstSave(async () =>
        {
            await using var other = _pg.CreateContext();
            other.PlaceholderClaimRequests.Add(Open(linkshellId, "real", null));
            await other.SaveChangesAsync();
        });
        await using (var db = _pg.CreateContext(otherClick))
        {
            var service = new PlaceholderClaimRequestService(db, new PlaceholderClaimService(db, new UserMergeService(db)));
            var added = await service.RequestForAccountAsync(linkshellId, "ph", "real", "Bob", CancellationToken.None);
            Assert.False(added);
        }

        await using var check = _pg.CreateContext();
        Assert.Single(await check.PlaceholderClaimRequests.AsNoTracking().ToListAsync());
    }

    // The officers' list joins requests to roster rows; the in-memory tests cannot show that the
    // query translates to SQL, and a query that does not would make the roster card fail for everyone.
    [Fact]
    public async Task TheOfficersList_RunsOnPostgres_AndHidesClaimedPlaceholders()
    {
        var linkshellId = await SeedAsync();
        await using (var db = _pg.CreateContext())
        {
            db.PlaceholderClaimRequests.AddRange(Open(linkshellId, "real", null), Open(linkshellId, null, "555"));
            await db.SaveChangesAsync();
        }

        await using (var db = _pg.CreateContext())
        {
            var service = new PlaceholderClaimRequestService(db, new PlaceholderClaimService(db, new UserMergeService(db)));
            var pending = await service.ListPendingAsync(linkshellId, CancellationToken.None);
            Assert.Equal(new[] { "account", "discord" }, pending.Select(p => p.RequesterKind).OrderBy(k => k));
            Assert.All(pending, p => Assert.Equal("Bobalt", p.PlaceholderName));

            var accountRequest = pending.Single(p => p.RequesterKind == "account");
            Assert.True((await service.ApproveAsync(linkshellId, accountRequest.Id, "officer", CancellationToken.None)).Success);
        }

        await using (var db = _pg.CreateContext())
        {
            var service = new PlaceholderClaimRequestService(db, new PlaceholderClaimService(db, new UserMergeService(db)));
            Assert.Empty(await service.ListPendingAsync(linkshellId, CancellationToken.None));
            // The Discord request for the same placeholder was closed by the approval.
            Assert.All(await db.PlaceholderClaimRequests.AsNoTracking().ToListAsync(), r => Assert.NotNull(r.ResolvedAtUtc));
        }
    }

    private sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeFirstSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }
}
