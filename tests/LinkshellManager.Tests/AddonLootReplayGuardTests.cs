using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Services;
using Xunit;

namespace LinkshellManager.Tests;

// A retried addon loot post must not charge the winner twice.
//
// The addon gives up waiting after a few seconds; if the first post had already been saved, the
// officer's second press of Post used to write a second row and take the DKP again. The addon now
// sends one clientRequestId per drop, and the server answers a repeat of a successful id with the
// first answer instead of writing again.
public class AddonLootReplayGuardTests
{
    private static Func<Task<(string Response, bool Succeeded)>> Post(Func<int> write, bool succeeds = true) =>
        () => Task.FromResult(($"row {write()}", succeeds));

    [Fact]
    public async Task SameId_SecondPost_GetsTheFirstAnswer_AndWritesNothing()
    {
        var guard = new AddonLootReplayGuard();
        var writes = 0;

        var first = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);
        var second = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);

        Assert.Equal(1, writes);
        Assert.False(first.Replayed);
        Assert.True(second.Replayed);
        Assert.Equal("row 1", second.Response);
        Assert.True(second.Succeeded);
    }

    [Fact]
    public async Task NoId_IsNeverDeduplicated()
    {
        // An older addon sends no id: every post is written, as before.
        var guard = new AddonLootReplayGuard();
        var writes = 0;

        await guard.RunOnceAsync(7, null, Post(() => ++writes), CancellationToken.None);
        await guard.RunOnceAsync(7, "   ", Post(() => ++writes), CancellationToken.None);
        await guard.RunOnceAsync(7, null, Post(() => ++writes), CancellationToken.None);

        Assert.Equal(3, writes);
    }

    [Fact]
    public async Task ARefusedPost_IsNotRemembered()
    {
        // "Not enough DKP" can be fixed and sent again with the same id.
        var guard = new AddonLootReplayGuard();
        var writes = 0;

        var refused = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes, succeeds: false), CancellationToken.None);
        var retried = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.False(retried.Replayed);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task OtherTokens_AndOtherIds_DoNotShare()
    {
        var guard = new AddonLootReplayGuard();
        var writes = 0;

        await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);
        var otherToken = await guard.RunOnceAsync(8, "drop-1", Post(() => ++writes), CancellationToken.None);
        var otherDrop = await guard.RunOnceAsync(7, "drop-2", Post(() => ++writes), CancellationToken.None);

        Assert.False(otherToken.Replayed);
        Assert.False(otherDrop.Replayed);
        Assert.Equal(3, writes);
    }

    [Fact]
    public async Task AnIdLongerThanTheLimit_IsIgnored()
    {
        var guard = new AddonLootReplayGuard();
        var writes = 0;
        var tooLong = new string('x', AddonLootReplayGuard.MaxKeyLength + 1);

        await guard.RunOnceAsync(7, tooLong, Post(() => ++writes), CancellationToken.None);
        await guard.RunOnceAsync(7, tooLong, Post(() => ++writes), CancellationToken.None);

        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task TheMemory_RunsOutAfterADay()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var guard = new AddonLootReplayGuard(() => now);
        var writes = 0;

        await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);
        now = now.AddHours(23);
        var withinADay = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);
        now = now.AddHours(2);
        var afterADay = await guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);

        Assert.True(withinADay.Replayed);
        Assert.False(afterADay.Replayed);
        Assert.Equal(2, writes);
    }

    [Fact]
    public async Task ARetry_ArrivingWhileTheFirstIsStillSaving_WaitsAndGetsItsAnswer()
    {
        var guard = new AddonLootReplayGuard();
        var writes = 0;
        var release = new TaskCompletionSource();

        var first = guard.RunOnceAsync(7, "drop-1", async () =>
        {
            await release.Task;
            return ($"row {++writes}", true);
        }, CancellationToken.None);
        var retry = guard.RunOnceAsync(7, "drop-1", Post(() => ++writes), CancellationToken.None);

        Assert.False(retry.IsCompleted);
        release.SetResult();
        var results = await Task.WhenAll(first, retry);

        Assert.Equal(1, writes);
        Assert.True(results[1].Replayed);
        Assert.Equal("row 1", results[1].Response);
    }

    // Wiring. Reflection can't prove a call site, so pin the shape (as AddonLootPermissionTests
    // does): the guard wraps the write, after the permission gate, keyed by the token.
    [Fact]
    public void AddAddonLoot_WritesThroughTheReplayGuard_AfterThePermissionGate()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoPath("Controllers"), "AddonApiController.AddonParticipation.cs"));
        var from = text.IndexOf("private async Task<IActionResult> AddAddonLootAsync(", StringComparison.Ordinal);
        var write = text.IndexOf("private async Task<IActionResult> WriteAddonLootAsync(", StringComparison.Ordinal);
        Assert.True(from >= 0 && write > from, "AddAddonLootAsync / WriteAddonLootAsync not found.");
        var body = text[from..write];

        var gate = body.IndexOf("TokenIssuerCanAsync(token, token.LinkshellId, PostsAddonLoot", StringComparison.Ordinal);
        var guard = body.IndexOf("lootReplay.RunOnceAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "The permission gate moved.");
        Assert.True(guard > gate, "The replay guard must run after the permission gate.");
        Assert.Contains("token.Id,", body);
        Assert.Contains("request.ClientRequestId,", body);
        Assert.Contains("WriteAddonLootAsync(", body);
        Assert.DoesNotContain("manualLoot.AddAsync(", body);
    }

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
