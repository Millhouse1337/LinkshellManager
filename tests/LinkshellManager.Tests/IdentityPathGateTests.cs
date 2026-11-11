using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LinkshellManager.Tests;

// The stock ASP.NET Identity pages must answer 404 (Program.cs uses this gate). Only our sign-in
// page and the cookie's logout/access-denied pages stay, and the Discord sign-in flow, which lives
// outside /Identity/, must never be touched.
public class IdentityPathGateTests
{
    [Theory]
    [InlineData("/Identity/Account/Login")]
    [InlineData("/Identity/Account/Login/")]
    [InlineData("/identity/account/login")]
    [InlineData("/Identity/Account/Logout")]
    [InlineData("/Identity/Account/AccessDenied")]
    public void KeptIdentityPages_AreAllowed(string path)
    {
        Assert.True(IdentityPathGate.IsAllowed(new PathString(path)));
    }

    [Theory]
    [InlineData("/Identity/Account/ExternalLogin")]
    [InlineData("/Identity/Account/ExternalLogin/")]
    [InlineData("/IDENTITY/ACCOUNT/EXTERNALLOGIN")]
    [InlineData("/Identity//Account/ExternalLogin")]
    [InlineData("/Identity/Account/Register")]
    [InlineData("/Identity/Account/RegisterConfirmation")]
    [InlineData("/Identity/Account/ForgotPassword")]
    [InlineData("/Identity/Account/ResetPassword")]
    [InlineData("/Identity/Account/ConfirmEmail")]
    [InlineData("/Identity/Account/LoginWith2fa")]
    [InlineData("/Identity/Account/Manage")]
    [InlineData("/Identity/Account/Manage/SetPassword")]
    [InlineData("/Identity/Account/Manage/DeletePersonalData")]
    [InlineData("/Identity/Account/Manage/ExternalLogins")]
    [InlineData("/Identity/Account/Login/Extra")]
    [InlineData("/Identity/Account")]
    [InlineData("/Identity")]
    [InlineData("/Identity/Error")]
    public void StockIdentityPages_AreBlocked(string path)
    {
        Assert.False(IdentityPathGate.IsAllowed(new PathString(path)));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/auth/discord/login")]
    [InlineData("/auth/discord/callback")]
    [InlineData("/signin-discord")]
    [InlineData("/api/web/session")]
    [InlineData("/api/web/sign-out")]
    [InlineData("/api/activity/overview")]
    [InlineData("/discord-activity/events")]
    [InlineData("/Account/Login")]
    [InlineData("/IdentityStuff/Account/Manage")]
    [InlineData("/_content/Microsoft.AspNetCore.Identity.UI/css/site.css")]
    public void PathsOutsideIdentity_AreAllowed(string path)
    {
        Assert.True(IdentityPathGate.IsAllowed(new PathString(path)));
    }
}
