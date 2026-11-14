using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Controllers;

// Pure functions that turn rows into the Activity's DTOs (and a few name normalisers), shared by
// more than one Activity controller. No services, no database: everything they need is passed in.
// The controllers import them with "using static", so their call sites read as before.
internal static class ActivityMappers
{
    internal static ActivityLinkshellSettingsDto MapLinkshellSettingsDto(
        Linkshell? linkshell,
        IReadOnlyList<ActivityMonsterSetupDto>? monsterSetups = null)
    {
        if (linkshell is null)
        {
            return new ActivityLinkshellSettingsDto(
                "Dkp", true, true, true, true, true, true, true, "Quarter",
                Array.Empty<string>(), null, null, false,
                false, 3, 2, false);
        }

        return new ActivityLinkshellSettingsDto(
            string.IsNullOrWhiteSpace(linkshell.LootStructure) ? "Dkp" : linkshell.LootStructure,
            linkshell.EnableHnmSection,
            linkshell.EnableAuctions,
            linkshell.EnableToDs,
            linkshell.EnableEndgame,
            linkshell.EnableEvents,
            linkshell.EnableDkp,
            linkshell.EnableTreasury,
            NormalizeDkpRounding(linkshell.DkpRoundingIncrement),
            ParseHiddenTodMonsters(linkshell.HiddenTodMonsters),
            linkshell.DiscordGuildId,
            linkshell.DiscordGuildName,
            linkshell.LockToDiscordGuild,
            linkshell.EnableActivityTracking,
            linkshell.InactiveAfterAbsences,
            linkshell.ActiveAfterAttendances,
            linkshell.OutsidePartySignupEnabled,
            linkshell.DiscussionChannelId,
            HnmAttendanceModes.Normalize(linkshell.HnmAttendanceMode),
            linkshell.WdDkpPerWindow,
            linkshell.WdClaimBonus,
            linkshell.WdKillBonus,
            linkshell.HnmStandardOpenBonus,
            linkshell.HnmStandardCloseBonus,
            linkshell.HnmStandardClaimBonus,
            linkshell.HnmStandardKillBonus,
            monsterSetups,
            linkshell.HnmStandardWindowBonus,
            linkshell.WdOpenBonus,
            linkshell.WdCloseBonus,
            linkshell.HnmStandardMiscBonus);
    }

