using System;
using System.IO;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Options;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OAuth.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Npgsql;
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

builder.Services
    .AddAuthentication()
    .AddOAuth("DiscordWebsite", options =>
    {
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.ClientId = builder.Configuration["Discord:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Discord:ClientSecret"] ?? string.Empty;
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

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

builder.Services.AddOptions<DiscordOAuthOptions>()
    .Bind(builder.Configuration.GetSection("Discord"))
    .ValidateDataAnnotations();

builder.Services.AddHttpClient();
builder.Services.AddScoped<DiscordIdentityService>();
builder.Services.AddScoped<AppUserProfileService>();
builder.Services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddCors(options =>
{
    options.AddPolicy("DiscordCors", policy =>
    {
        policy
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()
            .SetIsOriginAllowed(origin =>
            {
                try
                {
                    var uri = new Uri(origin);
                    return uri.Scheme == "https" && (
                        uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".discordsays.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".pinggy.link", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok-free.dev", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok.io", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:4200", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:5001", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:7051", StringComparison.OrdinalIgnoreCase)
                    );
                }
                catch
                {
                    return false;
                }
            });
    });
});

var app = builder.Build();

await EnsureLiveEventSchemaAsync(app);

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
    ctx.Response.OnStarting(() =>
    {
        ctx.Response.Headers.Remove("X-Frame-Options");

        var csp = string.Join(" ",
            "default-src 'self';",
            "base-uri 'self';",
            "frame-ancestors 'self' https://discord.com https://*.discord.com https://*.discordsays.com https://*.pinggy.link https://*.ngrok-free.app https://*.ngrok-free.dev https://*.ngrok.io https://*.trycloudflare.com https://localhost:* http://localhost:*;",
            "connect-src 'self' https://discord.com https://*.discord.com https://*.discordsays.com https://*.pinggy.link https://*.ngrok-free.app https://*.ngrok-free.dev https://*.ngrok.io https://*.trycloudflare.com https://localhost:* http://localhost:* ws://localhost:* wss://localhost:*;",
            "img-src 'self' data: blob: https://cdn.discordapp.com https://media.discordapp.net https://*.discordsays.com;",
            "font-src 'self' data:;",
            "style-src 'self' 'unsafe-inline';",
            "script-src 'self' blob:;",
            "object-src 'none';",
            "frame-src https://discord.com https://*.discord.com https://*.discordsays.com https://*.pinggy.link https://*.ngrok-free.app https://*.ngrok-free.dev https://*.ngrok.io https://*.trycloudflare.com;"
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.Run();

static async Task EnsureLiveEventSchemaAsync(WebApplication app)
{
    await using var scope = app.Services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

    const string sql = """
        CREATE TABLE IF NOT EXISTS "AppUserEventStatusLedgers" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "AppUserEventId" integer NOT NULL,
            "EventId" integer NOT NULL,
            "AppUserId" character varying(450),
            "ActionType" character varying(32) NOT NULL,
            "OccurredAt" timestamp with time zone NOT NULL,
            "RequiresVerification" boolean NOT NULL,
            "VerifiedAt" timestamp with time zone NULL,
            "VerifiedBy" character varying(256) NULL,
            CONSTRAINT "FK_AppUserEventStatusLedgers_AppUserEvents_AppUserEventId"
                FOREIGN KEY ("AppUserEventId") REFERENCES "AppUserEvents" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_AppUserEventStatusLedgers_Events_EventId"
                FOREIGN KEY ("EventId") REFERENCES "Events" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_AppUserEventStatusLedgers_AspNetUsers_AppUserId"
                FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_AppUserEventStatusLedgers_AppUserEventId_OccurredAt"
            ON "AppUserEventStatusLedgers" ("AppUserEventId", "OccurredAt");

        CREATE TABLE IF NOT EXISTS "DkpLedgerEntries" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "AppUserId" character varying(450) NULL,
            "LinkshellId" integer NOT NULL,
            "EventHistoryId" integer NULL,
            "EntryType" character varying(32) NOT NULL,
            "Amount" double precision NOT NULL,
            "Sequence" integer NOT NULL,
            "OccurredAt" timestamp with time zone NOT NULL,
            "CharacterName" character varying(256) NULL,
            "EventName" character varying(256) NULL,
            "EventType" character varying(256) NULL,
            "EventLocation" character varying(256) NULL,
            "EventStartTime" timestamp with time zone NULL,
            "EventEndTime" timestamp with time zone NULL,
            "ItemName" character varying(256) NULL,
            "Details" character varying(1024) NULL,
            CONSTRAINT "FK_DkpLedgerEntries_AspNetUsers_AppUserId"
                FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
            CONSTRAINT "FK_DkpLedgerEntries_Linkshells_LinkshellId"
                FOREIGN KEY ("LinkshellId") REFERENCES "Linkshells" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_DkpLedgerEntries_EventHistories_EventHistoryId"
                FOREIGN KEY ("EventHistoryId") REFERENCES "EventHistories" ("Id") ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS "IX_DkpLedgerEntries_LinkshellId_AppUserId_OccurredAt_Sequence"
            ON "DkpLedgerEntries" ("LinkshellId", "AppUserId", "OccurredAt", "Sequence");

        CREATE TABLE IF NOT EXISTS "Auctions" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "LinkshellId" integer NOT NULL,
            "AuctionTitle" character varying(256) NULL,
            "CreatedByUserId" character varying(450) NULL,
            "CreatedBy" character varying(256) NULL,
            "StartTime" timestamp with time zone NULL,
            "EndTime" timestamp with time zone NULL,
            "StartedAt" timestamp with time zone NULL,
            CONSTRAINT "FK_Auctions_Linkshells_LinkshellId"
                FOREIGN KEY ("LinkshellId") REFERENCES "Linkshells" ("Id") ON DELETE CASCADE
        );

        ALTER TABLE "Auctions" ADD COLUMN IF NOT EXISTS "CreatedByUserId" character varying(450) NULL;
        ALTER TABLE "Auctions" ADD COLUMN IF NOT EXISTS "CreatedBy" character varying(256) NULL;
        ALTER TABLE "Auctions" ADD COLUMN IF NOT EXISTS "AuctionTitle" character varying(256) NULL;
        ALTER TABLE "Auctions" ADD COLUMN IF NOT EXISTS "StartedAt" timestamp with time zone NULL;

        CREATE TABLE IF NOT EXISTS "AuctionHistories" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "LinkshellId" integer NOT NULL,
            "AuctionTitle" character varying(256) NULL,
            "CreatedByUserId" character varying(450) NULL,
            "CreatedBy" character varying(256) NULL,
            "StartTime" timestamp with time zone NULL,
            "EndTime" timestamp with time zone NULL,
            "StartedAt" timestamp with time zone NULL,
            "ClosedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
            CONSTRAINT "FK_AuctionHistories_Linkshells_LinkshellId"
                FOREIGN KEY ("LinkshellId") REFERENCES "Linkshells" ("Id") ON DELETE CASCADE
        );

        ALTER TABLE "AuctionHistories" ADD COLUMN IF NOT EXISTS "CreatedByUserId" character varying(450) NULL;
        ALTER TABLE "AuctionHistories" ADD COLUMN IF NOT EXISTS "CreatedBy" character varying(256) NULL;
        ALTER TABLE "AuctionHistories" ADD COLUMN IF NOT EXISTS "AuctionTitle" character varying(256) NULL;
        ALTER TABLE "AuctionHistories" ADD COLUMN IF NOT EXISTS "StartedAt" timestamp with time zone NULL;
        ALTER TABLE "AuctionHistories" ADD COLUMN IF NOT EXISTS "ClosedAt" timestamp with time zone NOT NULL DEFAULT NOW();

        CREATE TABLE IF NOT EXISTS "AuctionItems" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "AuctionId" integer NULL,
            "AuctionHistoryId" integer NULL,
            "ItemName" character varying(256) NULL,
            "ItemType" character varying(128) NULL,
            "StartingBidDkp" integer NULL,
            "CurrentHighestBid" integer NULL,
            "CurrentHighestBidder" character varying(256) NULL,
            "CurrentHighestBidderAppUserId" character varying(450) NULL,
            "EndingBidDkp" integer NULL,
            "StartTime" timestamp with time zone NULL,
            "EndTime" timestamp with time zone NULL,
            "Status" character varying(32) NULL,
            "Notes" character varying(1024) NULL,
            CONSTRAINT "FK_AuctionItems_Auctions_AuctionId"
                FOREIGN KEY ("AuctionId") REFERENCES "Auctions" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_AuctionItems_AuctionHistories_AuctionHistoryId"
                FOREIGN KEY ("AuctionHistoryId") REFERENCES "AuctionHistories" ("Id") ON DELETE CASCADE
        );

        ALTER TABLE "AuctionItems" ADD COLUMN IF NOT EXISTS "AuctionHistoryId" integer NULL;
        ALTER TABLE "AuctionItems" ADD COLUMN IF NOT EXISTS "CurrentHighestBidderAppUserId" character varying(450) NULL;
        ALTER TABLE "AuctionItems" ADD COLUMN IF NOT EXISTS "ItemType" character varying(128) NULL;
        ALTER TABLE "AuctionItems" ADD COLUMN IF NOT EXISTS "Notes" character varying(1024) NULL;

        CREATE TABLE IF NOT EXISTS "Bids" (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "AuctionItemId" integer NOT NULL,
            "AppUserId" character varying(450) NULL,
            "CharacterName" character varying(256) NOT NULL,
            "BidAmount" integer NOT NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW(),
            CONSTRAINT "FK_Bids_AuctionItems_AuctionItemId"
                FOREIGN KEY ("AuctionItemId") REFERENCES "AuctionItems" ("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_Bids_AspNetUsers_AppUserId"
                FOREIGN KEY ("AppUserId") REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL
        );

        ALTER TABLE "Bids" ADD COLUMN IF NOT EXISTS "AppUserId" character varying(450) NULL;
        ALTER TABLE "Bids" ADD COLUMN IF NOT EXISTS "CreatedAt" timestamp with time zone NOT NULL DEFAULT NOW();

        CREATE INDEX IF NOT EXISTS "IX_Auctions_LinkshellId"
            ON "Auctions" ("LinkshellId");

        CREATE INDEX IF NOT EXISTS "IX_AuctionHistories_LinkshellId"
            ON "AuctionHistories" ("LinkshellId");

        CREATE INDEX IF NOT EXISTS "IX_AuctionItems_AuctionId"
            ON "AuctionItems" ("AuctionId");

        CREATE INDEX IF NOT EXISTS "IX_AuctionItems_AuctionHistoryId"
            ON "AuctionItems" ("AuctionHistoryId");

        CREATE INDEX IF NOT EXISTS "IX_Bids_AuctionItemId_CreatedAt"
            ON "Bids" ("AuctionItemId", "CreatedAt");
        """;

    try
    {
        await dbContext.Database.ExecuteSqlRawAsync(sql);
    }
    catch (PostgresException exception)
    {
        throw new InvalidOperationException("Failed to ensure the live event status ledger schema.", exception);
    }
}



