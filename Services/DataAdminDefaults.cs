namespace LinkshellManagerDiscordApp.Services;

// Every tunable and default rule of the Data Admin engine in one place, so the numbers that
// bound a page, a dropdown or a cascade walk are never scattered as magic literals across the
// catalog, the query service and the views.
public static class DataAdminDefaults
{
    // Rows per list page. Matches the other paged MVC lists (ViewModels/LootHistoryViewModel.cs).
    public const int PageSize = 25;

    // Columns shown on a list page beyond the label and the key. The details page shows them all.
    public const int MaxListColumns = 8;

    // A foreign-key field on the edit form becomes a <select> of the principal's labels only
    // while the principal table is this small; above it, the raw key is typed in.
    public const int MaxSelectRows = 500;

    // Bounds for the delete preview's cascade walk: how deep it follows Cascade edges, and how
    // many rows on one edge it is willing to fetch keys for before it stops descending.
    public const int CascadeMaxDepth = 6;
    public const int CascadeMaxKeysPerLevel = 20_000;

    // Keys are fetched and matched in chunks so a single IN/ANY parameter never gets huge.
    public const int KeyChunkSize = 1_000;

    // Cell text longer than this is truncated on the list page (the details page shows it all).
    public const int ListCellMaxLength = 80;

    // Column names tried, in order, when a table has no label override in the policy. The first
    // visible string column with one of these names becomes the row label; none -> the key.
    // Entity-specific names come first: many tables also carry a denormalised LinkshellName copy
    // (Rule, Item, Announcement...), which must never beat the row's own title.
    public static readonly IReadOnlyList<string> LabelGuessOrder = new[]
    {
        "RuleTitle", "AnnouncementTitle", "ItemName", "EventName", "MonsterName", "AuctionTitle",
        "EntryNumber", "AccountName", "KeyItemName", "CharacterName", "GlobalName", "DiscordDisplayName",
        "NotificationType", "TokenPrefix", "Name", "Title", "Label", "Key", "Username", "UserName",
        "LinkshellName",
    };

    // IdentityUser columns that are never listed nor edited on any Identity-derived entity.
    public static readonly IReadOnlySet<string> IdentityInternals = new HashSet<string>(StringComparer.Ordinal)
    {
        "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "NormalizedUserName", "NormalizedEmail",
        "PhoneNumber", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnd", "LockoutEnabled",
        "AccessFailedCount", "EmailConfirmed",
    };

    // Literal route segments under /data-admin. A table slug must never collide with one.
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tables", "create", "edit", "delete",
    };
}
