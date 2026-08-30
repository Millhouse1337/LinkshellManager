using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// One readiness tag a member can put on their own signup.
//
// RingColor is the wedge colour on the board's spiked halo — the renderer rims every halo,
// so a colour here is free to be as dark as the board's own ground. Emoji is the same tag in
// markdown text. Code is its ONE-character form for the monospace column grid, where an emoji
// would be double-width and knock every column after it out of alignment.
public sealed record EventReadinessTag(
    string Value,
    string Label,
    string Emoji,
    char Code,
    string Description,
    string RingColor);

// The self-declared readiness tags a member sets on their OWN Discord board signup once
// they're on it — the gear/prep claims a party leader wants to see at a glance while
// filling out the board.
//
// These are declarations, not permissions: nothing validates them, exactly like the job a
// member signs up with. Multiple tags are allowed at once (a relic holder can also be
// enfeeb ready), which is what decides the board's rendering — the halo splits into equal
// wedges rather than picking one tag to show. This is Discord-board-only; the web and
// Activity boards don't surface it.
public static class EventReadiness
{
    public const string Enfeeb = "enfeeb";
    public const string Resist = "resist";
    public const string Relic = "relic";

    // Order matters and is the single source of truth for it: it fixes the wedge order
    // on a multi-tag halo, the Color Key order, and the option order in the picker, so
    // every board reads the same way.
    public static readonly IReadOnlyList<EventReadinessTag> All = new[]
    {
        // Emoji are SYMBOLS, not colour swatches. They were ⬛/⬜/🟨 to match the rendered PNG
        // board's black/white/gold halo, but three coloured squares in a row read as decoration
        // — nobody can tell which is which without the key. A test tube, a shield and a blade
        // say what they mean at a glance. RingColor still carries the halo's colour for the PNG.
        //
        // Codes are E / R / L ("reLic") — Resist and Relic both start with R, so the third
        // can't just be its initial.
        new EventReadinessTag(
            Enfeeb, "Enfeeb Ready", "🧪", 'E',
            "Enfeebling gear / merits — set up to land debuffs",
            "#101014"),
        new EventReadinessTag(
            Resist, "Resist Ready", "🛡️", 'R',
            "Wearing resist gear for this fight",
            "#eef2f5"),
        new EventReadinessTag(
            Relic, "Relic Weapon", "🗡️", 'L',
            "Bringing a relic weapon",
            "#e3b23c"),
    };

    // The tags a signup carries, in All order. The bool→tag mapping lives HERE and nowhere
    // else — the board renderer, the text fallback and the picker all read this, so adding
    // a fourth tag is one edit rather than four.
    public static IReadOnlyList<EventReadinessTag> Selected(bool enfeeb, bool resist, bool relic)
    {
        var picked = new List<EventReadinessTag>(All.Count);
        foreach (var tag in All)
        {
            var on = tag.Value switch
            {
                Enfeeb => enfeeb,
                Resist => resist,
                Relic => relic,
                _ => false,
            };
            if (on)
            {
                picked.Add(tag);
            }
        }
        return picked;
    }

    // The tags as their emoji, for the text board where there's no rendered image to carry the
    // halo. Empty when none.
    public static string Markers(bool enfeeb, bool resist, bool relic)
        => string.Concat(Selected(enfeeb, resist, relic).Select(tag => tag.Emoji));

    // The tags as their one-character codes, for the monospace column grid. Always padded to
    // the same width so a member carrying none still occupies the same columns as one carrying
    // all three — otherwise the grid shears on the first tagged member.
    public static string Codes(bool enfeeb, bool resist, bool relic)
    {
        var picked = Selected(enfeeb, resist, relic);
        var codes = string.Concat(picked.Select(tag => tag.Code));
        return codes.PadRight(All.Count);
    }

    // Width Codes() always returns, so callers can budget a column without guessing.
    public static int CodeWidth => All.Count;

    // The tags as emoji with ALWAYS all three slots filled — an unset tag becomes `spacer`.
    //
    // For the monospace column grid, where an emoji is a fractional number of cells and only a
    // CONSTANT count per cell keeps the columns from shearing. A member with one tag and a
    // member with none must occupy exactly the same width, so "no tag" has to be an emoji too
    // rather than a space. Callers should only spend these slots when somebody on the board has
    // actually set a tag — otherwise it's a wall of spacers carrying no information.
    public static string PaddedMarkers(
        bool enfeeb, bool resist, bool relic, string spacer, IReadOnlyList<EventReadinessTag> slots)
    {
        var sb = new System.Text.StringBuilder(slots.Count * 2);
        foreach (var tag in slots)
        {
            var on = tag.Value switch
            {
                Enfeeb => enfeeb,
                Resist => resist,
                Relic => relic,
                _ => false,
            };
            sb.Append(on ? tag.Emoji : spacer);
        }
        return sb.ToString();
    }

    // The tags actually IN USE somewhere on this board, in All order — the slots the grid has
    // to reserve. A board where only Enfeeb has been claimed spends ONE slot, not three, which
    // is four characters of every character name bought back for free. Empty when nobody has
    // set anything, so an untagged board carries no readiness columns at all.
    public static IReadOnlyList<EventReadinessTag> SlotsInUse(IEnumerable<EventPartySlotSignup> signups)
    {
        var rows = signups as IReadOnlyCollection<EventPartySlotSignup> ?? signups.ToList();
        var inUse = new List<EventReadinessTag>(All.Count);
        foreach (var tag in All)
        {
            var used = tag.Value switch
            {
                Enfeeb => rows.Any(s => s.EnfeebReady),
                Resist => rows.Any(s => s.ResistReady),
                Relic => rows.Any(s => s.RelicWeapon),
                _ => false,
            };
            if (used)
            {
                inUse.Add(tag);
            }
        }
        return inUse;
    }
}
