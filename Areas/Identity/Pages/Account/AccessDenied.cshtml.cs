using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LinkshellManagerDiscordApp.Areas.Identity.Pages.Account;

// Where the sign-in cookie sends a signed-in browser that is refused a non-/api page
// (Program.cs: AccessDeniedPath). /api requests get a JSON 403 instead. It replaces the stock
// Identity UI page of the same address, so that redirect never lands on a 404.
[AllowAnonymous]
public sealed class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
    }
}
