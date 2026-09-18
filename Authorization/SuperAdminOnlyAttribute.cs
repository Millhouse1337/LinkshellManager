using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LinkshellManagerDiscordApp.Authorization;

// Site-wide super-admin gate. The Settings POSTs in Controllers/AccountController.cs inline the
// same three lines (load the user, Challenge when missing, Forbid unless IsSuperAdmin); it is
// extracted here so a controller with many actions cannot forget it on one of them.
//
// Deliberately NOT AdminOverrideService: that is the per-linkshell permission override, gated
// behind the admin.override toggle and scoped to memberships (see its header comment). A
// server-wide page must not vanish because that toggle is off.
//
// Pair it with [Authorize]: the authorization middleware challenges anonymous requests before
// any MVC filter runs, so this filter only ever sees an authenticated principal.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class SuperAdminOnlyAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string UserContextKey = "SuperAdminUser";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (context.Result is not null)
        {
            return;
        }

        var userManager = context.HttpContext.RequestServices.GetRequiredService<UserManager<AppUser>>();
        var user = await userManager.GetUserAsync(context.HttpContext.User);
        var verdict = Decide(user);
        if (verdict is not null)
        {
            context.Result = verdict;
            return;
        }

        // Stash the actor so actions can read it without a second user lookup.
        context.HttpContext.Items[UserContextKey] = user;
    }

    // Pure, so tests can pin the matrix without standing up a UserManager. Forbid() on a
    // non-/api path becomes the AccessDenied redirect (Program.cs OnRedirectToAccessDenied).
    public static IActionResult? Decide(AppUser? user)
    {
        if (user is null)
        {
            return new ChallengeResult();
        }
        return user.IsSuperAdmin ? null : new ForbidResult();
    }

    public static AppUser GetUser(HttpContext context) =>
        context.Items[UserContextKey] as AppUser
        ?? throw new InvalidOperationException("SuperAdminOnly did not run for this request.");
}
