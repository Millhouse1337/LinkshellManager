using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LinkshellManagerDiscordApp.Models;

// How a Standard-mode camp for a monster takes attendance — the "Window Method" column of the
// Monster Setups editor.
//   OpenClose = "Open/Close": an Open post and a Close post, however many spawn windows the
//               monster sits through. Only those two pay (HNM Settings' Open and Close bonuses).
//   WindowN   = "Every Window": every window can be posted and pays the Regular window rate; window 1
//               pays the Open bonus and the close window pays the Close bonus. A monster with no
//               window grid posts open-ended numbered windows up to HnmConfig.MaxWindow.
// Manual Check In camps ignore it — they are paid from check-ins, not posts.
public static class MonsterWindowMethods
{
    public const string OpenClose = "OpenClose";
    public const string WindowN   = "WindowN";

    public static readonly IReadOnlyList<string> All = new[]
    {
        OpenClose, WindowN,
    };

    public static bool IsValid(string? value)
        => !string.IsNullOrEmpty(value) && All.Contains(value);

    // Null for anything unrecognised: callers read null as "no method stamped", which keeps a camp
    // on the rules it ran under before this setting existed rather than guessing one for it.
    public static string? Normalize(string? value)
        => IsValid(value) ? value : null;
}

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

    // Seconds added on top of the cooldown when a ToD's repop is computed — the fine offset for a
    // monster whose real repop runs a little past the round number. Lived on the Log ToD form as
    // "Additional seconds" until it moved here with the cooldown and interval: it is a property of
    // the monster, not of one kill, and typing it per ToD is how it got forgotten.
    public int AdditionalSeconds { get; set; }

    // "HNMs" / "Sky NMs" / "Other NMs" — the heading this row sorts under in the editor. Free-form
    // so a stale category from a retired group still renders (it falls back to "Other NMs").
    [MaxLength(32)]
    public string? Category { get; set; }

    // True when a linkshell added this monster itself. The editor only lets custom rows be DELETED;
    // a built-in row resets to its HnmConfig default instead. Server-side rather than a client-side
    // "is this name in the built-in list" string compare, which was how the old blob editor decided.
    public bool IsCustom { get; set; }

    // Whether the in-game addon should capture claim-shield lotteries for this monster — the
    // Monster Setups "Claim Shield" Yes/No, which says the monster has a Claim Shield in game.
    //
    // Defaults FALSE: a monster a linkshell adds itself starts on No. The built-in HNMs are seeded
    // on (MonsterTimingDefaults.ToEntity), and rows that existed before 2026-10-30 keep whatever
    // they had. Off matters because the addon still hears every lottery line (it listens for the
    // line, not for a monster list), and without this every stray NM roll in the zone lands in the
    // capture panel as noise.
    //
    // Per MONSTER rather than per camp: the capture is keyed off the lottery line, which can arrive
    // with no camp open at all, so a camp-scoped flag would have nothing to read at the moment the
    // decision is made.
    public bool ClaimShieldEnabled { get; set; } = false;

    // MonsterWindowMethods value, or null for "the built-in default" (MonsterTimingDefaults
    // .DefaultWindowMethod). Null rather than a backfilled value so a built-in row keeps following
    // the default, and the editor stores null again whenever the chosen value equals it.
    [MaxLength(16)]
    public string? WindowMethod { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
