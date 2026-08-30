using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// The wishlist is the FIRST Charts write path open to a member without CanManageCharts, so the
// ownership rule is the security boundary here that credit resolution is for pop items. One copy of
// it, reachable from both controllers, pinned below.
public class ChartWishlistTests
{
    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ChartWishlistRequest Request(
        int id,
        string? boss,
        string item = "Ridill",
        string status = ChartWishlistStatuses.Pending,
        string? appUserId = "user-1",
        string board = ChartBoardCatalog.Dynamis) => new()
    {
        Id = id,
        LinkshellId = 1,
        Board = board,
        Boss = boss,
        ItemName = item,
        Quantity = 1,
        Status = status,
        RequestedByAppUserId = appUserId,
        RequestedByMembershipId = 1,
        RequestedByCharacterName = "Millhouse",
    };

    // ---- NormalizeDraft ---------------------------------------------------------

    [Fact]
    public void NormalizeDraft_CanonicalisesTheBoardAndTheBoss()
    {
        var draft = ChartWishlistService.NormalizeDraft("dynamis", " xarcabard ", "  Ridill ", 2, "  for DRK ");

        Assert.NotNull(draft);
        Assert.Equal("Dynamis", draft!.Board);
        Assert.Equal("Xarcabard", draft.Boss);
        Assert.Equal("Ridill", draft.ItemName);
        Assert.Equal(2, draft.Quantity);
        Assert.Equal("for DRK", draft.Notes);
    }

    // "Anywhere on this board" is the option the form OPENS on, so a blank zone is a real answer
    // rather than a missing one.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeDraft_TurnsABlankBossIntoAnywhere(string? boss)
    {
        var draft = ChartWishlistService.NormalizeDraft(ChartBoardCatalog.Dynamis, boss, "Ridill", 1, null);

        Assert.NotNull(draft);
        Assert.Null(draft!.Boss);
    }

    // Naming a card and getting it wrong is REFUSED rather than quietly widened to "anywhere":
    // silently broadening what somebody asked for is worse than saying no.
    [Fact]
    public void NormalizeDraft_RefusesABossFromAnotherBoard() =>
        Assert.Null(ChartWishlistService.NormalizeDraft(
            ChartBoardCatalog.Dynamis, "Byakko", "Ridill", 1, null));

    [Fact]
    public void NormalizeDraft_RefusesABoardThatOffersNoWishlist()
    {
        Assert.False(ChartBoardCatalog.Find(ChartBoardCatalog.Sky)!.AllowsWishlist);
        Assert.Null(ChartWishlistService.NormalizeDraft(ChartBoardCatalog.Sky, null, "Kirin's Osode", 1, null));
    }

    [Theory]
    [InlineData("nowhere", "Ridill")]
    [InlineData(ChartBoardCatalog.Dynamis, "")]
    [InlineData(ChartBoardCatalog.Dynamis, "   ")]
    [InlineData(ChartBoardCatalog.Dynamis, null)]
    public void NormalizeDraft_RefusesAnUnknownBoardOrABlankItem(string board, string? item) =>
        Assert.Null(ChartWishlistService.NormalizeDraft(board, null, item, 1, null));

    // A request for zero of something is not a request, and the check constraint refuses it anyway.
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void NormalizeDraft_ClampsQuantityToAtLeastOne(int typed) =>
        Assert.Equal(1, ChartWishlistService.NormalizeDraft(
            ChartBoardCatalog.Dynamis, null, "Ridill", typed, null)!.Quantity);

    // ---- CanEditRequest: THE ownership rule ------------------------------------

