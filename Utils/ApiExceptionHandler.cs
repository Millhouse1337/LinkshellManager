using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinkshellManagerDiscordApp.Utils;

// Turns a crash inside an /api request into a JSON 500 the app can read.
//
// Without this, UseExceptionHandler("/Home/Error") answered every /api crash with the HTML
// error page. The app can't read HTML, so it told officers "your session may have expired":
// they reloaded and retried (maybe repeating a half-done save), and bug reports looked like
// login problems. Now the app gets a plain message plus an id, and the same id is in the log
// next to the real exception, so a screenshot of the message is enough to find it.
//
// The exception text is never sent back: it can carry SQL, table names or other server detail.
//
// Pages (anything not under /api) are left alone: returning false here lets the middleware
// fall through to the normal /Home/Error page.
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public const string ErrorMessage = "Something went wrong on the server.";

    private readonly ILogger<ApiExceptionHandler> _logger;

    public ApiExceptionHandler(ILogger<ApiExceptionHandler> logger)
    {
        _logger = logger;
    }

    public static bool IsApiPath(PathString path) => path.StartsWithSegments("/api");

    // The path the request was really for. UseExceptionHandler("/Home/Error") rewrites
    // Request.Path to "/Home/Error" BEFORE it asks this handler, so Request.Path alone never
    // looks like /api and every /api crash would still get the HTML page. The middleware keeps
    // the original path on IExceptionHandlerPathFeature.
    public static PathString OriginalPath(HttpContext httpContext)
    {
        var original = httpContext.Features.Get<IExceptionHandlerPathFeature>()?.Path;
        return string.IsNullOrEmpty(original) ? httpContext.Request.Path : new PathString(original);
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var path = OriginalPath(httpContext);
        if (!IsApiPath(path))
        {
            return false;
        }

        // Same shape as the hand-written catch in End Event: a short random id, logged with
        // the full exception, and the only detail the caller gets.
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogError(exception,
            "Unhandled error on {Method} {Path}. CorrelationId={CorrelationId}",
            httpContext.Request.Method, path.Value, correlationId);

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(new { error = ErrorMessage, correlationId }, cancellationToken);
        return true;
    }
}
