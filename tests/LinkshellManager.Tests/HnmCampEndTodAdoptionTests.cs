using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// Ending an HNM camp from the ADDON has to leave exactly one board behind, pointed at the kill's
// Time of Death.
//
// It left two. The addon settles the ToD in its own call BEFORE it ends the camp — from the End
// Event dialog, or from the ToD Capture panel earlier in the night — and that post auto-created a
// next-pop event, because HnmAutoEventService will not recycle a camp that is still live. Then the
// end parked the camp as a second row, still advertising the PREVIOUS pop's repop time with no
// re-post scheduled, because the generic end path never touched StartTime / SourceTodId /
// HnmRepostAt the way the board's own End Camp form always has (HnmCampPopService).
//
// So the ToD post stands down while the camp is live, and the end adopts the ToD instead.
public class HnmCampEndTodAdoptionTests
{
    private const int LinkshellId = 11;
    private const int EventId = 300;
    private const string Monster = "Fafnir/Nidhogg";

    private static readonly DateTime Now = new(2026, 9, 3, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime OldRepop = Now.AddHours(-4);
    private static readonly DateTime NewRepop = Now.AddHours(22);

    private static ApplicationDbContext NewInMemoryContext() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static HnmCampReviewHandoffService NewHandoff(ApplicationDbContext db) =>
        new(db,
            new WdCampFinalizer(db, NullLogger<WdCampFinalizer>.Instance),
            new HnmStandardCampFinalizer(db, NullLogger<HnmStandardCampFinalizer>.Instance),
            NullLogger<HnmCampReviewHandoffService>.Instance);

    // The camp the officer is standing at: live, created from the PREVIOUS pop's ToD (id 1).
    private static Event LiveCamp() => new()
    {
        Id = EventId,
        LinkshellId = LinkshellId,
        EventName = "faf test 1",
        EventType = "HNM",
        EventLocation = "aery",
        AssignedMonsterName = Monster,
        DayNumber = 1,
        StartTime = OldRepop,
        CommencementStartTime = Now.AddHours(-3),
        EndTime = null,
        SourceTodId = 1,
        HnmWindowNumber = 3,
    };

    private static Tod PreviousTod() => new()
    {
        Id = 1,
        LinkshellId = LinkshellId,
        MonsterName = Monster,
        DayNumber = 1,
        Time = Now.AddHours(-26),
        RepopTime = OldRepop,
    };

    /// <summary>The ToD the addon posts for this kill, moments before it ends the camp.</summary>
    private static Tod SettledTod() => new()
    {
        Id = 2,
        LinkshellId = LinkshellId,
        MonsterName = Monster,
        DayNumber = 1,
        Time = Now,
        RepopTime = NewRepop,
    };

    private static async Task<ApplicationDbContext> SeededAsync(bool repeatOnTod)
    {
        var db = NewInMemoryContext();
        db.Linkshells.Add(new Linkshell
        {
            Id = LinkshellId,
            LinkshellName = "TestLinkshell",
            EnableHnmSection = true,
        });
        db.Events.Add(LiveCamp());
        db.Tods.Add(PreviousTod());
        if (repeatOnTod)
        {
            db.HnmRecurringBoards.Add(new HnmRecurringBoard
            {
                Id = 1,
                LinkshellId = LinkshellId,
                MonsterName = Monster,
                EventNameTemplate = "faf test 1",
                Enabled = true,
                LeadHours = 1,
                LastSourceTodId = 1,
                CreatedAt = Now.AddDays(-7),
            });
        }
        await db.SaveChangesAsync();
        return db;
    }

    /// <summary>The addon's End Event: post the ToD, then end the camp.</summary>
    private static async Task PostTodThenEndAsync(ApplicationDbContext db)
    {
        db.Tods.Add(SettledTod());
        await db.SaveChangesAsync();
        await NewHandoff(db).HandOffAndRecycleAsync(EventId, CancellationToken.None);
    }

    // The bug as reported: one kill, one board.
    [Fact]
    public async Task PostTodThenEnd_LeavesOneBoard_NotTwo()
    {
        using var db = await SeededAsync(repeatOnTod: true);

        await PostTodThenEndAsync(db);

        Assert.Equal(EventId, db.Events.Single().Id);
    }

    // A Repeat-on-ToD board is re-posted by the poller LeadHours before the pop, which is what the
    // officer asked for by enabling it — so the board STAYS parked, and only the times it advertises
    // move onto the new cycle. Before this, the card sat there naming a repop that had already been
    // and gone, and told the officer to turn on a setting that was already on.
    [Fact]
    public async Task PostTodThenEnd_RepeatOnTod_ParksTheBoardOnTheNewPop()
    {
        using var db = await SeededAsync(repeatOnTod: true);

        await PostTodThenEndAsync(db);

        var board = db.Events.Single();
        Assert.NotNull(board.HnmDefeatedAt);                 // parked, awaiting its re-post
        Assert.Equal(2, board.SourceTodId);                  // ...on the ToD just settled
        Assert.Equal(NewRepop, board.StartTime);
        Assert.Equal(NewRepop.AddHours(-1), board.HnmRepostAt);
    }

    // Stamping SourceTodId is also what lets the poller recognise this row as the new cycle's board:
    // its idempotency check matches on it, so without the stamp it posts a second board for the pop.
    [Fact]
    public async Task PostTodThenEnd_RepeatOnTod_LeavesTheTemplateStampOnThePreviousCycle()
    {
        using var db = await SeededAsync(repeatOnTod: true);

        await PostTodThenEndAsync(db);

        // Untouched: stamping the new ToD here would mark the cycle handled and the board would
        // never re-post, which is the whole point of the template.
        Assert.Equal(1, db.HnmRecurringBoards.Single().LastSourceTodId);
    }

    // Repeat-on-ToD OFF on the create form: nobody asked for this board back, so End Event removes
    // it rather than re-queueing it for the next pop. It used to be revived on the spot, which put a
    // fresh board straight back into the channel for a camp the officer had said not to repeat.
    [Fact]
    public async Task PostTodThenEnd_WithoutARepeatingBoard_RemovesTheBoard()
    {
        using var db = await SeededAsync(repeatOnTod: false);

        await PostTodThenEndAsync(db);

        Assert.Empty(db.Events);
    }

    // Same with no ToD settled at all: there is nothing for it to come back FOR, and no switch
    // asking it to.
    [Fact]
    public async Task EndWithNoNewTod_WithoutARepeatingBoard_RemovesTheBoard()
    {
        using var db = await SeededAsync(repeatOnTod: false);

        await NewHandoff(db).HandOffAndRecycleAsync(EventId, CancellationToken.None);

        Assert.Empty(db.Events);
    }

    // Turned off after being on: a disabled template is "off", not "on with a stale setting".
    [Fact]
    public async Task PostTodThenEnd_WithADisabledRepeatingBoard_RemovesTheBoard()
    {
        using var db = await SeededAsync(repeatOnTod: true);
        db.HnmRecurringBoards.Single().Enabled = false;
        await db.SaveChangesAsync();

        await PostTodThenEndAsync(db);

        Assert.Empty(db.Events);
    }

    // The other order the addon can produce: the officer hit Post in the ToD Capture panel and the
    // dialog then only confirms the end. Same single board either way.
    [Fact]
    public async Task EndThenPostTod_AlsoLeavesOneBoard()
    {
        using var db = await SeededAsync(repeatOnTod: true);

        await NewHandoff(db).HandOffAndRecycleAsync(EventId, CancellationToken.None);
        db.Tods.Add(SettledTod());
        await db.SaveChangesAsync();

        Assert.Equal(EventId, db.Events.Single().Id);
    }

    // A camp ended with NO Time of Death settled: nothing to adopt, so the board is left exactly as
    // it was rather than being re-pointed at the pop that just died. This is also the web and
    // Activity End Event actions, which post no ToD at all.
    [Fact]
    public async Task EndWithNoNewTod_LeavesTheBoardWhereItWas()
    {
        using var db = await SeededAsync(repeatOnTod: true);

        await NewHandoff(db).HandOffAndRecycleAsync(EventId, CancellationToken.None);

        var parked = db.Events.Single();
        Assert.NotNull(parked.HnmDefeatedAt);
        Assert.Equal(1, parked.SourceTodId);
        Assert.Equal(OldRepop, parked.StartTime);
    }
}
