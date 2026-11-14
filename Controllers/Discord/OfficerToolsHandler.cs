using System.Text.Json;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers.Discord;

// The party board's officer buttons: ➕ Add Member (with its "find a member by name" modal), Move
// Member, Set Leader, 🔒 Lock Member and Remove Member. Each step re-checks Manage parties, since
// the buttons are visible to everyone.
public sealed class OfficerToolsHandler : DiscordInteractionHandlerBase, IDiscordInteractionHandler
{
    private readonly ApplicationDbContext _db;
    private readonly DiscordEventChannelQueue _eventQueue;
    private readonly OfficerAddTargetCache _officerAddTargets;
    private readonly DiscordPermissionGates _gates;
    private readonly DiscordBoardSupport _board;
    private readonly PartyBoardHandler _partyBoard;

    public OfficerToolsHandler(
        DiscordInteractionContext interaction,
        ApplicationDbContext db,
        DiscordEventChannelQueue eventQueue,
        OfficerAddTargetCache officerAddTargets,
        DiscordPermissionGates gates,
        DiscordBoardSupport board,
        PartyBoardHandler partyBoard)
        : base(interaction)
    {
        _db = db;
        _eventQueue = eventQueue;
        _officerAddTargets = officerAddTargets;
        _gates = gates;
        _board = board;
        _partyBoard = partyBoard;
    }

    public IReadOnlyList<DiscordInteractionRoute> Routes => new[]
    {
        // "➕ Add Member (officers)" → officers only: ephemeral picker of who to seat.
        Component(DiscordEventMessageBuilder.OfficerAddButtonPrefix, nameof(HandleOfficerAddStartAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.OfficerAddButtonPrefix);
            return await HandleOfficerAddStartAsync(eventId, AppUserId, cancellationToken);
        }),

