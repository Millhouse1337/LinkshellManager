using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts the auction card to the linkshell's Auctions channel route via the bot — a text
// embed (status, start/end and host, then the numbered items with their bids; see
// AuctionBoardMessageBuilder) carrying an item picker, My bids and an "Open in app" link:
//  • Create — post the card (one message).
//  • Update — EDIT that same message in place (a new bid, or the auction started / ended), so
//             a live auction is one evolving card, not a stack of per-bid messages.
//  • Close  — post the final results card (winner + DKP per item).
// Best-effort: a failed post is logged and dropped; the auction itself is unaffected. Never
// throws to the caller. No-op when no Auctions route is set or the bot isn't configured.
public sealed class DiscordAuctionChannelPublisher
{
    private readonly ApplicationDbContext _db;
    private readonly ChannelRouteResolver _routes;
    private readonly IConfiguration _configuration;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordAuctionChannelPublisher> _logger;

    public DiscordAuctionChannelPublisher(
        ApplicationDbContext db,
        ChannelRouteResolver routes,
        IConfiguration configuration,
        DiscordBotClient bot,
        ILogger<DiscordAuctionChannelPublisher> logger)
    {
        _db = db;
        _routes = routes;
        _configuration = configuration;
        _bot = bot;
        _logger = logger;
    }

    public async Task HandleAsync(AuctionChannelJob job, CancellationToken cancellationToken)
    {
        try
        {
            switch (job.Kind)
            {
                case AuctionChannelJobKind.Create:
                    await CreateAsync(job.EntityId, cancellationToken);
                    break;
                case AuctionChannelJobKind.Close:
                    await CloseAsync(job.EntityId, cancellationToken);
                    break;
                case AuctionChannelJobKind.Update:
                    await UpdateAsync(job.EntityId, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Auction channel job {Kind} for entity {EntityId} failed.", job.Kind, job.EntityId);
        }
    }

    private async Task CreateAsync(int auctionId, CancellationToken ct)
    {
        // Tracked (not AsNoTracking) so we can persist the posted message id back
        // onto the auction for later in-place bid edits.
        var auction = await _db.Auctions
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null)
        {
            return;
        }

        var channelId = await _routes.ResolveChannelIdAsync(auction.LinkshellId, ChannelPostTypes.Auctions, ct);
        if (string.IsNullOrEmpty(channelId) || !_bot.IsConfigured)
        {
            return;
        }

        var messageId = await _bot.PostMessageAsync(channelId, LivePayload(auction), ct);
        if (!string.IsNullOrEmpty(messageId))
        {
            auction.DiscordChannelId = channelId;
            auction.DiscordMessageId = messageId;
            AppendMessageId(auction, messageId);
            await _db.SaveChangesAsync(ct);
        }
    }

    // Track every posted card id so they can all be deleted from the channel on
    // close (leaving just the results summary).
    private static void AppendMessageId(Auction auction, string messageId)
    {
        auction.DiscordMessageIds = string.IsNullOrEmpty(auction.DiscordMessageIds)
            ? messageId
            : $"{auction.DiscordMessageIds},{messageId}";
    }

    // A bid landed, or the auction started / ended → EDIT the single card in place, so the
    // auction is one evolving card rather than a stack of per-bid messages. No-op when the
    // auction was never posted or the bot isn't configured.
    private async Task UpdateAsync(int auctionId, CancellationToken ct)
    {
        // Tracked so we can repoint DiscordMessageId if we have to re-post.
        var auction = await _db.Auctions
            .Include(a => a.AuctionItems)
            .FirstOrDefaultAsync(a => a.Id == auctionId, ct);
        if (auction is null
            || string.IsNullOrEmpty(auction.DiscordChannelId)
            || !_bot.IsConfigured)
        {
            return;
        }

        var channelId = auction.DiscordChannelId!;
        var payload = LivePayload(auction);

        // Intentionally collapses the 3-way result to success/not — on any non-success
        // (transient or gone) we fall through and re-post a fresh card below, since this
        // path re-points DiscordMessageId rather than protecting a kept-alive id.
        if (!string.IsNullOrEmpty(auction.DiscordMessageId)
            && await _bot.EditMessageAsync(channelId, auction.DiscordMessageId!, payload, ct) == DiscordEditResult.Edited)
        {
            return;
        }

        // No card yet, or the edit failed: post a fresh card and repoint DiscordMessageId at it.
        var newId = await _bot.PostMessageAsync(channelId, payload, ct);
        if (!string.IsNullOrEmpty(newId))
        {
            auction.DiscordMessageId = newId;
            AppendMessageId(auction, newId);
            await _db.SaveChangesAsync(ct);
        }
    }

    private async Task CloseAsync(int auctionHistoryId, CancellationToken ct)
    {
        var history = await _db.AuctionHistories
            .AsNoTracking()
            .Include(h => h.AuctionItems)
            .FirstOrDefaultAsync(h => h.Id == auctionHistoryId, ct);
        if (history is null)
        {
            return;
        }

        var channelId = await _routes.ResolveChannelIdAsync(history.LinkshellId, ChannelPostTypes.Auctions, ct);
        if (string.IsNullOrEmpty(channelId) || !_bot.IsConfigured)
        {
            return;
        }

        await _bot.PostMessageAsync(channelId, AuctionBoardMessageBuilder.BuildClosedPayload(history, BuildAppLink()), ct);
    }

    private object LivePayload(Auction auction)
        => AuctionBoardMessageBuilder.BuildLivePayload(auction, BuildAppLink(), DateTime.UtcNow);

    private string? BuildAppLink()
    {
        var baseUrl = _configuration["App:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        // The app's Auctions tab (the website's own UI now). Older posts still link /Auction,
        // which LegacyUrlRedirects sends to the same place.
        return $"{baseUrl.TrimEnd('/')}{LegacyUrlRedirects.AppBase}auctions";
    }
}
