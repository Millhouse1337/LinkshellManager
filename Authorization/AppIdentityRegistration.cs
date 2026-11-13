using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using Microsoft.AspNetCore.Identity;

namespace LinkshellManagerDiscordApp.Authorization;

// The ASP.NET Identity pieces the app really uses, and nothing more. Program.cs calls this, and
// RouteTableTests calls it too, so the test sees exactly the pages the app serves.
//
// It used to be AddDefaultIdentity, which also brought the stock Identity UI: ready-made pages
// under /Identity/ (ExternalLogin, Manage/SetPassword, ...) that could make an account from a
// typed email or set a password behind the app's back. Sign-in is Discord only
// (/auth/discord/login → /signin-discord → /auth/discord/callback), so those pages are gone, along
// with the password/lockout rules and the email/password-reset token makers only they used.
public static class AppIdentityRegistration
{
    public static IServiceCollection AddAppIdentity(this IServiceCollection services)
    {
        // The same cookie setup AddDefaultIdentity made: the main sign-in cookie is the default,
        // and the short-lived "external" cookie holds the Discord answer until the callback turns
        // it into a sign-in. AddIdentityCookies also keeps the two-factor cookie schemes, which
        // SignInManager looks for. Program.cs fine-tunes the cookies (ConfigureApplicationCookie).
        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services
            .AddIdentityCore<AppUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = false;

                // Spelled out so the model never drifts from the migrations (see
                // ApplicationDbContext.IdentityKeyMaxLength for why that stops startup).
                options.Stores.MaxLengthForKeys = ApplicationDbContext.IdentityKeyMaxLength;
            })
            // SignInManager: the website signs in and out with it (DiscordAccountController,
            // WebSessionController), and the cookie uses its security-stamp check.
            .AddSignInManager()
            .AddEntityFrameworkStores<ApplicationDbContext>();

        return services;
    }
}