        // Officer "Move Member" — pick a participant → pick a destination slot (or bench).
        // The more-specific src/dest/bench prefixes are checked before the bare button.
        Component(DiscordEventMessageBuilder.MoveSourcePickPrefix, nameof(HandleMoveSourcePickedAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.MoveSourcePickPrefix);
            return await HandleMoveSourcePickedAsync(eventId, AppUserId, SelectedValue(Data), cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.MoveDestClaimPrefix, nameof(HandleMoveDestinationPickedAsync), async cancellationToken =>
        {
            return await HandleMoveDestinationPickedAsync(CustomId, AppUserId, SelectedSlotId(Data), cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.MoveBenchPrefix, nameof(HandleMoveBenchAsync), async cancellationToken =>
        {
            return await HandleMoveBenchAsync(CustomId, AppUserId, cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.MoveMemberButtonPrefix, nameof(HandleMoveStartAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.MoveMemberButtonPrefix);
            return await HandleMoveStartAsync(eventId, AppUserId, cancellationToken);
        }),

        // Officer "Set Leader" — pick a seated member → set their party's 👑.
        Component(DiscordEventMessageBuilder.SetLeaderPickPrefix, nameof(HandleSetLeaderPickedAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.SetLeaderPickPrefix);
            return await HandleSetLeaderPickedAsync(eventId, AppUserId, SelectedValue(Data), cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.SetLeaderButtonPrefix, nameof(HandleSetLeaderStartAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.SetLeaderButtonPrefix);
            return await HandleSetLeaderStartAsync(eventId, AppUserId, cancellationToken);
        }),

        // Officer "🔒 Lock Member" — pick a seated member (value = s:{slotId}), toggle their
        // "stay next window" lock. The pick prefix is checked first (it's the more specific
        // string, though the two don't actually overlap under StartsWith).
        Component(DiscordEventMessageBuilder.OfficerLockPickPrefix, nameof(HandleOfficerLockPickedAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.OfficerLockPickPrefix);
            return await HandleOfficerLockPickedAsync(eventId, AppUserId, SelectedValue(Data), cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.OfficerLockButtonPrefix, nameof(HandleOfficerLockStartAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.OfficerLockButtonPrefix);
            return await HandleOfficerLockStartAsync(eventId, AppUserId, cancellationToken);
        }),

        // Officer "Remove Member" — pick a participant → confirm → remove completely.
        Component(DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix, nameof(HandleWithdrawConfirmAsync), async cancellationToken =>
        {
            return await HandleWithdrawConfirmAsync(CustomId, AppUserId, cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.WithdrawMemberPickPrefix, nameof(HandleWithdrawPickedAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.WithdrawMemberPickPrefix);
            return await HandleWithdrawPickedAsync(eventId, AppUserId, SelectedValue(Data), cancellationToken);
        }),
        Component(DiscordEventMessageBuilder.WithdrawMemberButtonPrefix, nameof(HandleWithdrawStartAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.WithdrawMemberButtonPrefix);
            return await HandleWithdrawStartAsync(eventId, AppUserId, cancellationToken);
        }),

        // Officer-add member picker → an existing member (value = AppUserId) or "add new"
        // (sentinel → name modal). The chosen target is cached, then the slot picker shows.
        Component(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, nameof(HandleOfficerAddMemberPickedAsync), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.OfficerAddMemberPickPrefix);
            return await HandleOfficerAddMemberPickedAsync(eventId, AppUserId, SelectedValue(Data), cancellationToken);
        }),

        // Officer-add slot picker select → claim the chosen slot for the cached target member.
        Component(DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix, PartyBoardStep(nameof(PartyBoardHandler.HandlePartySlotClaimAsync)), async cancellationToken =>
        {
            var eventId = ParseTrailingId(CustomId, DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix);
            return await _partyBoard.HandlePartySlotClaimAsync(
                eventId, SelectedSlotId(Data), AppUserId, asLeader: false, cancellationToken, officerAdd: true);
        }),

        // Officer-add job wizard (role → main → sub) — same shape as the self-signup
        // wizard, but each step re-enters with officerAdd:true so the final claim is
        // attributed to the cached target rather than the clicking officer.
        Component(DiscordEventMessageBuilder.OfficerAddWizardRolePrefix, PartyBoardStep(nameof(PartyBoardHandler.AdvancePartyJobWizardAsync)), async cancellationToken =>
        {
            var p = CustomId[DiscordEventMessageBuilder.OfficerAddWizardRolePrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, SelectedValue(Data), null, null, false, AppUserId, asLeader: false, cancellationToken, officerAdd: true);
        }),
        Component(DiscordEventMessageBuilder.OfficerAddWizardMainPrefix, PartyBoardStep(nameof(PartyBoardHandler.AdvancePartyJobWizardAsync)), async cancellationToken =>
        {
            var p = CustomId[DiscordEventMessageBuilder.OfficerAddWizardMainPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, role, SelectedValue(Data), null, false, AppUserId, asLeader: false, cancellationToken, officerAdd: true);
        }),
        Component(DiscordEventMessageBuilder.OfficerAddWizardSubPrefix, PartyBoardStep(nameof(PartyBoardHandler.AdvancePartyJobWizardAsync)), async cancellationToken =>
        {
            var p = CustomId[DiscordEventMessageBuilder.OfficerAddWizardSubPrefix.Length..].Split(':');
            var eventId = p.Length > 0 && int.TryParse(p[0], out var e) ? e : 0;
            var slotId = p.Length > 1 && int.TryParse(p[1], out var s) ? s : 0;
            var role = p.Length > 2 ? NormalizeWizardValue(p[2]) : null;
            var main = p.Length > 3 ? NormalizeWizardValue(p[3]) : null;
            return await _partyBoard.AdvancePartyJobWizardAsync(
                eventId, slotId, role, main, SelectedValue(Data), true, AppUserId, asLeader: false, cancellationToken, officerAdd: true);
        }),

        // Officer-only "find a member by name" modal → look the typed name up among the people
        // ALREADY on this linkshell's roster and seat the one meant. Nobody is created here: a
        // player joins the roster by signing up from the board themselves. One match goes
        // straight to the slot picker; several show a picker of just those. Re-checks the
        // officer here since a modal submit is its own request.
        Modal(DiscordEventMessageBuilder.OfficerAddFindModalPrefix, nameof(HandleOfficerAddFindModalAsync), async cancellationToken =>
            await HandleOfficerAddFindModalAsync(CustomId, Data, Interaction.DiscordUserId!, cancellationToken)),
    };

    // The officer-add slot claim and job wizard are the party board's own steps, run for the
    // cached target member.
    private static string PartyBoardStep(string method) => $"{nameof(PartyBoardHandler)}.{method} (officerAdd)";

    // The "find a member by name" modal's submit. `discordUserId` is the submitting officer,
    // already read from the payload.
    private async Task<IActionResult> HandleOfficerAddFindModalAsync(
        string customId, JsonElement data, string discordUserId, CancellationToken cancellationToken)
    {
        var eventId = ParseTrailingId(customId, DiscordEventMessageBuilder.OfficerAddFindModalPrefix);
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }

        var officerAppUserId = await _db.DiscordActivityUsers
            .Where(link => link.DiscordUserId == discordUserId && link.IdentityUserId != null)
            .Select(link => link.IdentityUserId!)
            .FirstOrDefaultAsync(cancellationToken);
        if (!await _gates.CanEditPartyBoardAsync(ev, officerAppUserId, cancellationToken))
        {
            return Ephemeral("You need the **Manage parties** permission to add members to the board.");
        }

        var typed = ExtractModalValue(data, DiscordEventMessageBuilder.OfficerAddFindNameFieldId)?.Trim();
        if (string.IsNullOrWhiteSpace(typed))
        {
            return Ephemeral("Enter a character name to look for.");
        }

        var roster = await LinkshellRosterNames.LoadCandidatesAsync(_db, ev.LinkshellId, cancellationToken);
        var matches = RosterMemberSearch.Find(roster, typed);
        if (matches.Count == 0)
        {
            return Ephemeral(
                $"Nobody on the roster matches \"{typed}\". Add Member only seats people already on the roster — a new player joins by signing up from the board themselves.");
        }
        if (matches.Count == 1)
        {
            return await SetOfficerAddTargetAsync(ev, discordUserId, matches[0].AppUserId, cancellationToken);
        }

        // Several matches: a picker of just those, alts shown so two similar names can be told
        // apart. The pick lands on the normal member-picker handler.
        var options = matches
            .Select(match => new
            {
                Name = (match.MembershipCharacterName ?? match.AccountCharacterName ?? "Member").Trim(),
                Alts = string.Join(", ", new[] { match.AltCharacterName1, match.AltCharacterName2 }
                    .Where(alt => !string.IsNullOrWhiteSpace(alt)).Select(alt => alt!.Trim())),
                match.AppUserId,
            })
            .OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .Select(match =>
            {
                var label = match.Alts.Length > 0 ? $"{match.Name} (alts: {match.Alts})" : match.Name;
                return (object)new { label = label.Length > 100 ? label[..100] : label, value = match.AppUserId };
            })
            .ToArray();
        var heading = matches.Count > 25
            ? $"{matches.Count} members match \"{typed}\" — showing 25, type more of the name to narrow it. Pick who to seat:"
            : $"{matches.Count} members match \"{typed}\" — pick who to seat:";
        return PickerResponse(
            EventHeading(ev.EventName, heading),
            SelectRow(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, eventId.ToString(), "Pick a member", options));
    }


