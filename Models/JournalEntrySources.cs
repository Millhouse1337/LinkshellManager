namespace LinkshellManagerDiscordApp.Models;

// WHERE an entry came from. Lets the UI distinguish "an officer typed this" from "the app
// recorded this when an auction closed", which matters when someone is trying to work out why
// the treasury moved without anyone touching it.
public static class JournalEntrySources
{
    // An officer recorded it by hand.
    public const string Manual = "Manual";

    // An inventory item was marked sold (either surface).
    public const string ItemSale = "ItemSale";

    // A gil auction closed and the winner is owed / was paid the gil.
    public const string GilAuction = "GilAuction";

    // Built from a legacy RevenueEntries row by the one-time conversion.
    public const string Migration = "Migration";
}
