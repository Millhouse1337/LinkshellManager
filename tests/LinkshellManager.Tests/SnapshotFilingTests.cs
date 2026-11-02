using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Filing an unlinked /lsm now capture (the app's Unlinked Snapshots row, the addon's Unlinked Posts
// panel) always lands it as a MISC post, whatever it is filed on. There is no post type to choose:
// a capture filed as a window could pay that window twice, and window attendance is what the
// addon's own window posts record. Pinned because a capture landing as a window pays silently.
public class SnapshotFilingTests
{
    private const int CampId = 40;
    private const int EventId = 700;
    private const int Filing = 1; // the capture being filed

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static SnapshotFilingService NewFiling(ApplicationDbContext db) =>
        new(db, new WindowEventLinkService(db, new MonsterTimingResolver(db)), new AllianceIdentityService(db));

    private static readonly DateTime CapturedAt = new(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc);

    // As it arrives from /lsm now: unlinked, with the Window default and (from an older filing,
    // since undone) a stale window number.
    private static AttendanceSnapshot Capture(string? name = "stayed for ToD") => new()
    {
        Id = Filing,
        LinkshellId = 1,
        CapturedAtUtc = CapturedAt,
        SlotKind = AttendanceSnapshotSlotKinds.Window,
        WindowNumber = 3,
        SnapshotStatus = AttendanceSnapshotStatuses.Active,
        Name = name,
    };

    private static async Task<AttendanceSnapshot> SeedAsync(ApplicationDbContext db, AttendanceSnapshot capture)
    {
        db.AttendanceSnapshots.Add(capture);
        await db.SaveChangesAsync();
        return await db.AttendanceSnapshots.Include(s => s.Entries).SingleAsync(s => s.Id == Filing);
    }

    private static Task<AttendanceSnapshot> SavedAsync(ApplicationDbContext db) =>
        db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == Filing);

    [Fact]
    public async Task OntoALiveCamp_LandsAsMisc()
    {
        using var db = NewDb();
        db.Events.Add(new Event
        {
            Id = CampId, LinkshellId = 1, EventName = "Cerberus", EventType = "HNM",
            CommencementStartTime = CapturedAt.AddHours(-1),
        });
        var snapshot = await SeedAsync(db, Capture());

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(null, null, CampId, false), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FiledAgainstCamp);
        var saved = await SavedAsync(db);
        Assert.Equal(CampId, saved.LinkedEventId);
        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, saved.SlotKind);
        Assert.Null(saved.WindowNumber);
    }

    [Fact]
    public async Task OntoAnOpenAttendanceEvent_LandsAsMisc()
    {
        using var db = NewDb();
        db.WindowEvents.Add(new WindowEvent
        {
            Id = EventId, LinkshellId = 1, Name = "Sky", NormalizedName = "SKY",
            Status = WindowEventStatuses.Open, FirstCapturedAtUtc = CapturedAt, LastCapturedAtUtc = CapturedAt,
        });
        var snapshot = await SeedAsync(db, Capture());

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(EventId, null, null, false), CancellationToken.None);

        Assert.Null(result.Error);
        var saved = await SavedAsync(db);
        Assert.Equal(EventId, saved.WindowEventId);
        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, saved.SlotKind);
        Assert.Null(saved.WindowNumber);
    }

    // Make New Event From This Post: a new event named after the post, and the post on it as Misc.
    [Fact]
    public async Task MakeNewEvent_NamesItAfterThePost_AndLandsAsMisc()
    {
        using var db = NewDb();
        var snapshot = await SeedAsync(db, Capture("Late Kirin"));

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(null, null, null, CreateNew: true), CancellationToken.None);

        Assert.Null(result.Error);
        var saved = await SavedAsync(db);
        var created = await db.WindowEvents.AsNoTracking().SingleAsync();
        Assert.Equal("Late Kirin", created.Name);
        Assert.Equal(created.Id, saved.WindowEventId);
        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, saved.SlotKind);
        Assert.Null(saved.WindowNumber);
    }

    [Fact]
    public async Task MakeNewEvent_WithNoName_IsRefused()
    {
        using var db = NewDb();
        var snapshot = await SeedAsync(db, Capture(name: null));

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(null, null, null, CreateNew: true), CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Null((await SavedAsync(db)).WindowEventId);
    }
}
