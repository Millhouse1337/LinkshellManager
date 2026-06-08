using System.Text;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Services;

// One person signed up to an event (for rendering the Discord roster).
public sealed record EventSignupLine(string CharacterName, string? JobName);

// Builds the Discord message payload for an event announcement: an embed with the
// event details + current signup roster, a job-pick select menu, and a Withdraw
// button. The same payload shape is used both for the initial bot post
// (POST /channels/{id}/messages) and for the interaction UPDATE_MESSAGE response
// (type 7) that refreshes it after a click, so the message edits itself in place.
public static class DiscordEventMessageBuilder
{
    private const int EmbedColor = 0x5865F2; // Blurple.

    // custom_id prefixes routed by DiscordInteractionsController.
    public const string JobSelectPrefix = "evt:job:";
    public const string WithdrawPrefix = "evt:withdraw:";
    // Party-setup board interactions:
    //   evt:pssignup:{eventId}        — board button → ephemeral slot picker
    //   evt:psclaim:{eventId}         — picker select (value = slotId) → claim/wizard
    //   evt:psleave:{eventId}         — board button → leave my slot
    // Job-pick wizard — ephemeral selects shown (one at a time) when the chosen
    // slot doesn't pin the role/job; each custom_id carries the picks so far:
    //   evt:pswr:{eventId}:{slotId}                 — role select
    //   evt:pswm:{eventId}:{slotId}:{role}          — main-job select
    //   evt:psws:{eventId}:{slotId}:{role}:{main}   — sub-job select
    public const string PartySlotSignUpPrefix = "evt:pssignup:";
    public const string PartySlotClaimPrefix = "evt:psclaim:";
    public const string PartySlotLeavePrefix = "evt:psleave:";
    // General attendance on a party-board event: join the roster WITHOUT claiming
    // a slot (overflow / "I'm coming"). Backed by AppUserEvent, same as the
    // attendance roster used for DKP at close.
    public const string PartyJoinEventPrefix = "evt:joinevent:";
    public const string PartyWizardRolePrefix = "evt:pswr:";
    public const string PartyWizardMainPrefix = "evt:pswm:";
    public const string PartyWizardSubPrefix = "evt:psws:";
    public const string PartyWizardNoSub = "__nosub__";

    // "Join (no slot)" job-pick wizard (role → main → sub). Same shape as the
    // slot wizard prefixes, but there's no slot to claim — the picks become a
    // general-attendance AppUserEvent. Role can be "no role"; the main job is
    // still required so an attendee always says what they're coming as.
    public const string PartyJoinWizardRolePrefix = "evt:gjwr:";
    public const string PartyJoinWizardMainPrefix = "evt:gjwm:";
    public const string PartyJoinWizardSubPrefix = "evt:gjws:";
    public const string PartyWizardNoRole = "__norole__";

    // Leader-path variants of the sign-up flow. Identical to the prefixes above
    // except they additionally mark the claimed slot as that party's leader
    // (first-claim-wins). Separate prefixes keep the leader intent flowing through
    // the picker → claim → job-pick wizard without re-encoding the tail:
    //   evt:psLsignup:{eventId}   — board "Sign up as leader" button
    //   evt:psLclaim:{eventId}    — leader slot picker select
    //   evt:psLwr/psLwm/psLws:... — leader job-pick wizard steps (same tail format)
    public const string PartySlotLeaderSignUpPrefix = "evt:psLsignup:";
    public const string PartySlotClaimLeaderPrefix = "evt:psLclaim:";
    public const string PartyWizardLeaderRolePrefix = "evt:psLwr:";
    public const string PartyWizardLeaderMainPrefix = "evt:psLwm:";
    public const string PartyWizardLeaderSubPrefix = "evt:psLws:";

