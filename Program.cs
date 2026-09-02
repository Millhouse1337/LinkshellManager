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
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using NodaTime;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// Build a Npgsql data source with dynamic JSON enabled. CLR types mapped to
// jsonb columns (notably AppUserLinkshell.JobLevels = int[]) only serialize when
// this is on; Npgsql 8 disables it by default, so without it any write to
// JobLevels throws "Writing values of 'System.Int32[]' ... is not supported".
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
dataSourceBuilder.EnableDynamicJson();
var npgsqlDataSource = dataSourceBuilder.Build();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(npgsqlDataSource));

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services
    .AddDefaultIdentity<AppUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;

        // Make the password/lockout policy explicit rather than relying on
        // framework defaults (6 chars, no lockout). Discord OAuth is the primary
        // identity provider, so this only governs any local password accounts.
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
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

    // API endpoints (the Discord Activity + addon) must get a status code on auth
    // failure, NOT a 302 redirect to an HTML login / access-denied page. Without
    // this, an ApiController returning Forbid()/Challenge() (e.g. a permission
    // check) sends the cookie handler's HTML redirect, which fetch follows to a
    // 200 HTML page — the client then chokes on "Unexpected token '<'" trying to
    // JSON.parse it. MVC pages keep the normal redirect behavior.
    options.Events.OnRedirectToLogin = async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Give the SPA/addon a readable JSON body instead of an empty 401 —
            // the client already surfaces a `{ error }` string.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"Your session has expired. Reload and sign in again.\"}");
            return;
        }
        context.Response.Redirect(context.RedirectUri);
    };
    options.Events.OnRedirectToAccessDenied = async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            // Every `/api` permission denial (ApiController `Forbid()`) funnels
            // through here. A generic friendly body turns the bare "status 403"
            // into something a user can act on, without editing each call site.
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"error\":\"You don't have permission to do that. If you think you should, ask a linkshell leader to update your role.\"}");
            return;
        }
        context.Response.Redirect(context.RedirectUri);
    };
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

builder.Services.AddHttpClient();
builder.Services.AddScoped<DiscordIdentityService>();
builder.Services.AddScoped<InviteCandidateService>();
builder.Services.AddScoped<ManualMemberService>();
builder.Services.AddScoped<AltCharacterValidator>();
builder.Services.AddScoped<AppUserProfileService>();
builder.Services.AddScoped<AddonApiAuthService>();
builder.Services.AddScoped<GlobalSettingsService>();
builder.Services.AddScoped<AdminOverrideService>();
builder.Services.AddScoped<JobsRosterService>();
builder.Services.AddScoped<HnmClaimStatsService>();
builder.Services.AddScoped<HnmWindowStatsService>();
builder.Services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);
builder.Services.AddSingleton<TimeZoneConversionService>();
builder.Services.AddScoped<LootEditService>();
// Hand-entered loot from the Loot System. Separate from the event-close debit path because it
// charges the winner immediately -- see ManualLootService for why that needs its own stamp.
builder.Services.AddScoped<ManualLootService>();
// Turns the addon-reported alliance leader into the alliance NUMBERS the rest of the app speaks.
// See AllianceIdentityService for why the number stopped being typed by hand.
builder.Services.AddScoped<AllianceIdentityService>();
builder.Services.AddScoped<EventHistoryEditService>();
builder.Services.AddScoped<EventCommentService>();
builder.Services.AddSingleton<AiCommentSummaryService>();
builder.Services.AddSingleton<SignupCharacterChoiceCache>();
builder.Services.AddSingleton<OfficerAddTargetCache>();
builder.Services.AddScoped<TodImageUploadService>();
builder.Services.AddScoped<SubmissionApprovalService>();
// Data Protection guards the encrypted Google refresh tokens, auth cookies and
// antiforgery tokens. By default the key ring lands under the app's content
// root, which the deploy script replaces wholesale on every release -- new keys
// each deploy meant every linkshell's stored Google Sheet token became
// undecryptable (then got wiped) and everyone was logged out. Persist the keys
// to a stable directory OUTSIDE the deploy artifact dir (set
// DataProtection:KeyRingPath in production, e.g. /var/lib/lsmanager/keys) so a
// connection survives redeploys. Locally the key absent -> default behavior
// (a stable per-user store), which already persists across runs.
var dataProtection = builder.Services
    .AddDataProtection()
    .SetApplicationName("LSManager");
