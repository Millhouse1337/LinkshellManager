using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// The one invariant a windowed HNM board lives by: THE NUMBER IT PRINTS AND THE ROSTER UNDERNEATH
// IT MOVE ON THE SAME TICK. Every other test here checks a piece of that (the cadence table, the
// wipe itself, the capture it takes); this one walks a camp through the real transitions — queued
// → live → boundary → boundary — driving the same two background services production runs, and
// asserts the pairing at each step.
//
// It exists because the pairing broke at exactly one of those transitions and nothing caught it.
// FocusWindow is HnmWindowNumber + 1 whenever a next window exists, and "a next window exists"
// switches on when the camp goes LIVE — so a board flipped "Window 1 of 25" → "Window 2 of 25" on
// its own while the clear, gated on the counter, sat still. The camp then spent a full hour naming
// window 2 over window 1's signups, which is what "the window advanced but the board never
// cleared" looked like from Discord.
public class HnmWindowClearLifecycleTests
{
    // Every monster whose camp re-forms per window, and the cadence it re-forms on. The wyrms step
    // hourly and the ToAU three every six hours — two different bands, one rule; the kings/dragons
    // are covered separately as the deliberate NON-wiping case.
    public static TheoryData<string, int> WipingMonsters() => new()
    {
        { "Tiamat", 60 }, { "Jormungand", 60 }, { "Vrtra", 60 },
        { "Cerberus", 360 }, { "Hydra", 360 }, { "Khimaira", 360 },
    };

    private const int EventId = 1;

