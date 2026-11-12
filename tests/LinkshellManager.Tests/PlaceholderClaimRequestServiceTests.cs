using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// By-name claims of imported members wait for an officer (PlaceholderClaimRequestService). A name is
// public, so "Bob typed Bobalt" proves nothing; the officer's Approve is what hands the DKP over.
public class PlaceholderClaimRequestServiceTests
{
    private const int Ls = 1;
    private const string Officer = "officer";

    private static ApplicationDbContext NewDb()
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            // An approved claim opens a transaction; the in-memory provider has none.
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.Add(new AppUser { Id = "real", UserName = "real", CharacterName = "Bobalt" });
        db.Users.Add(new AppUser { Id = "other", UserName = "other", CharacterName = "Bobalt" });
        db.Users.Add(new AppUser { Id = "ph", UserName = "ph", CharacterName = "Bobalt", IsPlaceholder = true });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 11, LinkshellId = Ls, AppUserId = "ph", CharacterName = "Bobalt", LinkshellDkp = 100 });
        db.SaveChanges();
        return db;
    }

    private static PlaceholderClaimRequestService NewService(ApplicationDbContext db) =>
        new(db, new PlaceholderClaimService(db, new UserMergeService(db)));

    [Fact]
    public async Task ARequest_ChangesNothing_UntilAnOfficerApproves()
    {
        using var db = NewDb();
        var service = NewService(db);

        Assert.True(await service.RequestForAccountAsync(Ls, "ph", "real", "Bob", CancellationToken.None));

        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal("ph", member.AppUserId);
        var pending = Assert.Single(await service.ListPendingAsync(Ls, CancellationToken.None));
        Assert.Equal("Bobalt", pending.PlaceholderName);
        Assert.Equal(100d, pending.PlaceholderDkp);
        Assert.Equal("Bob", pending.RequesterName);
        Assert.Equal("account", pending.RequesterKind);
    }

    [Fact]
    public async Task AskingTwice_FilesOneRequest()
    {
        using var db = NewDb();
        var service = NewService(db);

        Assert.True(await service.RequestForAccountAsync(Ls, "ph", "real", "Bob", CancellationToken.None));
        Assert.False(await service.RequestForAccountAsync(Ls, "ph", "real", "Bob", CancellationToken.None));

        Assert.Single(db.PlaceholderClaimRequests);
    }

    [Fact]
    public async Task ApprovingAnAccountRequest_RunsTheClaim_AndClosesTheOtherRequests()
    {
        using var db = NewDb();
        var service = NewService(db);
        await service.RequestForAccountAsync(Ls, "ph", "real", "Bob", CancellationToken.None);
        await service.RequestForAccountAsync(Ls, "ph", "other", "Impostor", CancellationToken.None);
        var bobsRequest = db.PlaceholderClaimRequests.Single(r => r.RequesterAppUserId == "real").Id;

        var result = await service.ApproveAsync(Ls, bobsRequest, Officer, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        Assert.Equal("real", member.AppUserId);
        Assert.Equal(100d, member.LinkshellDkp);
        Assert.Null(db.Users.Find("ph"));

        var requests = db.PlaceholderClaimRequests.AsNoTracking().ToList();
        Assert.All(requests, r => Assert.NotNull(r.ResolvedAtUtc));
        Assert.All(requests, r => Assert.Equal(Officer, r.ResolvedByAppUserId));
        Assert.True(requests.Single(r => r.RequesterAppUserId == "real").Approved);
        Assert.False(requests.Single(r => r.RequesterAppUserId == "other").Approved);
        Assert.Empty(await service.ListPendingAsync(Ls, CancellationToken.None));
    }

    [Fact]
    public async Task ApprovingADiscordRequest_LinksTheirDiscord_AsConfirmed()
    {
        using var db = NewDb();
        var service = NewService(db);
        await service.RequestForDiscordAsync(Ls, "ph", "555", "Bob (Discord)", CancellationToken.None);
        var request = Assert.Single(db.PlaceholderClaimRequests);
        Assert.Equal("discord", Assert.Single(await service.ListPendingAsync(Ls, CancellationToken.None)).RequesterKind);

        var result = await service.ApproveAsync(Ls, request.Id, Officer, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        var member = Assert.Single(db.AppUserLinkshells);
        // Still the placeholder (they have no account yet); their next Sign Up and first LSM launch
        // find it by this Discord id.
        Assert.Equal("ph", member.AppUserId);
        Assert.Equal("555", member.DiscordUserId);
        Assert.True(member.DiscordIdConfirmed);
    }

    [Fact]
    public async Task Denying_ChangesNothing_AndCannotBeApprovedAfterwards()
    {
        using var db = NewDb();
        var service = NewService(db);
        await service.RequestForAccountAsync(Ls, "ph", "other", "Impostor", CancellationToken.None);
        var request = Assert.Single(db.PlaceholderClaimRequests);

        Assert.True((await service.DenyAsync(Ls, request.Id, Officer, CancellationToken.None)).Success);
        var late = await service.ApproveAsync(Ls, request.Id, Officer, CancellationToken.None);

        Assert.False(late.Success);
        Assert.Equal("ph", Assert.Single(db.AppUserLinkshells).AppUserId);
        Assert.False(db.PlaceholderClaimRequests.AsNoTracking().Single().Approved);
    }

    [Fact]
    public async Task AnotherLinkshellsOfficer_CannotResolveTheRequest()
    {
        using var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = 2, LinkshellName = "Other" });
        db.SaveChanges();
        var service = NewService(db);
        await service.RequestForAccountAsync(Ls, "ph", "other", "Impostor", CancellationToken.None);
        var request = Assert.Single(db.PlaceholderClaimRequests);

        var result = await service.ApproveAsync(2, request.Id, Officer, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ph", Assert.Single(db.AppUserLinkshells).AppUserId);
    }

    [Fact]
    public async Task ARequestWhosePlaceholderIsGone_IsNotListed()
    {
        using var db = NewDb();
        var service = NewService(db);
        await service.RequestForAccountAsync(Ls, "ph", "other", "Impostor", CancellationToken.None);
        // The player opened LSM and took the placeholder over in place.
        db.Users.Find("ph")!.IsPlaceholder = false;
        db.SaveChanges();

        Assert.Empty(await service.ListPendingAsync(Ls, CancellationToken.None));
    }
}
