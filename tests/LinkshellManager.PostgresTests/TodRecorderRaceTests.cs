using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.PostgresTests;

// P3.6 (logic-10): two posts of one kill at the same moment -- two officers pressing Post in the
// addon, the addon and the app, or End Camp pressed twice -- each looked for the other's ToD before
// either had saved, found nothing, and both saved one. TodRecorder takes a lock per linkshell +
// spawn first, so the second waits for the first to commit and then finds its row.
[Collection("postgres")]
public class TodRecorderRaceTests : IAsyncLifetime
{
    private static readonly DateTime KilledAt = new(2026, 11, 5, 13, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public TodRecorderRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        await db.SaveChangesAsync();
        return linkshell.Id;
    }

    // One post, start to finish, on its own context (its own "request").
    private async Task<TodRecordResult> RecordAsync(
        int linkshellId, string monster, DateTime killedAt, bool? claim, params IInterceptor[] interceptors)
    {
        await using var db = _pg.CreateContext(interceptors);
        var recorder = new TodRecorder(db, new MonsterTimingResolver(db), NullLogger<TodRecorder>.Instance);
        return await recorder.RecordAsync(
            new TodRecordInput(linkshellId, monster, Hq: null, killedAt, AdditionalSeconds: null, PopWindow: null,
                claim, Killed: null, DayNumber: null, TodSources.Addon, ActorAppUserId: null),
            CancellationToken.None);
    }

    // The first post is held just before it saves its new row (it has already looked and found
    // nothing). The second starts then. Without the lock it would look, find nothing either, and save
    // a second ToD while the first waits; with it, it waits for the first to commit.
    [Fact]
    public async Task TwoPostsOfOneKillAtOnce_SaveOneTod()
    {
        var linkshellId = await SeedAsync();

        Task<TodRecordResult>? second = null;
        var hold = new BeforeFirstSave(async () =>
        {
            // A different spelling of the same spawn, a few seconds off, as two characters send it.
            second = RecordAsync(linkshellId, "Nidhogg", KilledAt.AddSeconds(4), claim: true);
            await Task.Delay(500);
            Assert.False(second.IsCompleted, "the second post should be waiting on the first's lock.");
        });

        var first = await RecordAsync(linkshellId, "Fafnir/Nidhogg", KilledAt, claim: null, hold);
        var secondResult = await second!;

        Assert.False(first.MergedIntoExisting);
        Assert.True(secondResult.MergedIntoExisting);
        Assert.Equal(first.Tod.Id, secondResult.Tod.Id);

        await using var check = _pg.CreateContext();
        var tod = await check.Tods.SingleAsync();
        Assert.Equal(KilledAt, tod.Time);
        Assert.True(tod.Claim); // the second post's answer, added to the first's row
    }

    // Plain overlap, several kills, no hand-holding: still one ToD per kill.
    [Fact]
    public async Task ManyPostsAtOnce_StillOneTodPerKill()
    {
        var linkshellId = await SeedAsync();

        for (var kill = 0; kill < 5; kill++)
        {
            var killedAt = KilledAt.AddDays(kill);
            await Task.WhenAll(
                RecordAsync(linkshellId, "Cerberus", killedAt, claim: true),
                RecordAsync(linkshellId, "Cerberus", killedAt.AddSeconds(3), claim: true),
                RecordAsync(linkshellId, "Cerberus", killedAt.AddSeconds(-2), claim: null));
        }

        await using var check = _pg.CreateContext();
        Assert.Equal(5, await check.Tods.CountAsync());
    }

    // A different spawn is a different lock: a Cerberus post never waits on a Fafnir one.
    [Fact]
    public async Task ADifferentSpawn_DoesNotWait()
    {
        var linkshellId = await SeedAsync();

        Task<TodRecordResult>? other = null;
        var hold = new BeforeFirstSave(async () =>
        {
            other = RecordAsync(linkshellId, "Cerberus", KilledAt, claim: true);
            await other.WaitAsync(TimeSpan.FromSeconds(10));
        });

        await RecordAsync(linkshellId, "Fafnir", KilledAt, claim: true, hold);

        Assert.False((await other!).MergedIntoExisting);
        await using var check = _pg.CreateContext();
        Assert.Equal(2, await check.Tods.CountAsync());
    }

    private sealed class BeforeFirstSave : SaveChangesInterceptor
    {
        private readonly Func<Task> _then;
        private bool _fired;

        public BeforeFirstSave(Func<Task> then) => _then = then;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await _then();
            }
            return result;
        }
    }
}