    // ─── Officer "Add Member" ───────────────────────────────────────────────────────────

    // "➕ Add Member (officers)" → officers only: an ephemeral select of roster members who
    // aren't already on this board, plus a "find a member by name" option (opens a name modal).
    // The select is capped at Discord's 25-option limit; rosters past that are reachable via
    // the find, which only ever matches people already on the roster (RosterMemberSearch).
    private async Task<IActionResult> HandleOfficerAddStartAsync(
        int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("You need the **Manage parties** permission to add members to the board.");
        }

        // Exclude anyone already on the board (a slot signup or a no-slot attendance) so the
        // list is just people who still need seating.
        var slotUserIds = await _db.EventPartySlotSignups
            .Where(s => s.EventId == eventId && s.AppUserId != null)
            .Select(s => s.AppUserId!)
            .ToListAsync(cancellationToken);
        var attendeeUserIds = await _db.AppUserEvents
            .Where(p => p.EventId == eventId && p.AppUserId != null)
            .Select(p => p.AppUserId!)
            .ToListAsync(cancellationToken);
        var onBoard = new HashSet<string>(slotUserIds.Concat(attendeeUserIds), StringComparer.Ordinal);

        var members = await _db.AppUserLinkshells
            .Where(m => m.LinkshellId == ev.LinkshellId && m.AppUserId != null && m.CharacterName != null)
            .Select(m => new { m.AppUserId, m.CharacterName })
            .ToListAsync(cancellationToken);

