using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The reviewer side of the ToD submission queue, behind the Activity's ToD Submissions Needing
// Approval section. Pinned here: scoping. Every review and
// reject call names the linkshell it acts in, and a submission from another linkshell is NotFound
// and left exactly as it was. Reject used to take only the submission id, so a reviewer in one
// linkshell could delete another linkshell's queue.
public class SubmissionApprovalReviewTests
{
    private const int HomeLinkshell = 1;
    private const int OtherLinkshell = 2;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static SubmissionApprovalService NewService(ApplicationDbContext db) =>
        new(db, TodCallers.Recorder(db), NullLogger<SubmissionApprovalService>.Instance);

    private static async Task<PendingTodSubmission> SeedTodAsync(ApplicationDbContext db, int linkshellId)
    {
        var submission = new PendingTodSubmission { LinkshellId = linkshellId, SubmittedAtUtc = DateTime.UtcNow, MonsterName = "Fafnir" };
        db.PendingTodSubmissions.Add(submission);
        await db.SaveChangesAsync();
        return submission;
    }

    [Fact]
    public async Task Reject_InAnotherLinkshell_IsNotFound_AndKeepsTheSubmission()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedTodAsync(db, OtherLinkshell);

        Assert.Equal(ApprovalResult.NotFound, await NewService(db).RejectTodAsync(HomeLinkshell, submission.Id, null, CancellationToken.None));
        Assert.Single(db.PendingTodSubmissions);
    }

    [Fact]
    public async Task Reject_InItsOwnLinkshell_RemovesIt()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedTodAsync(db, HomeLinkshell);

        Assert.Equal(ApprovalResult.Rejected, await NewService(db).RejectTodAsync(HomeLinkshell, submission.Id, null, CancellationToken.None));
        Assert.Empty(db.PendingTodSubmissions);
    }

    [Fact]
    public async Task ApproveTodWithEdits_InAnotherLinkshell_IsNotFound_AndKeepsTheSubmission()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedTodAsync(db, OtherLinkshell);
        var edits = new PendingTodEdits("Nidhogg", null, null, null, null, null, null);

        var result = await NewService(db).ApproveTodWithEditsAsync(HomeLinkshell, submission.Id, edits, CancellationToken.None);

        Assert.Equal(ApprovalResult.NotFound, result);
        Assert.Equal("Fafnir", db.PendingTodSubmissions.Single().MonsterName);
    }

    // A ToD carries no loot: approving a submission creates the ToD and moves nobody's DKP.
    [Fact]
    public async Task Approve_CreatesTheTodWithoutLoot()
    {
        await using var db = NewInMemoryContext();
        var submission = await SeedTodAsync(db, HomeLinkshell);

        var result = await NewService(db).ApproveTodWithEditsAsync(
            HomeLinkshell, submission.Id,
            new PendingTodEdits(null, null, true, null, null, null, null), CancellationToken.None);

        Assert.Equal(ApprovalResult.Approved, result);
        Assert.Single(db.Tods);
        Assert.Empty(db.DkpLedgerEntries);
        Assert.Empty(db.PendingTodSubmissions);
    }
}