    // When the event has a linked party setup, the message shows the board
    // (parties -> slots -> who's in each) + a single "Sign Up" button (which opens
    // an ephemeral slot picker) + "Leave my slot"; otherwise it's the ad-hoc
    // job-pick select + Withdraw. `partySetup` must be loaded with
    // Alliances -> Parties -> Slots; `slotSignups` are the per-EVENT signups keyed
    // by slot id (so one event's roster never bleeds onto another).
    public static object Build(
        Event ev,
        IReadOnlyList<EventSignupLine> signups,
        PartySetup? partySetup = null,
        IReadOnlyDictionary<int, EventPartySlotSignup>? slotSignups = null)
    {
        if (partySetup is not null)
        {
            var signupsBySlot = slotSignups ?? new Dictionary<int, EventPartySlotSignup>();
            return new
            {
                embeds = new[] { BuildBoardEmbed(ev, partySetup, signupsBySlot, signups) },
                components = BuildBoardComponents(ev.Id),
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
        }

        return new
        {
            embeds = new[] { BuildEmbed(ev, signups) },
            components = BuildComponents(ev.Id),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Action rows for the ephemeral "pick a slot" message shown when someone hits
    // Sign Up: a select of the OPEN slots (≤25; Discord's select cap). Returns an
    // empty array when nothing is open (caller shows a "full" notice instead).
    public static object[] BuildSlotPickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        bool asLeader = false)
    {
        var options = new List<object>();
        foreach (var (party, label) in LabeledParties(setup))
        {
            // For the leader flow, only offer slots in parties that don't already
            // have a leader (first-claim-wins). A full party always has a leader
            // (auto-promoted on fill), so leaderless parties always have open slots.
            if (asLeader && party.Slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader))
            {
                continue;
            }
            foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
            {
                if (slotSignups.ContainsKey(slot.Id))
                {
                    continue;
                }
                if (options.Count >= 25)
                {
                    break;
                }
                options.Add(new
                {
                    label = Truncate($"{label}: {SlotShortLabel(slot)}", 100),
                    value = slot.Id.ToString(),
                    emoji = new { name = RoleIcon(slot, null) },
                });
            }
            if (options.Count >= 25)
            {
                break;
            }
        }

        if (options.Count == 0)
        {
            return Array.Empty<object>();
        }

        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{(asLeader ? PartySlotClaimLeaderPrefix : PartySlotClaimPrefix)}{eventId}",
                        placeholder = asLeader ? "Pick a slot to lead" : "Pick a slot to claim",
                        min_values = 1,
                        max_values = 1,
                        options = options.ToArray(),
                    },
                },
            },
        };
    }

    private static object BuildEmbed(Event ev, IReadOnlyList<EventSignupLine> signups)
    {
        var fields = new List<object>();
        if (ev.CommencementStartTime is { } commenced)
        {
            fields.Add(new { name = "Started", value = TimestampMarkup(commenced), inline = true });
        }
        else if (ev.StartTime is { } start)
        {
            fields.Add(new { name = "Starts", value = TimestampMarkup(start), inline = true });
        }
        if (ev.DkpPerHour is { } dkpPerHour)
        {
            fields.Add(new { name = "DKP / hour", value = dkpPerHour.ToString(), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(ev.EventLocation))
        {
            fields.Add(new { name = "Location", value = Escape(ev.EventLocation!.Trim()), inline = true });
        }

        fields.Add(new
        {
            name = $"Signed up ({signups.Count})",
            value = BuildRoster(signups),
            inline = false,
        });

        var typePrefix = string.IsNullOrWhiteSpace(ev.EventType) ? string.Empty : $"{ev.EventType!.Trim()}: ";
        var title = Truncate($"⚔️ {typePrefix}{ev.EventName ?? $"Event #{ev.Id}"}", 250);

        return new
        {
            title,
            description = string.IsNullOrWhiteSpace(ev.Details) ? null : Truncate(Escape(ev.Details!.Trim()), 1500),
            color = EmbedColor,
            fields = fields.ToArray(),
            footer = new { text = "Pick your job below to sign up · Withdraw to drop out" },
        };
    }

    private static string BuildRoster(IReadOnlyList<EventSignupLine> signups)
    {
        if (signups.Count == 0)
        {
            return "_No one yet — be the first!_";
        }

        var sb = new StringBuilder();
        var shown = 0;
        foreach (var signup in signups)
        {
            var job = string.IsNullOrWhiteSpace(signup.JobName) ? "—" : signup.JobName!.Trim();
            var line = $"• **{Escape(signup.CharacterName)}** — {Escape(job)}";
            // Embed field values cap at 1024 chars; leave room for an overflow note.
            if (sb.Length + line.Length + 1 > 950)
            {
                sb.Append($"\n…and {signups.Count - shown} more");
                break;
            }
            if (sb.Length > 0) { sb.Append('\n'); }
            sb.Append(line);
            shown++;
        }
        return sb.ToString();
    }

    private static object[] BuildComponents(int eventId)
    {
        var options = EventJobCatalog.MainJobOptions
            .Select(job => new { label = job, value = job })
            .ToArray<object>();

        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{JobSelectPrefix}{eventId}",
                        placeholder = "Pick your job to sign up",
                        min_values = 1,
                        max_values = 1,
                        options,
                    },
                },
            },
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary
                        label = "Withdraw",
                        custom_id = $"{WithdrawPrefix}{eventId}",
                    },
                },
            },
        };
    }

    // Event detail fields for the board embed. Full-width (not inline) so long
    // values like the start timestamp don't wrap in a narrow 1/3-width column.
    private static List<object> BuildEventDetailFields(Event ev)
    {
        var fields = new List<object>();
        if (ev.CommencementStartTime is { } commenced)
        {
            fields.Add(new { name = "Started", value = TimestampMarkup(commenced), inline = false });
        }
        else if (ev.StartTime is { } start)
        {
            fields.Add(new { name = "Starts", value = TimestampMarkup(start), inline = false });
        }
        if (ev.DkpPerHour is { } dkpPerHour)
        {
            fields.Add(new { name = "DKP / hour", value = dkpPerHour.ToString(), inline = true });
        }
        if (!string.IsNullOrWhiteSpace(ev.EventLocation))
        {
            fields.Add(new { name = "Location", value = Escape(ev.EventLocation!.Trim()), inline = true });
        }
        return fields;
    }

    // Board embed: event details + one inline field per party listing each slot
    // with a role-colored dot — claimed slots show the member + jobs (from the
    // per-EVENT signup), open slots show the requirement. Inline fields lay the
    // parties out side-by-side, echoing the in-app board.
    private static object BuildBoardEmbed(
        Event ev, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<EventSignupLine> generalSignups)
    {
        var fields = BuildEventDetailFields(ev);

        foreach (var (party, label) in LabeledParties(setup))
        {
            // Embeds cap at 25 fields; leave a little headroom.
            if (fields.Count >= 24)
            {
                break;
            }

            var slots = party.Slots.OrderBy(s => s.SortOrder).ToList();
            var filled = slots.Count(s => slotSignups.ContainsKey(s.Id));
            var sb = new StringBuilder();
            foreach (var slot in slots)
            {
                slotSignups.TryGetValue(slot.Id, out var signup);
                var icon = RoleIcon(slot, signup);
                // Crown the actual party leader (the per-event signup flagged as
                // leader), not the template's designated-leader slot.
                var crown = (signup?.IsPartyLeader ?? false) ? "👑 " : string.Empty;
                string line;
                if (signup is not null)
                {
                    var jobs = SignedUpJobs(signup);
                    line = $"{icon} {crown}**{Escape(signup.CharacterName ?? "Member")}**"
                         + (string.IsNullOrEmpty(jobs) ? string.Empty : $" — {Escape(jobs)}");
                }
                else
                {
                    line = $"{icon} {crown}{Escape(SlotRequirement(slot))}";
                }
                if (sb.Length > 0) { sb.Append('\n'); }
                sb.Append(line);
            }
            if (sb.Length == 0) { sb.Append("_No slots_"); }

            // Full-width (not inline) so member names + jobs get the whole embed
            // width and don't wrap. Parties stack vertically; a Discord embed has
            // a fixed width, so wider lines means fewer columns.
            fields.Add(new
            {
                name = Truncate($"{label} ({filled}/{slots.Count})", 250),
                value = Truncate(sb.ToString(), 1024),
                inline = false,
            });
        }

        // General attendees (the AppUserEvent roster) who joined WITHOUT claiming a
        // party slot — shown so "Join (no slot)" overflow is visible on the board.
        // Slot-holders are excluded (they already appear in their party above).
        var slotNames = new HashSet<string>(
            slotSignups.Values
                .Where(s => !string.IsNullOrWhiteSpace(s.CharacterName))
                .Select(s => s.CharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var extra = generalSignups
            .Where(g => !string.IsNullOrWhiteSpace(g.CharacterName) && !slotNames.Contains(g.CharacterName.Trim()))
            .ToList();
        if (extra.Count > 0 && fields.Count < 25)
        {
            var sb = new StringBuilder();
            foreach (var g in extra)
            {
                if (sb.Length > 0) { sb.Append('\n'); }
                var jobs = string.IsNullOrWhiteSpace(g.JobName) ? string.Empty : $" — {Escape(g.JobName!)}";
                sb.Append($"• {Escape(g.CharacterName)}{jobs}");
            }
            fields.Add(new
            {
                name = Truncate($"Also attending — no slot ({extra.Count})", 250),
                value = Truncate(sb.ToString(), 1024),
                inline = false,
            });
        }

        var typePrefix = string.IsNullOrWhiteSpace(ev.EventType) ? string.Empty : $"{ev.EventType!.Trim()}: ";
        var title = Truncate($"⚔️ {typePrefix}{ev.EventName ?? $"Event #{ev.Id}"}", 250);

        return new
        {
            title,
            description = string.IsNullOrWhiteSpace(ev.Details) ? null : Truncate(Escape(ev.Details!.Trim()), 1500),
            color = EmbedColor,
            fields = fields.ToArray(),
            footer = new { text = "Sign Up to claim a slot · Join (no slot) for attendance · Leave event to drop out" },
        };
    }

    // Board components: a single "Sign Up" button (opens the ephemeral slot
    // picker) + "Leave my slot". Keeps the message clean regardless of setup size.
    private static object[] BuildBoardComponents(int eventId)
    {
        return new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 2, // button
                        style = 1, // primary
                        label = "Sign Up",
                        custom_id = $"{PartySlotSignUpPrefix}{eventId}",
                    },
                    new
                    {
                        type = 2, // button
                        style = 3, // success (green) — stands out as the leader option
                        label = "👑 Sign up as leader",
                        custom_id = $"{PartySlotLeaderSignUpPrefix}{eventId}",
                    },
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary — general attendance, no party slot
                        label = "Join (no slot)",
                        custom_id = $"{PartyJoinEventPrefix}{eventId}",
                    },
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary — drops both the slot AND general attendance
                        label = "Leave event",
                        custom_id = $"{PartySlotLeavePrefix}{eventId}",
                    },
                },
            },
        };
    }

    // Flattens the setup to its parties in board order, each with a display label
    // ("Party 1", a custom name, or "A2 · {name}" when there's more than one
    // alliance). Used by both the embed fields and the slot buttons so they line
    // up by the same party label.
    private static IEnumerable<(PartySetupParty Party, string Label)> LabeledParties(PartySetup setup)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        for (var ai = 0; ai < alliances.Count; ai++)
        {
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            for (var pi = 0; pi < parties.Count; pi++)
            {
                var name = string.IsNullOrWhiteSpace(parties[pi].Name) ? $"Party {pi + 1}" : parties[pi].Name!;
                yield return (parties[pi], multiAlliance ? $"A{ai + 1} · {name}" : name);
            }
        }
    }

    // A role-colored dot for a slot: the signed-up role when filled, else the
    // pinned role; job/any slots get a neutral dot. Used in both the embed lines
    // and the picker option emoji.
    private static string RoleIcon(PartySetupSlot slot, EventPartySlotSignup? signup)
    {
        var role = signup is not null && !string.IsNullOrWhiteSpace(signup.Role)
            ? signup.Role
            : slot.Role;
        return role?.Trim().ToLowerInvariant() switch
        {
            "tank" => "🔵",
            "heal" => "🟢",
            "healer" => "🟢",
            "support" => "🟡",
            "dps" => "🔴",
            _ => "⚪",
        };
    }

    // Compact slot requirement for a button: the role ("Tank"), the job
    // ("WAR" / "WAR/NIN"), or "Any" — without the "Any " prefix the embed uses.
    private static string SlotShortLabel(PartySetupSlot slot)
    {
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(slot.Role) ? "Any" : slot.Role!;
        }
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(slot.MainJob)
                ? "Any"
                : (string.IsNullOrWhiteSpace(slot.SubJob) ? slot.MainJob! : $"{slot.MainJob}/{slot.SubJob}");
        }
        return "Any";
    }

    // Mirrors the in-app slot requirement label (Any Role / Any {role} /
    // {main}[/{sub}]).
    public static string SlotRequirement(PartySetupSlot slot)
    {
        var label = string.IsNullOrWhiteSpace(slot.Label) ? string.Empty : $" ({slot.Label})";
        string core;
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            core = string.IsNullOrWhiteSpace(slot.Role) ? "Any Role" : $"Any {slot.Role}";
        }
        else if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
        {
            core = string.IsNullOrWhiteSpace(slot.MainJob)
                ? "Any job"
                : (string.IsNullOrWhiteSpace(slot.SubJob) ? slot.MainJob! : $"{slot.MainJob}/{slot.SubJob}");
        }
        else
        {
            core = "Any Role";
        }
        return core + label;
    }

    private static string SignedUpJobs(EventPartySlotSignup signup)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(signup.Role)) { parts.Add(signup.Role!); }
        if (!string.IsNullOrWhiteSpace(signup.MainJob))
        {
            parts.Add(string.IsNullOrWhiteSpace(signup.SubJob)
                ? signup.MainJob!
                : $"{signup.MainJob}/{signup.SubJob}");
        }
        return string.Join(" - ", parts);
    }

    private static string TimestampMarkup(DateTime utc)
    {
        var unix = ((DateTimeOffset)DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return $"<t:{unix}:f> (<t:{unix}:R>)";
    }

    private static string Escape(string value) => value
        .Replace("\\", "\\\\").Replace("`", "\\`").Replace("*", "\\*")
        .Replace("_", "\\_").Replace("~", "\\~").Replace("|", "\\|");

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "…";
}
