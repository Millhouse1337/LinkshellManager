using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Announces an awarded loot row (item + winner + DKP) to the linkshell's Loot
// Discord channel as a bot message. Append-only: one embed per drop, no message
// id tracked. Best-effort — failures are logged and dropped. No-op when the bot
// token isn't configured or no Loot channel is set. Mirrors
// DiscordAuctionChannelPublisher's shape.
public sealed class DiscordLootChannelPublisher
{
    private const int LootColor = 0xF1C40F; // Gold.

    private readonly ApplicationDbContext _db;
    private readonly DiscordBotClient _bot;
    private readonly ChannelRouteResolver _routes;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DiscordLootChannelPublisher> _logger;

    public DiscordLootChannelPublisher(
        ApplicationDbContext db,
        DiscordBotClient bot,
        ChannelRouteResolver routes,
        IConfiguration configuration,
        ILogger<DiscordLootChannelPublisher> logger)
    {
        _db = db;
        _bot = bot;
        _routes = routes;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task HandleAsync(DiscordLootJob job, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        try
        {
            var loot = job.Source == LootJobSource.Tod
                ? await LoadTodLootAsync(job.EntityId, cancellationToken)
                : await LoadEventLootAsync(job.EntityId, cancellationToken);
            if (loot is null || string.IsNullOrWhiteSpace(loot.ItemWinner))
            {
                return;
            }

            var channelId = await _routes.ResolveChannelIdAsync(loot.LinkshellId, ChannelPostTypes.Loot, cancellationToken);
            if (channelId is null)
            {
                return; // No Loot route configured.
            }

            var payload = BuildPayload(loot);
            await _bot.PostMessageAsync(channelId, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loot announce for {Source} #{EntityId} failed.", job.Source, job.EntityId);
        }
    }

    private async Task<LootLine?> LoadTodLootAsync(int detailId, CancellationToken cancellationToken)
    {
        var row = await _db.TodLootDetails
            .AsNoTracking()
            .Include(detail => detail.Tod)
            .FirstOrDefaultAsync(detail => detail.Id == detailId, cancellationToken);
        if (row?.Tod is null)
        {
            return null;
        }
        return new LootLine(
            row.Tod.LinkshellId, row.ItemName, row.ItemWinner, row.WinningDkpSpent, row.Tod.MonsterName);
    }

    private async Task<LootLine?> LoadEventLootAsync(int detailId, CancellationToken cancellationToken)
    {
        var row = await _db.EventLootDetails
            .AsNoTracking()
            .Include(detail => detail.Event)
            .Include(detail => detail.EventHistory)
            .FirstOrDefaultAsync(detail => detail.Id == detailId, cancellationToken);
        if (row is null)
        {
            return null;
        }
        var linkshellId = row.Event?.LinkshellId ?? row.EventHistory?.LinkshellId ?? 0;
        if (linkshellId <= 0)
        {
            return null;
        }
        var context = row.Event?.EventName ?? row.EventHistory?.EventName;
        return new LootLine(linkshellId, row.ItemName, row.ItemWinner, row.WinningDkpSpent, context);
    }

    private object BuildPayload(LootLine loot)
    {
        var fields = new List<object>
        {
            new { name = "Winner", value = Escape(loot.ItemWinner!.Trim()), inline = true },
        };
        if (loot.WinningDkpSpent is > 0)
        {
            fields.Add(new { name = "DKP", value = loot.WinningDkpSpent!.Value.ToString(), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(loot.Context))
        {
            fields.Add(new { name = "From", value = Escape(loot.Context!.Trim()), inline = true });
        }

        var embed = new
        {
            title = Truncate($"🎁 {loot.ItemName ?? "Loot"}", 250),
            color = LootColor,
            fields = fields.ToArray(),
            footer = new { text = "Loot awarded" },
            timestamp = DateTime.UtcNow.ToString("o"),
        };

        var components = BuildComponents();
        return components is null
            ? new { embeds = new[] { embed }, allowed_mentions = new { parse = Array.Empty<string>() } }
            : new { embeds = new[] { embed }, components, allowed_mentions = new { parse = Array.Empty<string>() } };
    }

    // Optional "View in app" link button (no interaction needed). Null when no
    // public base URL is configured.
    private object[]? BuildComponents()
    {
        var baseUrl = _configuration["App:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }
        var url = $"{baseUrl.TrimEnd('/')}/LootHistory";
        return new object[]
        {
            new
            {
                type = 1,
                components = new object[]
                {
                    new { type = 2, style = 5, label = "View loot in app", url },
                },
            },
        };
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";

    private sealed record LootLine(int LinkshellId, string? ItemName, string? ItemWinner, int? WinningDkpSpent, string? Context);
}