var dataProtectionKeyRingPath = builder.Configuration["DataProtection:KeyRingPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeyRingPath))
{
    Directory.CreateDirectory(dataProtectionKeyRingPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyRingPath));
}
builder.Services.AddScoped<DkpSheetService>();
// DKP pools: a per-linkshell partition of event types into named wallets. The resolver answers
// "which pool does this event type pay into", the balance service DERIVES per-pool balances from
// the ledger (nothing is stored), and DkpLedgerWriter is the single choke-point every DKP
// mutation goes through so the balance and the ledger can never disagree.
builder.Services.AddScoped<DkpPoolProvisioner>();
builder.Services.AddScoped<DkpPoolResolver>();
builder.Services.AddScoped<LinkshellMonsterTimingProvisioner>();
builder.Services.AddScoped<MonsterTimingResolver>();
builder.Services.AddScoped<DkpPoolBalanceService>();
builder.Services.AddScoped<DkpLedgerWriter>();
// Gil treasury: the one place "how much gil does this linkshell have" is answered. Same
// derived-balance doctrine as DkpPoolBalanceService above — nothing is stored, so there is no
// cached total to drift. Five call sites used to compute this independently and four of them
// disagreed; one of those numbers gates whether a gil auction may be created.
builder.Services.AddScoped<LedgerAccountProvisioner>();
builder.Services.AddScoped<LedgerPeriodGuard>();
builder.Services.AddScoped<TreasuryBalanceService>();
builder.Services.AddScoped<TreasuryJournalWriter>();
// Paying members off the balance sheet's "who we owe" list. Shared so tick-and-pay behaves
// identically on the website and in the Activity.
builder.Services.AddScoped<TreasurySettlementService>();
builder.Services.AddScoped<ItemSaleRecorder>();
// Charts (Sky, Sea, …). Shared by the Activity API and the website's ChartsController so the two
// surfaces cannot derive a different Farming Credit Ledger from the same rows.
builder.Services.AddScoped<ChartBoardService>();
builder.Services.AddScoped<ChartWishlistService>();
builder.Services.AddScoped<ChartKeyItemService>();
// Shared save logic for the pools config (Activity + web), and the catalog of event types an
// officer can assign — including the free-text ones their own events actually used.
builder.Services.AddScoped<DkpPoolEventTypeCatalog>();
builder.Services.AddScoped<DkpPoolEditor>();
builder.Services.AddScoped<MonsterTimingEditor>();
// DKP import (Excel/CSV → update existing members + create placeholders) and the
// first-launch "claim your DKP" merge that links an imported placeholder to a
// real account.
builder.Services.AddScoped<DkpImportService>();
builder.Services.AddScoped<PlaceholderClaimService>();
// Live DKP sheet → Discord channel (full table + .xlsx, edit-in-place): a
// SaveChanges hook enqueues linkshells whose DKP changed; the hosted service
// debounces + refreshes the channel post. No-ops when no DKP channel is set.
builder.Services.AddSingleton<DkpSheetPostQueue>();
builder.Services.AddScoped<DiscordDkpSheetPublisher>();
builder.Services.AddHostedService<DkpSheetPostBackgroundService>();
builder.Services.AddMemoryCache();

