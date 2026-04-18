using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkshellManagerDiscordApp.Areas.Identity.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public void OnGet(string? returnUrl = null)
    {
        ReturnUrl = returnUrl;
    }
}
