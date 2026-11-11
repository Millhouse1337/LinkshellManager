using System;
using System.IO;
using System.Linq;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// Who may create and run auctions.
//
// Close pays treasury gil to the winner and draws inventory down, so "any member" was too wide: a
// member could auction the whole treasury, bid 1 DKP on it, and close it to themselves. Now create
// needs Manage auctions, and the other actions need the creator OR Manage auctions - and either way
// the caller must still be a member of the auction's own linkshell.
public class AuctionPermissionTests
{
    private const string CreatorId = "creator-user";
    private const string OtherId = "other-user";

    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static Auction PendingAuction() => new()
    {
        Id = 1,
        LinkshellId = 7,
        CreatedByUserId = CreatorId,
        StartTime = Now.AddHours(1),
        EndTime = Now.AddHours(2)
    };

    [Fact]
    public void Creator_WhoIsStillAMember_ControlsTheirAuction()
    {
        Assert.True(ActivityDataController.CanControlAuction(
            CreatorId, PendingAuction(), isMemberOfAuctionLinkshell: true, canManageAuctions: false));
    }

    // Creator ids are compared the same way as before (case-insensitive).
    [Fact]
    public void Creator_IdMatch_IgnoresCase()
    {
        Assert.True(ActivityDataController.CanControlAuction(
            CreatorId.ToUpperInvariant(), PendingAuction(), isMemberOfAuctionLinkshell: true, canManageAuctions: false));
    }

    // A creator who was removed from the linkshell must not keep control of what they left behind.
    [Fact]
    public void Creator_NoLongerAMember_LosesControl()
    {
        Assert.False(ActivityDataController.CanControlAuction(
            CreatorId, PendingAuction(), isMemberOfAuctionLinkshell: false, canManageAuctions: false));
    }

    // An officer can clean up an auction someone else started.
    [Fact]
    public void OtherMember_WithManageAuctions_ControlsAnyAuction()
    {
        Assert.True(ActivityDataController.CanControlAuction(
            OtherId, PendingAuction(), isMemberOfAuctionLinkshell: true, canManageAuctions: true));
    }

    // A plain member can't end, close or delete an auction someone else created.
    [Fact]
    public void OtherMember_WithoutManageAuctions_HasNoControl()
    {
        Assert.False(ActivityDataController.CanControlAuction(
            OtherId, PendingAuction(), isMemberOfAuctionLinkshell: true, canManageAuctions: false));
    }

    [Fact]
    public void NonMember_HasNoControl_EvenWithThePermissionFlag()
    {
        Assert.False(ActivityDataController.CanControlAuction(
            OtherId, PendingAuction(), isMemberOfAuctionLinkshell: false, canManageAuctions: true));
    }

    [Fact]
    public void Edit_And_Start_FollowControl()
    {
        var auction = PendingAuction();

        Assert.True(ActivityDataController.CanEditAuction(canControl: true, auction, Now));
        Assert.True(ActivityDataController.CanStartAuction(canControl: true, auction, Now));
        Assert.False(ActivityDataController.CanEditAuction(canControl: false, auction, Now));
        Assert.False(ActivityDataController.CanStartAuction(canControl: false, auction, Now));
    }

    // Control does not override the time rules: an ended auction still can't be edited or started.
    [Fact]
    public void Edit_And_Start_StillBlockedOnceEnded()
    {
        var auction = PendingAuction();
        var afterEnd = auction.EndTime!.Value.AddMinutes(1);

        Assert.False(ActivityDataController.CanEditAuction(canControl: true, auction, afterEnd));
        Assert.False(ActivityDataController.CanStartAuction(canControl: true, auction, afterEnd));
    }

    // The create gate is CanAsync(membership, r => r.CanManageAuctions). These pin what that means
    // for the built-in ranks out of the box: leadership yes, members and trials no.
    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public void DefaultRoles_ManageAuctions(string rank, bool expected)
    {
        var role = LinkshellRoleDefaults.BuildDefaultRoles(linkshellId: 1).Single(r => r.Name == rank);
        Assert.Equal(expected, role.CanManageAuctions);
    }

    // Reflection can't prove a call site, so pin the shape: create checks the permission, and no
    // auction endpoint falls back to a bare "is the creator" test (which skips the membership
    // re-check). Precedent: ChartsWriteGateParityTests.
    [Fact]
    public void AuctionEndpoints_UseThePermissionGates()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "ActivityDataController.Auctions.cs"));

        var createStart = text.IndexOf("CreateAuctionAsync(", StringComparison.Ordinal);
        var createEnd = text.IndexOf("_dbContext.Auctions.Add(", createStart, StringComparison.Ordinal);
        Assert.True(createStart >= 0 && createEnd > createStart, "CreateAuctionAsync not found.");
        Assert.Contains("CanManageAuctions", text[createStart..createEnd]);

        Assert.DoesNotContain("IsAuctionCreator(", text);
    }

    /// <summary>Walks up from the test binary to the repo root, which holds the app's Controllers.</summary>
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
