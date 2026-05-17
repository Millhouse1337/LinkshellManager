using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public static class LinkshellTypes
{
    public const string SkySeaDynamis = "SkySeaDynamis";
    public const string HnmOnly       = "HnmOnly";
    public const string Both          = "Both";

    public static readonly IReadOnlyList<string> All = new[]
    {
        SkySeaDynamis, HnmOnly, Both,
    };

    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && All.Contains(value);

    // Anything unknown/null is treated as Both (fail-open) so a missing or
    // stale value never hides content the linkshell expects to see.
    public static string Normalize(string? value)
        => IsValid(value) ? value! : Both;
}

public class Linkshell
{
    [Key]
    public int Id { get; set; }

    public string? AppUserId { get; set; }

    [ForeignKey(nameof(AppUserId))]
    public AppUser? AppUser { get; set; }

    public string? LinkshellName { get; set; }

    [NotMapped]
    public int? TotalMembers { get; set; }

    [NotMapped]
    public int? TotalItems { get; set; }

    [NotMapped]
    public int? Revenue { get; set; }

    public string? Status { get; set; }

    public string? Details { get; set; }

    // Drives which content the in-game addon and the web sidebar surface:
    //   SkySeaDynamis = timed-event experience (no HNM presets / no Window
    //                   Events nav)
    //   HnmOnly       = HNM snapshot sessions only (no Create Event / timed
    //                   presets / Queued / Active; no Event System nav)
    //   Both          = everything (default; matches legacy behavior)
    [MaxLength(16)]
    public string LinkshellType { get; set; } = LinkshellTypes.Both;

    [MaxLength(32)]
    public string LootStructure { get; set; } = "Dkp";

    [MaxLength(16)]
    public string DkpRoundingIncrement { get; set; } = "Quarter";

    public bool EnableHnmSection { get; set; } = true;

    // Disabled by default for Beta — Missions/Endgame UIs are placeholders.
    // Linkshell admins can opt in via Customize to surface the tabs for testers.
    public bool EnableMissions { get; set; } = false;

    public bool EnableAuctions { get; set; } = true;

    public bool EnableToDs { get; set; } = true;

    public bool EnableEndgame { get; set; } = false;

    public bool EnableEvents { get; set; } = true;

    public bool EnableDkp { get; set; } = true;

    public bool EnableItems { get; set; } = true;

    public bool EnableRevenue { get; set; } = true;

    // Pipe-separated list of monster names to hide from this linkshell's
    // ToD Tracker (Discord Activity + legacy MVC views). Empty = nothing
    // hidden. Pipe separator avoids the comma collision risk if FFXI ever
    // grows a mob name with a comma in it. Edited from the Customize panel.
    public string HiddenTodMonsters { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? GoogleSpreadsheetId { get; set; }

    [MaxLength(64)]
    public string? GoogleSheetTabName { get; set; }

    public string? GoogleOAuthRefreshTokenEnc { get; set; }

    [MaxLength(256)]
    public string? GoogleOAuthUserEmail { get; set; }

    public DateTime? GoogleOAuthConnectedAt { get; set; }

    public bool SheetSyncEnabled { get; set; } = false;

    // Per-LS override of the AttInput tab name. Default is "AttInput", but
    // each linkshell can rename their tab. The sync service appends rows here
    // rather than overwriting Main!C so the user's existing formula chain
    // (AttInput -> Tally -> Main!F -> Main!C) keeps working unchanged.
    [MaxLength(64)]
    public string? AttInputTabName { get; set; }

    // Fallback Entry Type for snapshots / window posts that have no linked
    // event with an AttInputEntryType set. Examples: "Misc Camp", "Kill".
    [MaxLength(32)]
    public string? AttInputDefaultEntryType { get; set; }

    // Per-LS override of the ManualPoints tab name. Default is "ManualPoints".
    // Each DKP audit appends a new column to this tab; the cell at the
    // member's row gets the audit amount.
    [MaxLength(64)]
    public string? ManualPointsTabName { get; set; }

    // Discord channel webhook URL (Channel Settings -> Integrations ->
    // Webhooks). When set, a `/lsm now` attendance snapshot is also posted to
    // this channel as a party-grouped embed. Null = Discord posting disabled.
    [MaxLength(512)]
    public string? DiscordWebhookUrl { get; set; }

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<Event> Events { get; set; } = new List<Event>();

    public ICollection<EventHistory> EventHistories { get; set; } = new List<EventHistory>();
}