        var eligible = members
            .Where(m => !onBoard.Contains(m.AppUserId!))
            .OrderBy(m => m.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var options = eligible
            .Take(24) // 25-option cap, less one reserved for "find a member by name"
            .Select(m => (object)new { label = m.CharacterName!.Trim(), value = m.AppUserId })
            .Append((object)new { label = "🔎 Find a member by name…", value = DiscordEventMessageBuilder.OfficerAddFindSentinel })
            .ToArray();

        // Say when the list is cut short. Not knowing the rest are reachable is what used to send
        // officers to type a name that then created a duplicate of someone already on the roster.
        var heading = eligible.Count > 24
            ? $"Add a member to the board — pick who to seat (showing 24 of {eligible.Count}; use 🔎 Find for anyone else):"
            : "Add a member to the board — pick who to seat:";
        return PickerResponse(
            EventHeading(ev.EventName, heading),
            SelectRow(DiscordEventMessageBuilder.OfficerAddMemberPickPrefix, eventId.ToString(), "Pick a member", options));
    }

    // Officer-add member picker select → either open the "find a member by name" modal, or
    // (a listed member) cache them as the add target and show the slot picker.
    private async Task<IActionResult> HandleOfficerAddMemberPickedAsync(
        int eventId, string? appUserId, string? value, CancellationToken cancellationToken)
    {
        if (eventId <= 0)
        {
            return Ephemeral("That event isn't recognized.");
        }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null)
        {
            return Ephemeral("That event is no longer open.");
        }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken))
        {
            return Ephemeral("You need the **Manage parties** permission to add members to the board.");
        }
        if (string.IsNullOrEmpty(Interaction.DiscordUserId))
        {
            return Ephemeral("Couldn't read your Discord account from that click.");
        }

        if (string.Equals(value, DiscordEventMessageBuilder.OfficerAddFindSentinel, StringComparison.Ordinal))
        {
            return OfficerAddFindModal(eventId);
        }

        return await SetOfficerAddTargetAsync(ev, Interaction.DiscordUserId, value, cancellationToken);
    }

    // The chosen roster member becomes the officer-add target (cached per officer + event), and
    // the slot picker shows. Shared by a pick from the member list and by the find modal.
    private async Task<IActionResult> SetOfficerAddTargetAsync(
        Event ev, string officerDiscordUserId, string? targetAppUserId, CancellationToken cancellationToken)
    {
        var membership = string.IsNullOrEmpty(targetAppUserId)
            ? null
            : await _db.AppUserLinkshells
                .FirstOrDefaultAsync(m => m.LinkshellId == ev.LinkshellId && m.AppUserId == targetAppUserId, cancellationToken);
        if (membership?.AppUserId is null)
        {
            return Ephemeral("That member isn't in this linkshell anymore.");
        }

        var targetName = membership.CharacterName?.Trim() is { Length: > 0 } cn ? cn : "Member";
        _officerAddTargets.Set(officerDiscordUserId, ev.Id,
            new OfficerAddTargetCache.Target(membership.AppUserId, targetName, membership.DiscordUserId));

        return await ShowOfficerAddSlotPickerAsync(ev, targetName, cancellationToken);
    }

    // The "find a member by name" modal: the officer types a name (main or alt, or part of
    // one). On submit (HandleModalSubmitAsync) it's looked up among the people already on the
    // roster. A single text field — Discord modals are text-only.
    private IActionResult OfficerAddFindModal(int eventId)
    {
        return Ok(new
        {
            type = ResponseModal,
            data = new
            {
                custom_id = $"{DiscordEventMessageBuilder.OfficerAddFindModalPrefix}{eventId}",
                title = "Find a member",
                components = new object[]
                {
                    new
                    {
                        type = 1, // action row
                        components = new object[]
                        {
                            new
                            {
                                type = 4, // text input
                                custom_id = DiscordEventMessageBuilder.OfficerAddFindNameFieldId,
                                label = "Character name (main or alt)",
                                style = 1, // short
                                min_length = 1,
                                max_length = ManualMemberService.MaxCharacterNameLength,
                                required = true,
                                placeholder = "e.g. Millhouse",
                            }
                        }
                    }
                }
            }
        });
    }

    // Ephemeral OPEN-slot picker for the officer-add flow, routed through the officer-add
    // claim prefix so a pick seats the cached target member. `ev` must have its party-setup
    // tree loaded.
    private async Task<IActionResult> ShowOfficerAddSlotPickerAsync(
        Event ev, string targetName, CancellationToken cancellationToken)
    {
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, ev.Id, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(
            ev.Id, ev.PartySetup!, slotSignups, asLeader: false,
            claimPrefixOverride: DiscordEventMessageBuilder.OfficerAddSlotClaimPrefix);
        if (picker.Length == 0)
        {
            return Ephemeral("Every slot is taken right now — free one up first.");
        }
        return PickerResponse(
            EventHeading(ev.EventName, $"Seat {targetName} — pick a slot:"),
            picker);
    }

    // ─── Officer "Move / Set Leader / Remove Member" ────────────────────────────────────
    //
    // All three share the participant picker (BuildMoveSourceComponents). The chosen
    // member rides in the select VALUE as a source token: "s:{slotId}" (seated),
    // "a:{appUserId}" or "d:{discordUserId}" (Also Attending). Each step re-checks the
    // officer gate (the buttons are visible to everyone). Final actions reuse the same
    // backend the web Activity uses.

    private static bool IsValidSourceToken(string? src)
        => src is not null && src.Length > 2
           && (src.StartsWith("s:", StringComparison.Ordinal)
               || src.StartsWith("a:", StringComparison.Ordinal)
               || src.StartsWith("d:", StringComparison.Ordinal));

    // Maps a source token → MoveMemberAsync's (fromSlotId, appUserId, discordUserId).
    private static (int? FromSlotId, string? AppUserId, string? DiscordUserId) MapSource(string src)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal))
        {
            return (int.TryParse(val, out var slotId) ? slotId : (int?)null, null, null);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal)) { return (null, val, null); }
        if (src.StartsWith("d:", StringComparison.Ordinal)) { return (null, null, val); }
        return (null, null, null);
    }

    // Splits "{prefix}{eventId}:{kind}:{val}[:{ai}]" → (eventId, "kind:val").
    private static (int EventId, string? Src) ParseEventAndSource(string customId, string prefix)
    {
        var parts = customId[prefix.Length..].Split(':');
        var eventId = parts.Length > 0 && int.TryParse(parts[0], out var e) ? e : 0;
        var src = parts.Length >= 3 ? $"{parts[1]}:{parts[2]}" : null;
        return (eventId, src);
    }

    // Builds the "Also Attending" picker options (no-slot AppUserEvent rows that aren't
    // currently seated), value-encoded as a:/d: source tokens.
    private async Task<List<(string Label, string Value)>> LoadAttendeeSourceOptionsAsync(
        int eventId, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, CancellationToken cancellationToken)
    {
        var seatedAppUsers = new HashSet<string>(
            slotSignups.Values.Where(s => s.AppUserId != null).Select(s => s.AppUserId!), StringComparer.Ordinal);
        var seatedDiscord = new HashSet<string>(
            slotSignups.Values.Where(s => s.DiscordUserId != null).Select(s => s.DiscordUserId!), StringComparer.Ordinal);

        var attendees = await _db.AppUserEvents
            .AsNoTracking()
            .Where(p => p.EventId == eventId)
            .Select(p => new { p.AppUserId, p.DiscordUserId, p.CharacterName, p.JobType, p.JobName, p.SubJobName })
            .ToListAsync(cancellationToken);

        var options = new List<(string Label, string Value)>();
        foreach (var a in attendees)
        {
            if (a.AppUserId != null && seatedAppUsers.Contains(a.AppUserId)) { continue; }
            if (a.AppUserId == null && a.DiscordUserId != null && seatedDiscord.Contains(a.DiscordUserId)) { continue; }
            var value = a.AppUserId != null ? $"a:{a.AppUserId}"
                : a.DiscordUserId != null ? $"d:{a.DiscordUserId}"
                : null;
            if (value is null) { continue; }
            var name = string.IsNullOrWhiteSpace(a.CharacterName) ? "Member" : a.CharacterName!.Trim();
            var role = string.IsNullOrWhiteSpace(a.JobType) ? null : a.JobType!.Trim();
            var job = string.IsNullOrWhiteSpace(a.JobName) ? null
                : (string.IsNullOrWhiteSpace(a.SubJobName) ? a.JobName!.Trim() : $"{a.JobName!.Trim()}/{a.SubJobName!.Trim()}");
            var jobs = string.Join(" - ", new[] { role, job }.Where(s => !string.IsNullOrEmpty(s)));
            options.Add((jobs.Length > 0 ? $"{name} — {jobs}" : name, value));
        }
        return options;
    }

    private async Task<string?> ResolveSourceNameAsync(int eventId, string src, CancellationToken cancellationToken)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(val, out var slotId))
        {
            return await _db.EventPartySlotSignups.AsNoTracking()
                .Where(s => s.EventId == eventId && s.PartySetupSlotId == slotId)
                .Select(s => s.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal))
        {
            return await _db.AppUserEvents.AsNoTracking()
                .Where(p => p.EventId == eventId && p.AppUserId == val)
                .Select(p => p.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        if (src.StartsWith("d:", StringComparison.Ordinal))
        {
            return await _db.AppUserEvents.AsNoTracking()
                .Where(p => p.EventId == eventId && p.DiscordUserId == val)
                .Select(p => p.CharacterName).FirstOrDefaultAsync(cancellationToken);
        }
        return null;
    }

    // The (appUserId, discordUserId) for a source token, used for a complete removal.
    // For a seated source we read the slot signup so BOTH identity columns are passed
    // (a placeholder-matched slot carries an AppUserId AND a DiscordUserId). Null when
    // the seated source row is gone.
    private async Task<(string? AppUserId, string? DiscordUserId)?> ResolveSourceIdentityAsync(
        int eventId, string src, CancellationToken cancellationToken)
    {
        var val = src.Length > 2 ? src[2..] : string.Empty;
        if (src.StartsWith("s:", StringComparison.Ordinal) && int.TryParse(val, out var slotId))
        {
            var row = await _db.EventPartySlotSignups.AsNoTracking()
                .Where(s => s.EventId == eventId && s.PartySetupSlotId == slotId)
                .Select(s => new { s.AppUserId, s.DiscordUserId })
                .FirstOrDefaultAsync(cancellationToken);
            return row is null ? null : (row.AppUserId, row.DiscordUserId);
        }
        if (src.StartsWith("a:", StringComparison.Ordinal)) { return (val, null); }
        if (src.StartsWith("d:", StringComparison.Ordinal)) { return (null, val); }
        return null;
    }

    // Loads the event (with party setup) and verifies the clicker may edit the party board.
    // Returns the event on success, or an ephemeral error result in `error`.
    private async Task<Event?> LoadOfficerEventAsync(
        int eventId, string? appUserId, string deniedMessage, CancellationToken cancellationToken,
        Func<string, IActionResult> ephemeral, Action<IActionResult> setError)
    {
        if (eventId <= 0) { setError(ephemeral("That event isn't recognized.")); return null; }
        var ev = await _board.LoadEventWithSetupAsync(eventId, cancellationToken);
        if (ev is null || ev.PartySetup is null) { setError(ephemeral("That event is no longer open.")); return null; }
        if (!await _gates.CanEditPartyBoardAsync(ev, appUserId, cancellationToken)) { setError(ephemeral(deniedMessage)); return null; }
        return ev;
    }

    // ── Move ──
    private async Task<IActionResult> HandleMoveStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var attendees = await LoadAttendeeSourceOptionsAsync(eventId, slotSignups, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, attendees,
            DiscordEventMessageBuilder.MoveSourcePickPrefix, seatedOnly: false);
        if (picker.Length == 0) { return Ephemeral("Nobody's on the board to move yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Move a member — pick who to move:"), picker);
    }

    private async Task<IActionResult> HandleMoveSourcePickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildSlotPickerComponents(
            eventId, ev.PartySetup!, slotSignups, asLeader: false,
            claimPrefixOverride: DiscordEventMessageBuilder.MoveDestClaimPrefix,
            idSuffixOverride: $":{src}");

        var rows = new List<object>(picker);
        var seated = src!.StartsWith("s:", StringComparison.Ordinal);
        if (seated && rows.Count < 5)
        {
            rows.Add(new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 2,
                        label = "⬇ Bench → Also Attending",
                        custom_id = $"{DiscordEventMessageBuilder.MoveBenchPrefix}{eventId}:{src}",
                    },
                },
            });
        }
        if (rows.Count == 0) { return Ephemeral("Every slot is taken and there's nothing to bench."); }
        return PickerResponse(
            EventHeading(ev.EventName, "Pick a destination slot" + (seated ? ", or bench:" : ":")),
            rows.ToArray());
    }

    private async Task<IActionResult> HandleMoveDestinationPickedAsync(string customId, string? appUserId, int slotId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.MoveDestClaimPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (slotId <= 0 || !IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var (fromSlotId, mAppUser, mDiscord) = MapSource(src!);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: slotId, mAppUser, mDiscord, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    private async Task<IActionResult> HandleMoveBenchAsync(string customId, string? appUserId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.MoveBenchPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to move members on the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)) { return Ephemeral("Only a seated member can be benched."); }

        var (fromSlotId, _, _) = MapSource(src);
        var result = await EventPartyBoardEditService.MoveMemberAsync(
            _db, eventId, fromSlotId, toSlotId: null, null, null, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // ── Set Leader ──
    private async Task<IActionResult> HandleSetLeaderStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to set the party leader.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.SetLeaderPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return Ephemeral("Nobody's in a slot to make leader yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Set the 👑 party leader — pick a member:"), picker);
    }

    private async Task<IActionResult> HandleSetLeaderPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to set the party leader.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)
            || !int.TryParse(src[2..], out var slotId) || slotId <= 0)
        {
            return Ephemeral("That selection isn't recognized.");
        }

        var result = await EventPartySignupService.SetPartyLeaderBySlotAsync(_db, eventId, slotId, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }

    // ── Lock Member (stay next window) ──
    // Officers pin a member's slot so it survives the window-turnover wipe. Same shape as
    // Set Leader: button → seated-member picker → the select toggles the chosen slot's lock.
    private async Task<IActionResult> HandleOfficerLockStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to lock members.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.OfficerLockPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return Ephemeral("Nobody's in a slot to lock yet."); }
        return PickerResponse(EventHeading(ev.EventName, "🔒 Lock a member for next window — pick who stays:"), picker);
    }

    private async Task<IActionResult> HandleOfficerLockPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to lock members.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (src is null || !src.StartsWith("s:", StringComparison.Ordinal)
            || !int.TryParse(src[2..], out var slotId) || slotId <= 0)
        {
            return Ephemeral("That selection isn't recognized.");
        }

        var result = await EventPartySignupService.SetStayNextWindowBySlotAsync(_db, eventId, slotId, cancellationToken);
        if (!result.Success) { return WizardStep($"⚠️ {result.Error}", Array.Empty<object>()); }
        await _db.SaveChangesAsync(cancellationToken);
        _eventQueue.Enqueue(eventId);

        // Re-render the picker so an officer can lock/unlock several members in one sitting;
        // the board itself (with the 🔒 marks + count) refreshes via the queue above.
        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, Array.Empty<(string Label, string Value)>(),
            DiscordEventMessageBuilder.OfficerLockPickPrefix, seatedOnly: true);
        if (picker.Length == 0) { return _board.DismissPickerSilently(); }
        var who = string.IsNullOrWhiteSpace(result.Name) ? "that member" : result.Name!.Trim();
        var verb = result.Locked ? $"🔒 Locked **{who}** for next window" : $"🔓 Unlocked **{who}**";
        return PickerResponse(EventHeading(ev.EventName, $"{verb} — pick another to toggle, or dismiss:"), picker);
    }

    // ── Remove Member ──
    private async Task<IActionResult> HandleWithdrawStartAsync(int eventId, string? appUserId, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }

        var slotSignups = await EventPartySignupService.GetSignupsForEventAsync(_db, eventId, cancellationToken);
        var attendees = await LoadAttendeeSourceOptionsAsync(eventId, slotSignups, cancellationToken);
        var picker = DiscordEventMessageBuilder.BuildMoveSourceComponents(
            eventId, ev.PartySetup!, slotSignups, attendees,
            DiscordEventMessageBuilder.WithdrawMemberPickPrefix, seatedOnly: false);
        if (picker.Length == 0) { return Ephemeral("Nobody's on the board to remove yet."); }
        return PickerResponse(EventHeading(ev.EventName, "Remove a member — pick who to remove:"), picker);
    }

    private async Task<IActionResult> HandleWithdrawPickedAsync(int eventId, string? appUserId, string? src, CancellationToken cancellationToken)
    {
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var name = await ResolveSourceNameAsync(eventId, src!, cancellationToken);
        var who = string.IsNullOrWhiteSpace(name) ? "this member" : name!.Trim();
        var confirm = new object[]
        {
            new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 2, style = 4, // danger
                        label = "Remove from event",
                        custom_id = $"{DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix}{eventId}:{src}",
                    },
                },
            },
        };
        return PickerResponse(
            EventHeading(ev.EventName, $"Remove **{who}** from the event entirely? This frees their slot and drops their attendance/DKP."),
            confirm);
    }

    private async Task<IActionResult> HandleWithdrawConfirmAsync(string customId, string? appUserId, CancellationToken cancellationToken)
    {
        var (eventId, src) = ParseEventAndSource(customId, DiscordEventMessageBuilder.WithdrawMemberConfirmPrefix);
        IActionResult? err = null;
        var ev = await LoadOfficerEventAsync(eventId, appUserId, "You need the **Manage parties** permission to remove members from the board.",
            cancellationToken, Ephemeral, e => err = e);
        if (ev is null) { return err!; }
        if (!IsValidSourceToken(src)) { return Ephemeral("That selection isn't recognized."); }

        var identity = await ResolveSourceIdentityAsync(eventId, src!, cancellationToken);
        if (identity is null) { return WizardStep("⚠️ That member is no longer on the board.", Array.Empty<object>()); }
        var (mAppUser, mDiscord) = identity.Value;
        await EventPartySignupService.RemoveMemberCompletelyAsync(_db, eventId, mAppUser, mDiscord, cancellationToken);
        _eventQueue.Enqueue(eventId);
        return _board.DismissPickerSilently();
    }
}
