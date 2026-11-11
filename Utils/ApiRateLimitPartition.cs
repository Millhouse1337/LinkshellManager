using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace LinkshellManagerDiscordApp.Utils;

// Picks the rate-limit bucket for a request (Program.cs's global limiter). Without a
// cap, a script can send thousands of made-up bearer tokens; each one makes us ask
// Discord, and enough failed asks get this server's IP banned by Discord.
//
// Buckets:
// - Not under /api: no limit here (pages, static files; /auth/discord has its own).
// - POST /api/discord/interactions: no limit. Every button click on a Discord board
//   arrives from Discord's own servers, so one shared bucket would throttle a busy
//   board for everyone. The endpoint checks Discord's signature before doing anything
//   else and never calls out to Discord for a bad request, so it can't be used to
//   get the IP banned.
// - /api/activity/uploads (ToD screenshots): a separate bucket per client IP, so image
//   loads never use up the bucket API calls share.
// - A bearer token Discord accepted in the last 12 hours: one bucket per token.
//   The Activity reaches us through Discord's proxy, which hides players' IPs, so
//   without this every Activity user would share one IP bucket.
// - Everything else (website cookies, addon tokens, first Activity calls, made-up
//   tokens): one bucket per client IP. UseForwardedHeaders runs before the limiter,
//   so this is the real visitor's IP (nginx restores it from Cloudflare's header).
public static class ApiRateLimitPartition
{
    public const int DefaultPermitPerMinute = 300;

    // The bucket key, or null when the request isn't limited.
    // recentlyValidatedTokenKey: returns a key for a bearer Discord recently accepted,
    // else null (DiscordIdentityService.RecentlyValidatedTokenKey in the app).
    public static string? ResolveKey(
        HttpRequest request,
        IPAddress? remoteIp,
        Func<string, string?> recentlyValidatedTokenKey)
    {
        if (!request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.Path.StartsWithSegments("/api/discord/interactions", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // ToD screenshots load as plain <img> tags, so they carry no bearer, and through
        // the Activity every viewer shares Discord's proxy IP. A ToD tab full of
        // thumbnails would otherwise use up the shared IP bucket that other Activity
        // users' sign-in calls need. They get their own per-IP bucket: the endpoint
        // only reads a file and never calls Discord.
        if (request.Path.StartsWithSegments("/api/activity/uploads", StringComparison.OrdinalIgnoreCase))
        {
            return "uploads:" + (remoteIp?.ToString() ?? "unknown");
        }

        if (AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var header)
            && "Bearer".Equals(header.Scheme, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(header.Parameter))
        {
            var tokenKey = recentlyValidatedTokenKey(header.Parameter);
            if (tokenKey is not null)
            {
                return "token:" + tokenKey;
            }
        }

        return "ip:" + (remoteIp?.ToString() ?? "unknown");
    }
}
