using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// One readiness tag a member can put on their own signup.
//
// RingColor is the wedge colour on the board's spiked halo — the renderer rims every halo,
// so a colour here is free to be as dark as the board's own ground. Emoji is the same tag in
// markdown text.
public sealed record EventReadinessTag(
    string Value,
    string Label,
    string Emoji,
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
        new EventReadinessTag(
            Enfeeb, "Enfeeb Ready", "🧪",
            "Enfeebling gear / merits — set up to land debuffs",
            "#101014"),
        new EventReadinessTag(
            Resist, "Resist Ready", "🛡️",
            "Wearing resist gear for this fight",
            "#eef2f5"),
        new EventReadinessTag(
            Relic, "Relic Weapon", "🗡️",
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
}
