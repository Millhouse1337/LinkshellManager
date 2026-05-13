using System;
using System.IO;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using NodaTime;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services
    .AddDefaultIdentity<AppUser>(options => options.SignIn.RequireConfirmedAccount = false)
    .AddEntityFrameworkStores<ApplicationDbContext>();

var discordClientId = builder.Configuration["Discord:ClientId"]
    ?? throw new InvalidOperationException("Configuration value 'Discord:ClientId' is required.");
var discordClientSecret = builder.Configuration["Discord:ClientSecret"]
    ?? throw new InvalidOperationException("Configuration value 'Discord:ClientSecret' is required.");

builder.Services
    .AddAuthentication()
    .AddOAuth("DiscordWebsite", options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = discordClientId;
        options.ClientSecret = discordClientSecret;
        options.CallbackPath = "/signin-discord";
        options.AuthorizationEndpoint = "https://discord.com/oauth2/authorize";
        options.TokenEndpoint = "https://discord.com/api/oauth2/token";
        options.UserInformationEndpoint = "https://discord.com/api/users/@me";
        options.SaveTokens = true;
        options.Scope.Clear();
        options.Scope.Add("identify");

        options.ClaimActions.Add(new JsonKeyClaimAction(ClaimTypes.NameIdentifier, ClaimValueTypes.String, "id"));
        options.ClaimActions.Add(new JsonKeyClaimAction("urn:discord:username", ClaimValueTypes.String, "username"));
        options.ClaimActions.Add(new JsonKeyClaimAction("urn:discord:discriminator", ClaimValueTypes.String, "discriminator"));
        options.ClaimActions.Add(new JsonKeyClaimAction("urn:discord:global_name", ClaimValueTypes.String, "global_name"));
        options.ClaimActions.Add(new JsonKeyClaimAction("urn:discord:avatar", ClaimValueTypes.String, "avatar"));

        options.Events = new OAuthEvents
        {
            OnCreatingTicket = async context =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await context.Backchannel.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.HttpContext.RequestAborted);
                response.EnsureSuccessStatusCode();

                await using var payload = await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted);
                using var document = await JsonDocument.ParseAsync(payload, cancellationToken: context.HttpContext.RequestAborted);
                context.RunClaimActions(document.RootElement);
            }
        };
    });

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
});

builder.Services.ConfigureExternalCookie(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.IsEssential = true;
});

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "XSRF-TOKEN";
    options.Cookie.HttpOnly = false;
    options.Cookie.SameSite = SameSiteMode.None;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

var mvcBuilder = builder.Services.AddControllersWithViews(options =>
{
    // Cookie-authenticated POST/PUT/DELETE requests must carry an antiforgery
    // token. Bearer-authenticated requests (Discord Activity SPA, addon) bypass
    // this - see CookieAuthAntiforgeryFilter for the rationale.
    options.Filters.Add<CookieAuthAntiforgeryFilter>();
}).AddJsonOptions(options =>
{
    // Force every DateTime on the wire to be UTC with an explicit `Z`
    // suffix. Without this, EF Core hands back DateTimeKind.Unspecified
    // for `timestamp without time zone` columns and the default serializer
    // emits a naive ISO string, which the JS client then parses as
    // browser-local time — quietly shifting every state comparison by
    // the browser's UTC offset.
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
    options.JsonSerializerOptions.Converters.Add(new UtcNullableDateTimeJsonConverter());
});
var razorPagesBuilder = builder.Services.AddRazorPages();

if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
    razorPagesBuilder.AddRazorRuntimeCompilation();
}

builder.Services.AddOptions<DiscordOAuthOptions>()
    .Bind(builder.Configuration.GetSection("Discord"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<GoogleSheetsOptions>()
    .Bind(builder.Configuration.GetSection("GoogleSheets"));

builder.Services.AddHttpClient();
builder.Services.AddScoped<DiscordIdentityService>();
builder.Services.AddScoped<AltCharacterValidator>();
builder.Services.AddScoped<AppUserProfileService>();
builder.Services.AddScoped<AddonApiAuthService>();
builder.Services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);
builder.Services.AddSingleton<TimeZoneConversionService>();
builder.Services.AddScoped<LootEditService>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<GoogleSheetsSyncService>();
builder.Services.AddSingleton<SheetSyncQueue>();
builder.Services.AddScoped<GoogleOAuthService>();
builder.Services.AddScoped<SheetMigrationService>();
builder.Services.AddHostedService<SheetSyncBackgroundService>();

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;

    if (builder.Environment.IsDevelopment())
    {
        // Dev tunnels (pinggy/ngrok/cloudflared) and localhost reverse proxies are not on
        // the default loopback allowlist, so we accept forwarded headers from any source.
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();
    }
    else
    {
        // In production, populate KnownProxies / KnownNetworks via configuration so we
        // only honor X-Forwarded-* from trusted reverse proxies.
        var trustedProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
        foreach (var proxy in trustedProxies)
        {
            if (System.Net.IPAddress.TryParse(proxy, out var ip))
            {
                options.KnownProxies.Add(ip);
            }
        }

        var trustedNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
        foreach (var network in trustedNetworks)
        {
            var parts = network.Split('/', 2);
            if (parts.Length == 2 &&
                System.Net.IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, prefixLength));
            }
        }
    }
});