    [Fact]
    public void CanEditRequest_LetsTheOwnerAct() =>
        Assert.True(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: "user-1"), "user-1", canManage: false));

    [Fact]
    public void CanEditRequest_RefusesAStranger() =>
        Assert.False(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: "user-1"), "user-2", canManage: false));

    [Fact]
    public void CanEditRequest_LetsAnOfficerActOnAnybodys() =>
        Assert.True(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: "user-1"), "user-2", canManage: true));

    /// <summary>
    /// A null viewer never matches, even against a row whose requester id is ALSO null.
    ///
    /// An unsynced member has no account behind the name, so "nobody" must not own "nobody else's" -
    /// otherwise one signed-out request would be editable by every other signed-out visitor.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void CanEditRequest_RefusesAViewerWithNoAccount(string? viewer)
    {
        Assert.False(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: null), viewer, canManage: false));
        Assert.False(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: "user-1"), viewer, canManage: false));
    }

    // Ordinal, not OrdinalIgnoreCase: an Identity user id is a case-sensitive GUID string, and
    // loosening the comparison here would widen ownership rather than be forgiving.
    [Fact]
    public void CanEditRequest_ComparesTheAccountIdExactly() =>
        Assert.False(ChartWishlistService.CanEditRequest(
            Request(1, "Xarcabard", appUserId: "USER-1"), "user-1", canManage: false));

    [Theory]
    [InlineData("pending", ChartWishlistStatuses.Pending)]
    [InlineData("  FULFILLED ", ChartWishlistStatuses.Fulfilled)]
    public void NormalizeStatus_CanonicalisesWhatItKnows(string typed, string expected) =>
        Assert.Equal(expected, ChartWishlistService.NormalizeStatus(typed));

    [Theory]
    [InlineData("Withdrawn")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeStatus_RejectsAnythingElse(string? typed) =>
        Assert.Null(ChartWishlistService.NormalizeStatus(typed));

    // ---- BuildWishlist and its badge counts -------------------------------------

    /// <summary>
    /// The counts are folded out of the SAME list the page renders, so a badge saying 3 above a list
    /// showing 2 is impossible. This pins the three things that keep a request OUT of a badge.
    /// </summary>
    [Fact]
    public void BuildWishlist_CountsPendingRequestsPerCard()
    {
        var board = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;
        var rows = new[]
        {
            Request(1, "Xarcabard"),
            Request(2, "xarcabard"),                                        // any case lands on the card
            Request(3, "Xarcabard", status: ChartWishlistStatuses.Fulfilled), // settled: no badge
            Request(4, null),                                               // anywhere: badges nothing
            Request(5, "Beaucedine"),
        };

        var built = ChartWishlistService.BuildWishlist(board, rows, "user-1", canManage: false);

        Assert.Equal(2, built.PendingCountsByBoss["Xarcabard"]);
        Assert.Equal(1, built.PendingCountsByBoss["Beaucedine"]);
        // Four pending in total, including the one tied to no card at all.
        Assert.Equal(4, built.PendingCount);
        Assert.Equal(5, built.Requests.Count);
    }

    // Leaving the zone blank said "anywhere", and there is no card to badge. It still appears in the
    // board's list, which is the honest rendering of what was asked for.
    [Fact]
    public void BuildWishlist_GivesABoardLevelRequestNoCardBadge()
    {
        var board = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;
        var built = ChartWishlistService.BuildWishlist(
            board, new[] { Request(1, null) }, "user-1", canManage: false);

        Assert.Empty(built.PendingCountsByBoss);
        Assert.Equal(1, built.PendingCount);
        Assert.Single(built.Requests);
    }

    // A row naming a card the board no longer has badges nothing rather than throwing - the same
    // degrade-quietly call ChartBoard.LeadsToFor makes for a bad arrow target.
    [Fact]
    public void BuildWishlist_IgnoresARowNamingACardThisBoardDoesNotHave()
    {
        var board = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;
        var built = ChartWishlistService.BuildWishlist(
            board, new[] { Request(1, "Atlantis") }, "user-1", canManage: false);

        Assert.Empty(built.PendingCountsByBoss);
        Assert.Single(built.Requests);
    }

    // Decided per viewer HERE so no template re-derives it.
    [Fact]
    public void BuildWishlist_StampsCanWithdrawPerViewer()
    {
        var board = ChartBoardCatalog.Find(ChartBoardCatalog.Dynamis)!;
        var rows = new[] { Request(1, null, appUserId: "user-1"), Request(2, null, appUserId: "user-9") };

        var asOwner = ChartWishlistService.BuildWishlist(board, rows, "user-1", canManage: false);
        Assert.True(asOwner.Requests[0].CanWithdraw);
        Assert.False(asOwner.Requests[1].CanWithdraw);

        var asOfficer = ChartWishlistService.BuildWishlist(board, rows, "user-1", canManage: true);
        Assert.All(asOfficer.Requests, request => Assert.True(request.CanWithdraw));
    }

    // ---- ReorderAsync: the same all-or-nothing boundary credits draw -------------

    [Fact]
    public async Task ReorderAsync_RewritesPrioritiesInTheOrderGiven()
    {
        using var db = NewInMemoryContext();
        db.ChartWishlistRequests.AddRange(Request(1, null), Request(2, null), Request(3, null));
        await db.SaveChangesAsync();

        var error = await new ChartWishlistService(db).ReorderAsync(
            1, ChartBoardCatalog.Dynamis, new[] { 3, 1, 2 }, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Null(error);
        Assert.Equal(
            new[] { 3, 1, 2 },
            db.ChartWishlistRequests.OrderBy(row => row.Priority).Select(row => row.Id).ToArray());
    }

    // An id from another linkshell refuses the WHOLE reorder rather than shuffling the rest: a
    // partial write here leaves a queue nobody asked for. Same boundary ResolveCreditsAsync draws.
    [Fact]
    public async Task ReorderAsync_RefusesTheWholeListOnAForeignId()
    {
        using var db = NewInMemoryContext();
        var mine = Request(1, null);
        var theirs = Request(2, null);
        theirs.LinkshellId = 99;
        db.ChartWishlistRequests.AddRange(mine, theirs);
        await db.SaveChangesAsync();

        var error = await new ChartWishlistService(db).ReorderAsync(
            1, ChartBoardCatalog.Dynamis, new[] { 2, 1 }, CancellationToken.None);

        Assert.NotNull(error);
        // Nothing moved: the legitimate half is NOT reordered.
        Assert.Equal(0, db.ChartWishlistRequests.Single(row => row.Id == 1).Priority);
    }

    // A request on another BOARD is refused the same way - the ordered list is a board's queue.
    [Fact]
    public async Task ReorderAsync_RefusesAnIdFromAnotherBoard()
    {
        using var db = NewInMemoryContext();
        db.ChartWishlistRequests.Add(Request(1, null));
        db.ChartWishlistRequests.Add(Request(2, null, board: ChartBoardCatalog.Limbus));
        await db.SaveChangesAsync();

        Assert.NotNull(await new ChartWishlistService(db).ReorderAsync(
            1, ChartBoardCatalog.Dynamis, new[] { 2, 1 }, CancellationToken.None));
    }
}
