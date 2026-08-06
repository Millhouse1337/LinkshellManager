using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// The officers' free-text note on where a boss stands — the mockup's "Progress: First Virtue", and
/// the status word on the mini-NM card.
///
/// Deliberately ONE free-text field rather than a modelled progress enum. What "progress" means
/// differs per boss and per linkshell (a virtue number, a farm state, "waiting on Yurin"), and a
/// dropdown of states someone has to keep synchronised with the game is a worse answer than a line
/// officers can type. Nothing is derived from it.
///
/// A missing row means no note, which is the normal state — rows are created on first edit rather
/// than seeded, so an untouched board carries no storage.
/// </summary>
public class ChartBossProgress
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    [MaxLength(16)]
    public string Board { get; set; } = string.Empty;

    [MaxLength(64)]
    public string Boss { get; set; } = string.Empty;

    [MaxLength(128)]
    public string? ProgressNote { get; set; }

    [MaxLength(450)]
    public string? UpdatedByAppUserId { get; set; }

    [MaxLength(256)]
    public string? UpdatedByCharacterName { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
