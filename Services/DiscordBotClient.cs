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

    // Text/announcement channels of a guild (id + name) for the config pick-list.
    // Null when no bot token, the bot isn't in the guild, or the call fails.
    public async Task<IReadOnlyList<DiscordChannelInfo>?> ListTextChannelsAsync(
        string guildId, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(guildId))
        {
            return null;
        }

        var cacheKey = $"discord-bot-channels:{guildId}";
        if (_cache.TryGetValue<IReadOnlyList<DiscordChannelInfo>>(cacheKey, out var cached) && cached is not null)
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

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "…";

    private sealed record DiscordChannelPayload(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("position")] int Position);

    private sealed record DiscordMessagePayload(
        [property: JsonPropertyName("id")] string Id);
}

// A Discord channel (id + name + position) the bot can post to.
public sealed record DiscordChannelInfo(string Id, string Name, int Position);
