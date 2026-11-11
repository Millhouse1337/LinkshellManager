using Microsoft.AspNetCore.Http;

namespace LinkshellManagerDiscordApp.Utils;

// AddDefaultIdentity still maps every stock ASP.NET Identity page under /Identity/ (ExternalLogin,
// Manage/SetPassword, Manage/DeletePersonalData, ...). The app never uses them: sign-in is Discord
// only (/auth/discord/login → /signin-discord → /auth/discord/callback, none under /Identity/), and
// sign-out is POST /api/web/sign-out. Left reachable, ExternalLogin's confirmation step
// creates an account with whatever email is typed, and the Manage pages let members add a password
// or delete their account behind the app's back. Program.cs answers 404 for every /Identity/ path
// this gate refuses. Removing the stock UI for good (AddIdentity without the default UI) is a later
// step; this gate is the quick, safe cut.
//
// Kept:
// - Account/Login: our own page (Areas/Identity/Pages/Account/Login.cshtml). It only renders a link
//   to /auth/discord/login, posts nothing, and needs no assets under /Identity/. It is also the
//   cookie's default login path and where the Discord callback sends sign-in errors.
// - Account/Logout, Account/AccessDenied: the cookie's default logout and access-denied paths, so a
//   redirect the cookie handler makes never lands on a 404. Neither can create or change an account.
// Account/Register is NOT kept: our override only redirects to Login and nothing links to it.
public static class IdentityPathGate
{
    private static readonly string[] AllowedAccountPages = { "Login", "Logout", "AccessDenied" };

    // True when the request may go on; false when Program.cs should answer 404.
    // Paths outside /Identity/ are always allowed. Matching is per segment and case-insensitive
    // (routing ignores case too), and empty segments are skipped so "//" can't slip past.
    public static bool IsAllowed(PathString path)
    {
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || !segments[0].Equals("Identity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return segments.Length == 3
            && segments[1].Equals("Account", StringComparison.OrdinalIgnoreCase)
            && AllowedAccountPages.Any(page => segments[2].Equals(page, StringComparison.OrdinalIgnoreCase));
    }
}
