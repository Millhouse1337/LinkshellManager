using System.Net.Http.Headers;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LinkshellManagerDiscordApp.Authorization;

// Validates antiforgery tokens for state-changing requests, but only when the
// request is using cookie-based authentication. Bearer-token callers (the
// Discord Activity SPA + the addon) provide a credential the browser cannot
// forge cross-site, so they don't need the CSRF round-trip; cookie-authed
// callers (the MVC web pages) do.
public sealed class CookieAuthAntiforgeryFilter : IAsyncAuthorizationFilter
{
    private static readonly HashSet<string> SafeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Get,
        HttpMethods.Head,
        HttpMethods.Options,
        HttpMethods.Trace
    };

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var request = context.HttpContext.Request;

        if (SafeMethods.Contains(request.Method))
        {
            return;
        }

        if (HasBearerToken(request))
        {
            return;
        }

        var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
        try
        {
            await antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(new
            {
                error = "Antiforgery validation failed."
            });
        }
    }

    private static bool HasBearerToken(HttpRequest request)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization, out var headerValue))
        {
            return false;
        }

        return "Bearer".Equals(headerValue.Scheme, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(headerValue.Parameter);
    }
}
