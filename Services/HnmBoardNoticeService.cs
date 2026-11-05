using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Replaces an HNM signup board's already-posted Discord message with a "defeated" note
// (monster down, predicted repop, and when the board auto-re-posts), stripping the signup
// buttons. Called when an officer logs the monster's Time of Death
// from the board's "Post ToD" button. The recurring-board poller later edits this same
// message back into a fresh signup board for the next pop (one message that cycles).
public sealed class HnmBoardNoticeService
{
    private readonly DiscordBotClient _bot;
    private readonly ILogger<HnmBoardNoticeService> _logger;

    public HnmBoardNoticeService(DiscordBotClient bot, ILogger<HnmBoardNoticeService> logger)
    {
        _bot = bot;
        _logger = logger;
    }

    // Edits the event's posted board message to the defeated note. No-op (returns false) if
    // the event was never posted to Discord (no channel configured). Reads the repop time
    // from ev.StartTime and the re-post time from ev.HnmRepostAt, so callers set those first.
    //
    // eventHistoryId is the camp's PAST EVENT — the archive written at End Camp. It puts a
    // "View Camp Details" button on this note, which is the only way back to a camp once its
    // board is gone: the roster, the DKP, the loot and the ToD all live on a page nobody in
    // Discord has a link to, and the board that used to carry them has just been replaced by
    // these three lines. Null leaves the note exactly as it was.
    //
    // summaryEmbed is that camp's "🏁 Event ended" summary (DiscordEventEndedPublisher), drawn
    // ABOVE the defeated part so ending a camp leaves one message instead of two. The publisher
    // skips its own post for a camp whose board carries it.
    //
    // details is what the note says about the next pop beyond its time (ResolveDetailsAsync).
    public async Task<bool> PostDefeatedNoticeAsync(
        Event ev, CancellationToken cancellationToken,
        int? eventHistoryId = null,
        object? summaryEmbed = null,
        DefeatedNoticeDetails? details = null)
    {
        if (string.IsNullOrWhiteSpace(ev.DiscordChannelId) || string.IsNullOrWhiteSpace(ev.DiscordMessageId))
        {
            return false;
        }

        var monster = !string.IsNullOrWhiteSpace(ev.AssignedMonsterName)
            ? ev.AssignedMonsterName!
            : (!string.IsNullOrWhiteSpace(ev.EventName) ? ev.EventName! : "The monster");

        var title = $"💀 {monster} defeated";
        object defeated = new
        {
            title,
            description = BuildDefeatedDescription(ev, details),
            color = 0x6B7280
        };
        var payload = new
        {
            content = string.Empty,
            embeds = summaryEmbed is null ? new[] { defeated } : new[] { summaryEmbed, defeated },
            // This REPLACES the board's sign-up buttons rather than adding to them (Discord
            // keeps fields that are omitted, so the array must be sent explicitly). It is
            // empty unless the camp has an archive to open, which is the pre-existing
            // behaviour for a camp nobody attended.
            components = DiscordEventMessageBuilder.BuildDefeatedNoticeComponents(eventHistoryId),
            attachments = Array.Empty<object>()
        };

        // Intentionally collapses the 3-way result to success/not — this is a one-shot
        // "defeated" edit, not a kept-alive board, so transient vs gone are treated the same.
        var ok = await _bot.EditMessageAsync(ev.DiscordChannelId!, ev.DiscordMessageId!, payload, cancellationToken)
            == DiscordEditResult.Edited;
        if (!ok)
        {
            _logger.LogWarning(
                "Could not edit HNM board message {MessageId} into a defeated note for event {EventId}; "
                + "replacing it with a freshly posted note instead.",
                ev.DiscordMessageId, ev.Id);

            // A FAILED EDIT USED TO END HERE, and what it left behind was the worst of the
            // available outcomes: the full sign-up board, untouched, still carrying live Sign Up
            // buttons for a monster already down.
            //
            // Discord rejects this edit for reasons the API answer knows and we do not, and a
            // board that will not convert is not a reason to leave a dead camp advertising itself.
            // So the note stops depending on the edit: delete the message and POST the note. The
            // outcome is what matters -- a small note where a board used to be -- not which verb
            // achieved it.
            //
            // The new id is stored, so the cycle survives: the recurring poller edits THIS message
            // back into the next pop's board exactly as it would have the original. A post that
            // fails leaves DiscordMessageId null, and the publisher posts a fresh board when it
            // next renders, which is the same recovery it already uses for a deleted board.
            await _bot.DeleteMessageAsync(ev.DiscordChannelId!, ev.DiscordMessageId!, cancellationToken);
            var replacementId = await _bot.PostMessageAsync(ev.DiscordChannelId!, payload, cancellationToken);
            ev.DiscordMessageId = string.IsNullOrWhiteSpace(replacementId) ? null : replacementId;
            ok = !string.IsNullOrWhiteSpace(replacementId);
        }

        // A board posted by the old full-width mode is one message per alliance. The note replaces
        // the FIRST; the rest have to go, or the channel keeps showing a live-looking roster for a
        // monster that is already down — buttons and all.
        if (!string.IsNullOrWhiteSpace(ev.DiscordExtraMessageIds))
        {
            foreach (var extra in ev.DiscordExtraMessageIds!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                await _bot.DeleteMessageAsync(ev.DiscordChannelId!, extra, cancellationToken);
            }
            ev.DiscordExtraMessageIds = null;
        }

        return ok;
    }

