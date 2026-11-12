using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LinkshellManagerDiscordApp.Authorization;

public sealed class AddonApiAuthAttribute : Attribute, IAsyncAuthorizationFilter
{
    public const string TokenContextKey = "AddonToken";
    public const string LinkshellIdContextKey = "AddonLinkshellId";

    // Sent with the 503 below so the addon can tell "paused by an admin" apart from "your
    // token is no good". Part of the addon contract: do not rename.
    public const string AddonDisabledCode = "addon_disabled";

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Global kill switch first, before the token is even looked at. The switch is a PAUSE,
        // not a revoke. The addon deletes its saved pairings when it gets a 401, so answering
        // the pause with 401 made every member pair again once the switch went back off. A 503
        // tells the addon to wait and keep its pairings. 401 below is only for bad or revoked
        // tokens.
        var globalSettings = context.HttpContext.RequestServices.GetRequiredService<GlobalSettingsService>();
        if (await globalSettings.IsAddonGloballyDisabledAsync(context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(new
            {
                error = "The addon is paused by the server administrator. Your pairing is kept; try again later.",
                code = AddonDisabledCode
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        var headers = context.HttpContext.Request.Headers.Authorization;
        if (!AuthenticationHeaderValue.TryParse(headers, out var headerValue)
            || !"Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(headerValue.Parameter))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Missing addon bearer token. Send Authorization: Bearer att_<token>."
            });
            return;
        }

        var service = context.HttpContext.RequestServices.GetRequiredService<AddonApiAuthService>();
        var token = await service.ValidateTokenAsync(headerValue.Parameter, context.HttpContext.RequestAborted);

        if (token is null)
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Addon token is invalid or revoked."
            });
            return;
        }

        context.HttpContext.Items[TokenContextKey] = token;
        context.HttpContext.Items[LinkshellIdContextKey] = token.LinkshellId;
    }

    public static AddonApiToken GetToken(HttpContext context)
    {
        return (AddonApiToken)context.Items[TokenContextKey]!;
    }

    public static int GetLinkshellId(HttpContext context)
    {
        return (int)context.Items[LinkshellIdContextKey]!;
    }
}
