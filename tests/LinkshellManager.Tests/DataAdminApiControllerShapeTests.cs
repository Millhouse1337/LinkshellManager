using System.Reflection;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LinkshellManager.Tests;

// Data Admin's JSON API reaches every table across every linkshell, so its gate must be
// structural: [Authorize] + [SuperAdminOnly] on the CLASS, no action opting out with
// [AllowAnonymous], and every POST carrying [ValidateAntiForgeryToken] on top of the global
// CookieAuthAntiforgeryFilter.
public class DataAdminApiControllerShapeTests
{
    private static readonly Type Controller = typeof(DataAdminApiController);

    private static IEnumerable<MethodInfo> Actions() =>
        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null);

    [Fact]
    public void Controller_IsGatedAtClassLevel()
    {
        Assert.NotNull(Controller.GetCustomAttribute<AuthorizeAttribute>(inherit: false));
        Assert.NotNull(Controller.GetCustomAttribute<SuperAdminOnlyAttribute>(inherit: false));
        Assert.Equal("api/data-admin", Controller.GetCustomAttribute<RouteAttribute>(inherit: false)?.Template);
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

        Assert.Equal(3, posts.Count);
        Assert.All(posts, post => Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }
}
