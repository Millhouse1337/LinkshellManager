using System.Net;
using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Builds and maintains the live "ToD board": one Discord message per
// board-enabled webhook that lists every ToD for the linkshell. The message
// is POSTed once, its id stored, and every subsequent change PATCHes that
// same message in place (never re-posts). Repop times use Discord's native
// relative-timestamp markdown (<t:unix:R>) so the "in 4 hours" countdown
// ticks on the client without us having to re-edit.
public sealed class DiscordTodBoardPublisher
{
    private const int EmbedColor = 0x5865F2; // Discord blurple.
    private const int MaxDescription = 4000;  // Discord hard limit is 4096.

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordTodBoardPublisher> _logger;

    public DiscordTodBoardPublisher(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordTodBoardPublisher> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task PublishAsync(int linkshellId, CancellationToken cancellationToken)
    {
        // Tracked: we persist TodBoardMessageId back onto these rows.
        var webhooks = await _db.LinkshellDiscordWebhooks
            .Where(w => w.LinkshellId == linkshellId && w.PostTodBoard && w.Url != null && w.Url != "")
            .OrderBy(w => w.Id)
            .ToListAsync(cancellationToken);
        if (webhooks.Count == 0)
        {
            return;
        }

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName, l.HiddenTodMonsters })
            .FirstOrDefaultAsync(cancellationToken);

