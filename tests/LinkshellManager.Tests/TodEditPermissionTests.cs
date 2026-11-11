using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Who may edit or delete a ToD, and what an edit may do with its picture.
//
// Update and delete used to need only membership, so any member (even a Trial) could move or
// delete a monster's ToD with one API call - and every parked board, the Discord ToD board and the
// in-game tracker would follow it. Both now need Manage ToDs, the same right as logging one.
// Separately, an edit could point ImagePath at ANOTHER ToD's screenshot and then clear it, which
// deleted that file from disk. Now an edit may keep, clear, or swap in a fresh upload only.
public class TodEditPermissionTests
{
    private const int LinkshellId = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Tod NewTod(int id, string? imagePath) => new()
    {
        Id = id,
        LinkshellId = LinkshellId,
        MonsterName = "Fafnir/Nidhogg",
        Time = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
        TimeStamp = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
        ImagePath = imagePath
    };

    private static Task<(bool Allowed, string? ImagePath)> ResolveAsync(ApplicationDbContext db, Tod tod, string? requested)
        => ActivityDataController.ResolveTodImageForUpdateAsync(db, tod, requested, CancellationToken.None);

    // ---- Permission: pin what the gate means for the built-in ranks, and that both endpoints use it.

    [Theory]
    [InlineData(LinkshellRanks.Leader, true)]
    [InlineData(LinkshellRanks.Officer, true)]
    [InlineData(LinkshellRanks.Member, false)]
    [InlineData(LinkshellRanks.Trial, false)]
    public void DefaultRoles_ManageTods(string rank, bool expected)
    {
        var role = LinkshellRoleDefaults.BuildDefaultRoles(linkshellId: 1).Single(r => r.Name == rank);
        Assert.Equal(expected, role.CanManageTods);
    }

    // Reflection can't prove a call site, so pin the shape (precedent: AuctionPermissionTests):
    // update and delete both check Manage ToDs, and neither falls back to "is a member".
    [Theory]
    [InlineData("DeleteTodAsync(", "UploadTodImageAsync(")]
    [InlineData("UpdateTodAsync(", "SaveChangesAsync(")]
    public void TodEndpoint_RequiresManageTods(string start, string end)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "ActivityDataController.Tod.cs"));

        var from = text.IndexOf(start, StringComparison.Ordinal);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"{start} not found.");

        var body = text[from..to];
        Assert.Contains("CanAsync(membership, r => r.CanManageTods", body);
        Assert.DoesNotContain("if (membership is null)", body);
    }

    // ---- Image on update.

    [Fact]
    public async Task KeepingTheSamePicture_IsAllowed()
    {
        await using var db = NewDb();
        var tod = NewTod(1, "/uploads/tods/mine.png");
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var (allowed, path) = await ResolveAsync(db, tod, "/uploads/tods/mine.png");

        Assert.True(allowed);
        Assert.Equal("/uploads/tods/mine.png", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/x.png")]
    public async Task BlankOrNonUploadPath_ClearsThePicture(string? requested)
    {
        await using var db = NewDb();
        var tod = NewTod(1, "/uploads/tods/mine.png");
        db.Tods.Add(tod);
        await db.SaveChangesAsync();

        var (allowed, path) = await ResolveAsync(db, tod, requested);

        Assert.True(allowed);
        Assert.Null(path);
    }

    // The edit form uploads a new screenshot first, then saves the ToD with its fresh path.
    [Fact]
    public async Task AFreshUpload_IsAllowed()
    {
        await using var db = NewDb();
        var tod = NewTod(1, "/uploads/tods/mine.png");
        db.Tods.AddRange(tod, NewTod(2, "/uploads/tods/theirs.png"));
        await db.SaveChangesAsync();

        var (allowed, path) = await ResolveAsync(db, tod, "/uploads/tods/fresh.png");

        Assert.True(allowed);
        Assert.Equal("/uploads/tods/fresh.png", path);
    }

    [Fact]
    public async Task AnotherTodsPicture_IsRefused()
    {
        await using var db = NewDb();
        var tod = NewTod(1, null);
        db.Tods.AddRange(tod, NewTod(2, "/uploads/tods/theirs.png"));
        await db.SaveChangesAsync();

        var (allowed, path) = await ResolveAsync(db, tod, "/uploads/tods/theirs.png");

        Assert.False(allowed);
        Assert.Null(path);
    }

    // An extra folder lands on the same file on disk (only the file name is used), so it is
    // refused even when it looks like a new path.
    [Fact]
    public async Task AnotherTodsPicture_ThroughAnExtraFolder_IsRefused()
    {
        await using var db = NewDb();
        var tod = NewTod(1, null);
        db.Tods.AddRange(tod, NewTod(2, "/uploads/tods/theirs.png"));
        await db.SaveChangesAsync();

        var (allowed, _) = await ResolveAsync(db, tod, "/uploads/tods/sneaky/theirs.png");

        Assert.False(allowed);
    }

    [Fact]
    public async Task AWaitingSubmissionsPicture_IsRefused()
    {
        await using var db = NewDb();
        var tod = NewTod(1, null);
        db.Tods.Add(tod);
        db.PendingTodSubmissions.Add(new PendingTodSubmission
        {
            Id = 5,
            LinkshellId = LinkshellId,
            ImagePath = "/uploads/tods/queued.png"
        });
        await db.SaveChangesAsync();

        var (allowed, _) = await ResolveAsync(db, tod, "/uploads/tods/queued.png");

        Assert.False(allowed);
    }

    // ---- The "still in use?" check the delete-from-disk step relies on.

    [Fact]
    public async Task UsedElsewhere_IgnoresTheTodItself_ButSeesOthersUnderAnySpelling()
    {
        await using var db = NewDb();
        db.Tods.AddRange(NewTod(1, "/uploads/tods/shared.png"), NewTod(2, "/uploads/tods/old/shared.png"));
        db.Tods.Add(NewTod(3, "/uploads/tods/alone.png"));
        await db.SaveChangesAsync();

        Assert.True(await ActivityDataController.IsTodImageUsedElsewhereAsync(db, "shared.png", 1, CancellationToken.None));
        Assert.False(await ActivityDataController.IsTodImageUsedElsewhereAsync(db, "alone.png", 3, CancellationToken.None));
        // A different file that merely ends the same way is not a match.
        Assert.False(await ActivityDataController.IsTodImageUsedElsewhereAsync(db, "lone.png", 99, CancellationToken.None));
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
