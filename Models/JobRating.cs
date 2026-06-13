using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One member's rating of another member's job, scoped to a linkshell. A row where
// RaterAppUserId == TargetAppUserId is the target's OWN self-assessment ("what you
// think"); rows where they differ are peer ratings ("what others think"). Each
// rater gets at most one row per (linkshell, target, job) — re-rating upserts it.
//
// JobIndex is the FFXI job's position in EventJobCatalog.MainJobOptions /
// PROFILE_JOB_OPTIONS (0 = WAR … 14 = SMN) — the same catalog the profile job
// grid uses. CharacterSlot selects which of the target's characters the rating is
// for (0 = main, 1 = alt 1, 2 = alt 2). A JobIndex of PlayerCommentJobIndex (-1)
// is the sentinel "player-level" row carrying the rater's free-text comment about
// the target (its Gear/Skill are unused).
public class JobRating
{
    // Sentinel JobIndex for the per-(rater, target) player-level comment row.
    public const int PlayerCommentJobIndex = -1;

    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    // Which of the target's characters this rating is for: 0 = main, 1 = alt 1,
    // 2 = alt 2. Lets self-assessment and peer ratings cover alts, not just the
    // main character.
    public int CharacterSlot { get; set; }

    [MaxLength(450)]
    public string TargetAppUserId { get; set; } = string.Empty;

    [MaxLength(450)]
    public string RaterAppUserId { get; set; } = string.Empty;

    public int JobIndex { get; set; }

    // 1–5 (0 = unset / "no opinion").
    public int Gear { get; set; }

    public int Skill { get; set; }

    // Does this character have any of the job's relic weapons? (= RelicNames
    // non-empty). Kept as a quick flag for the roster's flaming-pill marker.
    public bool HasRelic { get; set; }

    // The relic weapons this character owns for the job (e.g. ["Bravura",
    // "Ragnarok"]) — a job can have more than one possible relic (see
    // JOB_RELIC_OPTIONS). Null/empty when none.
    [Column(TypeName = "jsonb")]
    public string[]? RelicNames { get; set; }

    // Free-text comment a rater leaves about the target (player-level, stored on
    // the PlayerCommentJobIndex row). Null on per-job gear/skill rows.
    [MaxLength(1000)]
    public string? Comment { get; set; }

    public DateTime UpdatedAt { get; set; }
}
