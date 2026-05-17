using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One alliance within a PartySetup. Nameable (defaults to "Alliance {n}" when
// blank). SortOrder is the 0-based display position.
public class PartySetupAlliance
{
    [Key]
    public int Id { get; set; }

    public int PartySetupId { get; set; }

    [ForeignKey(nameof(PartySetupId))]
    public PartySetup? PartySetup { get; set; }

    public int SortOrder { get; set; }

    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    public ICollection<PartySetupParty> Parties { get; set; } = new List<PartySetupParty>();
}
