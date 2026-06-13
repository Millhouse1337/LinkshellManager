using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Post-event discussion comments on a closed event (EventHistory). Adds/lists/
// deletes comments and — when the linkshell has a DiscussionChannelId — mirrors
// each new comment to that Discord channel via DiscordBotClient (best-effort;
// a failed mirror never blocks the in-app comment).
public sealed class EventCommentService
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordBotClient _bot;

    public EventCommentService(ApplicationDbContext db, DiscordBotClient bot)
    {
        _db = db;
        _bot = bot;
    }

    public async Task<EventComment?> AddAsync(
        int historyId, string appUserId, string? characterName, string? body, bool isAnonymous, CancellationToken cancellationToken)
    {
        var history = await _db.EventHistories.AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == historyId, cancellationToken);
        if (history is null) return null;

        var trimmed = body?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        if (trimmed.Length > 2000) trimmed = trimmed[..2000];

        var comment = new EventComment
        {
            EventHistoryId = historyId,
            LinkshellId = history.LinkshellId,
            AppUserId = appUserId,
            CharacterName = characterName,
            Body = trimmed,
            IsAnonymous = isAnonymous,
            CreatedAt = DateTime.UtcNow,
        };
        _db.EventComments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        // Best-effort mirror to the linkshell's discussion channel.
        var channelId = await _db.Linkshells
            .Where(l => l.Id == history.LinkshellId)
            .Select(l => l.DiscussionChannelId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(channelId))
        {
            var who = isAnonymous ? "Anonymous" : (characterName ?? "Member");
            var content = $"💬 **{history.EventName ?? "Event"}** — {who}: {trimmed}";
            if (content.Length > 1900) { content = content[..1900] + "…"; }
            var messageId = await _bot.PostMessageAsync(channelId!, new { content }, cancellationToken);
            if (messageId is not null)
            {
                comment.DiscordMessageId = messageId;
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return comment;
    }

    public Task<List<EventComment>> ListAsync(int historyId, CancellationToken cancellationToken)
        => _db.EventComments.AsNoTracking()
            .Where(c => c.EventHistoryId == historyId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

    // The author can delete their own comment; an officer (canManage) can delete any.
    public async Task<bool> DeleteAsync(int commentId, string appUserId, bool canManage, CancellationToken cancellationToken)
    {
        var comment = await _db.EventComments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null) return false;
        if (!canManage && !string.Equals(comment.AppUserId, appUserId, StringComparison.Ordinal)) return false;
        _db.EventComments.Remove(comment);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
