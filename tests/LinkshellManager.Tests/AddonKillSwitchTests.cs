using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// THE ADDON KILL SWITCH IS A PAUSE, NOT A REVOKE.
//
// The switch used to make every addon request answer 401, the same as a revoked token. The addon
// deletes its saved pairings on a 401, so within a couple of minutes of a super admin pausing the
// addon every member lost their pairing and had to make a new code once it was switched back on.
// Now the switch is checked first and answers 503 with code "addon_disabled"; 401 is only for a
// missing, bad or revoked token. These pin both answers and that a pause never touches the token.
public class AddonKillSwitchTests
{
    private const string Me = "user-me";
    private const int LinkshellId = 5;

    private static ApplicationDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static async Task<(ApplicationDbContext Db, string RawToken)> SeededAsync(bool revoked = false)
    {
        var db = NewDb();
        db.Linkshells.Add(new Linkshell { Id = LinkshellId, LinkshellName = "Home" });
        db.LinkshellRoles.Add(new LinkshellRole
        {
            LinkshellId = LinkshellId,
            Name = LinkshellRanks.Member,
            CanUseAddon = true,
        });
        db.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = Me,
            LinkshellId = LinkshellId,
            Rank = LinkshellRanks.Member,
        });
        const string raw = "att_kill-switch-token";
        db.AddonApiTokens.Add(new AddonApiToken
        {
            Id = 1,
            LinkshellId = LinkshellId,
            IssuedToAppUserId = Me,
            TokenHash = Hash(raw),
            TokenPrefix = raw[..12],
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            RevokedAt = revoked ? DateTime.UtcNow.AddHours(-1) : null,
        });
        await db.SaveChangesAsync();
        return (db, raw);
    }

    private static async Task SetKillSwitchAsync(ApplicationDbContext db, bool disabled) =>
        await new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()))
            .SetAddonGloballyDisabledAsync(disabled);

    // Runs the real filter the way MVC does for an [AddonApiAuth] endpoint.
    private static async Task<AuthorizationFilterContext> RunFilterAsync(ApplicationDbContext db, string? bearer)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection()
            .AddSingleton(db)
            .AddSingleton<IMemoryCache>(cache)
            .AddSingleton(new GlobalSettingsService(db, cache))
            .AddSingleton(new AddonApiAuthService(db, cache, TestPermissions.For(db)))
            .BuildServiceProvider();

        var http = new DefaultHttpContext { RequestServices = services };
        if (bearer is not null) http.Request.Headers.Authorization = $"Bearer {bearer}";

        var context = new AuthorizationFilterContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>());
        await new AddonApiAuthAttribute().OnAuthorizationAsync(context);
        return context;
    }

    private static void AssertPaused(AuthorizationFilterContext context)
    {
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var code = result.Value!.GetType().GetProperty("code")!.GetValue(result.Value);
        Assert.Equal("addon_disabled", code);
        Assert.Equal(AddonApiAuthAttribute.AddonDisabledCode, code);
        Assert.NotNull(result.Value.GetType().GetProperty("error")!.GetValue(result.Value));
    }

    [Fact]
    public async Task KillSwitchOn_GoodToken_Answers503AddonDisabled_Not401()
    {
        var (db, raw) = await SeededAsync();
        await using var _ = db;
        await SetKillSwitchAsync(db, true);

        var context = await RunFilterAsync(db, raw);

        AssertPaused(context);
        Assert.IsNotType<UnauthorizedObjectResult>(context.Result);
    }

    [Fact]
    public async Task KillSwitchOn_IsCheckedBeforeTheToken()
    {
        var (db, _) = await SeededAsync();
        await using var __ = db;
        await SetKillSwitchAsync(db, true);

        // No header, a garbage token: the pause still wins, so the addon never sees a 401
        // while the switch is on.
        AssertPaused(await RunFilterAsync(db, bearer: null));
        AssertPaused(await RunFilterAsync(db, "att_not-a-real-token"));
    }

    [Fact]
    public async Task KillSwitchOn_LeavesTheTokenUntouched()
    {
        var (db, raw) = await SeededAsync();
        await using var _ = db;
        await SetKillSwitchAsync(db, true);

        await RunFilterAsync(db, raw);

        // A pause must not revoke the token or count as use of it.
        var token = await db.AddonApiTokens.AsNoTracking().SingleAsync();
        Assert.Null(token.RevokedAt);
        Assert.Null(token.LastUsedAt);
    }

    [Fact]
    public async Task KillSwitchOff_GoodToken_IsAccepted()
    {
        var (db, raw) = await SeededAsync();
        await using var _ = db;

        var context = await RunFilterAsync(db, raw);

        Assert.Null(context.Result);
        Assert.Equal(LinkshellId, AddonApiAuthAttribute.GetLinkshellId(context.HttpContext));
    }

    [Fact]
    public async Task KillSwitchBackOff_SamePairingWorksAgain()
    {
        var (db, raw) = await SeededAsync();
        await using var _ = db;
        await SetKillSwitchAsync(db, true);
        AssertPaused(await RunFilterAsync(db, raw));

        await SetKillSwitchAsync(db, false);

        Assert.Null((await RunFilterAsync(db, raw)).Result);
    }

    [Fact]
    public async Task KillSwitchOff_RevokedToken_Answers401()
    {
        var (db, raw) = await SeededAsync(revoked: true);
        await using var _ = db;

        Assert.IsType<UnauthorizedObjectResult>((await RunFilterAsync(db, raw)).Result);
    }

    [Fact]
    public async Task KillSwitchOff_MissingOrUnknownToken_Answers401()
    {
        var (db, _) = await SeededAsync();
        await using var __ = db;

        Assert.IsType<UnauthorizedObjectResult>((await RunFilterAsync(db, bearer: null)).Result);
        Assert.IsType<UnauthorizedObjectResult>((await RunFilterAsync(db, "att_not-a-real-token")).Result);
    }
}
