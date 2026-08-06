using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The security boundary on farming credit.
//
// A credit request names people by membership id. Those ids have to be checked against THIS
// linkshell, or a request could attribute farming credit to a member of somebody else's linkshell —
// and a mismatch has to refuse the WHOLE request rather than quietly recording the rest, which is
// how ResolveTreasuryRecipientsAsync guards a gil split.
public class ChartCreditResolutionTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppUserLinkshell Membership(int id, int linkshellId, string name) => new()
    {
        Id = id,
        LinkshellId = linkshellId,
        AppUserId = $"user-{id}",
        CharacterName = name,
        Rank = "Member",
    };

    [Fact]
    public async Task ResolveCredits_RefusesAMembershipFromAnotherLinkshell()
    {
        using var db = NewInMemoryContext();
        db.AppUserLinkshells.Add(Membership(1, linkshellId: 10, "Aeris"));
        db.AppUserLinkshells.Add(Membership(2, linkshellId: 99, "Stranger"));
        await db.SaveChangesAsync();

        var service = new ChartBoardService(db);
        var (error, credits) = await service.ResolveCreditsAsync(
            linkshellId: 10,
            new[] { new ChartCreditDraft(1, null, null), new ChartCreditDraft(2, null, null) },
            CancellationToken.None);

        Assert.NotNull(error);
        // The whole request dies — the legitimate half is NOT recorded.
        Assert.Empty(credits);
    }

    // The name is taken from the roster row, never from the request. Otherwise a request could name
    // a real membership id and attach any string it liked to it.
    [Fact]
    public async Task ResolveCredits_TakesTheNameFromTheRoster_NotTheRequest()
    {
        using var db = NewInMemoryContext();
        db.AppUserLinkshells.Add(Membership(1, linkshellId: 10, "Aeris"));
        await db.SaveChangesAsync();

        var service = new ChartBoardService(db);
        var (error, credits) = await service.ResolveCreditsAsync(
            linkshellId: 10,
            new[] { new ChartCreditDraft(1, "Somebody Else", "farmed 3") },
            CancellationToken.None);

        Assert.Null(error);
        Assert.Equal("Aeris", credits.Single().CharacterName);
        Assert.Equal("farmed 3", credits.Single().Detail);
    }

    // An unsynced character is a real member of the linkshell who really farmed; they just have no
    // account behind the name. Refusing them would make the board lie about who did the work.
    [Fact]
    public async Task ResolveCredits_AllowsAnUnsyncedFarmerByNameAlone()
    {
        using var db = NewInMemoryContext();
        var service = new ChartBoardService(db);

        var (error, credits) = await service.ResolveCreditsAsync(
            linkshellId: 10,
            new[] { new ChartCreditDraft(null, "  Mulepal  ", null) },
            CancellationToken.None);

        Assert.Null(error);
        Assert.Null(credits.Single().MembershipId);
        Assert.Equal("Mulepal", credits.Single().CharacterName);
    }

    [Fact]
    public async Task ResolveCredits_CollapsesDuplicateNames()
    {
        using var db = NewInMemoryContext();
        db.AppUserLinkshells.Add(Membership(1, linkshellId: 10, "Aeris"));
        await db.SaveChangesAsync();

        var service = new ChartBoardService(db);
        var (error, credits) = await service.ResolveCreditsAsync(
            linkshellId: 10,
            new[] { new ChartCreditDraft(1, null, null), new ChartCreditDraft(null, "aeris", null) },
            CancellationToken.None);

        Assert.Null(error);
        Assert.Single(credits);
    }

    // Clearing every farmer off a row is a legitimate edit, so an empty list is not an error.
    [Fact]
    public async Task ResolveCredits_AcceptsAnEmptyListAsAClear()
    {
        using var db = NewInMemoryContext();
        var service = new ChartBoardService(db);

        var (error, credits) = await service.ResolveCreditsAsync(
            linkshellId: 10, Array.Empty<ChartCreditDraft>(), CancellationToken.None);

        Assert.Null(error);
        Assert.Empty(credits);
    }

    // A list of nothing but blanks is a slip, not a clear — the officer meant to type someone.
    [Fact]
    public async Task ResolveCredits_RejectsAListOfOnlyBlankNames()
    {
        using var db = NewInMemoryContext();
        var service = new ChartBoardService(db);

        var (error, _) = await service.ResolveCreditsAsync(
            linkshellId: 10,
            new[] { new ChartCreditDraft(null, "   ", null) },
            CancellationToken.None);

        Assert.NotNull(error);
    }

    // Writes replace the whole list, so a duplicate cannot survive a write and stale rows cannot
    // linger. This is why there is no unique index on the table.
    [Fact]
    public async Task ReplaceCredits_ClearsWhatWasThereBefore()
    {
        using var db = NewInMemoryContext();
        var item = new ChartPopItem
        {
            Id = 5, LinkshellId = 10, Board = ChartBoardCatalog.Sea,
            Boss = "Jailer of Faith", ItemName = "Faith Torque",
        };
        db.ChartPopItems.Add(item);
        db.ChartPopItemCredits.Add(new ChartPopItemCredit
        {
            Id = 1, ChartPopItemId = 5, LinkshellId = 10, CharacterName = "OldFarmer",
        });
        await db.SaveChangesAsync();

        var service = new ChartBoardService(db);
        await service.ReplaceCreditsAsync(
            item,
            new[] { new ChartResolvedCredit(null, "NewFarmer", null) },
            new ChartBoardActor("user-1", "Officer"),
            CancellationToken.None);
        await db.SaveChangesAsync();

        var remaining = db.ChartPopItemCredits.Where(credit => credit.ChartPopItemId == 5).ToList();
        Assert.Equal("NewFarmer", remaining.Single().CharacterName);
        // The credit carries the item's linkshell, so the ledger can read it without joining back.
        Assert.Equal(10, remaining.Single().LinkshellId);
    }

    /// <summary>
    /// Adding a pop item and crediting its farmers is ONE insert. Both surfaces let an officer name
    /// the farmers on the add form, and the row must not be able to land without the credit that was
    /// asked for in the same breath.
    ///
    /// This is why AttachCredits exists at all: ReplaceCreditsAsync keys its child rows on item.Id,
    /// which is still 0 before the insert, so using it here would write credits pointing at nothing.
    /// </summary>
    [Fact]
    public async Task AttachCredits_SavesANewItemAndItsFarmersTogether()
    {
        using var db = NewInMemoryContext();
        var item = new ChartPopItem
        {
            LinkshellId = 10, Board = ChartBoardCatalog.Sky, Boss = "Genbu", ItemName = "Winterstone",
        };

        ChartBoardService.AttachCredits(
            item,
            new[]
            {
                new ChartResolvedCredit(1, "Aeris", null),
                new ChartResolvedCredit(null, "Unsynced", null),
            },
            new ChartBoardActor("user-1", "Officer"));

        db.ChartPopItems.Add(item);
        await db.SaveChangesAsync();

        var saved = db.ChartPopItemCredits.Where(credit => credit.ChartPopItemId == item.Id).ToList();
        Assert.Equal(2, saved.Count);
        // The FK is filled in by the insert, so the item really got an id and the credits point at it.
        Assert.NotEqual(0, item.Id);
        // Each credit carries the item's linkshell, so the ledger reads it without joining back.
        Assert.All(saved, credit => Assert.Equal(10, credit.LinkshellId));
        Assert.All(saved, credit => Assert.Equal("Officer", credit.CreditedByCharacterName));
        Assert.Equal(new[] { "Aeris", "Unsynced" }, saved.Select(credit => credit.CharacterName).ToArray());
    }

    // A draft is only accepted when the boss actually belongs to the named board — the guard that
    // stops a Sea request filing a row under a Sky god.
    [Fact]
    public void NormalizeDraft_RejectsABossFromAnotherBoard()
    {
        Assert.Null(ChartBoardService.NormalizeDraft(
            ChartBoardCatalog.Sea, "Byakko", "Diorite", null, null, 1, null));

        Assert.NotNull(ChartBoardService.NormalizeDraft(
            ChartBoardCatalog.Sea, "jailer of love", "Love Torque", null, null, 1, null));
    }

    [Fact]
    public void NormalizeDraft_RequiresAnItemName_AndClampsQuantity()
    {
        Assert.Null(ChartBoardService.NormalizeDraft(
            ChartBoardCatalog.Sea, "Jailer of Hope", "   ", null, null, 1, null));

        var draft = ChartBoardService.NormalizeDraft(
            ChartBoardCatalog.Sea, "Jailer of Hope", "  Hope Staff  ", "  Miyu  ", null, -4, "  note  ");

        Assert.Equal("Hope Staff", draft!.ItemName);
        Assert.Equal("Miyu", draft.HeldByCharacterName);
        // 0 is a real state ("we used it, none left"); negatives are not, and the check constraint
        // would refuse them anyway.
        Assert.Equal(0, draft.Quantity);
        Assert.Equal("note", draft.Notes);
    }
}
