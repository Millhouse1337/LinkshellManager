using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

/// <summary>
/// One of an account's characters: slot 0 is the main, 1 and 2 the two alts (the same numbering as
/// JobRating.CharacterSlot).
///
/// Since Round 5 (R5.5) the only place names, jobs and crafts are written: every writer goes through
/// CharacterWriter, and every save copies slot 0's name onto the account (AppUser.CharacterName).
/// The name lookups read it
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

    // Longest name the table holds. CharacterWriter takes at most 64 for now (CharacterWriter.MaxNameLength:
    // a rollback to update B re-adds varchar(64) copies of the alt names).
    public const int MaxNameLength = 256;

    [Key]
    public int Id { get; set; }

    [MaxLength(450)]
    public string AppUserId { get; set; } = string.Empty;

    public short Slot { get; set; }

    // The character's name, trimmed.
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
