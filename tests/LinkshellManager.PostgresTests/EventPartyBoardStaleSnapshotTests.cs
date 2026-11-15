using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.PostgresTests;

// Edit Event can swap a board from its private snapshot back to a template, which leaves the old
// snapshot behind (OwnerEventId still = the event). The unique IX_PartySetups_OwnerEventId index then
// refused every new clone, so each live-board edit ("Move member") failed. The first edit now
// removes the leftover and clones again.
[Collection("postgres")]
public class EventPartyBoardStaleSnapshotTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;

    public EventPartyBoardStaleSnapshotTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => _pg.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static PartySetup Setup(int linkshellId, string name, int? ownerEventId)
    {
        var setup = new PartySetup
        {
            LinkshellId = linkshellId, Name = name, EventType = "HNM", OwnerEventId = ownerEventId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var alliance = new PartySetupAlliance { SortOrder = 0, Name = "A1" };
        var party = new PartySetupParty { SortOrder = 0, Name = "P1" };
        party.Slots.Add(new PartySetupSlot { SortOrder = 0, RequirementType = "Any" });
        party.Slots.Add(new PartySetupSlot { SortOrder = 1, RequirementType = "Any" });
        alliance.Parties.Add(party);
        setup.Alliances.Add(alliance);
        return setup;
    }

    [Fact]
    public async Task First_edit_replaces_a_leftover_snapshot_and_keeps_the_seating()
    {
        int eventId, templateId, staleId, templateSlotId;
        await using (var seed = _pg.CreateContext())
        {
            var linkshell = new Linkshell { LinkshellName = "Board LS" };
            seed.Linkshells.Add(linkshell);
            await seed.SaveChangesAsync();

            var template = Setup(linkshell.Id, "Behemoth setup", ownerEventId: null);
            seed.PartySetups.Add(template);
            var ev = new Event { LinkshellId = linkshell.Id, EventName = "Behemoth", EventType = "HNM", StartTime = DateTime.UtcNow };
            seed.Events.Add(ev);
            await seed.SaveChangesAsync();

            // The leftover: an old private copy, while the event now points at the template.
            var stale = Setup(linkshell.Id, "Behemoth setup", ownerEventId: ev.Id);
            stale.ClonedFromPartySetupId = template.Id;
            seed.PartySetups.Add(stale);
            ev.PartySetupId = template.Id;
            await seed.SaveChangesAsync();

            templateSlotId = template.Alliances.Single().Parties.Single().Slots.OrderBy(s => s.SortOrder).First().Id;
            seed.EventPartySlotSignups.Add(new EventPartySlotSignup
            {
                EventId = ev.Id, PartySetupSlotId = templateSlotId, CharacterName = "Seated", SignedUpAtUtc = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
            (eventId, templateId, staleId) = (ev.Id, template.Id, stale.Id);
        }

        await using (var db = _pg.CreateContext())
        {
            // The caller already tracks the event, as the Discord handlers do.
            _ = await db.Events.SingleAsync(e => e.Id == eventId);
            var result = await EventPartyBoardEditService.EnsureEventOwnedSetupAsync(db, eventId, CancellationToken.None);

            Assert.NotNull(result.SetupId);
            Assert.NotEqual(templateId, result.SetupId);
            Assert.NotEqual(staleId, result.SetupId);
            Assert.True(result.SlotIdMap.ContainsKey(templateSlotId));
        }

        await using var check = _pg.CreateContext();
        var owned = await check.PartySetups.Where(ps => ps.OwnerEventId == eventId).Select(ps => ps.Id).ToListAsync();
        var pointer = await check.Events.Where(e => e.Id == eventId).Select(e => e.PartySetupId).SingleAsync();
        Assert.Equal(new[] { pointer!.Value }, owned);
        Assert.True(await check.PartySetups.AnyAsync(ps => ps.Id == templateId));

        var signup = await check.EventPartySlotSignups.SingleAsync(s => s.EventId == eventId);
        var signupSetupId = await check.PartySetupSlots
            .Where(s => s.Id == signup.PartySetupSlotId)
            .Select(s => s.Party!.Alliance!.PartySetupId)
            .SingleAsync();
        Assert.Equal(pointer.Value, signupSetupId);

        // A second edit finds the event's own copy and clones nothing.
        await using var again = _pg.CreateContext();
        var second = await EventPartyBoardEditService.EnsureEventOwnedSetupAsync(again, eventId, CancellationToken.None);
        Assert.Equal(pointer.Value, second.SetupId);
        Assert.Empty(second.SlotIdMap);
    }
}
