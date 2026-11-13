using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// "Confirmed by" on a capture. The confirm stamp was saved on every capture but never shown.
//
// It is only worth a line when somebody OTHER than the poster confirmed it: a moderator's own post
// is stamped as confirmed by themselves the moment it lands, and repeating their name says nothing.
public class SnapshotConfirmedByTests
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["member-1"] = "Poster",
        ["officer-1"] = "Reviewer",
    };

    private static AttendanceSnapshot Capture(
        string? postedBy, string? verifiedBy, string status = AttendanceSnapshotStatuses.Active) => new()
    {
        Id = 1,
        LinkshellId = 1,
        CapturedAtUtc = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc),
        SnapshotStatus = status,
        PostedByAppUserId = postedBy,
        VerifiedByAppUserId = verifiedBy,
        VerifiedAtUtc = verifiedBy is null ? null : new DateTime(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void MembersCapture_ConfirmedByAnOfficer_NamesTheOfficer()
    {
        var dto = ActivityDataController.MapActivityWindowSnapshot(
            Capture("member-1", "officer-1"), accountNames: Names);

        Assert.Equal("Reviewer", dto.ConfirmedByName);
    }

    [Fact]
    public void OfficersOwnPost_SaysNothing()
    {
        Assert.Null(ActivityDataController.ConfirmerIdToShow(Capture("officer-1", "officer-1")));
        Assert.Null(ActivityDataController.MapActivityWindowSnapshot(
            Capture("officer-1", "officer-1"), accountNames: Names).ConfirmedByName);
    }

    [Fact]
    public void Pending_OrRejected_SaysNothing()
    {
        Assert.Null(ActivityDataController.ConfirmerIdToShow(
            Capture("member-1", null, AttendanceSnapshotStatuses.Pending)));
        // A Reject stamps the same two fields; "Confirmed by" would be the wrong word for it.
        Assert.Null(ActivityDataController.ConfirmerIdToShow(
            Capture("member-1", "officer-1", AttendanceSnapshotStatuses.Ignored)));
    }

    [Fact]
    public void NoNameLookup_LeavesTheLineOff()
    {
        Assert.Null(ActivityDataController.MapActivityWindowSnapshot(
            Capture("member-1", "officer-1")).ConfirmedByName);
    }
}
