using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// Per-character DKP override for a Window Event. When a row exists for a
// character on a given Window Event, the AttInput append and ledger services
// use this amount in place of WindowEvent.DkpAmount for that character only.
// Characters without an override fall through to the event's default.
public class WindowEventMemberDkp
{
    [Key]
    public int Id { get; set; }

    public int WindowEventId { get; set; }

    [ForeignKey(nameof(WindowEventId))]
    public WindowEvent? WindowEvent { get; set; }

    [MaxLength(256)]
    public string CharacterName { get; set; } = string.Empty;

    public double DkpAmount { get; set; }
}