// Liveness has no checks (process up = healthy); readiness verifies the database
// is reachable so an orchestrator/proxy can gate traffic until the DB is ready.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database", tags: new[] { "ready" });
builder.Services.AddScoped<WindowEventDkpLedgerService>();
// Builds the attendance-snapshot sections shared by the Event System page and Attendance History.
builder.Services.AddScoped<AttendanceSectionsBuilder>();
builder.Services.AddScoped<WindowEventLinkService>();
builder.Services.AddScoped<MemberActivityService>();
builder.Services.AddScoped<HnmAutoEventService>();
// The two HNM earn formulas — "what did this camp owe?", per attendance mode. Neither pays any
// more: End Camp hands the roster to HnmCampReviewHandoffService, which stages it as a pending row
// in the Event System page's attendance sections, and an officer's Post is what credits DKP. The old
// WdProcessingBackgroundService (auto-finalize once the Awaiting-Processing grace elapsed) is gone
// with the grace — the review step replaced the timer.
builder.Services.AddScoped<WdCampFinalizer>();
builder.Services.AddScoped<HnmStandardCampFinalizer>();
builder.Services.AddScoped<HnmCampReviewHandoffService>();
// Shared "pop / end an HNM camp" (log ToD, re-point to next repop, stop the camp) — used by the
// Discord Pop/End Camp modal and the Activity End-Camp form so both behave identically.
builder.Services.AddScoped<HnmCampPopService>();
builder.Services.AddSingleton<DiscordWebhookQueue>();
builder.Services.AddScoped<DiscordSnapshotPublisher>();
builder.Services.AddHostedService<DiscordWebhookBackgroundService>();
builder.Services.AddSingleton<DiscordTodBoardQueue>();
builder.Services.AddScoped<DiscordTodBoardPublisher>();
builder.Services.AddHostedService<DiscordTodBoardBackgroundService>();
builder.Services.AddSingleton<DiscordAuctionChannelQueue>();
builder.Services.AddScoped<DiscordAuctionChannelPublisher>();
builder.Services.AddHostedService<DiscordAuctionChannelBackgroundService>();
// Phase 2: bot-posts-to-channel event announcements + the inline-signup
// interactions endpoint.
builder.Services.AddScoped<DiscordBotClient>();
// Suppress the public Activity "launch" card by making the entry-point app-handled.
builder.Services.AddHostedService<DiscordEntryPointConfigurator>();
builder.Services.AddSingleton<DiscordInteractionVerifier>();
builder.Services.AddSingleton<DiscordEventChannelQueue>();
// Renders the party board to a PNG (headless Chromium); singleton so the browser
// is launched once and reused. EventBoardPoster picks image-or-text-fallback.
builder.Services.AddSingleton<EventBoardImageRenderer>();
builder.Services.AddScoped<EventBoardPoster>();
// Edits an HNM board's Discord message to a "defeated" note when its ToD is logged.
builder.Services.AddScoped<HnmBoardNoticeService>();
// Resolves which channel each kind of post goes to, from a linkshell's
// user-defined LinkshellChannelRoutes. Injected into every Discord publisher.
builder.Services.AddScoped<ChannelRouteResolver>();
// Shared save logic for the channel-routes config (Activity + web).
builder.Services.AddScoped<ChannelRouteEditor>();
// One-time idempotent backfill of legacy LinkshellDiscordChannel rows into the
// new routes table. Safe to keep until the legacy table is dropped.
builder.Services.AddHostedService<ChannelRouteBackfillService>();
// Best-effort: downloads the Chromium binary on boot so the image board works
// without a manual `playwright install` (opt out with LSM_PLAYWRIGHT_AUTOINSTALL=0).
builder.Services.AddHostedService<PlaywrightBrowserInstaller>();
builder.Services.AddScoped<DiscordEventChannelPublisher>();
builder.Services.AddHostedService<DiscordEventChannelBackgroundService>();
// Posts an "event ended" summary (duration + per-attendee DKP) to the event's
// channel when an event closes. Fires off EventHistory being added.
builder.Services.AddSingleton<DiscordEventEndedQueue>();
builder.Services.AddScoped<DiscordEventEndedPublisher>();
builder.Services.AddHostedService<DiscordEventEndedBackgroundService>();
// Deletes an event's signup/party board from Discord once the event is over
// (fires off the Event row being deleted on any end/cancel path).
builder.Services.AddSingleton<DiscordEventCleanupQueue>();
builder.Services.AddHostedService<DiscordEventCleanupBackgroundService>();
builder.Services.AddSingleton<DiscordLootChannelQueue>();
builder.Services.AddScoped<DiscordLootChannelPublisher>();
builder.Services.AddHostedService<DiscordLootChannelBackgroundService>();

// In-memory per-linkshell change bus that powers the Activity's live long-poll
// change feed (GET /api/activity/changes). Singleton so its state is shared
// across requests; injected into ApplicationDbContext to fire on every commit.
builder.Services.AddSingleton<LinkshellChangeNotifier>();

// Starts events flagged "Auto start at start time" once their StartTime passes.
builder.Services.AddHostedService<EventAutoStartBackgroundService>();

