using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Filing an unlinked capture as "Window N" is refused when the event already has window N. A
// capture filed as a window pays that window's value on its own, so a second one would pay the
// same people twice. Pinned here because the double payout is silent: nothing looks wrong until
// the DKP totals are off.
public class SnapshotWindowTakenTests
{
    private const int CampId = 40;
    private const int EventId = 700;
    private const int Filing = 1; // the capture being filed

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AttendanceSnapshot Capture(
        int id, int? windowNumber, string slotKind = AttendanceSnapshotSlotKinds.Window,
        string status = AttendanceSnapshotStatuses.Active) => new()
    {
        Id = id,
        LinkshellId = 1,
        CapturedAtUtc = new DateTime(2026, 11, 2, 21, 0, 0, DateTimeKind.Utc),
        SlotKind = slotKind,
        WindowNumber = windowNumber,
        SnapshotStatus = status,
    };

    private static Task<bool> CampTaken(ApplicationDbContext db, int window) =>
        SnapshotFilingService.CampWindowTakenAsync(db, CampId, window, Filing, CancellationToken.None);

    private static Task<bool> EventTaken(ApplicationDbContext db, int window, string? alliance) =>
        SnapshotFilingService.EventWindowTakenAsync(db, EventId, window, alliance, Filing, CancellationToken.None);

    // ---- live camps ----

    [Fact]
    public async Task Camp_WindowTheAddonPosted_IsTaken()
    {
        using var db = NewDb();
        db.EventAttendanceWindows.Add(new EventAttendanceWindow { Id = 1, EventId = CampId, SequenceNumber = 1 });
        await db.SaveChangesAsync();

        Assert.True(await CampTaken(db, 1));
        Assert.False(await CampTaken(db, 2));
    }

    // Kill rosters get their own sequence number, but they are not a window anyone can file.
    [Fact]
    public async Task Camp_KillRoster_DoesNotTakeItsNumber()
    {
        using var db = NewDb();
        db.EventAttendanceWindows.Add(new EventAttendanceWindow
        {
            Id = 1, EventId = CampId, SequenceNumber = 3, IsKillWindow = true
        });
        await db.SaveChangesAsync();

        Assert.False(await CampTaken(db, 3));
    }

    [Fact]
    public async Task Camp_OtherCaptureFiledAsThatWindow_IsTaken()
    {
        using var db = NewDb();
        var other = Capture(2, windowNumber: 2);
        other.LinkedEventId = CampId;
        db.AttendanceSnapshots.Add(other);
        await db.SaveChangesAsync();

        Assert.True(await CampTaken(db, 2));
    }

    // An ignored capture pays nobody, and re-filing the same capture must not trip over itself.
    [Fact]
    public async Task Camp_IgnoredCaptureOrTheSameCapture_IsNotTaken()
    {
        using var db = NewDb();
        var ignored = Capture(2, windowNumber: 2, status: AttendanceSnapshotStatuses.Ignored);
        ignored.LinkedEventId = CampId;
        var self = Capture(Filing, windowNumber: 2);
        self.LinkedEventId = CampId;
        db.AttendanceSnapshots.AddRange(ignored, self);
        await db.SaveChangesAsync();

        Assert.False(await CampTaken(db, 2));
    }

    // Misc posts have no window, so they never block one.
    [Fact]
    public async Task Camp_MiscCapture_IsNotTaken()
    {
        using var db = NewDb();
        var misc = Capture(2, windowNumber: 2, slotKind: AttendanceSnapshotSlotKinds.Misc);
        misc.LinkedEventId = CampId;
        db.AttendanceSnapshots.Add(misc);
        await db.SaveChangesAsync();

        Assert.False(await CampTaken(db, 2));
    }

    // ---- filing through the service (the Activity row and the addon panel both use it) ----

    private static SnapshotFilingService NewFiling(ApplicationDbContext db) =>
        new(db, new WindowEventLinkService(db, new MonsterTimingResolver(db)), new AllianceIdentityService(db));

    private static void SeedLiveCamp(ApplicationDbContext db) =>
        db.Events.Add(new Event
        {
            Id = CampId, LinkshellId = 1, EventName = "Cerberus", EventType = "HNM",
            CommencementStartTime = new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc),
        });

    [Fact]
    public async Task Filing_OntoALiveCamp_ParksItInThatSlot()
    {
        using var db = NewDb();
        SeedLiveCamp(db);
        db.AttendanceSnapshots.Add(Capture(Filing, windowNumber: null));
        await db.SaveChangesAsync();
        var snapshot = await db.AttendanceSnapshots.SingleAsync(s => s.Id == Filing);

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(null, null, CampId, false, AttendanceSnapshotSlotKinds.Misc, null),
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.FiledAgainstCamp);
        var saved = await db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == Filing);
        Assert.Equal(CampId, saved.LinkedEventId);
        Assert.Equal(AttendanceSnapshotSlotKinds.Misc, saved.SlotKind);
    }

    [Fact]
    public async Task Filing_AWindowTheCampAlreadyHas_IsRefusedAndNothingIsSaved()
    {
        using var db = NewDb();
        SeedLiveCamp(db);
        db.EventAttendanceWindows.Add(new EventAttendanceWindow { Id = 1, EventId = CampId, SequenceNumber = 1 });
        db.AttendanceSnapshots.Add(Capture(Filing, windowNumber: null));
        await db.SaveChangesAsync();
        var snapshot = await db.AttendanceSnapshots.SingleAsync(s => s.Id == Filing);

        var result = await NewFiling(db).FileAsync(snapshot,
            new SnapshotFilingService.Request(null, null, CampId, false, AttendanceSnapshotSlotKinds.Window, 1),
            CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("already posted", result.Error);
        Assert.Null((await db.AttendanceSnapshots.AsNoTracking().SingleAsync(s => s.Id == Filing)).LinkedEventId);
    }

    // ---- attendance events ----

    [Fact]
    public async Task Event_SameWindowSameAlliance_IsTaken()
    {
        using var db = NewDb();
        var other = Capture(2, windowNumber: 4);
        other.WindowEventId = EventId;
        other.AllianceKey = "a1";
        db.AttendanceSnapshots.Add(other);
        await db.SaveChangesAsync();

        Assert.True(await EventTaken(db, 4, "a1"));
    }

    // Two alliances each posting window 4 is normal: they are different people.
    [Fact]
    public async Task Event_SameWindowOtherAlliance_IsAllowed()
    {
        using var db = NewDb();
        var other = Capture(2, windowNumber: 4);
        other.WindowEventId = EventId;
        other.AllianceKey = "a1";
        db.AttendanceSnapshots.Add(other);
        await db.SaveChangesAsync();

        Assert.False(await EventTaken(db, 4, "a2"));
        Assert.False(await EventTaken(db, 5, "a1"));
    }
}
