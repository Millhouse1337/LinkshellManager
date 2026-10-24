using Microsoft.AspNetCore.Http;

namespace LinkshellManagerDiscordApp.Utils;

// The old Razor website's page addresses, mapped to the same place in the Angular app (one URL per
// tab; discord-activity/src/app/home/tab-routes.ts) now that the website IS the Activity UI.
// Program.cs runs this for GET/HEAD only: an old form still posts to its controller until the
// cleanup phase deletes it, and that controller's RedirectToAction then lands back here.
//
// Matching is per path segment and case-insensitive, so /Event never swallows /EventHistory.
// A linkshell named in the old URL (/linkshells/5/..., /Linkshell/Details/5, ?linkshellId=5)
// becomes ?ls=5, which the app switches to. LegacyUrlRedirectsTests pins every rule, and that each
// target's tab slug really exists in tab-routes.ts.
public static class LegacyUrlRedirects
{
    public const string AppBase = "/discord-activity/";

    // Matched in order, so a more specific prefix comes before the one it starts with.
    // LinkshellIdSegment: the old URL carried the linkshell id as the next path segment, or as ?id=.
    private static readonly (string Prefix, string Target, bool LinkshellIdSegment)[] Rules =
    {
        ("/Linkshell/Customize", "configurations", true),
        ("/Linkshell/Create", "configurations", false),
        ("/Linkshell/Edit", "configurations", true),
        ("/Linkshell/Delete", "configurations", true),
        ("/Linkshell/Permissions", "permissions", true),
        ("/Linkshell/GameAddon", "addon", false),
        ("/Linkshell/DownloadDkpTemplate", "/api/activity/dkp-import/template", false),
        ("/Linkshell", "management", true),
        ("/Dashboard", "", false),
        ("/Announcement", "", false),
        ("/Rule", "", false),
        ("/AuctionHistory", "auctions/history", false),
        ("/Auction", "auctions", false),
        ("/EventHistory", "events/past", false),
        ("/Event", "events", false),
        ("/Tod", "tods", false),
        ("/PartySetup", "party-setup", false),
        ("/ManageTeam", "management", false),
        // The DKP tab opens on the Sheet, so the old ledger pages name its History view.
        ("/DkpHistory", "dkp/history", false),
        ("/DkpAdjustment", "dkp/history", false),
        ("/LootHistory", "loot", false),
        ("/ManageFinances", "treasury", false),
        ("/ManageItem", "treasury/items", false),
        ("/ManageRevenue", "treasury", false),
        ("/Account/Profile", "profile", false),
        ("/Account/Settings", "admin", false),
        ("/Messages", "messages", false),
        ("/Contact", "messages", false),
    };

    // /Charts/{board}: the board is its own sub-view in the app (Sky, the default, is bare /charts).
    private static readonly string[] ChartBoards = { "Sky", "Sea", "Dynamis", "Limbus", "HENM" };

    public static bool TryResolve(PathString path, QueryString query, out string target)
    {
        target = string.Empty;
        var segments = (path.Value ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var queryLinkshell = ReadLinkshellQuery(query, "linkshellId");

        // /linkshells/{id}/... — the linkshell-scoped pages.
        if (segments.Length >= 3
            && segments[0].Equals("linkshells", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(segments[1], out var scopedId) && scopedId > 0)
        {
            var page = segments[2].ToLowerInvariant();
            var rest = segments.Length > 3 ? segments[3].ToLowerInvariant() : null;
            target = (page, rest) switch
            {
                ("dkp-sheet", "export.xlsx") => $"/api/activity/dkp-sheet/export.xlsx?linkshellId={scopedId}",
                ("dkp-sheet", null) => App("dkp", scopedId),
                ("window-events", "history") => App("events/past", scopedId),
                // Events Pending DKP Post is Events → Pending; the Unlinked Snapshots triage is on
                // Current; members' ToD submissions are reviewed on the ToDs tab.
                ("window-events", null) => App("events/pending", scopedId),
                ("attendance-snapshots", null) => App("events", scopedId),
                ("pending-submissions", _) => App("tods", scopedId),
                _ => string.Empty
            };
            return target.Length > 0;
        }

        // /data-admin/... keeps its whole path: the app's Data Admin tab routes the same segments
        // (slug, id, create / edit / delete). The list's ?q=&f.= are dropped; the app keeps its own.
        if (path.StartsWithSegments("/data-admin", out var dataAdminRest))
        {
            target = AppBase + "data-admin" + dataAdminRest.ToUriComponent();
            return true;
        }

        if (path.StartsWithSegments("/Charts", out var chartsRest))
        {
            var board = chartsRest.Value?.Trim('/') ?? string.Empty;
            var known = ChartBoards.FirstOrDefault(b => b.Equals(board, StringComparison.OrdinalIgnoreCase));
            target = App(known is null || known == ChartBoards[0] ? "charts" : $"charts/{known.ToLowerInvariant()}", queryLinkshell);
            return true;
        }

        foreach (var (prefix, rule, linkshellIdSegment) in Rules)
        {
            if (!path.StartsWithSegments(prefix, out var remaining))
            {
                continue;
            }

            var linkshellId = queryLinkshell;
            if (linkshellIdSegment)
            {
                linkshellId ??= ReadLinkshellQuery(query, "id");
                var next = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (linkshellId is null && int.TryParse(next, out var segmentId) && segmentId > 0)
                {
                    linkshellId = segmentId;
                }
            }

            target = rule.StartsWith('/') ? rule : App(rule, linkshellId);
            return true;
        }

        return false;
    }

    private static string App(string appPath, int? linkshellId) =>
        AppBase + appPath + (linkshellId is { } id ? $"?ls={id}" : string.Empty);

    private static int? ReadLinkshellQuery(QueryString query, string name)
    {
        if (!query.HasValue)
        {
            return null;
        }
        var values = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(query.Value);
        return values.TryGetValue(name, out var raw) && int.TryParse(raw.ToString(), out var id) && id > 0 ? id : null;
    }
}
