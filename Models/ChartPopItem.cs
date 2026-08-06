using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One pop item on a Charts board: what it is, which boss it pops, who is sitting on it, and how many
/// the linkshell has.
///
/// One table for every board (Sky, Sea, and later Dynamis / Limbus) rather than a table each, because
/// they are the same feature with a different cast — same rows, same per-item credit, same derived
/// ledger. <see cref="Board"/> and <see cref="Boss"/> together say which card a row appears on.
///
/// Rows are free-form: officers add and remove them per boss, the way Treasury → Items works, rather
/// than being seeded from a canonical catalog. Which items a linkshell actually tracks (and what it
/// calls them) differs per linkshell and per server.
/// </summary>
public class ChartPopItem
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    /// <summary>Board key from <see cref="Services.ChartBoardCatalog"/> — "Sky", "Sea", …</summary>
    [MaxLength(16)]
    public string Board { get; set; } = string.Empty;

    /// <summary>Which card this row appears under. Always a canonical catalog spelling.</summary>
    [MaxLength(64)]
    public string Boss { get; set; } = string.Empty;

    [MaxLength(128)]
    public string ItemName { get; set; } = string.Empty;

    // Who is physically holding it. The NAME is the record and the membership id is a convenience
    // that survives a character rename — an unsynced character is a real member of the linkshell,
    // they just have no account behind the name. Same split as TodLootDetail.ItemWinner.
    //
    // HeldByMembershipId is deliberately a plain nullable int with no relationship: Linkshells
    // already cascades into AppUserLinkshells, so an FK from here would be a second cascade path
    // into this table. Resolved at read time instead.
    [MaxLength(256)]
    public string? HeldByCharacterName { get; set; }

    public int? HeldByMembershipId { get; set; }

    public int Quantity { get; set; } = 1;

    [MaxLength(512)]
    public string? Notes { get; set; }

    /// <summary>Row order within the boss's card, so officers can put the important pops on top.</summary>
    public int SortOrder { get; set; }

    [MaxLength(450)]
    public string? CreatedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Who farmed this one. The Farming Credit Ledger is derived entirely from these.</summary>
    public ICollection<ChartPopItemCredit> Credits { get; set; } = new List<ChartPopItemCredit>();
}
