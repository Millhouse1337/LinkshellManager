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

var mvcBuilder = builder.Services.AddControllersWithViews();
var razorPagesBuilder = builder.Services.AddRazorPages();

if (builder.Environment.IsDevelopment())
{
    mvcBuilder.AddRazorRuntimeCompilation();
    razorPagesBuilder.AddRazorRuntimeCompilation();
}

builder.Services.AddOptions<DiscordOAuthOptions>()
    .Bind(builder.Configuration.GetSection("Discord"))
    .ValidateDataAnnotations();

builder.Services.AddHttpClient();
builder.Services.AddScoped<DiscordIdentityService>();
builder.Services.AddScoped<AppUserProfileService>();
builder.Services.AddScoped<AddonApiAuthService>();
builder.Services.AddSingleton<IDateTimeZoneProvider>(DateTimeZoneProviders.Tzdb);

builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor |
                               Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var isDevelopment = builder.Environment.IsDevelopment();

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
                    if (uri.Scheme != "https") return false;

                    if (uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".discordsays.com", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    if (isDevelopment && (
                        uri.Host.EndsWith(".pinggy.link", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok-free.app", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok-free.dev", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".ngrok.io", StringComparison.OrdinalIgnoreCase) ||
                        uri.Host.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:4200", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:5001", StringComparison.OrdinalIgnoreCase) ||
                        origin.Equals("https://localhost:7051", StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
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

var app = builder.Build();

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

        var devHosts = isDevelopment
            ? " https://*.pinggy.link https://*.ngrok-free.app https://*.ngrok-free.dev https://*.ngrok.io https://*.trycloudflare.com"
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
            $"frame-ancestors 'self' https://discord.com https://*.discord.com https://*.discordsays.com{devHosts}{devLocalhostFrame};",
            $"connect-src 'self' https://discord.com https://*.discord.com https://*.discordsays.com{devHosts}{devLocalhost};",
            "img-src 'self' data: blob: https://cdn.discordapp.com https://media.discordapp.net https://*.discordsays.com;",
            "font-src 'self' data:;",
            "style-src 'self' 'unsafe-inline';",
            $"script-src 'self' 'nonce-{nonce}' blob:;",
            "object-src 'none';",
            $"frame-src https://discord.com https://*.discord.com https://*.discordsays.com{devHosts};"
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