// Marches a live HNM camp's window counter on its monster's built-in cadence (wyrms 60 min × 25,
// kings/dragons 10 min × 7) and keeps the board's "Next window N <countdown>" line pointing at the
// real next boundary, so a camp advances without anyone pressing anything.
//
// This was previously left unregistered because it double-advanced the window alongside the board's
// manual "Next Window" button (auto stepped it, then the officer's click stepped it again, stacking
// an extra interval onto the countdown). That conflict is gone for good: the manual step buttons no
// longer exist, so this service is the SOLE owner of Event.HnmWindowNumber. Event.HnmClearedWindow
// still tracks the wyrm roster clear so it happens exactly once per window.
builder.Services.AddHostedService<HnmWindowAdvanceBackgroundService>();

// Re-posts standing HNM signup boards before the next predicted pop (from new ToDs).
builder.Services.AddHostedService<HnmRecurringBoardBackgroundService>();

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
    // Auto-migrate on startup by default, but allow it to be turned off
    // (Database:RunMigrationsOnStartup=false) for multi-instance deploys where
    // migrations are applied as a separate, single-runner step. A failure is
    // fatal and logged so it surfaces clearly instead of an opaque crash loop.
    var runMigrations = app.Configuration.GetValue("Database:RunMigrationsOnStartup", true);
    if (runMigrations)
    {
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        try
        {
            services.GetRequiredService<ApplicationDbContext>().Database.Migrate();
        }
        catch (Exception ex)
        {
            services.GetRequiredService<ILogger<Program>>()
                .LogCritical(ex, "Database migration on startup failed; aborting startup.");
            throw;
        }
    }
}

// Promote the configured super admin account(s). Idempotent — only flips the
// flag when not already set. Matched by normalized email and/or character name
// so it lands regardless of whether Discord OAuth captured an email. Wrapped so
// an un-migrated dev DB (no IsSuperAdmin column yet) doesn't abort startup.
using (var seedScope = app.Services.CreateScope())
{
    var seedLogger = seedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var superAdminEmail = app.Configuration["SuperAdmin:Email"];
        var superAdminCharacter = app.Configuration["SuperAdmin:CharacterName"];
        if (!string.IsNullOrWhiteSpace(superAdminEmail) || !string.IsNullOrWhiteSpace(superAdminCharacter))
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var normalizedEmail = superAdminEmail?.Trim().ToUpperInvariant();
            var matches = await db.Users
                .Where(u =>
                    (normalizedEmail != null && u.NormalizedEmail == normalizedEmail) ||
                    (superAdminCharacter != null && u.CharacterName == superAdminCharacter))
                .ToListAsync();

            var promoted = 0;
            foreach (var candidate in matches)
            {
                if (!candidate.IsSuperAdmin)
                {
                    candidate.IsSuperAdmin = true;
                    promoted++;
                }
            }
            if (promoted > 0)
            {
                await db.SaveChangesAsync();
                seedLogger.LogInformation("SuperAdmin seeding: promoted {Count} account(s).", promoted);
            }
            else if (matches.Count == 0)
            {
                seedLogger.LogWarning(
                    "SuperAdmin seeding: no AppUser matched email '{Email}' or character '{Character}'. " +
                    "The addon kill-switch toggle will be hidden until a matching account exists.",
                    superAdminEmail, superAdminCharacter);
            }
        }
    }
    catch (Exception ex)
    {
        seedLogger.LogWarning(ex, "SuperAdmin seeding skipped (is the database migrated?).");
    }
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
            // Google Fonts is the one external asset host: the home page's masthead
            // (Views/Home/Index.cshtml) loads Cinzel from it. The stylesheet comes from
            // fonts.googleapis.com and the font files it points at from fonts.gstatic.com.
            "font-src 'self' data: https://fonts.gstatic.com;",
            // 'unsafe-inline' on style-src is retained because the existing
            // Razor views use inline style attributes throughout; migrating
            // those to stylesheets is tracked as follow-up cleanup. The
            // script-src is locked to nonce-only (no 'unsafe-inline', no blob:).
            "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com;",
            $"script-src 'self' 'nonce-{nonce}';",
            "object-src 'none';",
            $"frame-src 'self' https://discord.com https://*.discord.com https://*.discordsays.com https://docs.google.com https://accounts.google.com{devTunnelHostFragment};"
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

// Liveness: process is up. Readiness: database is reachable.
app.MapHealthChecks("/healthz", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/readyz", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();
