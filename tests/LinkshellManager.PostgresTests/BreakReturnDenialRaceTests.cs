using System;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Deny on a member's "I'm back" takes the time back. When the member has taken another break since,
// that means taking the banked time off Duration -- so two Denies of the same return (a double
// click, or two officers at once) must not both do it. BreakReturnDenial
// locks the request's row: the second Deny waits, then finds it already resolved.
[Collection("postgres")]
public class BreakReturnDenialRaceTests : IAsyncLifetime
{
    private static readonly DateTime Start = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);

    private readonly PostgresFixture _pg;

    public BreakReturnDenialRaceTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Joined at Start, broke at +1h, self-returned at +2h (pending), broke again at +2h30 -- so 1.5h
    // banked, half an hour of it from the return Deny takes back. Returns (event id, return id).
    private async Task<(int EventId, int ReturnId)> SeedAsync()
    {
        await using var db = _pg.CreateContext();
        var linkshell = new Linkshell { LinkshellName = "LS" };
        db.Linkshells.Add(linkshell);
        db.Users.Add(new AppUser { Id = "member", UserName = "member", CharacterName = "Member" });
        var ev = new Event
        {
            Linkshell = linkshell,
            EventName = "Sky",
            EventType = "Sky",
            CommencementStartTime = Start,
            DkpPerHour = 10,
        };
        db.Events.Add(ev);
        var participation = new AppUserEvent
        {
            Event = ev,
            AppUserId = "member",
            CharacterName = "Member",
            StartTime = Start,
            Duration = 1.5,
            IsOnBreak = true,
        };
        db.AppUserEvents.Add(participation);
        var pendingReturn = new AppUserEventStatusLedger
        {
            AppUserEvent = participation, Event = ev,
            ActionType = "BreakReturn", OccurredAt = Start.AddHours(2), RequiresVerification = true,
        };
        db.AppUserEventStatusLedgers.AddRange(
            new AppUserEventStatusLedger
            {
                AppUserEvent = participation, Event = ev,
                ActionType = "BreakStart", OccurredAt = Start.AddHours(1),
            },
            pendingReturn,
            new AppUserEventStatusLedger
            {
                AppUserEvent = participation, Event = ev,
                ActionType = "BreakStart", OccurredAt = Start.AddHours(2.5),
            });
        await db.SaveChangesAsync();
        return (ev.Id, pendingReturn.Id);
    }

    private async Task<BreakReturnDenial.Outcome> DenyAsync(int eventId, int returnId)
    {
        await using var db = _pg.CreateContext();
        var ev = await db.Events.AsNoTracking().SingleAsync(e => e.Id == eventId);
        return await BreakReturnDenial.DenyAsync(db, ev, returnId, CancellationToken.None);
    }

    [Fact]
    public async Task TwoDeniesAtOnce_TheTimeComesOffOnce()
    {
        var (eventId, returnId) = await SeedAsync();

        // Hold the row the way a first Deny mid-save does, and start two Denies behind it.
        await using var holder = _pg.CreateContext();
        await using (var hold = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync(
                $"""SELECT 1 FROM "AppUserEventStatusLedgers" WHERE "Id" = {returnId} FOR UPDATE""");

            var first = DenyAsync(eventId, returnId);
            var second = DenyAsync(eventId, returnId);
            await Task.Delay(500);
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);

            await hold.RollbackAsync();
            var outcomes = await Task.WhenAll(first, second);

            Assert.Contains(BreakReturnDenial.Outcome.Denied, outcomes);
            Assert.Contains(BreakReturnDenial.Outcome.AlreadyResolved, outcomes);
        }

        await using var check = _pg.CreateContext();
        var participation = await check.AppUserEvents.SingleAsync();
        Assert.Equal(1.0, participation.Duration!.Value, 6);
        Assert.True(participation.IsOnBreak);
        var denied = await check.AppUserEventStatusLedgers.SingleAsync(e => e.Id == returnId);
        Assert.NotNull(denied.DeniedAt);
    }
}
