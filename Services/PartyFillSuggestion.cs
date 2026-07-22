using LinkshellManagerDiscordApp.Models;

namespace LinkshellManagerDiscordApp.Services;

// "Fill earlier alliances first" nudge. Given a member about to take `targetSlot`,
// finds the earliest OPEN slot in an EARLIER alliance (lower SortOrder) that the
// member's role/job can fill. Returns null when none exists — i.e. the member's
// job doesn't fit any earlier opening, so the caller just signs them up where they
// chose. Pure: the caller has already loaded the PartySetup tree + the per-event
// signups dictionary (keyed by PartySetupSlotId). Mirrors the open-slot matching in
// DiscordInteractionsController.FindBestOpenSlotForCombo so "fillable" agrees
// everywhere.
public static class PartyFillSuggestion
{
    public static PartySetupSlot? SuggestEarlierSlot(
        PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> signups,
        PartySetupSlot targetSlot,
        string? memberRole,
        string? memberMainJob)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();

        // Locate the target slot's alliance position in display order.
        var targetIndex = -1;
        for (var i = 0; i < alliances.Count; i++)
        {
            if (alliances[i].Parties.Any(p => p.Slots.Any(s => s.Id == targetSlot.Id)))
            {
                targetIndex = i;
                break;
            }
        }
        // Target is in the first alliance (or couldn't be located) → nothing earlier.
        if (targetIndex <= 0)
        {
            return null;
        }

        for (var i = 0; i < targetIndex; i++)
        {
            foreach (var party in alliances[i].Parties.OrderBy(p => p.SortOrder))
            {
                foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
                {
                    if (signups.ContainsKey(slot.Id)) { continue; } // taken
                    if (CanFill(slot, memberRole, memberMainJob)) { return slot; }
                }
            }
        }
        return null;
    }

    private static bool CanFill(PartySetupSlot slot, string? memberRole, string? memberMainJob)
    {
        var type = (slot.RequirementType ?? "Any").Trim();
        if (string.Equals(type, "Any", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(type, "Role", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(slot.Role)
                && !string.IsNullOrWhiteSpace(memberRole)
                && string.Equals(slot.Role!.Trim(), memberRole!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        if (string.Equals(type, "Job", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(slot.MainJob)
                && !string.IsNullOrWhiteSpace(memberMainJob)
                && string.Equals(slot.MainJob!.Trim(), memberMainJob!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    // Short human label for a slot's requirement, used in the nudge text
    // ("an open Tank spot" / "an open WHM spot" / "an open spot").
    public static string RequirementLabel(PartySetupSlot slot)
    {
        var type = (slot.RequirementType ?? "Any").Trim();
        if (string.Equals(type, "Role", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(slot.Role))
        {
            return slot.Role!.Trim();
        }
        if (string.Equals(type, "Job", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(slot.MainJob))
        {
            return slot.MainJob!.Trim();
        }
        return "open";
    }

    // Convenience for callers building the warning text: "Alliance 1 · Party 2".
    public static string DescribeSlot(PartySetup setup, PartySetupSlot slot)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        for (var ai = 0; ai < alliances.Count; ai++)
        {
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            for (var pi = 0; pi < parties.Count; pi++)
            {
                if (parties[pi].Slots.Any(s => s.Id == slot.Id))
                {
                    var allianceName = string.IsNullOrWhiteSpace(alliances[ai].Name) ? $"Alliance {ai + 1}" : alliances[ai].Name!;
                    var partyName = string.IsNullOrWhiteSpace(parties[pi].Name) ? $"Party {pi + 1}" : parties[pi].Name!;
                    return alliances.Count > 1 ? $"{allianceName} · {partyName}" : partyName;
                }
            }
        }
        return "an earlier party";
    }
}
