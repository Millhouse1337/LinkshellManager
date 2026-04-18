using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkshellManagerDiscordApp.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class RegisterModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet(string? returnUrl = null)
    {
        var loginUrl = string.IsNullOrWhiteSpace(returnUrl)
            ? "/Identity/Account/Login"
            : $"/Identity/Account/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

        return LocalRedirect(loginUrl);
    }
}
