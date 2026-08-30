using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// EventPartySignupService.ClearWindowRosterAsync captures the roster into EventWindowRosterSnapshot
// immediately before wiping it, which is the only reason "View Previous Window" has anything to
// show: the signup rows themselves are deleted moments later, and the party setup those rows point
// at is a reusable template whose slots get rebuilt on edit. So the snapshot has to be standalone.
public class HnmWindowRosterSnapshotTests
{
    private const int EventId = 42;
    private const int AllianceId = 10;
    private const int PartyId = 100;

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // A one-party board. Only FKs are set — the in-memory provider fixes up the navigations that
    // ClearWindowRosterAsync's Include chain walks (slot → party → alliance).
    private static ApplicationDbContext SeededBoard(params EventPartySlotSignup[] signups)
    {
        var db = NewInMemoryContext();
        db.Events.Add(new Event { Id = EventId, EventName = "Tiamat", EventType = "HNM", HnmWindowNumber = 6 });
        db.PartySetupAlliances.Add(new PartySetupAlliance
        {
            Id = AllianceId, PartySetupId = 1, Name = "Alliance A", SortOrder = 0,
        });
        db.PartySetupParties.Add(new PartySetupParty
        {
            Id = PartyId, PartySetupAllianceId = AllianceId, Name = "Party 1", SortOrder = 0,
        });
        foreach (var signup in signups)
        {
            db.PartySetupSlots.Add(new PartySetupSlot
            {
                Id = signup.PartySetupSlotId,
                PartySetupPartyId = PartyId,
                SortOrder = signup.PartySetupSlotId,
                Label = "(GHORN)",
            });
            db.EventPartySlotSignups.Add(signup);
        }
        db.SaveChanges();
        return db;
    }

    private static EventPartySlotSignup Signup(
        int slotId, string name, bool locked = false, bool partyLeader = false) => new()
    {
        Id = slotId,
        EventId = EventId,
        PartySetupSlotId = slotId,
        AppUserId = $"user-{slotId}",
        CharacterName = name,
        MainJob = "PLD",
        SubJob = "WAR",
        StayNextWindow = locked,
        IsPartyLeader = partyLeader,
    };

    // The whole point: after the wipe the signups are gone, but the window they belonged to is still
    // readable. Both members are captured — including the one about to be deleted.
    [Fact]
    public async Task Capture_RecordsEverySeatedSignup_ThenWipesTheUnlockedOnes()
    {
        using var db = SeededBoard(Signup(1, "Solaire"), Signup(2, "Mirena"));

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 5, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(await db.EventPartySlotSignups.ToListAsync());
        var snapshot = await db.EventWindowRosterSnapshots.OrderBy(s => s.SlotSortOrder).ToListAsync();
        Assert.Equal(new[] { "Solaire", "Mirena" }, snapshot.Select(s => s.CharacterName));
        Assert.All(snapshot, s => Assert.Equal(5, s.WindowNumber));
    }

    // A 🔒 row survives the wipe, so it legitimately belongs to BOTH windows — it must still appear
    // in the snapshot of the window that just ended, flagged as the reason it carried over.
    [Fact]
    public async Task Capture_IncludesLockedSignups_WhichAlsoSurviveTheWipe()
    {
        using var db = SeededBoard(Signup(1, "Solaire", locked: true), Signup(2, "Mirena"));

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 5, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal("Solaire", (await db.EventPartySlotSignups.SingleAsync()).CharacterName);
        var snapshot = await db.EventWindowRosterSnapshots.ToListAsync();
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.Single(s => s.CharacterName == "Solaire").WasLocked);
        Assert.False(snapshot.Single(s => s.CharacterName == "Mirena").WasLocked);
    }

    // Grouping + job are copied as VALUES. A snapshot that stored slot ids would render blank once
    // the party setup template was edited, since editing rebuilds its slots.
    [Fact]
    public async Task Capture_DenormalizesGroupingAndJob_SoItSurvivesATemplateRebuild()
    {
        using var db = SeededBoard(Signup(1, "Solaire", partyLeader: true));

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 5, CancellationToken.None);
        await db.SaveChangesAsync();

        // Editing a party setup rebuilds its slots — model that by dropping the whole tree.
        db.PartySetupSlots.RemoveRange(await db.PartySetupSlots.ToListAsync());
        db.PartySetupParties.RemoveRange(await db.PartySetupParties.ToListAsync());
        db.PartySetupAlliances.RemoveRange(await db.PartySetupAlliances.ToListAsync());
        await db.SaveChangesAsync();

        var row = await db.EventWindowRosterSnapshots.SingleAsync();
        Assert.Equal("Alliance A", row.AllianceName);
        Assert.Equal("Party 1", row.PartyName);
        Assert.Equal("(GHORN)", row.SlotLabel);
        Assert.Equal("PLD", row.MainJob);
        Assert.Equal("WAR", row.SubJob);
        Assert.True(row.IsPartyLeader);
    }

    // Window 1 is never cleared, so nothing should ever be attributed to window 0. A caller with no
    // meaningful window still gets the wipe, just no snapshot.
    [Fact]
    public async Task Capture_SkippedWhenThereIsNoWindowToAttributeItTo()
    {
        using var db = SeededBoard(Signup(1, "Solaire"));

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 0, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(await db.EventPartySlotSignups.ToListAsync());
        Assert.Empty(await db.EventWindowRosterSnapshots.ToListAsync());
    }

    // A Manual Check In wyrm wipes its grid every window like any other wyrm, but its ATTENDANCE is
    // AppUserEvent.WdArrivalWindow / WdDepartureWindow — WdCampFinalizer pays arrival..departure off
    // those very rows at End Camp. Deleting them at the hour boundary would erase everyone's credit
    // back to whoever checked in during the final window, so a checked-in participation rides
    // through the wipe whether or not its slot was locked. Its slot still goes.
    [Fact]
    public async Task Wipe_SparesCheckedInParticipations_SoAManualCheckInCampKeepsItsCredit()
    {
        using var db = SeededBoard(Signup(1, "Solaire"), Signup(2, "Mirena"));
        db.AppUserEvents.AddRange(
            new AppUserEvent { Id = 1, EventId = EventId, AppUserId = "user-1", CharacterName = "Solaire", WdArrivalWindow = 2 },
            // Signed up but never checked in — nothing to protect, so this goes with the grid.
            new AppUserEvent { Id = 2, EventId = EventId, AppUserId = "user-2", CharacterName = "Mirena" });
        await db.SaveChangesAsync();

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 5, CancellationToken.None);
        await db.SaveChangesAsync();

        var kept = await db.AppUserEvents.SingleAsync();
        Assert.Equal("Solaire", kept.CharacterName);
        Assert.Equal(2, kept.WdArrivalWindow);
        Assert.Empty(await db.EventPartySlotSignups.ToListAsync());
    }

    // An empty board writes no rows at all, so "View Previous Window" falls through to its "nothing
    // captured" reply rather than showing an empty window that looks like data loss.
    [Fact]
    public async Task Capture_WritesNothingForAnEmptyRoster()
    {
        using var db = SeededBoard();

        await EventPartySignupService.ClearWindowRosterAsync(db, EventId, closingWindow: 5, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Empty(await db.EventWindowRosterSnapshots.ToListAsync());
    }
}
