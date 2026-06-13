using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LinkshellManagerDiscordApp.Services;

// Summarizes the free-text peer comments a member has received into a short,
// neutral blurb shown on their "what others think" panel. Backed by the
// Anthropic Messages API (cheap/fast Haiku model by default). Summaries are
// cached in-process keyed by the comment set, so re-opening the panel does not
// re-bill the API. When no API key is configured the service returns null and
// callers fall back to listing the raw comments.
public sealed class AiCommentSummaryService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AiCommentSummaryService> _logger;

    // hash(comments) -> summary. Bounded by the number of distinct comment sets
    // viewed this process lifetime; comments change rarely so this stays tiny.
    private readonly ConcurrentDictionary<string, string> _cache = new();

    private const string AnthropicEndpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultModel = "claude-haiku-4-5-20251001";

    public AiCommentSummaryService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<AiCommentSummaryService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    // True when an API key is configured (so callers can decide whether to even
    // surface the "AI summary" affordance).
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    // Returns a one-paragraph summary of the supplied comments, or null when the
    // service is unconfigured / the call fails (caller falls back to raw list).
    public async Task<string?> SummarizeAsync(IReadOnlyList<string> comments, CancellationToken cancellationToken)
    {
        var cleaned = comments
            .Select(c => c?.Trim() ?? string.Empty)
            .Where(c => c.Length > 0)
            .ToList();
        if (cleaned.Count == 0) return null;

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var cacheKey = HashComments(cleaned);
        if (_cache.TryGetValue(cacheKey, out var cached)) return cached;

        try
        {
            var model = _configuration["Ai:Model"];
            if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;

            var numbered = string.Join("\n", cleaned.Select((c, i) => $"{i + 1}. {c}"));
            var prompt =
                "You are summarizing anonymous peer feedback about a Final Fantasy XI player's job " +
                "performance and gear for a linkshell (guild). Write 1-3 short, neutral sentences " +
                "capturing the overall sentiment and any recurring themes. Do not invent details, do " +
                "not name individual commenters, and do not add a preamble. Comments:\n\n" + numbered;

            var requestBody = new
            {
                model,
                max_tokens = 256,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, AnthropicEndpoint)
            {
                Content = JsonContent.Create(requestBody)
            };
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", AnthropicVersion);

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(20);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AI comment summary call failed ({Status}): {Body}", (int)response.StatusCode, body);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var summary = ExtractText(document.RootElement);
            if (string.IsNullOrWhiteSpace(summary)) return null;

            summary = summary.Trim();
            _cache[cacheKey] = summary;
            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI comment summary call threw.");
            return null;
        }
    }

    // Anthropic returns { content: [ { type: "text", text: "..." }, ... ] }.
    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text))
            {
                builder.Append(text.GetString());
            }
        }
        return builder.ToString();
    }

    private string? ResolveApiKey()
    {
        return _configuration["Ai:ApiKey"]
            ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
    }

    private static string HashComments(IEnumerable<string> comments)
    {
        var joined = string.Join("", comments);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes);
    }
}
