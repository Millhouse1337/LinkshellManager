using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

public class PendingTodLootSubmission
{
    [Key]
    public int Id { get; set; }

    public int PendingTodSubmissionId { get; set; }

    [ForeignKey(nameof(PendingTodSubmissionId))]
    public PendingTodSubmission? PendingTodSubmission { get; set; }

    [MaxLength(256)]
    public string? ItemName { get; set; }

    [MaxLength(256)]
    public string? ItemWinner { get; set; }

    public int? WinningDkpSpent { get; set; }
}
