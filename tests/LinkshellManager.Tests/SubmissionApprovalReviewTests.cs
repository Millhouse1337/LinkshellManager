using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The reviewer side of the pending-submission queue, shared by the Razor review pages and the
// Activity's Pending Submissions tab. Two things are pinned here:
//   * Scoping: every review and reject call names the linkshell it acts in, and a submission
//     from another linkshell is NotFound and left exactly as it was. Reject used to take only
//     the submission id, so a reviewer in one linkshell could delete another linkshell's queue.
//   * Corrections: unticked snapshot entries are dropped before the snapshot is materialized.
public class SubmissionApprovalReviewTests
{
    private const int HomeLinkshell = 1;
    private const int OtherLinkshell = 2;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SubmissionApprovalService NewService(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        return new SubmissionApprovalService(
            db,
            new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance),
            pools,
            NullLogger<SubmissionApprovalService>.Instance);
    }

    private static async Task<PendingAttendanceSnapshotSubmission> SeedSnapshotAsync(ApplicationDbContext db, int linkshellId)
    {
        var submission = new PendingAttendanceSnapshotSubmission
        {
            LinkshellId = linkshellId,
            SubmittedAtUtc = DateTime.UtcNow,
            CapturedAtUtc = DateTime.UtcNow,
            CapturedByCharacterName = "Capturer",
            EntryCount = 3,
            Entries =
            {
                new PendingAttendanceSnapshotEntry { CharacterName = "Alpha" },
                new PendingAttendanceSnapshotEntry { CharacterName = "Bravo" },
                new PendingAttendanceSnapshotEntry { CharacterName = "Charlie" }
            }
        };
        db.PendingAttendanceSnapshotSubmissions.Add(submission);
        await db.SaveChangesAsync();
        return submission;
    }

    [Fact]
    public async Task ApproveSnapshotKeeping_DropsUntickedEntries()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedSnapshotAsync(db, HomeLinkshell);
        var keep = submission.Entries.Where(e => e.CharacterName != "Bravo").Select(e => e.Id).ToList();

        var result = await NewService(db).ApproveSnapshotKeepingAsync(HomeLinkshell, submission.Id, keep, CancellationToken.None);

        Assert.Equal(ApprovalResult.Approved, result);
        var snapshot = Assert.Single(db.AttendanceSnapshots);
        Assert.Equal(2, snapshot.EntryCount);
        Assert.Equal(new[] { "Alpha", "Charlie" }, db.AttendanceSnapshotEntries.Select(e => e.CharacterName).OrderBy(n => n).ToArray());
        Assert.Empty(db.PendingAttendanceSnapshotSubmissions);
    }

    [Fact]
    public async Task ApproveSnapshotKeeping_InAnotherLinkshell_IsNotFound_AndTouchesNothing()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedSnapshotAsync(db, OtherLinkshell);

        var result = await NewService(db).ApproveSnapshotKeepingAsync(HomeLinkshell, submission.Id, Array.Empty<int>(), CancellationToken.None);

        Assert.Equal(ApprovalResult.NotFound, result);
        Assert.Equal(3, db.PendingAttendanceSnapshotEntries.Count());
        Assert.Empty(db.AttendanceSnapshots);
    }

    [Fact]
    public async Task Reject_InAnotherLinkshell_IsNotFound_AndKeepsTheSubmission()
    {
        await using var db = NewInMemoryContext();
        var snapshot = await SeedSnapshotAsync(db, OtherLinkshell);
        db.PendingTodSubmissions.Add(new PendingTodSubmission { LinkshellId = OtherLinkshell, SubmittedAtUtc = DateTime.UtcNow, MonsterName = "Fafnir" });
        db.PendingAttendanceWindowSubmissions.Add(new PendingAttendanceWindowSubmission { LinkshellId = OtherLinkshell, SubmittedAtUtc = DateTime.UtcNow, EventId = 5, WindowIndex = 1 });
        await db.SaveChangesAsync();
        var service = NewService(db);

        Assert.Equal(ApprovalResult.NotFound, await service.RejectTodAsync(HomeLinkshell, db.PendingTodSubmissions.Single().Id, null, CancellationToken.None));
        Assert.Equal(ApprovalResult.NotFound, await service.RejectAttendanceWindowAsync(HomeLinkshell, db.PendingAttendanceWindowSubmissions.Single().Id, null, CancellationToken.None));
        Assert.Equal(ApprovalResult.NotFound, await service.RejectSnapshotAsync(HomeLinkshell, snapshot.Id, null, CancellationToken.None));

        Assert.Single(db.PendingTodSubmissions);
        Assert.Single(db.PendingAttendanceWindowSubmissions);
        Assert.Single(db.PendingAttendanceSnapshotSubmissions);
    }

    [Fact]
    public async Task Reject_InItsOwnLinkshell_RemovesIt()
    {
        await using var db = NewInMemoryContext();
        var snapshot = await SeedSnapshotAsync(db, HomeLinkshell);

        Assert.Equal(ApprovalResult.Rejected, await NewService(db).RejectSnapshotAsync(HomeLinkshell, snapshot.Id, null, CancellationToken.None));
        Assert.Empty(db.PendingAttendanceSnapshotSubmissions);
    }

    [Fact]
    public async Task ApproveTodWithEdits_InAnotherLinkshell_IsNotFound_AndKeepsTheSubmission()
    {
        await using var db = NewInMemoryContext();
        db.PendingTodSubmissions.Add(new PendingTodSubmission { LinkshellId = OtherLinkshell, SubmittedAtUtc = DateTime.UtcNow, MonsterName = "Fafnir" });
        await db.SaveChangesAsync();
        var edits = new PendingTodEdits("Nidhogg", null, null, null, null, null, null, null);

        var result = await NewService(db).ApproveTodWithEditsAsync(HomeLinkshell, db.PendingTodSubmissions.Single().Id, edits, CancellationToken.None);

        Assert.Equal(ApprovalResult.NotFound, result);
        Assert.Equal("Fafnir", db.PendingTodSubmissions.Single().MonsterName);
    }
}
