using System.Text;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;

namespace LinkshellManagerDiscordApp.Services;

// One person signed up to an event (for rendering the Discord roster).
public sealed record EventSignupLine(string CharacterName, string? JobName);

// Builds the Discord message payload for an event announcement: an embed with the
// event details + current signup roster, a job-pick select menu, and a Withdraw
// button. The same payload shape is used both for the initial bot post
// (POST /channels/{id}/messages) and for the interaction UPDATE_MESSAGE response
// (type 7) that refreshes it after a click, so the message edits itself in place.
public static class DiscordEventMessageBuilder
{
    private const int EmbedColor = 0x5865F2; // Blurple.

    // custom_id prefixes routed by DiscordInteractionsController.
    public const string JobSelectPrefix = "evt:job:";
    public const string WithdrawPrefix = "evt:withdraw:";

    public static object Build(Event ev, IReadOnlyList<EventSignupLine> signups)
    {
        return new
        {
            embeds = new[] { BuildEmbed(ev, signups) },
            components = BuildComponents(ev.Id),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    private static object BuildEmbed(Event ev, IReadOnlyList<EventSignupLine> signups)
    {
        var fields = new List<object>();
        if (ev.CommencementStartTime is { } commenced)
        {
            fields.Add(new { name = "Started", value = TimestampMarkup(commenced), inline = true });
        }
        else if (ev.StartTime is { } start)
        {
            fields.Add(new { name = "Starts", value = TimestampMarkup(start), inline = true });
        }
        if (ev.DkpPerHour is { } dkpPerHour)
        {
            fields.Add(new { name = "DKP / hour", value = dkpPerHour.ToString(), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(ev.EventLocation))
        {
            fields.Add(new { name = "Location", value = Escape(ev.EventLocation!.Trim()), inline = true });
        }

        fields.Add(new
        {
            name = $"Signed up ({signups.Count})",
            value = BuildRoster(signups),
            inline = false,
        });

        var typePrefix = string.IsNullOrWhiteSpace(ev.EventType) ? string.Empty : $"{ev.EventType!.Trim()}: ";
        var title = Truncate($"⚔️ {typePrefix}{ev.EventName ?? $"Event #{ev.Id}"}", 250);

        return new
        {
            title,
            description = string.IsNullOrWhiteSpace(ev.Details) ? null : Truncate(Escape(ev.Details!.Trim()), 1500),
            color = EmbedColor,
            fields = fields.ToArray(),
            footer = new { text = "Pick your job below to sign up · Withdraw to drop out" },
        };
    }

    private static string BuildRoster(IReadOnlyList<EventSignupLine> signups)
    {
        if (signups.Count == 0)
        {
            return "_No one yet — be the first!_";
        }

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var signup in signups)
        {
            var job = string.IsNullOrWhiteSpace(signup.JobName) ? "—" : signup.JobName!.Trim();
            var line = $"• **{Escape(signup.CharacterName)}** — {Escape(job)}";
            // Embed field values cap at 1024 chars; leave room for an overflow note.
            if (sb.Length + line.Length + 1 > 950)
            {
                sb.Append($"\n…and {signups.Count - shown} more");
                break;
            }
            if (sb.Length > 0) { sb.Append('\n'); }
            sb.Append(line);
            shown++;
        }
        return sb.ToString();
    }

    private static object[] BuildComponents(int eventId)
    {
        var options = EventJobCatalog.MainJobOptions
            .Select(job => new { label = job, value = job })
            .ToArray<object>();

        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{JobSelectPrefix}{eventId}",
                        placeholder = "Pick your job to sign up",
                        min_values = 1,
                        max_values = 1,
                        options,
                    },
                },
            },
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary
                        label = "Withdraw",
                        custom_id = $"{WithdrawPrefix}{eventId}",
                    },
                },
            },
        };
    }

    private static string TimestampMarkup(DateTime utc)
    {
        var unix = ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"<t:{unix}:f> (<t:{unix}:R>)";
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
