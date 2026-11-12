using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Bob earned 10 at a past event and an officer audited it to 8 (a -2 correction row). Editing or
// deleting that event used to forget the correction: delete reversed the +10 but kept the -2, and
// "set Bob to 8" made him 6. The correction now moves with the row it corrects.
public class PastEventAuditCorrectionTests
{
    private const int Ls = 7;
    private const int HistoryId = 50;
    private const int ParticipantId = 70;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (EventHistoryEditService Edits, DkpLedgerWriter Writer) NewServices(ApplicationDbContext db)
    {
        var pools = new DkpPoolResolver(db, new DkpPoolProvisioner(db));
        var writer = new DkpLedgerWriter(db, pools, NullLogger<DkpLedgerWriter>.Instance);
        return (new EventHistoryEditService(db, writer, pools), writer);
    }

    // Bob: 10 earned at the event, then audited to 8. `auditCarriesEvent` false is a correction
    // written before audits copied the event id.
    private static async Task<(DkpLedgerEntry Earned, DkpLedgerEntry Audit)> SeedAsync(
        ApplicationDbContext db, bool auditCarriesEvent)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS" });
        db.Users.Add(new AppUser { Id = "u2", UserName = "bob", CharacterName = "Bob" });
        var bob = new AppUserLinkshell { Id = 2, LinkshellId = Ls, AppUserId = "u2", CharacterName = "Bob", LinkshellDkp = 0 };
        db.AppUserLinkshells.Add(bob);
        db.EventHistories.Add(new EventHistory
        {
            Id = HistoryId, LinkshellId = Ls, EventName = "Sky", EventType = "Sky", DkpPerHour = 10, EventDkp = 10,
        });
        db.AppUserEventHistories.Add(new AppUserEventHistory
        {
            Id = ParticipantId, EventHistoryId = HistoryId, AppUserId = "u2", CharacterName = "Bob", EventDkp = 10, Duration = 1,
        });
        await db.SaveChangesAsync();

        var (_, writer) = NewServices(db);
        var earned = await writer.AppendAsync(bob, "EventEarned", 10, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Bob", EventHistoryId: HistoryId), CancellationToken.None);
        await db.SaveChangesAsync();
        var audit = await writer.AppendAsync(bob, "AuditAdjustment", -2, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(
                CharacterName: "Bob",
                EventHistoryId: auditCarriesEvent ? HistoryId : null,
                AuditRelatedLedgerEntryId: earned!.Id),
            CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (earned, audit!);
    }

    private static double BobDkp(ApplicationDbContext db) =>
        db.AppUserLinkshells.AsNoTracking().Single(m => m.AppUserId == "u2").LinkshellDkp ?? 0;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DeletingTheEvent_ReversesTheCorrectionToo(bool auditCarriesEvent)
    {
        await using var db = NewDb();
        await SeedAsync(db, auditCarriesEvent);
        Assert.Equal(8, BobDkp(db));

        Assert.True(await NewServices(db).Edits.DeleteEventAsync(HistoryId, CancellationToken.None));

        Assert.Equal(0, BobDkp(db));
        Assert.Empty(db.DkpLedgerEntries);
    }

    [Fact]
    public async Task RemovingTheMember_ReversesTheCorrectionToo()
    {
        await using var db = NewDb();
        await SeedAsync(db, auditCarriesEvent: false);

        var outcome = await NewServices(db).Edits.RemoveParticipantAsync(HistoryId, ParticipantId, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, outcome);
        Assert.Equal(0, BobDkp(db));
        Assert.Empty(db.DkpLedgerEntries);
    }

    [Fact]
    public async Task SettingTheMemberTo8_LeavesHimWith8_Not6()
    {
        await using var db = NewDb();
        var (earned, audit) = await SeedAsync(db, auditCarriesEvent: true);

        var outcome = await NewServices(db).Edits.SetParticipantDkpAsync(HistoryId, ParticipantId, 8, CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, outcome);
        Assert.Equal(8, BobDkp(db));
        // The correction is kept (it is the audit trail); the earned row carries the rest.
        Assert.Equal(-2, db.DkpLedgerEntries.Single(e => e.Id == audit.Id).Amount);
        Assert.Equal(10, db.DkpLedgerEntries.Single(e => e.Id == earned.Id).Amount);
        Assert.Equal(8, db.AppUserEventHistories.Single().EventDkp);
    }

    // The seed is an OLD audit: the Past Event still shows 10 while Bob really has 8. Doubling the
    // rate must scale what he has (8 -> 16), not the stale 10 -> 20, which wiped the -2 correction.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangingTheRate_ScalesWhatTheMemberReallyHas_KeepingTheCorrection(bool auditCarriesEvent)
    {
        await using var db = NewDb();
        var (_, audit) = await SeedAsync(db, auditCarriesEvent);

        var outcome = await NewServices(db).Edits.EditEventAsync(HistoryId,
            new EventHistoryEditInput("Sky", "Sky", null, null, null, 20), CancellationToken.None);

        Assert.Equal(EventHistoryEditOutcome.Done, outcome);
        Assert.Equal(16, BobDkp(db));
        Assert.Equal(-2, db.DkpLedgerEntries.Single(e => e.Id == audit.Id).Amount);
        Assert.Equal(16, db.AppUserEventHistories.Single().EventDkp);
    }

    // P3.7: the Past Event figure is read from the ledger (PastEventDkp), so an audit shows there
    // without anything patching the stored EventDkp -- old audits filed under no event included.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAudit_MovesTheAttendeesPastEventFigure_EvenThroughACorrectionOfACorrection(bool auditCarriesEvent)
    {
        await using var db = NewDb();
        var (_, audit) = await SeedAsync(db, auditCarriesEvent);

        async Task<double?> ShownAsync()
        {
            var totals = await PastEventDkp.LoadAsync(db, new[] { HistoryId }, CancellationToken.None);
            return totals.For(db.AppUserEventHistories.AsNoTracking().Single());
        }

        // The stored figure still says 10; the page shows what Bob really has.
        Assert.Equal(10, db.AppUserEventHistories.AsNoTracking().Single().EventDkp);
        Assert.Equal(8, await ShownAsync());

        // A second officer corrects the -2 row to -3.
        var bob = db.AppUserLinkshells.Single(m => m.AppUserId == "u2");
        await NewServices(db).Writer.AppendAsync(bob, "AuditAdjustment", -1, DateTime.UtcNow, DkpPoolRef.Derived("Sky"),
            new DkpEntryContext(CharacterName: "Bob", AuditRelatedLedgerEntryId: audit.Id), CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(7, await ShownAsync());
    }
}
