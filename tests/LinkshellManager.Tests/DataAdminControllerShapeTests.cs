using System.Reflection;
using LinkshellManagerDiscordApp.Authorization;
using LinkshellManagerDiscordApp.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
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

    // Every public instance method MVC would treat as an action -- the return type is irrelevant
    // to MVC, so filtering on it would let a Task<RedirectToActionResult> action slip past.
    private static IEnumerable<MethodInfo> Actions() =>
        Controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName && method.GetCustomAttribute<NonActionAttribute>() is null);

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

    // Builds MVC's action table for this controller the way the app does at startup. A route
    // template MVC cannot parse (a regex constraint with square brackets reads as a [token]
    // replacement) throws here instead of taking the whole site down on deploy, which happened once.
    [Fact]
    public void RouteTemplates_ParseAndCoverEveryPage()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers().AddApplicationPart(Controller.Assembly);
        using var provider = services.BuildServiceProvider();

        var templates = provider.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>()
            .Where(descriptor => descriptor.ControllerTypeInfo == Controller)
            .Select(descriptor => descriptor.AttributeRouteInfo?.Template)
            .ToList();

        Assert.Contains("data-admin", templates);
        Assert.Contains("data-admin/tables", templates);
        Assert.Contains("data-admin/{slug}", templates);
        Assert.Contains("data-admin/{slug}/{id}", templates);
        Assert.Contains("data-admin/{slug}/create", templates);
        Assert.Contains("data-admin/{slug}/{id}/edit", templates);
        Assert.Contains("data-admin/{slug}/{id}/delete", templates);
        Assert.All(templates, template => Assert.False(string.IsNullOrEmpty(template)));
    }
}
