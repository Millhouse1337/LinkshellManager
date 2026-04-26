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

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
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
