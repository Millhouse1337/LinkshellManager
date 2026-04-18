using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class TodLootDetail
{
    [Key]
    public int Id { get; set; }

    public int? TodId { get; set; }

    [ForeignKey(nameof(TodId))]
    public Tod? Tod { get; set; }

    public string? ItemName { get; set; }

    public string? ItemWinner { get; set; }

    public int? WinningDkpSpent { get; set; }
}
