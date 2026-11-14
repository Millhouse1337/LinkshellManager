using System.Reflection;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LinkshellManager.Tests;

// The Admin tab's API flips server-wide switches (admin override, addon and Claim Shield kill
// switches, the launcher link), so its gate must be structural, as
// DataAdminApiControllerShapeTests pins for Data Admin: [Authorize] + [SuperAdminOnly] on the
// CLASS, no action opting out, and every POST carrying [ValidateAntiForgeryToken] on top of the
// global filter.
public class WebAdminControllerShapeTests
{
    private static readonly Type Controller = typeof(WebAdminController);

    private static IEnumerable<MethodInfo> Actions() =>
        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null);

    [Fact]
    public void Controller_IsGatedAtClassLevel()
    {
        Assert.NotNull(Controller.GetCustomAttribute<AuthorizeAttribute>(inherit: false));
        Assert.NotNull(Controller.GetCustomAttribute<SuperAdminOnlyAttribute>(inherit: false));
        Assert.Equal("api/web/admin", Controller.GetCustomAttribute<RouteAttribute>(inherit: false)?.Template);
    }

    [Fact]
    public void NoAction_OptsOutOfTheGate()
    {
        var actions = Actions().ToList();

        Assert.NotEmpty(actions);
        Assert.All(actions, action => Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>()));
    }

    [Fact]
    public void EveryPost_ValidatesTheAntiforgeryToken()
    {
        var posts = Actions().Where(action => action.GetCustomAttribute<HttpPostAttribute>() is not null).ToList();

        Assert.Equal(4, posts.Count); // the launcher URL is server config now, so no launcher-url POST
        Assert.All(posts, post => Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }
}
