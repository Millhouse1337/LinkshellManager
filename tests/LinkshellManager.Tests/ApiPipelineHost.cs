using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace LinkshellManager.Tests;

// The whole app, Program.cs and all, answering real HTTP requests in memory. Used by the sign-in
// tests: they need the real middleware order (rate limiter, authentication, authorization, the
// cookie's JSON 401/403 bodies, MVC filters), which no hand-built controller has.
//
// What is swapped out, so nothing leaves this process:
// - Environment is Production. Development would load user-secrets, and those point at the live
//   database. A guard below refuses to start if the environment or connection string is not ours.
// - The database is EF's in-memory one (the fake Postgres connection string is never opened).
// - Every outbound HTTP call goes to FakeDiscord, which answers Discord's token and guild-member
//   lookups from a script and refuses everything else.
// - The app's background services (Discord boards, pollers, the entry-point setup) are removed.
// - Data Protection keys go to a temp folder, not the user's machine-wide key store.
public sealed class ApiPipelineHost : WebApplicationFactory<Program>
{
    public const string ClientId = "1100000000000000001";
    private const string NeverOpenedConnection =
        "Host=127.0.0.1;Port=1;Database=api_pipeline_never_opened;Username=none;Password=none";

    private readonly string _databaseName = "api-pipeline-" + Guid.NewGuid();
    private readonly string _keyRingPath = Path.Combine(Path.GetTempPath(), "lsm-api-pipeline-keys-" + Guid.NewGuid());
    private readonly int _permitPerMinute;

    public ApiPipelineHost() : this(permitPerMinute: 100_000)
    {
    }

    public ApiPipelineHost(int permitPerMinute)
    {
        _permitPerMinute = permitPerMinute;
    }

