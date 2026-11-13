using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
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
        var dto = ActivityWindowEventsController.MapActivityWindowSnapshot(
            Capture("member-1", "officer-1"), accountNames: Names);

        Assert.Equal("Reviewer", dto.ConfirmedByName);
    }

    [Fact]
    public void OfficersOwnPost_SaysNothing()
    {
        Assert.Null(ActivityWindowEventsController.ConfirmerIdToShow(Capture("officer-1", "officer-1")));
        Assert.Null(ActivityWindowEventsController.MapActivityWindowSnapshot(
            Capture("officer-1", "officer-1"), accountNames: Names).ConfirmedByName);
    }

    [Fact]
    public void Pending_OrRejected_SaysNothing()
    {
        Assert.Null(ActivityWindowEventsController.ConfirmerIdToShow(
            Capture("member-1", null, AttendanceSnapshotStatuses.Pending)));
        // A Reject stamps the same two fields; "Confirmed by" would be the wrong word for it.
        Assert.Null(ActivityWindowEventsController.ConfirmerIdToShow(
            Capture("member-1", "officer-1", AttendanceSnapshotStatuses.Ignored)));
    }

    [Fact]
    public void NoNameLookup_LeavesTheLineOff()
    {
        Assert.Null(ActivityWindowEventsController.MapActivityWindowSnapshot(
            Capture("member-1", "officer-1")).ConfirmedByName);
    }

    // One officer rejects a capture, another puts it back with "Use". The card must not then say
    // "Confirmed by" the officer who rejected it: "Use" restamps with whoever pressed it.
    [Fact]
    public async Task UseOnARejectedCapture_RestampsWithWhoeverPressedIt()
    {
        await using var db = await PostedCardCaptureEditTests.SeededAsync(
            posted: false, AttendanceSnapshotStatuses.Ignored);
        await StampAsync(db, "rejector");

        var result = await PostedCardCaptureEditTests.NewController(db).SetWindowSnapshotStatusAsync(
            PostedCardCaptureEditTests.OnCardId,
            new ActivityWindowEventsController.ActivityWindowSnapshotStatusRequest(AttendanceSnapshotStatuses.Active),
            CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var saved = await LoadAsync(db);
        Assert.Equal(PostedCardCaptureEditTests.Officer, saved.VerifiedByAppUserId);
        Assert.NotNull(saved.VerifiedAtUtc);
    }

    // Sent back for review: nobody has vouched for it any more.
    [Fact]
    public async Task SendingBackToPending_ClearsTheStamp()
    {
        await using var db = await PostedCardCaptureEditTests.SeededAsync(
            posted: false, AttendanceSnapshotStatuses.Active);
        await StampAsync(db, "confirmer");

        await PostedCardCaptureEditTests.NewController(db).SetWindowSnapshotStatusAsync(
            PostedCardCaptureEditTests.OnCardId,
            new ActivityWindowEventsController.ActivityWindowSnapshotStatusRequest(AttendanceSnapshotStatuses.Pending),
            CancellationToken.None);

        var saved = await LoadAsync(db);
        Assert.Null(saved.VerifiedByAppUserId);
        Assert.Null(saved.VerifiedAtUtc);
    }

    // Ignore keeps the stamp: it is not shown while ignored, and "Use" restamps it anyway.
    [Fact]
    public async Task Ignore_LeavesTheStampAlone()
    {
        await using var db = await PostedCardCaptureEditTests.SeededAsync(
            posted: false, AttendanceSnapshotStatuses.Active);
        await StampAsync(db, "confirmer");

        await PostedCardCaptureEditTests.NewController(db).SetWindowSnapshotStatusAsync(
            PostedCardCaptureEditTests.OnCardId,
            new ActivityWindowEventsController.ActivityWindowSnapshotStatusRequest(AttendanceSnapshotStatuses.Ignored),
            CancellationToken.None);

        Assert.Equal("confirmer", (await LoadAsync(db)).VerifiedByAppUserId);
    }

    private static async Task StampAsync(LinkshellManagerDiscordApp.Data.ApplicationDbContext db, string verifiedBy)
    {
        var snapshot = await db.AttendanceSnapshots.SingleAsync(s => s.Id == PostedCardCaptureEditTests.OnCardId);
        snapshot.PostedByAppUserId = "member-1";
        snapshot.VerifiedByAppUserId = verifiedBy;
        snapshot.VerifiedAtUtc = new DateTime(2026, 11, 10, 22, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static Task<AttendanceSnapshot> LoadAsync(LinkshellManagerDiscordApp.Data.ApplicationDbContext db) =>
        db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == PostedCardCaptureEditTests.OnCardId);
}
