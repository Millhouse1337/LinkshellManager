using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Officers mark live attendees Present/Absent; Absent stores IsVerified == false. Neither close
// loop used to look at it, so a member marked Absent was paid in full and got Active credit.
// Now both loops ask EventAttendanceDkpCalculator.ForParticipant, which gives Absent 0 DKP and no
// Active credit, while still writing their history row so the denial stays on record.
public class AbsentMemberEarnsNothingTests
{
    private const int Ls = 7;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static DkpLedgerWriter NewWriter(ApplicationDbContext db) =>
        new(db, new DkpPoolResolver(db, new DkpPoolProvisioner(db)), NullLogger<DkpLedgerWriter>.Instance);

    private static DkpPoolResolver NewPools(ApplicationDbContext db) =>
        new(db, new DkpPoolProvisioner(db));

    // ------------------------------------------------------------- the shared rule ---

    [Fact]
    public void Absent_EarnsZero_AndNoActiveCredit()
    {
        var credit = EventAttendanceDkpCalculator.ForParticipant(
            isVerified: false, countsTowardActive: true, isWindowed: false,
            windowsAttended: 0, durationHours: 3, dkpPerHour: 2, roundingStep: DkpRounding.QuarterStep);

        Assert.Equal(0, credit.Dkp);
        Assert.False(credit.ActiveCredit);
        Assert.True(credit.IsAbsent);
    }

    // Present (true) and not-yet-checked (null) members are paid exactly as before.
    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void NotAbsent_IsPaidForTimePresent_AndKeepsActiveCredit(bool? isVerified)
    {
        var credit = EventAttendanceDkpCalculator.ForParticipant(
            isVerified, countsTowardActive: true, isWindowed: false,
            windowsAttended: 0, durationHours: 3, dkpPerHour: 2, roundingStep: DkpRounding.QuarterStep);

        Assert.Equal(6, credit.Dkp, precision: 3);
        Assert.True(credit.ActiveCredit);
        Assert.False(credit.IsAbsent);
    }

    // An event that doesn't count toward Active gives nobody Active credit, present or not.
    [Fact]
    public void Present_OnANonActiveEvent_GetsNoActiveCredit()
    {
        var credit = EventAttendanceDkpCalculator.ForParticipant(
            isVerified: true, countsTowardActive: false, isWindowed: false,
            windowsAttended: 0, durationHours: 1, dkpPerHour: 1, roundingStep: DkpRounding.QuarterStep);

        Assert.False(credit.ActiveCredit);
    }

    // ------------------------------------------------- EventEndService close path ---

    [Fact]
    public async Task EventEndService_PaysAbsentMemberZero_AndKeepsTheirHistoryRow()
    {
        using var db = NewDb();
        var ev = SeedTimedEvent(db);

        await EventEndService.EndEventCoreAsync(db, NewWriter(db), NewPools(db), ev);

        var rows = await db.AppUserEventHistories.ToListAsync();
        var present = Assert.Single(rows, r => r.AppUserId == "present");
        var absent = Assert.Single(rows, r => r.AppUserId == "absent");

        Assert.True(present.EventDkp > 0);
        Assert.True(present.ActiveCredit);

        Assert.Equal(0, absent.EventDkp);
        Assert.False(absent.ActiveCredit);
        Assert.False(absent.IsVerified); // the denial stays on record

        // The absent member's balance did not move. Their ledger row exists at 0, so a later
        // Past Events correction can still move their balance through it.
        var absentMember = await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == "absent");
        Assert.Equal(0, absentMember.LinkshellDkp ?? 0);
        var absentLedger = await db.DkpLedgerEntries.SingleAsync(e => e.AppUserId == "absent");
        Assert.Equal("EventEarned", absentLedger.EntryType);
        Assert.Equal(0, absentLedger.Amount);

        var presentMember = await db.AppUserLinkshells.SingleAsync(m => m.AppUserId == "present");
        Assert.Equal(present.EventDkp, presentMember.LinkshellDkp);
    }

    // ------------------------------------------ Activity "End event" close path ---

    // ActivityDataController.EndEventAsync needs a signed-in HTTP context to run, so it is pinned
    // by source instead: both close loops must ask ForParticipant, and neither may call Compute
    // directly (which would skip the Absent rule) or stamp ActiveCredit from the event alone.
    [Fact]
    public void BothCloseLoops_UseTheSharedAbsentRule()
    {
        var repoRoot = FindRepoRoot();
        var closePaths = new[]
        {
            "Controllers/ActivityDataController.EventsLifecycle.cs",
            "Services/EventEndService.cs",
        };

        foreach (var relative in closePaths)
        {
            var path = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relative} not found -- did it move?");
            var source = File.ReadAllText(path);

            Assert.Contains("EventAttendanceDkpCalculator.ForParticipant(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("EventAttendanceDkpCalculator.Compute(", source, StringComparison.Ordinal);
            Assert.False(
                Regex.IsMatch(source, @"ActiveCredit\s*=\s*eventEntity\.CountsTowardActive"),
                $"{relative} stamps ActiveCredit straight from the event, which gives Absent members "
                + "Active credit. Use the ForParticipant result.");
        }
    }

    // A plain timed event (no windows) with one Present and one Absent member, both on for 2h.
    private static Event SeedTimedEvent(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Users.Add(new AppUser { Id = "present", UserName = "present" });
        db.Users.Add(new AppUser { Id = "absent", UserName = "absent" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 1, LinkshellId = Ls, AppUserId = "present", CharacterName = "Here" });
        db.AppUserLinkshells.Add(new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "absent", CharacterName = "Gone" });

        var start = DateTime.UtcNow.AddHours(-2);
        db.Events.Add(new Event
        {
            Id = 100,
            LinkshellId = Ls,
            EventName = "Sky run",
            EventType = "Sky",
            StartTime = start,
            CommencementStartTime = start,
            DkpPerHour = 1,
            CountsTowardActive = true
        });
        db.AppUserEvents.Add(new AppUserEvent
        {
            Id = 10, EventId = 100, AppUserId = "present", CharacterName = "Here",
            IsVerified = true, StartTime = start
        });
        db.AppUserEvents.Add(new AppUserEvent
        {
            Id = 11, EventId = 100, AppUserId = "absent", CharacterName = "Gone",
            IsVerified = false, StartTime = start
        });
        db.SaveChanges();

        return db.Events
            .Include(e => e.AppUserEvents)
            .Include(e => e.EventLootDetails)
            .Include(e => e.Linkshell)
            .First(e => e.Id == 100);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Controllers")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate the repo root above {AppContext.BaseDirectory}.");
    }
}
