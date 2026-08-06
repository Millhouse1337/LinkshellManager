namespace LinkshellManagerDiscordApp.Models;

// A treasury entry's lifecycle.
//
//     Draft ----confirm----> Confirmed ----reverse----> (Confirmed, with a reversal against it)
//       |                        |
//   editable                 IMMUTABLE
//   discardable              corrections are new entries, never edits
//
// A draft is a scratch pad: an officer can fix a typo or throw it away and nothing is on the
// books yet. The moment it is confirmed it stops being editable — a wrong confirmed entry is
// fixed by recording an opposite entry, so the original stays visible and the history of what
// was believed and when survives. That is the whole reason this replaced
// RevenueEntries.Remove().
//
// "Confirmed" rather than "Posted" deliberately: "post" already means posting a board or a
// message to Discord everywhere else in this app.
public static class JournalEntryStatuses
{
    public const string Draft = "Draft";
    public const string Confirmed = "Confirmed";

    public static bool IsDraft(string? status) =>
        string.Equals(status, Draft, StringComparison.OrdinalIgnoreCase);

    public static bool IsConfirmed(string? status) =>
        string.Equals(status, Confirmed, StringComparison.OrdinalIgnoreCase);
}
