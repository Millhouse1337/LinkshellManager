using System.Text;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts a "DKP spent" log to every channel flagged PostDkpSpendLog. Given a
// coalesced burst of ledger-entry ids, it loads the spends, groups them by
// linkshell, and posts one rich embed per channel (one line per spend —
// "**Name** −400 e body"). Append-only: a fresh message every burst, no
// edit-in-place, so nothing is persisted back and the publisher never writes
// to the db (so it can't recurse through the SaveChanges hook). Best-effort
// per channel — a failed post is dropped, matching DiscordSnapshotPublisher.
public sealed class DiscordDkpSpendPublisher
{
    private const int EmbedColor = 0xF1C40F; // Gold — DKP is the currency.
    private const int MaxDescription = 4000;  // Discord hard limit is 4096.

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ApplicationDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DiscordDkpSpendPublisher> _logger;

    public DiscordDkpSpendPublisher(
        ApplicationDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<DiscordDkpSpendPublisher> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task PublishAsync(
        IReadOnlyCollection<int> ledgerEntryIds, CancellationToken cancellationToken)
    {
        if (ledgerEntryIds.Count == 0)
        {
            return;
        }

        var idSet = ledgerEntryIds.ToHashSet();
        // Re-check Amount < 0 here so a non-spend that slipped through the
        // hook is never posted, and so a since-deleted entry is just absent.
        var entries = await _db.DkpLedgerEntries
            .AsNoTracking()
            .Where(e => idSet.Contains(e.Id) && e.Amount < 0 && e.LinkshellId > 0)
            .Select(e => new
            {
                e.Id,
                e.LinkshellId,
                e.CharacterName,
                e.Amount,
                e.ItemName,
                e.Details,
                e.EntryType,
                e.EventName,
                e.OccurredAt,
            })
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return;
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);

        foreach (var group in entries.GroupBy(e => e.LinkshellId))
        {
            var linkshellId = group.Key;

            var webhooks = await _db.LinkshellDiscordWebhooks
                .AsNoTracking()
                .Where(w => w.LinkshellId == linkshellId
                            && w.PostDkpSpendLog
                            && w.Url != null && w.Url != "")
                .OrderBy(w => w.Id)
                .ToListAsync(cancellationToken);
            if (webhooks.Count == 0)
            {
                continue;
            }

            var linkshellName = await _db.Linkshells
                .AsNoTracking()
                .Where(l => l.Id == linkshellId)
                .Select(l => l.LinkshellName)
                .FirstOrDefaultAsync(cancellationToken);

            var ordered = group
                .OrderBy(e => e.OccurredAt)
                .ThenBy(e => e.Id)
                .ToList();

            var description = new StringBuilder();
            var omitted = 0;
            foreach (var e in ordered)
            {
                var name = string.IsNullOrWhiteSpace(e.CharacterName)
                    ? "(unknown)"
                    : e.CharacterName!.Trim();
                var what = !string.IsNullOrWhiteSpace(e.ItemName)
                    ? e.ItemName!.Trim()
                    : !string.IsNullOrWhiteSpace(e.Details)
                        ? e.Details!.Trim()
                        : e.EntryType;
                var line = $"**{EscapeMarkdown(name)}** −{FormatAmount(e.Amount)} · "
                         + EscapeMarkdown(Truncate(what, 180));

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

            var total = ordered.Sum(e => Math.Abs(e.Amount));
            var payload = new
            {
                username = "LSM",
                embeds = new[]
                {
                    new
                    {
                        title = string.IsNullOrWhiteSpace(linkshellName)
                            ? "DKP Spent"
                            : $"DKP Spent — {Truncate(linkshellName!, 230)}",
                        description = description.ToString(),
                        color = EmbedColor,
                        footer = new
                        {
                            text = ordered.Count == 1
                                ? $"{FormatAmount(total)} DKP spent"
                                : $"{ordered.Count} spends · {FormatAmount(total)} DKP total",
                        },
                        timestamp = DateTime.UtcNow.ToString("o"),
                    },
                },
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);

            foreach (var webhook in webhooks)
            {
                var url = webhook.Url?.Trim();
                if (string.IsNullOrEmpty(url) || !IsDiscordWebhookUrl(url))
                {
                    _logger.LogWarning(
                        "Linkshell {LinkshellId} DKP-log webhook \"{Name}\" has a non-Discord URL; skipping.",
                        linkshellId, webhook.Name ?? "(unnamed)");
                    continue;
                }
                try
                {
                    using var content = new StringContent(json, Encoding.UTF8, "application/json");
                    using var response = await client.PostAsync(url, content, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        var body = await response.Content.ReadAsStringAsync(cancellationToken);
                        _logger.LogWarning(
                            "DKP spend post returned {Status} for linkshell {LinkshellId} webhook \"{Name}\": {Body}",
                            (int)response.StatusCode, linkshellId, webhook.Name ?? "(unnamed)", Truncate(body, 300));
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed posting DKP spend log for linkshell {LinkshellId} webhook \"{Name}\".",
                        linkshellId, webhook.Name ?? "(unnamed)");
                }
            }
        }
    }

    private static string FormatAmount(double value)
    {
        var abs = Math.Abs(value);
        return abs == Math.Floor(abs)
            ? ((long)abs).ToString()
            : abs.ToString("0.##");
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
