using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One of an account's characters: slot 0 is the main, 1 and 2 the two alts (the same numbering as
/// JobRating.CharacterSlot).
///
/// A SHADOW of AspNetUsers for now (AddCharacters). The account's name and job columns are still
/// the ones the app writes and reads; this table is a copy of them, one row per non-blank name,
/// kept in step on every save by ApplicationDbContext (CharacterSync). Nothing reads it yet. A
/// later deploy switches the name lookups to it, so one search covers every character of every
/// account instead of four separate name columns.
///
/// No unique name index yet: two accounts may share a name today (placeholders often carry a real
/// player's name), and a copy must never make a user's save fail.
/// </summary>
public class Character
{
    public const short MainSlot = 0;
    public const short Alt1Slot = 1;
    public const short Alt2Slot = 2;

    // Longest name the table holds. Longer account names are left out of the copy (and logged).
    public const int MaxNameLength = 256;

    [Key]
    public int Id { get; set; }

    [MaxLength(450)]
    public string AppUserId { get; set; } = string.Empty;

    public short Slot { get; set; }

    // Trimmed copy of the account's name for this slot.
    [MaxLength(MaxNameLength)]
    public string Name { get; set; } = string.Empty;

    // lower(Name), for case-insensitive lookups.
    [MaxLength(MaxNameLength)]
    public string NormalizedName { get; set; } = string.Empty;

    [Column(TypeName = "jsonb")]
    public int[]? JobLevels { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? StrongJobs { get; set; }

    [Column(TypeName = "jsonb")]
    public string[]? MeritJobs { get; set; }

    [Column(TypeName = "jsonb")]
    public int[]? CraftLevels { get; set; }

    public DateTime UpdatedAt { get; set; }
}