    public FakeDiscord Discord { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ConnectionStrings:DefaultConnection", NeverOpenedConnection);
        builder.UseSetting("Discord:ClientId", ClientId);
        builder.UseSetting("Discord:ClientSecret", "test-secret");
        builder.UseSetting("Database:RunMigrationsOnStartup", "false");
        builder.UseSetting("DataProtection:KeyRingPath", _keyRingPath);
        builder.UseSetting("RateLimiting:ApiPermitPerMinute", _permitPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.UseSetting("SuperAdmin:DiscordUserId", string.Empty);

        builder.ConfigureServices((context, _) =>
        {
            // Runs before the service provider exists, so nothing has touched a database yet.
            if (!context.HostingEnvironment.IsProduction()
                || context.Configuration.GetConnectionString("DefaultConnection") != NeverOpenedConnection)
            {
                throw new InvalidOperationException(
                    "ApiPipelineHost refuses to start: the environment or connection string is not the test one.");
            }
        });

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<ApplicationDbContext>));
            services.AddDbContext<ApplicationDbContext>(options => options
                .UseInMemoryDatabase(_databaseName)
                .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));

            var appAssembly = typeof(Program).Assembly;
            foreach (var hosted in services
                .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType?.Assembly == appAssembly)
                .ToList())
            {
                services.Remove(hosted);
            }

            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(Discord);
        });
    }

    // https, so the Secure cookies and HTTPS redirection behave as on the droplet. No redirects
    // followed and no cookie jar: each test sends exactly the credentials it means to.
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = false,
    });

    public async Task SeedAsync(Func<ApplicationDbContext, Task> seed)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<T> ReadAsync<T>(Func<ApplicationDbContext, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>());
    }

    // A user who signed in through Discord once: the AppUser and the DiscordActivityUser link, so
    // a bearer for this Discord id resolves to them without creating anything.
    public Task SeedUserAsync(string appUserId, string discordUserId) => SeedAsync(db =>
    {
        db.Users.Add(new AppUser
        {
            Id = appUserId,
            UserName = appUserId,
            NormalizedUserName = appUserId.ToUpperInvariant(),
            CharacterName = appUserId,
            SecurityStamp = Guid.NewGuid().ToString(),
        });
        db.DiscordActivityUsers.Add(new DiscordActivityUser
        {
            DiscordUserId = discordUserId,
            Username = appUserId,
            IdentityUserId = appUserId,
        });
        return Task.CompletedTask;
    });

    // The website's sign-in cookie for this user, made the way the app makes it (same ticket
    // format, same Data Protection keys), as a "Cookie" header value.
    public async Task<string> SignInCookieAsync(string appUserId)
    {
        using var scope = Services.CreateScope();
        var services = scope.ServiceProvider;
        var user = await services.GetRequiredService<UserManager<AppUser>>().FindByIdAsync(appUserId)
            ?? throw new InvalidOperationException($"No user {appUserId} to sign in.");
        var principal = await services.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>().CreateAsync(user);
        var options = services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var now = DateTimeOffset.UtcNow;
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now.AddHours(1) },
            IdentityConstants.ApplicationScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try
        {
            if (Directory.Exists(_keyRingPath))
            {
                Directory.Delete(_keyRingPath, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// Stands in for discord.com. Tokens it accepts and guild memberships are set by the test; any
// other outbound request is answered 503 and remembered, so a test can prove none happened.
public sealed class FakeDiscord : HttpMessageHandler, IHttpClientFactory
{
    private readonly ConcurrentDictionary<string, string> _discordUserIdByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Token, string GuildId), bool> _guildMembers = new();
    private readonly ConcurrentQueue<string> _calls = new();

    // Discord accepts this token as this Discord user (issued to our app).
    public void Accept(string token, string discordUserId) => _discordUserIdByToken[token] = discordUserId;

    public void AddGuildMember(string token, string guildId) => _guildMembers[(token, guildId)] = true;

    // Every request seen, as "GET /api/oauth2/@me token=..." lines.
    public IReadOnlyList<string> Calls => _calls.ToList();

    public int TokenLookups(string token) =>
        Calls.Count(call => call == $"GET /api/oauth2/@me token={token}");

    public IReadOnlyList<string> UnexpectedCalls => Calls.Where(call => call.StartsWith("UNEXPECTED ", StringComparison.Ordinal)).ToList();

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = request.Headers.Authorization?.Parameter ?? string.Empty;
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var isDiscord = request.RequestUri?.Host == "discord.com";

        if (isDiscord && request.Method == HttpMethod.Get && path == "/api/oauth2/@me")
        {
            _calls.Enqueue($"GET {path} token={token}");
            if (!_discordUserIdByToken.TryGetValue(token, out var discordUserId))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var json = "{\"application\":{\"id\":\"" + ApiPipelineHost.ClientId + "\"},"
                + "\"user\":{\"id\":\"" + discordUserId + "\",\"username\":\"user" + discordUserId + "\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        const string guildPrefix = "/api/users/@me/guilds/";
        if (isDiscord && request.Method == HttpMethod.Get && path.StartsWith(guildPrefix, StringComparison.Ordinal)
            && path.EndsWith("/member", StringComparison.Ordinal))
        {
            var guildId = path[guildPrefix.Length..^"/member".Length];
            _calls.Enqueue($"GET guild-member {guildId} token={token}");
            return Task.FromResult(_guildMembers.ContainsKey((token, guildId))
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        _calls.Enqueue($"UNEXPECTED {request.Method} {request.RequestUri}");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}

public static class ApiPipelineRequests
{
    public static HttpRequestMessage Get(string path, string? bearer = null, string? cookie = null, string? guildId = null)
        => Build(HttpMethod.Get, path, bearer, cookie, guildId, body: null);

    public static HttpRequestMessage Post(string path, string? bearer = null, string? cookie = null, string? guildId = null, string body = "{}")
        => Build(HttpMethod.Post, path, bearer, cookie, guildId, body);

    public static HttpRequestMessage Build(HttpMethod method, string path, string? bearer, string? cookie, string? guildId, string? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        if (guildId is not null)
        {
            request.Headers.Add("X-Discord-Guild-Id", guildId);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }
}
