using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One of an account's characters: slot 0 is the main, 1 and 2 the two alts (the same numbering as
/// JobRating.CharacterSlot).
///
/// A copy of AspNetUsers (AddCharacters). The account's name and job columns are still the ones
/// the app WRITES; this table is a copy of them, one row per non-blank name, kept in step on every
/// save by ApplicationDbContext (CharacterSync). Since CharactersSwitch the name lookups READ it
/// (CharacterResolver.LoadMembershipsAsync, LinkshellRosterNames.LoadCandidatesAsync, the addon
/// /roster alts), so one query covers every character of every account.
///
/// One row per name per account (unique AppUserId + NormalizedName): a slot that repeats an earlier
/// slot's name in the same account has no row, as the earlier slot already gives the account that
/// name. Different accounts may share a name (placeholders often carry a real player's name).
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
