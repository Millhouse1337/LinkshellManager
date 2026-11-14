using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LinkshellManagerDiscordApp.Controllers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinkshellManager.Tests;

// /download/launcher sends every visitor to the address in server config (Launcher:DownloadUrl),
// and only while a super admin has the link switched on.
public class LauncherControllerTests
{
    private const string Url = "https://github.com/example/launcher/releases/latest/download/Setup.exe";

    private static async Task<IActionResult> DownloadAsync(bool enabled, string? configuredUrl)
    {
        var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var settings = new GlobalSettingsService(db, new MemoryCache(new MemoryCacheOptions()));
        await settings.SetLauncherDownloadEnabledAsync(enabled);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LauncherDownloadPolicy.ConfigKey] = configuredUrl })
            .Build();

        var controller = new LauncherController(settings, configuration, NullLogger<LauncherController>.Instance);
        return await controller.Download(CancellationToken.None);
    }

    [Fact]
    public async Task On_RedirectsToTheConfiguredAddress()
    {
        var redirect = Assert.IsType<RedirectResult>(await DownloadAsync(enabled: true, Url));
        Assert.Equal(Url, redirect.Url);
    }

    [Fact]
    public async Task Off_IsNotFound()
    {
        Assert.IsType<NotFoundResult>(await DownloadAsync(enabled: false, Url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://example.com/setup.exe")]
    public async Task On_WithNoUsableAddress_IsNotFound(string? configuredUrl)
    {
        Assert.IsType<NotFoundResult>(await DownloadAsync(enabled: true, configuredUrl));
    }
}
