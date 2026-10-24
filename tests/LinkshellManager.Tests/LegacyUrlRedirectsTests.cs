using System.Text.RegularExpressions;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LinkshellManager.Tests;

// The old Razor website's addresses now redirect into the Activity UI. These rows pin where each
// old page lands, that the pages which stay server-side (sign-in, legal, API, launcher)
// are never redirected, and that every target names a tab the Angular app actually routes
// (tab-routes.ts), so renaming a slug there can't silently break an old bookmark.
public class LegacyUrlRedirectsTests
{
    [Theory]
    [InlineData("/Dashboard", "", "/discord-activity/")]
    [InlineData("/Dashboard/Index", "?linkshellId=5", "/discord-activity/?ls=5")]
    [InlineData("/Announcement/Create", "", "/discord-activity/")]
    [InlineData("/Rule", "", "/discord-activity/")]
    [InlineData("/Charts", "", "/discord-activity/charts")]
    [InlineData("/Charts/Sky", "", "/discord-activity/charts")]
    [InlineData("/charts/sea", "", "/discord-activity/charts/sea")]
    [InlineData("/Charts/HENM", "", "/discord-activity/charts/henm")]
    [InlineData("/Auction", "", "/discord-activity/auctions")]
    [InlineData("/Auction/Edit/12", "", "/discord-activity/auctions")]
    [InlineData("/AuctionHistory", "", "/discord-activity/auctions/history")]
    [InlineData("/Event", "", "/discord-activity/events")]
    [InlineData("/Event/Start/40", "", "/discord-activity/events")]
    [InlineData("/EventHistory/Details/9", "", "/discord-activity/events/past")]
    [InlineData("/Tod", "", "/discord-activity/tods")]
    [InlineData("/PartySetup/Details/3", "", "/discord-activity/party-setup")]
    [InlineData("/ManageTeam/JobsRoster", "", "/discord-activity/management")]
    [InlineData("/DkpHistory", "", "/discord-activity/dkp/history")]
    [InlineData("/DkpAdjustment", "?linkshellId=7", "/discord-activity/dkp/history?ls=7")]
    [InlineData("/LootHistory/Add", "", "/discord-activity/loot")]
    [InlineData("/ManageFinances/Record", "", "/discord-activity/treasury")]
    [InlineData("/ManageItem/AddItem", "", "/discord-activity/treasury/items")]
    [InlineData("/ManageRevenue", "", "/discord-activity/treasury")]
    [InlineData("/Linkshell", "", "/discord-activity/management")]
    [InlineData("/Linkshell/Details/5", "", "/discord-activity/management?ls=5")]
    [InlineData("/Linkshell/Create", "", "/discord-activity/configurations")]
    [InlineData("/Linkshell/Edit/5", "", "/discord-activity/configurations?ls=5")]
    [InlineData("/Linkshell/Customize", "?id=5", "/discord-activity/configurations?ls=5")]
    [InlineData("/Linkshell/Permissions", "", "/discord-activity/permissions")]
    [InlineData("/Linkshell/GameAddon", "", "/discord-activity/addon")]
    [InlineData("/Linkshell/DownloadDkpTemplate", "", "/api/activity/dkp-import/template")]
    [InlineData("/Account/Profile", "", "/discord-activity/profile")]
    [InlineData("/Account/Settings", "", "/discord-activity/admin")]
    [InlineData("/Messages", "", "/discord-activity/messages")]
    [InlineData("/Contact", "", "/discord-activity/messages")]
    [InlineData("/linkshells/5/dkp-sheet", "", "/discord-activity/dkp?ls=5")]
    [InlineData("/linkshells/5/dkp-sheet/export.xlsx", "", "/api/activity/dkp-sheet/export.xlsx?linkshellId=5")]
    [InlineData("/linkshells/5/window-events", "", "/discord-activity/events/pending?ls=5")]
    [InlineData("/linkshells/5/window-events/history", "", "/discord-activity/events/past?ls=5")]
    [InlineData("/linkshells/5/attendance-snapshots", "", "/discord-activity/events?ls=5")]
    [InlineData("/linkshells/5/pending-submissions", "", "/discord-activity/tods?ls=5")]
    [InlineData("/linkshells/5/pending-submissions/tod/31", "", "/discord-activity/tods?ls=5")]
    [InlineData("/data-admin", "", "/discord-activity/data-admin")]
    [InlineData("/data-admin/rule", "?q=afk&f.LinkshellId=5", "/discord-activity/data-admin/rule")]
    [InlineData("/data-admin/app-user/abc-123/edit", "", "/discord-activity/data-admin/app-user/abc-123/edit")]
    [InlineData("/data-admin/rule/create", "", "/discord-activity/data-admin/rule/create")]
    public void OldPage_RedirectsIntoTheApp(string path, string query, string expected)
    {
        Assert.True(LegacyUrlRedirects.TryResolve(new PathString(path), new QueryString(query), out var target));
        Assert.Equal(expected, target);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/privacy")]
    [InlineData("/terms")]
    [InlineData("/features")]
    [InlineData("/addon")]
    [InlineData("/Home/Error")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Logout")]
    [InlineData("/Account/AccessDenied")]
    [InlineData("/Identity/Account/Login")]
    [InlineData("/auth/discord/login")]
    [InlineData("/signin-discord")]
    [InlineData("/api/activity/overview")]
    [InlineData("/api/web/session")]
    [InlineData("/download/launcher")]
    [InlineData("/data-administration")]
    [InlineData("/discord-activity/events")]
    [InlineData("/Events")]
    [InlineData("/EventsArchive")]
    [InlineData("/linkshells/abc/dkp-sheet")]
    [InlineData("/linkshells/5/something-else")]
    public void ServerPagesAndUnknownPaths_AreLeftAlone(string path)
    {
        Assert.False(LegacyUrlRedirects.TryResolve(new PathString(path), QueryString.Empty, out _));
    }

    // Every in-app target's first segment is a tab the Angular router knows: either a SLUG_BY_TAB
    // value or a TAB_NAMES entry (a tab's slug defaults to its name).
    [Fact]
    public void EveryTarget_IsARoutedTab()
    {
        var app = FindRepoPath(Path.Combine("discord-activity", "src", "app", "home"));
        var tabRoutes = File.ReadAllText(Path.Combine(app, "tab-routes.ts"));
        var tabTypes = File.ReadAllText(Path.Combine(app, "activity-home.types.ts"));
        var known = Regex.Matches(tabRoutes + tabTypes, @"'([a-z-]*)'").Select(m => m.Groups[1].Value).ToHashSet();

        var paths = typeof(LegacyUrlRedirectsTests)
            .GetMethod(nameof(OldPage_RedirectsIntoTheApp))!
            .GetCustomAttributes(typeof(InlineDataAttribute), false)
            .Cast<InlineDataAttribute>()
            .Select(row => (string)row.GetData(null!).Single()[2]);

        foreach (var target in paths.Where(t => t.StartsWith(LegacyUrlRedirects.AppBase)))
        {
            var slug = target[LegacyUrlRedirects.AppBase.Length..].Split('?', '/')[0];
            Assert.True(known.Contains(slug), $"'{target}' points at '{slug}', which tab-routes.ts doesn't route.");
        }
    }

    private static string FindRepoPath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException($"Could not locate '{relative}' above {AppContext.BaseDirectory}.");
    }
}
