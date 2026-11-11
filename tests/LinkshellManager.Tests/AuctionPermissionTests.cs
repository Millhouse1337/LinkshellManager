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
// and every other action (edit, start, end, close, delete) need Manage auctions in the auction's
// own linkshell. Being the creator no longer counts on its own.
public class AuctionPermissionTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static Auction PendingAuction() => new()
    {
        Id = 1,
        LinkshellId = 7,
        CreatedByUserId = "creator-user",
        StartTime = Now.AddHours(1),
        EndTime = Now.AddHours(2)
    };

    // Being the creator is not enough on its own. A creator who was demoted, or a plain member who
    // made an auction before create needed the permission, could otherwise edit in the treasury's
    // gil and close it to themselves.
    [Fact]
    public void Member_WithoutManageAuctions_HasNoControl_EvenIfTheyCreatedIt()
    {
        Assert.False(ActivityDataController.CanControlAuction(
            isMemberOfAuctionLinkshell: true, canManageAuctions: false));
    }

    // An officer can run any auction in their linkshell, including one someone else started.
    [Fact]
    public void Member_WithManageAuctions_ControlsAnyAuction()
    {
        Assert.True(ActivityDataController.CanControlAuction(
            isMemberOfAuctionLinkshell: true, canManageAuctions: true));
    }

    // Someone who was removed from the linkshell must not keep control of what they left behind.
    [Fact]
    public void NonMember_HasNoControl_EvenWithThePermissionFlag()
    {
        Assert.False(ActivityDataController.CanControlAuction(
            isMemberOfAuctionLinkshell: false, canManageAuctions: true));
        Assert.False(ActivityDataController.CanControlAuction(
            isMemberOfAuctionLinkshell: false, canManageAuctions: false));
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
