namespace LinkshellManagerDiscordApp.Utils;

// Where the public pages (the landing page, Features and Addon; Views/Home) send people. The
// website's app is the Angular UI, the same one Discord shows, and signing in lands there.
public static class LandingLinks
{
    public const string App = LegacyUrlRedirects.AppBase;

    public const string SignIn = "/auth/discord/login?returnUrl=%2Fdiscord-activity%2F";

    // The app's Game Addon tab, where a member gets a pairing code. Signed out, the app shows its
    // sign-in screen first.
    public const string AddonTab = App + "addon";

    // Signs in and lands on the Game Addon tab, for the Addon page's closing button.
    public const string SignInToAddonTab = "/auth/discord/login?returnUrl=%2Fdiscord-activity%2Faddon";

    // Cinzel (headings) and Figtree (body). Kept here rather than written into the href, so Razor
    // never has to decide what the @ means.
    public const string Fonts = "https://fonts.googleapis.com/css2?family=Cinzel:wght@500;600;700&family=Figtree:wght@400;500;600&display=swap";
}
