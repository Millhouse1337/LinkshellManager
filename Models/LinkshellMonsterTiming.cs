using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// One monster's timing setup for one linkshell: how many spawn windows its camp runs, how far
// apart those windows open, and how long after a kill it repops.
//
// This table replaced two overlapping surfaces that each described half of the same monster:
// the per-linkshell ToD cooldown blob (Linkshell.TodMonsterTimings, a JSON string) and the
// read-only "Window setups" list projected off HnmConfig's compile-time cadence tables. They
// disagreed by construction — the ToD "interval" and the window "cadence" are the same number
// (Cerberus 1h vs 60 min, Adamantoise 10m vs 10 min) stored in two unrelated places, editable
// in only one — so they are one row here, and WindowCadenceMinutes serves both roles.
//
// A row is stored under the MERGED name for the three NQ/HQ families ("Fafnir/Nidhogg"), because
// both halves share one spawn and therefore one setup. Every lookup goes through
// HnmConfig.MonsterMatchNames, so a caller holding "Fafnir", "Nidhogg" or "Fafnir/Nidhogg" finds
// the same row — see MonsterTimingMap.For.
public class LinkshellMonsterTiming
{
    [Key]
    public int Id { get; set; }

    public int LinkshellId { get; set; }

    [ForeignKey(nameof(LinkshellId))]
    public Linkshell? Linkshell { get; set; }

    // Canonical display name, merged for the three NQ/HQ families.
    [MaxLength(128)]
    public string MonsterName { get; set; } = string.Empty;

    // Lower-invariant MonsterName, so the unique index and the EF `names.Contains(...)` predicates
    // can both use it. HnmRecurringBoardService does the ToLower() inside its predicate instead and
    // therefore can't use its index; this column is the cheap fix for the same problem.
    [MaxLength(128)]
    public string NormalizedMonsterName { get; set; } = string.Empty;

    // How many spawn windows the camp sits through. Null = this monster has no window grid, which
    // is a real answer (Sky gods, most ground NMs) and NOT the same as 1.
    public int? WindowCount { get; set; }

    // Minutes between window openings, and — for a monster with no window grid — how often the ToD
    // form suggests re-checking. Null = neither.
    public int? WindowCadenceMinutes { get; set; }

    // Repop cooldown after a kill. Required: every monster on the ToD tracker has one.
    public int CooldownMinutes { get; set; }

    // "HNMs" / "Sky NMs" / "Other NMs" — the heading this row sorts under in the editor. Free-form
    // so a stale category from a retired group still renders (it falls back to "Other NMs").
    [MaxLength(32)]
    public string? Category { get; set; }

    // True when a linkshell added this monster itself. The editor only lets custom rows be DELETED;
    // a built-in row resets to its HnmConfig default instead. Server-side rather than a client-side
    // "is this name in the built-in list" string compare, which was how the old blob editor decided.
    public bool IsCustom { get; set; }

    // Whether the in-game addon should capture claim-shield lotteries for this monster.
    //
    // Defaults TRUE, and the migration backfills every existing row to true, so turning the column
    // on changes nothing until an officer deliberately switches a monster off. Off is for the
    // monsters a linkshell doesn't contest — the addon still hears their lottery lines (it listens
    // for the line, not for a monster list), and without this every stray NM roll in the zone lands
    // in the capture panel as noise.
    //
    // Per MONSTER rather than per camp: the capture is keyed off the lottery line, which can arrive
    // with no camp open at all, so a camp-scoped flag would have nothing to read at the moment the
    // decision is made.
    public bool ClaimShieldEnabled { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
