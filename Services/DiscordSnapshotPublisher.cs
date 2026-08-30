using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Services;

// Posts an attendance snapshot to the linkshell's Attendance channel route via
// the bot as a single embed with the roster grouped into parties of six (FFXI
// alliance layout). No-op when no Attendance route is set or the bot isn't
// configured.
public sealed class DiscordSnapshotPublisher
{
    // FFXI party size; an alliance is three of these. Snapshot entries arrive in alliance order,
    // so chunking by this size reproduces the party layout.
    //
    // That used to be an assumption this embed could only hope for: a zone-scope capture could
    // carry 40 people read off the entity list in no particular order, and they rendered as seven
    // fictitious "parties". A snapshot is now exactly one alliance, capped at 18 and read from
    // party memory in slot order, so the chunking describes something real.
    private const int PartySize = 6;

    // Discord "blurple" so the embed reads as a first-party-ish card.
    private const int EmbedColor = 0x5865F2;

    private readonly ApplicationDbContext _db;
    private readonly ChannelRouteResolver _routes;
    private readonly DiscordBotClient _bot;
    private readonly ILogger<DiscordSnapshotPublisher> _logger;

    public DiscordSnapshotPublisher(
        ApplicationDbContext db,
        ChannelRouteResolver routes,
        DiscordBotClient bot,
        ILogger<DiscordSnapshotPublisher> logger)
    {
        _db = db;
        _routes = routes;
        _bot = bot;
        _logger = logger;
    }

    public async Task PublishAsync(int snapshotId, CancellationToken cancellationToken)
    {
        if (!_bot.IsConfigured)
        {
            return;
        }

        var snapshot = await _db.AttendanceSnapshots
            .AsNoTracking()
            .Include(s => s.Entries)
            .FirstOrDefaultAsync(s => s.Id == snapshotId, cancellationToken);
        if (snapshot is null)
        {
            return;
        }

        var channelId = await _routes.ResolveChannelIdAsync(
            snapshot.LinkshellId, ChannelPostTypes.Attendance, cancellationToken);
        if (string.IsNullOrEmpty(channelId))
        {
            // No Attendance route configured -- nothing to do.
            return;
        }

        var linkshellName = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == snapshot.LinkshellId)
            .Select(l => l.LinkshellName)
            .FirstOrDefaultAsync(cancellationToken);

        var payload = BuildPayload(snapshot, linkshellName);
        var messageId = await _bot.PostMessageAsync(channelId, payload, cancellationToken);
        if (string.IsNullOrEmpty(messageId))
        {
            _logger.LogWarning(
                "Attendance snapshot {SnapshotId}: the bot failed to post to channel {ChannelId} for linkshell " +
                "{LinkshellId} (check the bot is in the server with Send Messages).",
                snapshotId, channelId, snapshot.LinkshellId);
        }
    }

    private static object BuildPayload(Models.AttendanceSnapshot snapshot, string? linkshellName)
    {
        // Entry insertion order = capture (alliance) order. Id is identity, so
        // ordering by it is the stable proxy for the in-game member order.
        var orderedEntries = snapshot.Entries.OrderBy(e => e.Id).ToList();

        var fields = new List<object>();
        for (var partyIndex = 0; partyIndex * PartySize < orderedEntries.Count; partyIndex++)
        {
            var members = orderedEntries
                .Skip(partyIndex * PartySize)
                .Take(PartySize)
                .ToList();
            if (members.Count == 0)
            {
                break;
            }

            var sb = new StringBuilder();
            foreach (var m in members)
            {
                var name = EscapeMarkdown(string.IsNullOrWhiteSpace(m.CharacterName) ? "Unknown" : m.CharacterName);
                var job = FormatJob(m.MainJob, m.SubJob);
                sb.Append("• ").Append(name);
                if (!string.IsNullOrEmpty(job))
                {
                    sb.Append(" — `").Append(job).Append('`');
                }
                sb.Append('\n');
            }

            fields.Add(new
            {
                name = $"Party {partyIndex + 1} ({members.Count})",
                value = Truncate(sb.ToString().TrimEnd(), 1024),
                inline = true,
            });
        }

        var baseTitle = string.IsNullOrWhiteSpace(snapshot.Name)
            ? "Attendance Snapshot"
            : snapshot.Name.Trim();
        // The alliance rides in the title because several of them post the same camp within
        // seconds of each other, and the channel is where officers first notice one is missing.
        var title = Truncate(
            snapshot.AllianceNumber is { } alliance ? $"{baseTitle} — Alliance {alliance}" : baseTitle,
            256);

        var capturedBy = string.IsNullOrWhiteSpace(snapshot.CapturedByCharacterName)
            ? null
            : EscapeMarkdown(snapshot.CapturedByCharacterName);
        var description = capturedBy is null
            ? $"{snapshot.EntryCount} members"
            : $"Captured by **{capturedBy}** · {snapshot.EntryCount} members";
        // Said outright so the channel doesn't read as a settled payout. A pending capture is not
        // in the combined roster and earns nothing until an officer confirms it on the web.
        if (snapshot.SnapshotStatus == AttendanceSnapshotStatuses.Pending)
        {
            description += "\n⏳ Awaiting officer confirmation — not counted yet.";
        }

        return new
        {
            embeds = new[]
            {
                new
                {
                    title,
                    description,
                    color = EmbedColor,
                    timestamp = snapshot.CapturedAtUtc.ToString("o"),
                    fields = fields.ToArray(),
                    footer = new
                    {
                        text = string.IsNullOrWhiteSpace(linkshellName)
                            ? "LinkshellManager"
                            : Truncate(linkshellName, 2048),
                    },
                },
            },
            // Embeds never ping, but be explicit so a stray character name can
            // never resolve into a mention.
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    private static string FormatJob(string? mainJob, string? subJob)
    {
        var main = (mainJob ?? string.Empty).Trim();
        var sub = (subJob ?? string.Empty).Trim();
        if (main.Length == 0)
        {
            return string.Empty;
        }
        return sub.Length == 0 ? main : $"{main}/{sub}";
    }

    private static string EscapeMarkdown(string value)
    {
        // Defang Discord markdown so odd character names render literally.
        return value
            .Replace("\\", "\\\\")
            .Replace("`", "\\`")
            .Replace("*", "\\*")
            .Replace("_", "\\_")
            .Replace("~", "\\~")
            .Replace("|", "\\|");
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }
        return value.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
