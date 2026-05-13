using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

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

    public ICollection<AppUserLinkshell> AppUserLinkshells { get; set; } = new List<AppUserLinkshell>();

    public ICollection<Event> Events { get; set; } = new List<Event>();

    public ICollection<EventHistory> EventHistories { get; set; } = new List<EventHistory>();
}
