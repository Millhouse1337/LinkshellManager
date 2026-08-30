using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace LinkshellManager.Tests;

// Carrying a camp's party setup board across a pop.
//
// Ending an event DELETES the Event row, so Event.PartySetupId dies with it — which is why a camp
// ended from the addon and re-created from its own ToD used to come back with no board attached.
// EventHistory.PartySetupId is the only record that survives, and everything here is about it
// holding a TEMPLATE rather than a per-event snapshot: a snapshot is cascade-deleted with the
// event, so inheriting one would hand the next pop a reference that is about to dangle.
public class PartySetupInheritanceTests
{
    private const int Ls = 21;
    private const int OtherLs = 22;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static void SeedLinkshells(ApplicationDbContext db)
    {
        db.Linkshells.Add(new Linkshell { Id = Ls, LinkshellName = "LS", LootStructure = "Dkp" });
        db.Linkshells.Add(new Linkshell { Id = OtherLs, LinkshellName = "Other", LootStructure = "Dkp" });
        db.SaveChanges();
    }

    private static PartySetup AddTemplate(ApplicationDbContext db, int id, int linkshellId = Ls)
    {
        var setup = new PartySetup { Id = id, LinkshellId = linkshellId, Name = "Tiamat Standard" };
        db.PartySetups.Add(setup);
        db.SaveChanges();
        return setup;
    }

    private static Event AddEvent(ApplicationDbContext db, int id, int? partySetupId, int linkshellId = Ls)
    {
        var evt = new Event
        {
            Id = id,
            LinkshellId = linkshellId,
            EventName = "Tiamat D3",
            EventType = "HNM",
            AssignedMonsterName = "Tiamat",
            PartySetupId = partySetupId,
        };
        db.Events.Add(evt);
        db.SaveChanges();
        return evt;
    }

    [Fact]
    public async Task AnUneditedBoard_ResolvesToItsTemplate()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        AddTemplate(db, 900);
        var evt = AddEvent(db, 800, partySetupId: 900);

        Assert.Equal(900, await PartySetupInheritance.ResolveTemplateIdAsync(db, evt, CancellationToken.None));
    }

    // The case that matters: the officer drag-dropped the live board, which cloned the template
    // into a per-event snapshot. The snapshot is about to be deleted with the event, so what has
    // to survive is the template it came from.
    [Fact]
    public async Task AnEditedBoard_ResolvesToTheTemplateItWasClonedFrom()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        AddTemplate(db, 900);
        var evt = AddEvent(db, 800, partySetupId: null);
        db.PartySetups.Add(new PartySetup
        {
            Id = 901, LinkshellId = Ls, Name = "Tiamat Standard",
            OwnerEventId = evt.Id, ClonedFromPartySetupId = 900,
        });
        evt.PartySetupId = 901;
        db.SaveChanges();

        Assert.Equal(900, await PartySetupInheritance.ResolveTemplateIdAsync(db, evt, CancellationToken.None));
    }

    // A snapshot whose origin template has since been deleted, or one created before the
    // provenance column existed, has nothing to inherit — and must say so rather than handing back
    // the snapshot id, which is exactly the dangling reference this all exists to avoid.
    [Fact]
    public async Task ASnapshotWithNoRecoverableTemplate_ResolvesToNull()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        var evt = AddEvent(db, 800, partySetupId: null);
        db.PartySetups.Add(new PartySetup
        {
            Id = 901, LinkshellId = Ls, Name = "Ad-hoc", OwnerEventId = evt.Id, ClonedFromPartySetupId = null,
        });
        evt.PartySetupId = 901;
        db.SaveChanges();

        Assert.Null(await PartySetupInheritance.ResolveTemplateIdAsync(db, evt, CancellationToken.None));
    }

    [Fact]
    public async Task ASetupFromAnotherLinkshell_IsNeverInherited()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        AddTemplate(db, 900, linkshellId: OtherLs);
        var evt = AddEvent(db, 800, partySetupId: 900);

        Assert.Null(await PartySetupInheritance.ResolveTemplateIdAsync(db, evt, CancellationToken.None));
    }

    // The next pop's lookup: closed history for the same camp.
    [Fact]
    public async Task ANewPop_InheritsFromTheMostRecentClosedCamp()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        AddTemplate(db, 900);
        db.EventHistories.Add(new EventHistory
        {
            Id = 1, LinkshellId = Ls, EventName = "Tiamat", PartySetupId = 900,
            EndTime = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        // The new pop's name carries a day suffix that never matches the last one, so the bare
        // monster name is what actually finds it.
        var resolved = await PartySetupInheritance.ResolveForNewPopAsync(
            db, Ls, "Tiamat", "Tiamat D4", CancellationToken.None);

        Assert.Equal(900, resolved);
    }

    // A configured recurring board is the officer's explicit standing choice, so it outranks
    // whatever the last camp happened to run with.
    [Fact]
    public async Task ARecurringBoard_OutranksClosedHistory()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        AddTemplate(db, 900);
        AddTemplate(db, 901);
        db.EventHistories.Add(new EventHistory
        {
            Id = 1, LinkshellId = Ls, EventName = "Tiamat", PartySetupId = 900,
            EndTime = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
        });
        db.HnmRecurringBoards.Add(new HnmRecurringBoard
        {
            Id = 1, LinkshellId = Ls, MonsterName = "Tiamat", PartySetupId = 901, Enabled = true,
            UpdatedAt = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        var resolved = await PartySetupInheritance.ResolveForNewPopAsync(
            db, Ls, "Tiamat", "Tiamat D4", CancellationToken.None);

        Assert.Equal(901, resolved);
    }

    // A template deleted since the camp closed must not be attached to the new pop.
    [Fact]
    public async Task ADeletedTemplate_IsNotInherited()
    {
        using var db = NewDb();
        SeedLinkshells(db);
        db.EventHistories.Add(new EventHistory
        {
            Id = 1, LinkshellId = Ls, EventName = "Tiamat", PartySetupId = 900,
            EndTime = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc),
        });
        db.SaveChanges();

        Assert.Null(await PartySetupInheritance.ResolveForNewPopAsync(
            db, Ls, "Tiamat", "Tiamat D4", CancellationToken.None));
    }
}