    // Splits the pipe-separated storage form into a clean list of names
    // (trimmed, non-empty). Pipe avoids comma collisions if FFXI ever
    // grows a mob name with a comma in it.
    internal static IReadOnlyList<string> ParseHiddenTodMonsters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var parts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) list.Add(trimmed);
        }
        return list;
    }

    // Joins the wire form back into the pipe-separated storage form.
    // Drops empty / whitespace-only entries and de-dupes case-insensitively
    // so the stored value stays clean.
    internal static string SerializeHiddenTodMonsters(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0) return string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var trimmed = name.Trim();
            if (seen.Add(trimmed)) kept.Add(trimmed);
        }
        return string.Join('|', kept);
    }

    internal static string NormalizeDkpRounding(string? raw)
    {
        if (string.Equals(raw, "Half", StringComparison.OrdinalIgnoreCase)) return "Half";
        return "Quarter";
    }

    internal static ActivityPermissionsDto? ResolvePermissionsFor(
        int linkshellId,
        string? rank,
        Dictionary<int, Dictionary<string, LinkshellRole>> rolesByLinkshellAndName,
        bool adminOverrideActive)
    {
        // The app-wide admin override short-circuits to every permission. The caller
        // only ever passes memberships, so this cannot unlock a linkshell the user
        // has not joined. See AdminOverrideService.
        if (adminOverrideActive)
        {
            return ActivityPermissions.All;
        }

        if (!rolesByLinkshellAndName.TryGetValue(linkshellId, out var rolesByName))
        {
            return null;
        }

        var rankName = string.IsNullOrWhiteSpace(rank) ? "Member" : rank.Trim();
        if (!rolesByName.TryGetValue(rankName, out var role))
        {
            if (!rolesByName.TryGetValue("Member", out role))
            {
                return null;
            }
        }

        return new ActivityPermissionsDto(
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageCharts,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanLockAuctions,
            role.CanCustomizeLinkshell,
            role.CanManageParties,
            role.CanManageInvites,
            role.CanBid,
            role.CanUseAddon,
            role.CanManageAttendanceWithAddon,
            role.CanSubmitTodForApproval);
    }

    internal static ActivityLinkshellRoleDto MapLinkshellRoleDto(LinkshellRole role)
    {
        return new ActivityLinkshellRoleDto(
            role.Id,
            role.Name,
            role.IsSystem,
            role.SortOrder,
            role.CanManageRoles,
            role.CanManageMembers,
            role.CanManageEvents,
            role.CanModerateLiveEvent,
            role.CanAddLoot,
            role.CanManageInventory,
            role.CanManageCharts,
            role.CanManageTreasury,
            role.CanManageRules,
            role.CanManageAnnouncements,
            role.CanManageTods,
            role.CanAuditDkp,
            role.CanManageAuctions,
            role.CanLockAuctions,
            role.CanCustomizeLinkshell,
            role.CanManageParties,
            role.CanManageInvites,
            role.CanBid,
            role.CanUseAddon,
            role.CanManageAttendanceWithAddon,
            role.CanSubmitTodForApproval);
    }

    internal static IReadOnlyList<ActivityHnmClaimSliceDto> MapHnmClaimSlices(IReadOnlyList<HnmClaimSlice> slices) =>
        slices
            .Select(slice => new ActivityHnmClaimSliceDto(
                slice.MonsterName,
                slice.Count,
                slice.Attempts,
                slice.ClaimRate,
                slice.ColorClass,
                slice.IsHq,
                slice.HasHqVariant))
            .ToList();

    internal static ActivityHnmWindowsDto MapHnmWindows(HnmWindowStats stats) =>
        new(
            stats.Grids
                .Select(grid => new ActivityHnmWindowGridDto(
                    grid.ColumnCount,
                    grid.IsWide,
                    grid.Monsters
                        .Select(monster =>
                        {
                            // Tinted against the monster's busiest window, so its shape is readable
                            // whether its pops cluster in three windows or spread across twenty-five.
                            // Computed here rather than client-side.
                            var peakCount = monster.Bars.Count == 0 ? 0 : monster.Bars.Max(bar => bar.Count);
                            return new ActivityHnmWindowMonsterDto(
                                monster.MonsterName,
                                monster.ColorClass,
                                monster.TotalPops,
                                monster.WindowCount,
                                monster.PeakWindow,
                                monster.Bars
                                    .Select(bar => new ActivityHnmWindowBarDto(
                                        bar.Window,
                                        bar.Count,
                                        bar.Percent,
                                        peakCount == 0 ? 0 : Math.Round(bar.Count * 100.0 / peakCount, 1)))
                                    .ToList());
                        })
                        .ToList()))
                .ToList(),
            stats.TotalPops);

    internal static ActivityTodDto MapTodDto(Tod tod)
    {
        return new ActivityTodDto(
            tod.Id,
            tod.LinkshellId,
            tod.MonsterName ?? "Unknown monster",
            tod.DayNumber,
            tod.Time,
            tod.Claim,
            tod.Cooldown,
            tod.RepopTime,
            tod.Interval,
            tod.ImagePath,
            tod.Hq,
            tod.AdditionalSeconds,
            tod.PopWindow,
            tod.TimeStamp);
    }

    // One row of a capture linked to a live camp. Asks the roster the same question, in the same
    // order, as the review card this camp becomes at End Camp and the ledger that pays from it.
    internal static ActivityLinkedSnapshotEntryDto MapLinkedSnapshotEntry(
        AttendanceSnapshotEntry entry, RosterNameSet roster)
        => new(
            entry.Id,
            entry.CharacterName,
            entry.MainJob,
            entry.MainJobLevel,
            entry.SubJob,
            entry.SubJobLevel,
            entry.Zone,
            entry.AddedManually,
            roster.Knows(entry.CharacterName, entry.AppUserId));
}