    // The note's body. Discord <t:unix:D/T/R> renders each viewer's local time + a live "in N hours".
    // D + T ("August 26, 2026" + "6:07:35 PM") rather than the single F style: F is the only
    // style that carries the weekday but it stops at minutes, and a repop time is ToD math —
    // the seconds matter more here than the weekday does.
    internal static string BuildDefeatedDescription(Event ev, DefeatedNoticeDetails? details)
    {
        var description = new StringBuilder();
        if (ev.StartTime.HasValue)
        {
            var repop = ToUnix(ev.StartTime.Value);
            description.Append($"Will repop <t:{repop}:D> <t:{repop}:T> (<t:{repop}:R>).");
        }
        else
        {
            description.Append("Next pop time is being calculated.");
        }
        if (details?.NextDay is { } nextDay)
        {
            description.Append($"\nNext pop: **Day {nextDay}**.");
        }
        if (details?.RepopFormula is { } formula)
        {
            description.Append($"\nRepop = {formula}.");
        }
        if (ev.HnmRepostAt.HasValue)
        {
            var repost = ToUnix(ev.HnmRepostAt.Value);
            description.Append($"\n\nThe sign-up board will automatically re-post in this channel <t:{repost}:R>.");
        }
        return description.ToString();
    }

    // What the note can say about the next pop, read off the ToD the board is parked on.
    //
    // Nothing at all when that ToD is not this kill's. A camp ended with no ToD still points at the
    // ToD it was created from, whose repop is the pop that just happened. The day worked out from it
    // would be the day this camp was already on, so it is left off rather than printed wrong.
    public static async Task<DefeatedNoticeDetails> ResolveDetailsAsync(
        ApplicationDbContext db, Event ev, CancellationToken cancellationToken)
    {
        var tod = ev.SourceTodId is { } todId
            ? await db.Tods.AsNoTracking().FirstOrDefaultAsync(t => t.Id == todId, cancellationToken)
            : null;
        if (tod?.RepopTime is not { } repop
            || (ev.HnmDefeatedAt is { } defeatedAt && repop <= defeatedAt))
        {
            return new DefeatedNoticeDetails(null, null);
        }

        // The same answer the board gets when it comes back (HnmEventSeeder.ReviveForNewPopAsync's
        // callers). Only the three NQ/HQ families count days, and NextDay is 1 after any HQ.
        var nextDay = HnmConfig.HasHqVariant(ev.AssignedMonsterName)
            ? HnmDayCycle.NextDay(tod, ev.DayNumber)
            : null;
        return new DefeatedNoticeDetails(nextDay, TodRepopNote.Formula(tod.Cooldown, tod.AdditionalSeconds));
    }

    private static long ToUnix(DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}

public sealed record DefeatedNoticeDetails(int? NextDay, string? RepopFormula);
