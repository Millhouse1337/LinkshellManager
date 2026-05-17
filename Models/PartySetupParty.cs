using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One party within an alliance. Nameable (defaults to "Party {n}" when blank).
// SortOrder is the 0-based display position within the alliance.
public class PartySetupParty
{
    [Key]
    public int Id { get; set; }

    public int PartySetupAllianceId { get; set; }

    [ForeignKey(nameof(PartySetupAllianceId))]
    public PartySetupAlliance? Alliance { get; set; }

    public int SortOrder { get; set; }

    [MaxLength(64)]
    public string? Name { get; set; }

    public ICollection<PartySetupSlot> Slots { get; set; } = new List<PartySetupSlot>();
}
