using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// A named, reusable raid-composition template that officers design ahead of an
// event. It owns a tree of Alliances -> Parties -> Slots. Optionally assigned
// to a monster (by canonical name, same representation as Tod.MonsterName) so
// the ToD Tracker can link the planned composition on that monster's row.
public class PartySetup
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(128)]
    public string Name { get; set; } = string.Empty;

    // Canonical monster name (e.g. "Nidhogg"), matching Tod.MonsterName so the
    // ToD Tracker can join on it. Null = not assigned to any monster.
    [MaxLength(256)]
    public string? AssignedMonsterName { get; set; }

    [MaxLength(1024)]
    public string? Notes { get; set; }

    [MaxLength(450)]
    public string? CreatedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? CreatedByCharacterName { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public ICollection<PartySetupAlliance> Alliances { get; set; } = new List<PartySetupAlliance>();
}
