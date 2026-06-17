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

    // payload_json + files[0] for a single-image message/edit.
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

    // Edits an existing message in place (used to refresh an event's signup
    // roster). Returns false on failure.
    public async Task<bool> EditMessageAsync(
        string channelId, string messageId, object payload, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        try
        {
            using var client = CreateClient();
            using var content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(
                HttpMethod.Patch,
                $"{ApiBase}/channels/{Uri.EscapeDataString(channelId)}/messages/{Uri.EscapeDataString(messageId)}")
            {
                Content = content
            };
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Discord edit of message {MessageId} in channel {ChannelId} failed: {Status}.",
                    messageId, channelId, response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to edit message {MessageId} in channel {ChannelId}.", messageId, channelId);
            return false;
        }
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
