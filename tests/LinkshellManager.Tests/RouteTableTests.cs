using LinkshellManagerDiscordApp.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LinkshellManager.Tests;

// Builds the app's MVC + Razor Pages route table the way Program.cs does at startup, without a
// database or any config. Two jobs:
// 1. A route MVC cannot build throws here instead of at startup. That took the site down once
//    (2026-09-18: a regex constraint with square brackets read as a [token] replacement made
//    MapControllers() throw, and the service crash-looped behind a 502).
// 2. Only our own Identity pages are served. The stock ASP.NET Identity UI (ExternalLogin,
//    Manage/SetPassword, ...) let people make an account with a typed email or set a password
//    behind the app's back; it is gone, and this stops it from coming back.
public class RouteTableTests
{
    // The pages under /Identity/ the app means to serve:
    // - Login: our sign-in page (a link to Discord sign-in), the cookie's login path.
    // - Register: only redirects to Login.
    // - AccessDenied: the cookie's access-denied path, so that redirect never lands on a 404.
    private static readonly string[] IntendedIdentityPages =
    {
        "/Account/AccessDenied",
        "/Account/Login",
        "/Account/Register",
    };

    [Fact]
    public void OnlyOurOwnIdentityPagesAreServed()
    {
        using var app = BuildApp();

        var identityPages = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items
            .OfType<PageActionDescriptor>()
            .Where(page => string.Equals(page.AreaName, "Identity", StringComparison.OrdinalIgnoreCase))
            .Select(page => page.ViewEnginePath)
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(IntendedIdentityPages, identityPages);
    }

    [Fact]
    public void EveryRouteBuildsAndItsConstraintsResolve()
    {
        using var app = BuildApp();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        // A few routes the site cannot live without, so an empty table cannot pass.
        var patterns = endpoints.Select(endpoint => endpoint.RoutePattern.RawText).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("auth/discord/login", patterns);
        Assert.Contains("auth/discord/callback", patterns);
        Assert.Contains("api/web/sign-out", patterns);
        Assert.Contains("Identity/Account/Login", patterns);

        // Route constraints ("{id:int}") are turned into code only when the first request arrives.
        // Resolve every one now so a constraint MVC does not know fails the test, not the site.
        var resolver = app.Services.GetRequiredService<IInlineConstraintResolver>();
        foreach (var endpoint in endpoints)
        {
            foreach (var (parameter, policies) in endpoint.RoutePattern.ParameterPolicies)
            {
                foreach (var policy in policies.Where(policy => policy.Content is not null))
                {
                    Assert.True(
                        resolver.ResolveConstraint(policy.Content!) is not null,
                        $"Route '{endpoint.RoutePattern.RawText}' has a constraint '{policy.Content}' on '{parameter}' that MVC cannot resolve.");
                }
            }
        }
    }

    // The same MVC + Identity registration and endpoint mapping as Program.cs. ApplicationName
    // makes MVC load the app assembly and every Razor class library it references, as at startup,
    // so a package that ships pages shows up here too. "Production" keeps the
    // machine's user-secrets out of it. The app is only built, never started: no port is opened.
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = "Production",
        });
        builder.Services.AddControllersWithViews();
        builder.Services.AddRazorPages();
        // Program.cs's own Identity registration: if it ever brings a stock UI back, its pages show here.
        builder.Services.AddAppIdentity();

        var app = builder.Build();
        app.MapControllers();
        app.MapControllerRoute(name: "default", pattern: "{controller=Home}/{action=Index}/{id?}");
        app.MapRazorPages();
        return app;
    }
}
