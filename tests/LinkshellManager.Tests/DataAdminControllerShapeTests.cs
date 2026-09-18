using System.Reflection;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace LinkshellManager.Tests;

// The Data Admin controller can reach every shown table across every linkshell, so its gate must
// be structural: [Authorize] + [SuperAdminOnly] on the CLASS (no action can forget it), no action
// may opt out with [AllowAnonymous], and every mutation must carry [ValidateAntiForgeryToken] on
// top of the global CookieAuthAntiforgeryFilter, matching the rest of the MVC controllers. There
// is no MVC pipeline test in this repo, so this reflection check is the guard.
public class DataAdminControllerShapeTests
{
    private static readonly Type Controller = typeof(DataAdminController);

    private static IEnumerable<MethodInfo> Actions() =>
        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => typeof(IActionResult).IsAssignableFrom(method.ReturnType)
                             || typeof(Task<IActionResult>).IsAssignableFrom(method.ReturnType));

    [Fact]
    public void Controller_IsGatedAtClassLevel()
    {
        Assert.NotNull(Controller.GetCustomAttribute<AuthorizeAttribute>(inherit: false));
        Assert.NotNull(Controller.GetCustomAttribute<SuperAdminOnlyAttribute>(inherit: false));
        Assert.Equal("data-admin", Controller.GetCustomAttribute<RouteAttribute>(inherit: false)?.Template);
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

        Assert.NotEmpty(posts);
        Assert.All(posts, post => Assert.NotNull(post.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>()));
    }

    // Every action is attribute-routed under /data-admin; a conventional action would fall
    // outside the [Route] prefix and the literal-segment ordering the views rely on.
    [Fact]
    public void EveryAction_HasAnHttpMethodRoute()
    {
        Assert.All(Actions(), action => Assert.NotEmpty(action.GetCustomAttributes<HttpMethodAttribute>()));
    }
}