    // Hands both services the one in-memory context, standing in for the scope each opens per tick.
    private sealed class SingleContextScope : IServiceScope, IServiceProvider, IServiceScopeFactory
    {
        private readonly ApplicationDbContext _db;
        public SingleContextScope(ApplicationDbContext db) => _db = db;
        public IServiceProvider ServiceProvider => this;
        public IServiceScope CreateScope() => this;
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ApplicationDbContext) ? _db : null;
        public void Dispose() { }
    }

    private static Task GoLiveAsync(ApplicationDbContext db) =>
        new EventAutoStartBackgroundService(
            new SingleContextScope(db), NullLogger<EventAutoStartBackgroundService>.Instance)
            .StartDueEventsAsync(CancellationToken.None);

    private static Task TickAsync(ApplicationDbContext db) =>
        new HnmWindowAdvanceBackgroundService(
            new SingleContextScope(db), NullLogger<HnmWindowAdvanceBackgroundService>.Instance)
            .AdvanceLiveCampsAsync(CancellationToken.None);

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    // A posted-but-not-started board for `monster`, due to go live `dueIn` from now, with one empty
    // party on it. Only FKs are set — the in-memory provider fixes up the navigations the wipe's
    // Include chain walks (slot → party → alliance).
    private static ApplicationDbContext PostedBoard(string monster, TimeSpan dueIn)
    {
        var db = NewDb();
        db.Events.Add(new Event
        {
            Id = EventId, LinkshellId = 1, EventName = monster, EventType = "HNM",
            AssignedMonsterName = monster, StartTime = DateTime.UtcNow.Add(dueIn),
            HnmWindowNumber = 1, PartySetupId = 1,
        });
        db.PartySetupAlliances.Add(new PartySetupAlliance { Id = 10, PartySetupId = 1, Name = "Alliance A", SortOrder = 0 });
        db.PartySetupParties.Add(new PartySetupParty { Id = 100, PartySetupAllianceId = 10, Name = "Party 1", SortOrder = 0 });
        db.PartySetupSlots.Add(new PartySetupSlot { Id = 1000, PartySetupPartyId = 100, SortOrder = 0, Label = "(GHORN)" });
        db.SaveChanges();
        return db;
    }

    private static async Task SeatAsync(ApplicationDbContext db, string name)
    {
        db.EventPartySlotSignups.Add(new EventPartySlotSignup
        {
            EventId = EventId, PartySetupSlotId = 1000, AppUserId = $"user-{name}",
            CharacterName = name, MainJob = "BLM", SubJob = "RDM",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // Winds the camp's clock back by one cadence, which is how a boundary arrives without waiting
    // one out: the advancer derives everything from WindowAnchorAt, so moving the anchor into the
    // past is indistinguishable from time passing.
    private static async Task ElapseOneWindowAsync(ApplicationDbContext db, int minutes)
    {
        var ev = await db.Events.FirstAsync();
        ev.StartTime = ev.StartTime!.Value.AddMinutes(-minutes);
        ev.CommencementStartTime = ev.CommencementStartTime!.Value.AddMinutes(-minutes);
        ev.WindowAnchorAt = ev.WindowAnchorAt!.Value.AddMinutes(-minutes);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // What Discord is showing: the window number on the board, and how many people are seated
    // under it.
    private static async Task<(int Printed, int Seated)> ReadBoardAsync(ApplicationDbContext db)
    {
        db.ChangeTracker.Clear();
        var ev = await db.Events.AsNoTracking().FirstAsync();
        return (DiscordEventMessageBuilder.FocusWindow(ev),
                await db.EventPartySlotSignups.AsNoTracking().CountAsync());
    }

    // The camp going live IS a window turnover: window 1's single pop chance is spent the instant
    // the mob either shows or doesn't, so the board steps to window 2 and the roster it collected
    // before the pop goes with it. This is the transition that was silently skipped.
    [Theory]
    [MemberData(nameof(WipingMonsters))]
    public async Task GoingLive_StepsToWindow2_AndClearsTheSignupsItCollectedBeforeThePop(
        string monster, int cadenceMinutes)
    {
        using var db = PostedBoard(monster, TimeSpan.FromSeconds(-5));
        await SeatAsync(db, "Millhouse");

        var posted = await ReadBoardAsync(db);
        Assert.Equal(1, posted.Printed);
        Assert.Equal(1, posted.Seated);   // pre-pop signups stand while the board is queued

        await GoLiveAsync(db);
        await TickAsync(db);

        var live = await ReadBoardAsync(db);
        Assert.Equal(2, live.Printed);
        Assert.Equal(0, live.Seated);
        Assert.Equal(cadenceMinutes, HnmConfig.WindowAdvanceMinutes(monster));
    }

    // …and the roster it wiped is filed under the number the board was PRINTING for it (1), which
    // is what "View Previous Window" asks for. Filing it under the counter would have hidden it:
    // the counter is still 1 here, and the viewer only offers captures below the live window.
    [Theory]
    [MemberData(nameof(WipingMonsters))]
    public async Task GoingLive_CapturesThePrePopRosterAsWindow1(string monster, int cadenceMinutes)
    {
        _ = cadenceMinutes;
        using var db = PostedBoard(monster, TimeSpan.FromSeconds(-5));
        await SeatAsync(db, "Millhouse");

        await GoLiveAsync(db);
        await TickAsync(db);

        var captured = await db.EventWindowRosterSnapshots.AsNoTracking().ToListAsync();
        var row = Assert.Single(captured);
        Assert.Equal(1, row.WindowNumber);
        Assert.Equal("Millhouse", row.CharacterName);
    }

    // The whole camp, end to end: every change of the printed number takes the roster with it, and
    // the number never changes without one. Re-seating between boundaries is what a camp actually
    // does — people re-sign for each hour — so this also proves the wipe isn't a one-shot.
    [Theory]
    [MemberData(nameof(WipingMonsters))]
    public async Task EveryPrintedWindowChange_ClearsTheRosterItWasNaming(string monster, int cadenceMinutes)
    {
        using var db = PostedBoard(monster, TimeSpan.FromSeconds(-5));
        await SeatAsync(db, "Millhouse");
        await GoLiveAsync(db);
        await TickAsync(db);

        // Stop one short of the camp's LAST window. There the printed number stops changing —
        // FocusWindow collapses onto the final window because there is no next one to await — so
        // there is no turnover left to assert, and demanding one would only be asserting that the
        // camp never ends. A wyrm's 25 windows never come near this; the ToAU three's 5 do.
        var lastTurnover = HnmConfig.EffectiveWindowCount(monster) - 1;
        for (var printed = 2; printed <= Math.Min(5, lastTurnover); printed++)
        {
            var atWindow = await ReadBoardAsync(db);
            Assert.Equal(printed, atWindow.Printed);
            Assert.Equal(0, atWindow.Seated);

            await SeatAsync(db, $"Camper{printed}");
            await ElapseOneWindowAsync(db, cadenceMinutes);
            await TickAsync(db);

            // The roster that just went is filed under the window the board was naming while it
            // stood — not the counter, which trails it by one for the whole live camp.
            var filed = await db.EventWindowRosterSnapshots.AsNoTracking()
                .Where(s => s.WindowNumber == printed).ToListAsync();
            Assert.Equal($"Camper{printed}", Assert.Single(filed).CharacterName);
        }
    }

    // A second tick inside the same window must not wipe the roster people have just re-signed
    // with — that is what HnmClearedWindow is for, and it now sits on the printed scale.
    [Theory]
    [MemberData(nameof(WipingMonsters))]
    public async Task FurtherTicksInsideTheSameWindow_LeaveTheRosterAlone(string monster, int cadenceMinutes)
    {
        _ = cadenceMinutes;
        using var db = PostedBoard(monster, TimeSpan.FromSeconds(-5));
        await GoLiveAsync(db);
        await TickAsync(db);
        await SeatAsync(db, "Millhouse");

        await TickAsync(db);
        await TickAsync(db);

        var board = await ReadBoardAsync(db);
        Assert.Equal(2, board.Printed);
        Assert.Equal(1, board.Seated);
    }

    // A board that has not gone live yet is still COLLECTING window 1 — the advancer skips it
    // outright, so a camp posted hours ahead keeps every signup it gathers until the pop.
    [Fact]
    public async Task AQueuedBoard_IsNeverWiped()
    {
        using var db = PostedBoard("Khimaira", TimeSpan.FromHours(3));
        await SeatAsync(db, "Millhouse");

        await TickAsync(db);

        var board = await ReadBoardAsync(db);
        Assert.Equal(1, board.Printed);
        Assert.Equal(1, board.Seated);
        Assert.Empty(await db.EventWindowRosterSnapshots.AsNoTracking().ToListAsync());
    }

    // The kings/dragons march through ONE continuous camp at 10-minute steps, so their board steps
    // its number without ever wiping — including at go-live. Guarded here so the pairing above
    // can't be extended onto monsters it was never meant to apply to.
    [Theory]
    [InlineData("Fafnir")]
    [InlineData("Behemoth")]
    [InlineData("Adamantoise")]
    public async Task TheKingsAndDragons_StepTheirWindowWithoutClearing(string monster)
    {
        using var db = PostedBoard(monster, TimeSpan.FromSeconds(-5));
        await SeatAsync(db, "Millhouse");
        await GoLiveAsync(db);
        await TickAsync(db);

        var live = await ReadBoardAsync(db);
        Assert.Equal(2, live.Printed);
        Assert.Equal(1, live.Seated);

        await ElapseOneWindowAsync(db, HnmConfig.WindowAdvanceMinutes(monster));
        await TickAsync(db);

        var next = await ReadBoardAsync(db);
        Assert.Equal(3, next.Printed);
        Assert.Equal(1, next.Seated);
    }
}