var isDevelopment = builder.Environment.IsDevelopment();

// Discord proxies activities under https://<application_id>.discordsays.com.
// Restrict CORS to that exact host (plus discord.com itself for SDK callbacks)
// - the previous wildcard *.discordsays.com allowed every other Discord
// activity to call this API on behalf of an authenticated user.
var activityHost = $"{discordClientId}.discordsays.com";

// Optional dev tunnel host (set DEV_TUNNEL_HOST to e.g. "abc123.ngrok-free.app").
// We require an exact host match instead of wildcarding tunnel-provider domains.
var devTunnelHost = builder.Configuration["DEV_TUNNEL_HOST"]
    ?? Environment.GetEnvironmentVariable("DEV_TUNNEL_HOST");

builder.Services.AddCors(options =>
{
    options.AddPolicy("DiscordCors", policy =>
    {
        policy
            .WithHeaders("Authorization", "Content-Type", "X-XSRF-TOKEN", "Accept", "Cache-Control", "Pragma")
            .WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS")
            .AllowCredentials()
            .SetIsOriginAllowed(origin =>
            {
                try
                {
                    var uri = new Uri(origin);
                    if (uri.Scheme != "https") return false;

                    if (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.Equals(activityHost, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (isDevelopment)
                    {
                        if (!string.IsNullOrWhiteSpace(devTunnelHost) &&
                            uri.Host.Equals(devTunnelHost, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }

                        if (origin.Equals("https://localhost:4200", StringComparison.OrdinalIgnoreCase) ||
                            origin.Equals("https://localhost:5001", StringComparison.OrdinalIgnoreCase) ||
                            origin.Equals("https://localhost:7051", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    return false;
                }
                catch
                {
                    return false;
                }
            });
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // OAuth code exchange: per-IP fixed window. Each call hits Discord and may
    // create a new AppUser row, so unauthenticated flooding has both DB and
    // outbound-cost impact.
    options.AddPolicy("oauth-exchange", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));

    // Addon pairing-code redeem: per-IP. The 8-character code from a 32-char
    // alphabet (~1.1e12 search space) is brute-forceable without throttling.
    options.AddPolicy("addon-pair", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.Use(async (ctx, next) =>
{
    var nonceBytes = new byte[16];
    System.Security.Cryptography.RandomNumberGenerator.Fill(nonceBytes);
    var nonce = Convert.ToBase64String(nonceBytes);
    ctx.Items["CspNonce"] = nonce;

    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers.Remove("X-Frame-Options");

        var devTunnelHostFragment = isDevelopment && !string.IsNullOrWhiteSpace(devTunnelHost)
            ? $" https://{devTunnelHost}"
            : string.Empty;
        var devLocalhost = isDevelopment
            ? " https://localhost:* http://localhost:* ws://localhost:* wss://localhost:*"
            : string.Empty;
        var devLocalhostFrame = isDevelopment
            ? " https://localhost:* http://localhost:*"
            : string.Empty;

        var csp = string.Join(" ",
            "default-src 'self';",
            "base-uri 'self';",
            $"frame-ancestors 'self' https://discord.com https://*.discord.com https://*.discordsays.com{devTunnelHostFragment}{devLocalhostFrame};",
            $"connect-src 'self' https://discord.com https://*.discord.com https://*.discordsays.com{devTunnelHostFragment}{devLocalhost};",
            "img-src 'self' data: blob: https://cdn.discordapp.com https://media.discordapp.net https://*.discordsays.com;",
            "font-src 'self' data:;",
            // 'unsafe-inline' on style-src is retained because the existing
            // Razor views use inline style attributes throughout; migrating
            // those to stylesheets is tracked as follow-up cleanup. The
            // script-src is locked to nonce-only (no 'unsafe-inline', no blob:).
            "style-src 'self' 'unsafe-inline';",
            $"script-src 'self' 'nonce-{nonce}';",
            "object-src 'none';",
            $"frame-src https://discord.com https://*.discord.com https://*.discordsays.com{devTunnelHostFragment};"
        );

        ctx.Response.Headers["Content-Security-Policy"] = csp;
        ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        ctx.Response.Headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";

        return Task.CompletedTask;
    });

    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();

var activityPhysicalPath = Path.Combine(app.Environment.WebRootPath, "discord-activity", "browser");
if (Directory.Exists(activityPhysicalPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(activityPhysicalPath),
        RequestPath = "/discord-activity"
    });

    app.MapFallbackToFile("/discord-activity/{*path}", "discord-activity/browser/index.html");
}

app.UseRouting();
app.UseCors("DiscordCors");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.Run();