        var hidden = new HashSet<string>(
            (linkshell?.HiddenTodMonsters ?? string.Empty)
                .Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        // All ToDs for the linkshell (HNMs included — the board is the full
        // pop tracker, unlike the web /Tod page which hides true HNMs).
        var tods = await _db.Tods
            .AsNoTracking()
            .Where(t => t.LinkshellId == linkshellId)
            .Select(t => new { t.Id, t.MonsterName, t.DayNumber, t.Time, t.RepopTime, t.Claim, t.Cooldown })
            .ToListAsync(cancellationToken);

        // Latest row per monster (Time desc, Id desc — same as the web list),
        // hidden monsters removed, ordered by soonest repop (nulls last).
        var rows = tods
            .Where(t => !string.IsNullOrWhiteSpace(t.MonsterName)
                        && !hidden.Contains(t.MonsterName!.Trim()))
            .GroupBy(t => t.MonsterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(t => t.Time ?? DateTime.MinValue)
                .ThenByDescending(t => t.Id)
                .First())
            .OrderBy(t => t.RepopTime ?? DateTime.MaxValue)
            .ThenBy(t => t.MonsterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var description = new StringBuilder();
        if (rows.Count == 0)
        {
            description.Append("_No ToDs recorded yet._");
        }
        else
        {
            var omitted = 0;
            foreach (var row in rows)
            {
                // Name · Day N · <cooldown> CD — <repop absolute> (<live countdown>).
                // <t:unix:R> is Discord's native relative timestamp: it ticks
                // down on every client on its own, no re-edit from us needed.
                var meta = $"**{EscapeMarkdown(row.MonsterName!.Trim())}**";
                if (row.DayNumber is > 0)
                {
                    meta += $" · Day {row.DayNumber}";
                }
                if (!string.IsNullOrWhiteSpace(row.Cooldown))
                {
                    meta += $" · {EscapeMarkdown(row.Cooldown!.Trim())} CD";
                }

                string line;
                if (row.RepopTime.HasValue)
                {
                    var unix = ((DateTimeOffset)DateTime.SpecifyKind(row.RepopTime.Value, DateTimeKind.Utc))
                        .ToUnixTimeSeconds();
                    line = $"{meta} — <t:{unix}:f> (<t:{unix}:R>)";
                }
                else
                {
                    line = $"{meta} — _no repop time_";
                }

                if (description.Length + line.Length + 1 > MaxDescription)
                {
                    omitted++;
                    continue;
                }
                if (description.Length > 0)
                {
                    description.Append('\n');
                }
                description.Append(line);
            }
            if (omitted > 0)
            {
                description.Append($"\n_…and {omitted} more_");
            }
        }

        var payload = new
        {
            username = "LSM",
            embeds = new[]
            {
                new
                {
                    title = string.IsNullOrWhiteSpace(linkshell?.LinkshellName)
                        ? "ToD Tracker"
                        : $"ToD Tracker — {Truncate(linkshell!.LinkshellName!, 230)}",
                    description = description.ToString(),
                    color = EmbedColor,
                    footer = new { text = "Updated" },
                    timestamp = DateTime.UtcNow.ToString("o"),
                },
            },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        foreach (var webhook in webhooks)
        {
            var baseUrl = webhook.Url?.Trim();
            if (string.IsNullOrEmpty(baseUrl) || !IsDiscordWebhookUrl(baseUrl))
            {
                _logger.LogWarning(
                    "Linkshell {LinkshellId} board webhook \"{Name}\" has a non-Discord URL; skipping.",
                    linkshellId, webhook.Name ?? "(unnamed)");
                continue;
            }

            try
            {
                var edited = false;
                if (!string.IsNullOrEmpty(webhook.TodBoardMessageId))
                {
                    var editUrl = $"{baseUrl.TrimEnd('/')}/messages/{webhook.TodBoardMessageId}";
                    using var editContent = new StringContent(json, Encoding.UTF8, "application/json");
                    using var editResponse = await client.PatchAsync(editUrl, editContent, cancellationToken);
                    if (editResponse.IsSuccessStatusCode)
                    {
                        edited = true;
                    }
                    else if (editResponse.StatusCode == HttpStatusCode.NotFound)
                    {
                        // The board message was deleted in Discord — drop the
                        // stale id and fall through to post a fresh one.
                        webhook.TodBoardMessageId = null;
                    }
                    else
                    {
                        var body = await editResponse.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning(
                            "ToD board edit returned {Status} for linkshell {LinkshellId} webhook \"{Name}\": {Body}",
                            (int)editResponse.StatusCode, linkshellId, webhook.Name ?? "(unnamed)", Truncate(body, 300));
                    }
                }

                if (!edited && string.IsNullOrEmpty(webhook.TodBoardMessageId))
                {
                    // Append ?wait=true so Discord returns the created message
                    // (we need its id for future in-place edits).
                    var sep = baseUrl.Contains('?') ? '&' : '?';
                    var createUrl = $"{baseUrl}{sep}wait=true";
                    using var createContent = new StringContent(json, Encoding.UTF8, "application/json");
                    using var createResponse = await client.PostAsync(createUrl, createContent, cancellationToken);
                    if (!createResponse.IsSuccessStatusCode)
                    {
                        var body = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning(
                            "ToD board post returned {Status} for linkshell {LinkshellId} webhook \"{Name}\": {Body}",
                            (int)createResponse.StatusCode, linkshellId, webhook.Name ?? "(unnamed)", Truncate(body, 300));
                        continue;
                    }
                    var responseJson = await createResponse.Content.ReadAsStringAsync(cancellationToken);
                    var messageId = ExtractMessageId(responseJson);
                    if (!string.IsNullOrEmpty(messageId))
                    {
                        webhook.TodBoardMessageId = messageId.Length > 32
                            ? messageId[..32]
                            : messageId;
                        // Persist immediately so a later failure / retry edits
                        // this message instead of posting a duplicate board.
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed updating ToD board for linkshell {LinkshellId} webhook \"{Name}\".",
                    linkshellId, webhook.Name ?? "(unnamed)");
            }
        }

        // Persist any cleared (404) ids so a fresh post happens next pass.
        if (_db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static string? ExtractMessageId(string responseJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.TryGetProperty("id", out var idProp)
                ? idProp.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsDiscordWebhookUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.EndsWith("discord.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);

    private static string EscapeMarkdown(string value) => value
        .Replace("\\", "\\\\")
        .Replace("`", "\\`")
        .Replace("*", "\\*")
        .Replace("_", "\\_")
        .Replace("~", "\\~")
        .Replace("|", "\\|");

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
