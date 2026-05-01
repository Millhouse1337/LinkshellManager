using Microsoft.AspNetCore.Mvc.Rendering;

namespace LinkshellManager.Utils;

public static class HtmlHelperExtensions
{
    public static string CspNonce(this IHtmlHelper helper)
        => helper.ViewContext.HttpContext.Items["CspNonce"] as string ?? string.Empty;
}
