using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LinkshellManagerDiscordApp.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace LinkshellManagerDiscordApp.Services;

// Thin wrapper over the Discord REST API for actions the BOT performs in a guild
// or channel: listing channels (config UI pick-list) and posting/editing
// messages (event announcements with interactive components). Distinct from
// DiscordIdentityService, which is about user identity + membership. Uses the bot
// token from DiscordOAuthOptions; every call is best-effort — it returns
// null/false and logs on failure rather than throwing, so a Discord hiccup never
// breaks the app flow that triggered it.
public sealed class DiscordBotClient
{
    private const string ApiBase = "https://discord.com/api/v10";

    // Discord component/embed keys are snake_case; anonymous objects use those
    // literal names where needed. CamelCase only lowercases the first letter, so
    // already-lowercase snake_case identifiers pass through unchanged. Mirrors
    // DiscordAuctionChannelPublisher's serializer settings.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly TimeSpan ChannelsTtl = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscordOAuthOptions _options;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DiscordBotClient> _logger;

    public DiscordBotClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DiscordOAuthOptions> options,
        IMemoryCache cache,
        ILogger<DiscordBotClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _cache = cache;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.BotToken);

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", _options.BotToken);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    // Make the Activity's default "Launch" entry-point command APP-HANDLED (handler 1)
    // instead of DISCORD_LAUNCH_ACTIVITY (handler 2). With Discord's default handler,
    // Discord itself auto-posts the public "Game Invitation / Join" card to the channel
    // whenever someone launches the Activity. App-handled means our interactions endpoint
    // receives the launch and responds with a quiet LAUNCH_ACTIVITY callback instead — no
    // public card. Idempotent + best-effort (only patches when needed; logs and returns on
    // any failure, never throws). Run once at startup.
    public async Task EnsureEntryPointAppHandledAsync(CancellationToken cancellationToken)
    {
        const int PrimaryEntryPointType = 4; // PRIMARY_ENTRY_POINT command type
        const int AppHandler = 1;            // vs 2 = DISCORD_LAUNCH_ACTIVITY

        if (!IsConfigured || string.IsNullOrWhiteSpace(_options.ClientId))
        {
            return;
        }

        var commandsUrl = $"{ApiBase}/applications/{Uri.EscapeDataString(_options.ClientId)}/commands";
        try
        {
            using var client = CreateClient();
            using var listResponse = await client.GetAsync(commandsUrl, cancellationToken);
            if (!listResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Couldn't list application commands to suppress the launch card: {Status}.", listResponse.StatusCode);
                return;
            }

            var commands = await listResponse.Content.ReadFromJsonAsync<List<ApplicationCommandPayload>>(
                cancellationToken: cancellationToken);
            var entryPoint = commands?.FirstOrDefault(c => c.Type == PrimaryEntryPointType);
            if (entryPoint is null || string.IsNullOrWhiteSpace(entryPoint.Id))
            {
                _logger.LogInformation("No Activity entry-point command found; nothing to suppress.");
                return;
            }
            if (entryPoint.Handler == AppHandler)
            {
                return; // already app-handled
            }

            using var body = new StringContent(
                JsonSerializer.Serialize(new { handler = AppHandler }, JsonOptions), Encoding.UTF8, "application/json");
            using var patchResponse = await client.PatchAsync($"{commandsUrl}/{entryPoint.Id}", body, cancellationToken);
            if (patchResponse.IsSuccessStatusCode)
            {
                _logger.LogInformation("Activity entry-point set to APP_HANDLER — the public launch card is now suppressed.");
            }
            else
            {
                _logger.LogWarning(
                    "Couldn't set the entry-point command to APP_HANDLER: {Status}.", patchResponse.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Configuring the Activity entry-point command failed.");
        }
    }

    // Registers the global "/lsm" slash command — running it posts a "Join" card that
    // launches the Activity (officer-gated in the interactions handler). POSTing a global
    // command upserts it by name, so this is idempotent. Best-effort: logs + returns on
    // any failure, never throws. Run once at startup.
    public async Task EnsureLaunchCommandRegisteredAsync(CancellationToken cancellationToken)
    {
        const int ChatInputType = 1; // CHAT_INPUT slash command

        if (!IsConfigured || string.IsNullOrWhiteSpace(_options.ClientId))
        {
            return;
        }

        var commandsUrl = $"{ApiBase}/applications/{Uri.EscapeDataString(_options.ClientId)}/commands";
        try
        {
            using var client = CreateClient();
            using var body = new StringContent(
                JsonSerializer.Serialize(new
                {
                    name = "lsm",
                    description = "Post a button to launch the LinkshellManager app in this channel.",
                    type = ChatInputType,
                }, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(commandsUrl, body, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Registered the /lsm launch-card slash command.");
            }
            else
            {
                _logger.LogWarning("Couldn't register the /lsm command: {Status}.", response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Registering the /lsm command failed.");
        }
    }

    // Text/announcement channels of a guild (id + name) for the config pick-list.
    // Null when no bot token, the bot isn't in the guild, or the call fails.
    public async Task<IReadOnlyList<DiscordChannelInfo>?> ListTextChannelsAsync(
        string guildId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(guildId))
        {
            return null;
        }

        var cacheKey = $"discord-bot-channels:{guildId}";
        // forceRefresh skips the cached copy (still refreshing it below) so a just-
        // created Discord channel shows up immediately instead of after the TTL.
        if (!forceRefresh
            && _cache.TryGetValue<IReadOnlyList<DiscordChannelInfo>>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync(
                $"{ApiBase}/guilds/{Uri.EscapeDataString(guildId)}/channels", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord channel listing for guild {GuildId} failed: {Status}.", guildId, response.StatusCode);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<List<DiscordChannelPayload>>(
                cancellationToken: cancellationToken);
            // type 0 = GUILD_TEXT, 5 = GUILD_ANNOUNCEMENT — both accept bot posts.
            var channels = (payload ?? new List<DiscordChannelPayload>())
                .Where(channel => channel.Type is 0 or 5 && !string.IsNullOrWhiteSpace(channel.Id))
                .Select(channel => new DiscordChannelInfo(channel.Id, channel.Name ?? channel.Id, channel.Position))
                .OrderBy(channel => channel.Position)
                .ToList();
            _cache.Set(cacheKey, (IReadOnlyList<DiscordChannelInfo>)channels, ChannelsTtl);
            return channels;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to list channels for guild {GuildId}.", guildId);
            return null;
        }
    }

    // Posts a message to a channel and returns the new message id, or null on
    // failure (bot lacks Send Messages, channel gone, etc.).
    public async Task<string?> PostMessageAsync(string channelId, object payload, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        try
        {
            using var client = CreateClient();
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord post to channel {ChannelId} failed: {Status} {Body}.",
                    channelId, response.StatusCode, Truncate(body, 300));
                return null;
            }

            var message = await response.Content.ReadFromJsonAsync<DiscordMessagePayload>(
                cancellationToken: cancellationToken);
            return message?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to post to channel {ChannelId}.", channelId);
            return null;
        }
    }

    // Posts a message with a PNG attachment (the rendered event-board image) via
    // multipart. `payload` must reference the file as attachments:[{id:0,...}];
    // the file is uploaded as files[0]. Returns the new message id or null.
    public async Task<string?> PostMessageWithImageAsync(
        string channelId, object payload, byte[] image, string fileName, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId))
        {
            return null;
        }

        try
        {
            using var client = CreateClient();
            using var content = BuildMultipart(payload, image, fileName);
            using var response = await client.PostAsync(
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord image post to channel {ChannelId} failed: {Status} {Body}.",
                    channelId, response.StatusCode, Truncate(body, 300));
                return null;
            }

            var message = await response.Content.ReadFromJsonAsync<DiscordMessagePayload>(
                cancellationToken: cancellationToken);
            return message?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to image-post to channel {ChannelId}.", channelId);
            return null;
        }
    }

    // Edits a message to carry a freshly-rendered PNG (replacing the prior image).
    // Sets attachments:[{id:0,...}] so the old attachment is dropped. Returns false
    // on failure.
    public async Task<bool> EditMessageWithImageAsync(
        string channelId, string messageId, object payload, byte[] image, string fileName,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        try
        {
            using var client = CreateClient();
            using var content = BuildMultipart(payload, image, fileName);
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}")
            {
                Content = content
            };
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Discord image edit of message {MessageId} in channel {ChannelId} failed: {Status} {Body}.",
                    messageId, channelId, response.StatusCode, Truncate(body, 300));
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to image-edit message {MessageId} in channel {ChannelId}.", messageId, channelId);
            return false;
        }
    }

    // payload_json + files[0] for a single-file image message/edit (the rendered board).
    private MultipartFormDataContent BuildMultipart(object payload, byte[] image, string fileName)
    {
        var form = new MultipartFormDataContent();
        var json = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        form.Add(json, "payload_json");
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "files[0]", fileName);
        return form;
    }

    // A single attachment to upload alongside a message (bytes + filename + MIME).
    public sealed record OutgoingFile(byte[] Bytes, string FileName, string ContentType);

    // Posts a message carrying MULTIPLE attachments (e.g. the rendered PNG shown in
    // the embed via attachment://, PLUS a downloadable .xlsx). `payload` must list
    // each file in attachments:[{id:N,filename:…}] where N is its index in `files`.
    // Returns the new message id or null.
    public async Task<string?> PostMessageWithFilesAsync(
        string channelId, object payload, IReadOnlyList<OutgoingFile> files, CancellationToken cancellationToken)
    {
        var outcome = await SendMultipartAsync(channelId, null, payload, files, cancellationToken);
        return outcome.Result == DiscordEditResult.Edited ? outcome.MessageId : null;
    }

    // Edits a message to carry a fresh set of attachments (replacing the prior set,
    // since payload's attachments array fully redefines them). Returns a three-way
    // result so the caller can repost only when the message is genuinely gone, and
    // keep the stored id on a transient failure — see DiscordEditResult.
    public async Task<DiscordEditResult> EditMessageWithFilesAsync(
        string channelId, string messageId, object payload, IReadOnlyList<OutgoingFile> files,
        CancellationToken cancellationToken)
        => (await SendMultipartAsync(channelId, messageId, payload, files, cancellationToken)).Result;

    // messageId null → POST (Edited carries the new id); non-null → PATCH (Edited
    // echoes messageId). Failure classification + 429 retry live in SendClassifiedAsync.
    private async Task<MultipartOutcome> SendMultipartAsync(
        string channelId, string? messageId, object payload, IReadOnlyList<OutgoingFile> files,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId))
        {
            return new(DiscordEditResult.TransientFailure, null);
        }
        if (messageId is not null && string.IsNullOrWhiteSpace(messageId))
        {
            return new(DiscordEditResult.TransientFailure, null);
        }

        var url = messageId is null
            ? $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages"
            : $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";

        return await SendClassifiedAsync(
            messageId is null ? HttpMethod.Post : HttpMethod.Patch,
            url,
            () => BuildMultipart(payload, files),
            returnNewId: messageId is null,
            existingMessageId: messageId,
            channelId,
            messageId is null ? "multi-file post" : "multi-file edit",
            cancellationToken);
    }

    // Sends a request, rebuilding its content per attempt (so a 429 can be retried),
    // honoring one Retry-After backoff, and classifying the outcome. On a successful
    // POST (returnNewId) the new message id is read from the body; otherwise
    // existingMessageId is echoed back on success.
    private async Task<MultipartOutcome> SendClassifiedAsync(
        HttpMethod method, string url, Func<HttpContent> contentFactory, bool returnNewId,
        string? existingMessageId, string channelId, string verb, CancellationToken cancellationToken)
    {
        try
        {
            using var client = CreateClient();
            for (var attempt = 0; ; attempt++)
            {
                using var content = contentFactory();
                using var request = new HttpRequestMessage(method, url) { Content = content };
                using var response = await client.SendAsync(request, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    if (!returnNewId)
                    {
                        return new(DiscordEditResult.Edited, existingMessageId);
                    }
                    var message = await response.Content.ReadFromJsonAsync<DiscordMessagePayload>(
                        cancellationToken: cancellationToken);
                    return new(DiscordEditResult.Edited, message?.Id);
                }

                // One retry on a rate-limit: a DKP burst (e.g. closing an event credits the
                // whole roster) can momentarily exhaust the channel budget; a brief wait +
                // single retry avoids dropping the refresh without busy-looping.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
                {
                    var delay = ResolveRetryAfter(response);
                    _logger.LogWarning(
                        "Discord {Verb} to channel {ChannelId} was rate-limited (429); retrying once after {Delay}.",
                        verb, channelId, delay);
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                // 401/403 = the bot was removed from the server or lacks the channel perms;
                // this won't fix itself, so log it distinctly and actionably (the generic
                // warning below buries it among normal transient blips). We still classify it
                // as transient so the existing post/binding is kept, not orphaned.
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _logger.LogError(
                        "Discord {Verb} to channel {ChannelId} was rejected ({Status}). The bot may have been " +
                        "removed from the server or lacks Send Messages / Embed Links / Attach Files in that channel — " +
                        "re-invite the bot or re-select the channel in settings. {Body}",
                        verb, channelId, response.StatusCode, Truncate(body, 300));
                    return new(DiscordEditResult.TransientFailure, null);
                }
                _logger.LogWarning(
                    "Discord {Verb} to channel {ChannelId} failed: {Status} {Body}.",
                    verb, channelId, response.StatusCode, Truncate(body, 300));
                return new(ClassifyFailure(response.StatusCode, body), null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to {Verb} channel {ChannelId}.", verb, channelId);
            return new(DiscordEditResult.TransientFailure, null);
        }
    }

    // A genuinely gone target — HTTP 404 or Discord JSON error code 10008 ("Unknown
    // Message") — means the caller should repost fresh; everything else (429 after our
    // retry, 5xx, network) is transient and should keep the stored id for a later retry.
    private static DiscordEditResult ClassifyFailure(HttpStatusCode status, string body)
        => status == HttpStatusCode.NotFound
            || body.Contains("\"code\": 10008") || body.Contains("\"code\":10008")
            ? DiscordEditResult.MessageGone
            : DiscordEditResult.TransientFailure;

    // Backoff for a 429: prefer Discord's precise X-RateLimit-Reset-After (float
    // seconds), fall back to the standard Retry-After, clamped so a global limit can't
    // stall the background loop.
    private static TimeSpan ResolveRetryAfter(HttpResponseMessage response)
    {
        double seconds = 1;
        if (response.Headers.TryGetValues("X-RateLimit-Reset-After", out var resetValues)
            && double.TryParse(resetValues.FirstOrDefault(), NumberStyles.Any, CultureInfo.InvariantCulture, out var reset)
            && reset > 0)
        {
            seconds = reset;
        }
        else if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            seconds = delta.TotalSeconds;
        }
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0.5, 10));
    }

    // payload_json + files[0..n] for a multi-file message/edit.
    private MultipartFormDataContent BuildMultipart(object payload, IReadOnlyList<OutgoingFile> files)
    {
        var form = new MultipartFormDataContent();
        var json = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        form.Add(json, "payload_json");
        for (var i = 0; i < files.Count; i++)
        {
            var part = new ByteArrayContent(files[i].Bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue(files[i].ContentType);
            form.Add(part, $"files[{i}]", files[i].FileName);
        }
        return form;
    }

    // Edits an existing message in place (text/embed only — used to refresh an event's
    // signup roster, the ToD board, etc.). Returns a three-way result so callers can
    // repost only when the message is genuinely gone — see DiscordEditResult.
    public async Task<DiscordEditResult> EditMessageAsync(
        string channelId, string messageId, object payload, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
        {
            return DiscordEditResult.TransientFailure;
        }

        var url = $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}";
        var outcome = await SendClassifiedAsync(
            HttpMethod.Patch,
            url,
            () => new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
            returnNewId: false,
            existingMessageId: messageId,
            channelId,
            "edit",
            cancellationToken);
        return outcome.Result;
    }

    // Deletes a message (used to remove an event's signup board once the event
    // is over). Returns false on failure; a 404 (already gone) is treated as
    // success so re-runs don't error.
    public async Task<bool> DeleteMessageAsync(string channelId, string messageId, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.DeleteAsync(
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}",
                cancellationToken);
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return true;
            }
            _logger.LogWarning(
                "Discord delete of message {MessageId} in channel {ChannelId} failed: {Status}.",
                messageId, channelId, response.StatusCode);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to delete message {MessageId} in channel {ChannelId}.", messageId, channelId);
            return false;
        }
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    // Result + (for a POST) the new message id, from SendClassifiedAsync.
    private readonly record struct MultipartOutcome(DiscordEditResult Result, string? MessageId);

    private sealed record DiscordChannelPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("position")] int Position);

    private sealed record DiscordMessagePayload(
        [property: JsonPropertyName("id")] string Id);

    private sealed record ApplicationCommandPayload(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("handler")] int? Handler);
}

// A Discord channel (id + name + position) the bot can post to.
public sealed record DiscordChannelInfo(string Id, string Name, int Position);

// Outcome of an edit/post a caller may need to branch on: Edited = success;
// MessageGone = the target message no longer exists (HTTP 404 / "Unknown Message"),
// so repost fresh; TransientFailure = a rate-limit/5xx/network error, so keep any
// stored message id and retry on the next refresh rather than orphaning it.
public enum DiscordEditResult { Edited, MessageGone, TransientFailure }
