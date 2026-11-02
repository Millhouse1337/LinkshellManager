using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LinkshellManager.Tests;

// Ending an HNM camp used to post two Discord messages: the board turned into its "defeated" note,
// and a separate "Event ended" summary. The note carries the summary now, so the summary posts on
// its own only when there is no parked board to carry it.
public class EventEndedSummaryOnBoardTests
{
    private const int HistoryId = 50;

    private static async Task<ApplicationDbContext> SeededAsync(Event? camp)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.EventHistories.Add(new EventHistory { Id = HistoryId, LinkshellId = 1, EventName = "cer", EventType = "HNM" });
        if (camp is not null)
        {
            db.Events.Add(camp);
        }
        db.WindowEvents.Add(new WindowEvent
        {
            Id = 7,
            LinkshellId = 1,
            Name = "cer",
            CampEventHistoryId = HistoryId,
            SourceEventId = camp?.Id,
        });
        await db.SaveChangesAsync();
        return db;
    }

    private static Event Camp(DateTime? defeatedAt, string? messageId) => new()
    {
        Id = 300,
        LinkshellId = 1,
        EventName = "cer",
        EventType = "HNM",
        AssignedMonsterName = "Cerberus",
        HnmDefeatedAt = defeatedAt,
        DiscordChannelId = "chan",
        DiscordMessageId = messageId,
    };

    [Fact]
    public async Task A_board_parked_as_the_defeated_note_carries_it()
    {
        using var db = await SeededAsync(Camp(DateTime.UtcNow, "msg"));
        Assert.True(await DiscordEventEndedPublisher.CarriedByCampBoardAsync(db, HistoryId, CancellationToken.None));
    }

    [Fact]
    public async Task A_removed_board_does_not_so_the_summary_posts()
    {
        using var db = await SeededAsync(camp: null);
        Assert.False(await DiscordEventEndedPublisher.CarriedByCampBoardAsync(db, HistoryId, CancellationToken.None));
    }

    [Fact]
    public async Task A_board_never_posted_to_Discord_does_not()
    {
        using var db = await SeededAsync(Camp(DateTime.UtcNow, messageId: null));
        Assert.False(await DiscordEventEndedPublisher.CarriedByCampBoardAsync(db, HistoryId, CancellationToken.None));
    }

    [Fact]
    public async Task A_board_back_up_for_the_next_pop_does_not()
    {
        using var db = await SeededAsync(Camp(defeatedAt: null, "msg"));
        Assert.False(await DiscordEventEndedPublisher.CarriedByCampBoardAsync(db, HistoryId, CancellationToken.None));
    }
}
