using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class EventLootDetail
{
    [Key]
    public int Id { get; set; }

    public int EventId { get; set; }

    [ForeignKey(nameof(EventId))]
    public Event? Event { get; set; }

    public string? ItemName { get; set; }

    public string? ItemWinner { get; set; }

    public int? WinningDkpSpent { get; set; }
}
