using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// P2.8 (db-design-08): a windowed (Claim/Kill-style) close pays per window attended, and pays one
// participation row per account. It used to count windows per ROW, so a member with two rows (a
// double-clicked sign-up) whose windows were credited on the second row was paid only the first
// row's windows. Windows are now counted per account (WindowsAttendedTally), by both close paths.
public class WindowsAttendedPerAccountTests
{
    private const int Ls = 7;
    private const int EventId = 100;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) =>
        new(db, new DkpPoolProvisioner(db));

    // Bob has two rows for one 3-window event (the in-memory database does not enforce the unique
    // index, which is how production got them). Window 1 is on row 10, window 2 on row 11, and
    // window 3 on both (a legacy credit without an AppUserId, so the per-window index let it in).
    private static void SeedBobWithTwoRows(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "bob", UserName = "bob" });
        db.Users.Add(new AppUser { Id = "carol", UserName = "carol" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "bob", CharacterName = "Bob" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "carol", CharacterName = "Carol" });

        var start = DateTime.UtcNow.AddHours(-2);
        db.Events.Add(new Event
        {
            Id = EventId,
            LinkshellId = Ls,
            EventName = "Claim camp",
            EventType = "HNM",
            StartTime = start,
            CommencementStartTime = start,
            WindowCountOverride = 3,
            DkpPerHour = 10, // per window on a windowed event
            CountsTowardActive = true,
        });
        db.AppUserEvents.AddRange(
            new AppUserEvent { Id = 10, EventId = EventId, AppUserId = "bob", CharacterName = "Bob", IsVerified = true, StartTime = start },
            new AppUserEvent { Id = 11, EventId = EventId, AppUserId = "bob", CharacterName = "Bob", IsVerified = true, StartTime = start },
            new AppUserEvent { Id = 12, EventId = EventId, AppUserId = "carol", CharacterName = "Carol", IsVerified = true, StartTime = start });
        db.EventAttendanceWindows.AddRange(
            new EventAttendanceWindow { Id = 1, EventId = EventId, SequenceNumber = 1, PostedAt = start },
            new EventAttendanceWindow { Id = 2, EventId = EventId, SequenceNumber = 2, PostedAt = start },
            new EventAttendanceWindow { Id = 3, EventId = EventId, SequenceNumber = 3, PostedAt = start });
        db.AppUserEventWindows.AddRange(
            new AppUserEventWindow { EventAttendanceWindowId = 1, AppUserEventId = 10, AppUserId = "bob" },
            new AppUserEventWindow { EventAttendanceWindowId = 2, AppUserEventId = 11, AppUserId = "bob" },
            new AppUserEventWindow { EventAttendanceWindowId = 3, AppUserEventId = 10, AppUserId = "bob" },
            new AppUserEventWindow { EventAttendanceWindowId = 3, AppUserEventId = 11, AppUserId = null },
            new AppUserEventWindow { EventAttendanceWindowId = 1, AppUserEventId = 12, AppUserId = "carol" });
        db.SaveChanges();
    }

    [Fact]
    public async Task Tally_CountsEachAccountsDistinctWindows_WhicheverRowTheyHangOff()
    {
        using var db = NewDb();
        SeedBobWithTwoRows(db);

        var tally = await WindowsAttendedTally.LoadAsync(db, EventId, CancellationToken.None);

        // Either of Bob's rows reads the same 3 (windows 1, 2, 3; window 3 once).
        Assert.Equal(3, tally.For(new AppUserEvent { Id = 10, AppUserId = "bob" }));
        Assert.Equal(3, tally.For(new AppUserEvent { Id = 11, AppUserId = "bob" }));
        Assert.Equal(1, tally.For(new AppUserEvent { Id = 12, AppUserId = "carol" }));
        Assert.Equal(0, tally.For(new AppUserEvent { Id = 99, AppUserId = "nobody" }));
    }

    // An outside sign-up has no account, so its windows still count by its own row.
    [Fact]
    public async Task Tally_CountsAnAccountlessRowByItself()
    {
        using var db = NewDb();
        SeedBobWithTwoRows(db);
        db.AppUserEvents.Add(new AppUserEvent { Id = 20, EventId = EventId, DiscordUserId = "555", CharacterName = "Guest" });
        db.AppUserEventWindows.Add(new AppUserEventWindow { EventAttendanceWindowId = 2, AppUserEventId = 20 });
        db.SaveChanges();

        var tally = await WindowsAttendedTally.LoadAsync(db, EventId, CancellationToken.None);

        Assert.Equal(1, tally.For(new AppUserEvent { Id = 20 }));
        Assert.Equal(0, tally.For(new AppUserEvent { Id = 21 }));
    }

    [Fact]
    public async Task EventEndService_PaysEveryWindowOfAMemberWithTwoRows_Once()
    {
        using var db = NewDb();
        SeedBobWithTwoRows(db);
        var ev = await db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .SingleAsync(e => e.Id == EventId);

        await EventEndService.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev);

        // One history row for Bob, paid for all three windows (it was 2: row 10's windows only).
        var bob = Assert.Single(await db.AppUserEventHistories.Where(h => h.AppUserId == "bob").ToListAsync());
        Assert.Equal(3, bob.WindowsAttended);
        Assert.Equal(30, bob.EventDkp);
        Assert.Equal(30, (await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == "bob")).LinkshellDkp);
        Assert.Equal(30, (await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == "bob")).Amount);

        var carol = await db.AppUserEventHistories.SingleAsync(h => h.AppUserId == "carol");
        Assert.Equal(1, carol.WindowsAttended);
        Assert.Equal(10, carol.EventDkp);
    }

    // Pinned by source: the close must count through the shared tally, not per participation row.
    // Both End Event buttons run this one close (the app's since P3.5; see EndEventOnePathTests).
    [Fact]
    public void TheClose_CountsWindowsPerAccount()
    {
        var repoRoot = FindRepoRoot();
        foreach (var relative in new[]
                 {
                     "Services/EventEndService.cs",
                 })
        {
            var source = File.ReadAllText(Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains("WindowsAttendedTally.LoadAsync(", source, StringComparison.Ordinal);
            Assert.Contains("windowsAttendedTally.For(participation)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GroupBy(w => w.AppUserEventId", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LinkshellManagerDiscordApp.csproj")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
