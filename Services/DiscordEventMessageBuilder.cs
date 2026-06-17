using System.Text;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;

namespace LinkshellManagerDiscordApp.Services;

// One person signed up to an event (for rendering the Discord roster). For
// no-slot attendees JobName is the main job, SubJobName the sub, JobType the role.
public sealed record EventSignupLine(
    string CharacterName, string? JobName, string? SubJobName = null, string? JobType = null);

// Builds the Discord message payloads for an event announcement.
//
// The PARTY board is posted as a wide, readable EMBED (parties as fields) with the
// rendered PNG (EventBoardHtmlBuilder + EventBoardImageRenderer) shown INSIDE it via
// BuildBoardImageEmbedMessage — the embed fills the message column (a bare image
// attachment is capped narrow by Discord) and still carries the themed visual. When
// the image renderer is unavailable, Build() is the classic embed-only fallback so
// events always post. Both are classic messages (no Components V2 flag) so the board
// can be edited between the image-embed and the plain embed without Discord rejecting
// a flag toggle.
//
// The same payload shape is also used for the interaction UPDATE_MESSAGE response
// (type 7) and for bot edits, so the message refreshes in place after a click.
public static class DiscordEventMessageBuilder
{
    private const int EmbedColor = 0x5865F2; // Blurple.

    // custom_id prefixes routed by DiscordInteractionsController.
    public const string JobSelectPrefix = "evt:job:";
    public const string WithdrawPrefix = "evt:withdraw:";
    // Party-setup board interactions:
    //   evt:pssignup:{eventId}        — "Sign Up" button → ephemeral slot picker
    //   evt:psLsignup:{eventId}       — "Sign Up as Party Leader" → leader slot picker
    //   evt:psclaim:{eventId}         — picker select (value = slotId) → claim
    //   evt:psLclaim:{eventId}        — leader picker select → claim as leader
    //   evt:psleave:{eventId}         — "Withdraw" button → leave my slot + attendance
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

    // Leader-path variants of the job-pick wizard. Identical to the prefixes above
    // except they additionally mark the claimed slot as that party's leader
    // (first-claim-wins). Separate prefixes keep the leader intent flowing through
    // the job-pick wizard without re-encoding the tail.
    public const string PartySlotLeaderSignUpPrefix = "evt:psLsignup:";
    public const string PartySlotClaimLeaderPrefix = "evt:psLclaim:";
    public const string PartyWizardLeaderRolePrefix = "evt:psLwr:";
    public const string PartyWizardLeaderMainPrefix = "evt:psLwm:";
    public const string PartyWizardLeaderSubPrefix = "evt:psLws:";

    // Classic-embed payload: the fallback for the party board (when the image
    // renderer is unavailable) and the message for ad-hoc (no party setup) events.
    // `attachments` is cleared so an edit from the image board drops its PNG.
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
                content = BuildStartHeading(ev),
                embeds = new[] { BuildBoardEmbed(ev, partySetup, signupsBySlot, signups) },
                components = BuildBoardComponents(ev.Id),
                attachments = Array.Empty<object>(),
                allowed_mentions = new { parse = Array.Empty<string>() },
            };
        }

        return new
        {
            content = BuildStartHeading(ev),
            embeds = new[] { BuildEmbed(ev, signups) },
            components = BuildComponents(ev.Id),
            attachments = Array.Empty<object>(),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // The party board as a wide, readable EMBED (the parties listed as fields, the
    // event details, the localized start time) WITH the rendered PNG shown inside it
    // (image: attachment://file). The embed frame fills the message column — far
    // larger than a bare image attachment, which Discord caps narrow — so the board
    // reads big AND carries the themed visual. `attachments:[{id:0,...}]` references
    // the file uploaded as files[0]; the board buttons sit below.
    public static object BuildBoardImageEmbedMessage(
        Event ev, IReadOnlyList<EventSignupLine> signups, PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, string fileName)
    {
        return new
        {
            content = BuildStartHeading(ev),
            embeds = new[] { BuildBoardEmbed(ev, setup, slotSignups, signups, fileName) },
            components = BuildBoardComponents(ev.Id),
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Action rows for the ephemeral "pick a slot" message shown when someone hits
    // Sign Up. Returns an empty array when nothing is open (caller shows a "full"
    // notice instead).
    //
    // Discord caps a string select at 25 options AND a message at 5 action rows. A
    // board can hold far more than 25 open slots (e.g. Sky = 3 alliances × 18), so a
    // single flat select would silently drop every open slot past #25 — in practice
    // the whole last alliance. So we give EACH alliance its own select (≤25 options),
    // up to Discord's 5-row limit, so every alliance's open slots stay reachable.
    // Each select needs a custom_id unique within the message (Discord requires it),
    // so the alliance index rides after the event id; the claim handler parses the
    // leading id and ignores that suffix.
    public static object[] BuildSlotPickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        bool asLeader = false)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        var claimPrefix = asLeader ? PartySlotClaimLeaderPrefix : PartySlotClaimPrefix;
        var action = asLeader ? "to lead" : "to claim";

        var rows = new List<object>();
        for (var ai = 0; ai < alliances.Count && rows.Count < 5; ai++)
        {
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            var options = new List<object>();
            for (var pi = 0; pi < parties.Count && options.Count < 25; pi++)
            {
                var party = parties[pi];
                // Leader flow: only offer slots in parties without a leader yet
                // (first-claim-wins). A full party always has a leader (auto-promoted
                // on fill), so leaderless parties always have open slots.
                if (asLeader && party.Slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader))
                {
                    continue;
                }
                var partyName = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!.Trim();
                foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
                {
                    if (slotSignups.ContainsKey(slot.Id) || options.Count >= 25)
                    {
                        continue;
                    }
                    options.Add(new
                    {
                        label = Truncate($"{partyName}: {SlotShortLabel(slot)}", 100),
                        value = slot.Id.ToString(),
                        emoji = new { name = RoleIcon(slot, null) },
                    });
                }
            }

            if (options.Count == 0)
            {
                continue;
            }

            var allianceName = string.IsNullOrWhiteSpace(alliances[ai].Name)
                ? $"Alliance {ai + 1}"
                : alliances[ai].Name!.Trim();
            var placeholder = multiAlliance
                ? Truncate($"{allianceName} — pick a slot {action}", 150)
                : $"Pick a slot {action}";

            rows.Add(new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        // Suffix the alliance index so each select is unique in the
                        // message; ParseTrailingId reads just the leading event id.
                        custom_id = multiAlliance ? $"{claimPrefix}{eventId}:{ai}" : $"{claimPrefix}{eventId}",
                        placeholder,
                        min_values = 1,
                        max_values = 1,
                        options = options.ToArray(),
                    },
                },
            });
        }

        return rows.ToArray();
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

    // Event detail fields for the board embed. The start time is NOT here — it's
    // the larger `##` heading in the message content above the embed
    // (BuildStartHeading), since embed-field text can't be enlarged.
    private static List<object> BuildEventDetailFields(Event ev)
    {
        var fields = new List<object>();
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

    // The start time as a `##` heading in the message content (above the embed) so
    // it reads a little larger than embed-field text — the only way to enlarge text
    // in a Discord message. Uses Discord timestamp markup so it renders in each
    // viewer's own timezone. Empty when the event has no scheduled time. (Discord
    // has no way to center message text, so this sits left-aligned at the top.)
    private static string BuildStartHeading(Event ev)
    {
        var when = ev.CommencementStartTime ?? ev.StartTime;
        if (when is null)
        {
            return string.Empty;
        }
        var unix = ((DateTimeOffset)DateTime.SpecifyKind(when.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var label = ev.CommencementStartTime is not null ? "Started" : "Starts";
        return $"## 🕒 {label}: <t:{unix}:f> · <t:{unix}:R>";
    }

    // Board embed (the fallback when the image renderer is unavailable): event
    // details + one field per party listing each slot with a role-colored dot —
    // claimed slots show the member + jobs, open slots show the requirement.
    private static object BuildBoardEmbed(
        Event ev, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<EventSignupLine> generalSignups, string? imageFileName = null)
    {
        var fields = BuildEventDetailFields(ev);

        // Discord lays inline fields out up to 3-across. Each alliance is a
        // full-width "Alliance N" header (so the grouping reads as one title)
        // followed by its parties as inline fields sitting side by side. The
        // header also breaks the row so the first party never packs onto the
        // details row. A single alliance gets no header — just a blank divider
        // before its inline parties; a lone party stays full-width.
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        var totalParties = alliances.Sum(a => a.Parties.Count);
        var partiesInline = totalParties > 1;

        // The per-party text columns ALWAYS render (alongside the image, if any).
        // Discord embed fields can't be told not to wrap, so the lines are kept as
        // short as possible (role dropped — the colored dot conveys it — and the long
        // "Player's Choice" sub abbreviated to "PC") to minimize the ⅓-width wrapping.
        if (partiesInline && !multiAlliance)
        {
            // Zero-width space (U+200B) — a thin full-width divider that breaks the row
            // so single-alliance inline parties don't pack onto the details row. (The
            // embed's max-width is forced on the title line, not here, so this stays a
            // minimal divider with no empty band.)
            fields.Add(new { name = "​", value = "​", inline = false });
        }

        for (var ai = 0; ai < alliances.Count && fields.Count < 24; ai++)
        {
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            if (parties.Count == 0)
            {
                continue;
            }

            if (multiAlliance)
            {
                var allianceName = string.IsNullOrWhiteSpace(alliances[ai].Name)
                    ? $"Alliance {ai + 1}"
                    : alliances[ai].Name!.Trim();
                // Discord embeds can't center field text, so frame the alliance label
                // with box-drawing dashes — it reads as a tidy header divider rather
                // than a bare left-aligned word. Kept short so it doesn't wrap on narrow
                // mobile widths. (The rendered image board centers headers via CSS.)
                var allianceHeader = $"────── {allianceName} ──────";
                fields.Add(new { name = Truncate(allianceHeader, 250), value = "​", inline = false });
            }

            // How many party columns share this row (Discord packs inline fields up to
            // 3-across; the alliance header / divider breaks the row so an alliance's
            // parties pack among themselves). Drives how hard a long name is trimmed.
            var columns = partiesInline ? Math.Min(3, parties.Count) : 1;

            for (var pi = 0; pi < parties.Count && fields.Count < 24; pi++)
            {
                var party = parties[pi];
                var slots = party.Slots.OrderBy(s => s.SortOrder).ToList();
                var filled = slots.Count(s => slotSignups.ContainsKey(s.Id));
                var sb = new StringBuilder();
                foreach (var slot in slots)
                {
                    slotSignups.TryGetValue(slot.Id, out var signup);
                    var icon = RoleIcon(slot, signup);
                    var crown = (signup?.IsPartyLeader ?? false) ? "👑 " : string.Empty;
                    string line;
                    if (signup is not null)
                    {
                        var jobs = SignedUpJobs(signup);
                        // Trim the name with an ellipsis so "name — jobs" stays on ONE line
                        // in the narrow inline columns (it wraps otherwise, breaking the
                        // grid). Full names stay legible on the rendered image board.
                        var name = FitSlotName(signup.CharacterName ?? "Member", jobs, !string.IsNullOrEmpty(crown), columns);
                        line = $"{icon} {crown}**{Escape(name)}**"
                             + (string.IsNullOrEmpty(jobs) ? string.Empty : $" — {Escape(jobs)}");
                    }
                    else
                    {
                        line = $"{icon} {crown}{Escape(SlotRequirement(slot, compact: true))}";
                    }
                    if (sb.Length > 0) { sb.Append('\n'); }
                    // Empty slots render as Discord "subtext" (greyed) so they read as
                    // open; a FILLED slot drops the prefix so the claimed line stays
                    // full-brightness white and pops. (Subtext doesn't shrink text in
                    // embeds — but the dimming gives a clean empty-vs-filled cue.)
                    if (signup is null) { sb.Append("-# "); }
                    sb.Append(line);
                }
                if (sb.Length == 0) { sb.Append("_No slots_"); }

                var partyName = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!.Trim();
                fields.Add(new
                {
                    name = Truncate($"{partyName} ({filled}/{slots.Count})", 250),
                    value = Truncate(sb.ToString(), 1024),
                    inline = partiesInline, // side by side (≤3-across) when >1 party
                });
            }
        }

        // General attendees who joined WITHOUT a party slot (slot-holders excluded).
        // Shown with the same role dot + bold name + role/main/sub as a slot line.
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
                var icon = GeneralRoleIcon(g.JobType);
                var jobs = GeneralSignupJobs(g);
                sb.Append($"{icon} **{Escape(g.CharacterName)}**"
                    + (string.IsNullOrEmpty(jobs) ? string.Empty : $" — {Escape(jobs)}"));
            }
            fields.Add(new
            {
                name = "Also Attending",
                value = Truncate(sb.ToString(), 1024),
                inline = false,
            });
        }

        var typePrefix = string.IsNullOrWhiteSpace(ev.EventType) ? string.Empty : $"{ev.EventType!.Trim()}: ";
        var title = Truncate($"⚔️ {typePrefix}{ev.EventName ?? $"Event #{ev.Id}"}", 250);

        return new
        {
            title,
            // The description renders directly under the title, so use it to put a
            // little breathing room below the event type: the real details when set,
            // otherwise a single thin blank line. (Discord gives no other control over
            // title↕field spacing.)
            description = string.IsNullOrWhiteSpace(ev.Details) ? "​" : Truncate(Escape(ev.Details!.Trim()), 1500),
            color = EmbedColor,
            fields = fields.ToArray(),
            // The rendered board PNG, shown INSIDE the embed (omitted when null).
            // Referencing the uploaded file (files[0]) by name lets the wide,
            // readable embed carry the themed image too.
            image = imageFileName is null ? null : new { url = $"attachment://{imageFileName}" },
        };
    }

    // Board components: "Sign Up" and "Sign Up as Party Leader" both open the
    // ephemeral slot picker — the same flow, except the leader path marks the
    // claimed slot as the party's leader (👑) on the rendered board. "Sign Up (No
    // Slot)" is attendance-only, "Withdraw" drops out. Four buttons, shown below the
    // board image (or embed fallback). Discord allows up to five buttons per row.
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
                        style = 1, // primary — same flow, claims the slot as leader
                        label = "👑 Sign Up as Party Leader",
                        custom_id = $"{PartySlotLeaderSignUpPrefix}{eventId}",
                    },
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary — general attendance, no party slot
                        label = "Sign Up (No Slot)",
                        custom_id = $"{PartyJoinEventPrefix}{eventId}",
                    },
                    new
                    {
                        type = 2, // button
                        style = 2, // secondary — drops both the slot AND general attendance
                        label = "Withdraw",
                        custom_id = $"{PartySlotLeavePrefix}{eventId}",
                    },
                },
            },
        };
    }

    // A role-colored dot for a slot: the signed-up role when filled, else the
    // pinned role; job/any slots get a neutral dot. Used in the embed lines + the
    // picker option emoji.
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

    // Compact slot requirement for a picker option: the role ("Tank"), the job
    // ("WAR" / "WAR/NIN"), or "Any".
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
                : $"{slot.MainJob}/{(string.IsNullOrWhiteSpace(slot.SubJob) ? "Player's Choice" : slot.SubJob)}";
        }
        return "Any";
    }

    // Mirrors the in-app slot requirement label (Any Role / Any {role} /
    // {main}[/{sub}]). `compact` abbreviates the long "Player's Choice" sub to "PC"
    // so the 3-column board embed fits each requirement on one line (Discord embed
    // fields wrap, and we can't widen them); the full label is kept everywhere else.
    public static string SlotRequirement(PartySetupSlot slot, bool compact = false)
    {
        var label = string.IsNullOrWhiteSpace(slot.Label) ? string.Empty : $" ({slot.Label})";
        string core;
        if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Role, StringComparison.OrdinalIgnoreCase))
        {
            core = string.IsNullOrWhiteSpace(slot.Role) ? "Any Role" : $"Any {slot.Role}";
        }
        else if (string.Equals(slot.RequirementType, PartySetupSlotRequirementTypes.Job, StringComparison.OrdinalIgnoreCase))
        {
            var freeSub = compact ? "PC" : "Player's Choice";
            core = string.IsNullOrWhiteSpace(slot.MainJob)
                ? "Any job"
                : $"{slot.MainJob}/{(string.IsNullOrWhiteSpace(slot.SubJob) ? freeSub : slot.SubJob)}";
        }
        else
        {
            core = "Any Role";
        }
        return core + label;
    }

    // Just MAIN/SUB (no role) for the board embed: the slot's colored role dot
    // already conveys the role, and dropping the "Tank - " / "DPS - " prefix keeps
    // each line short enough to fit one embed column without wrapping.
    private static string SignedUpJobs(EventPartySlotSignup signup)
    {
        if (string.IsNullOrWhiteSpace(signup.MainJob)) { return string.Empty; }
        return string.IsNullOrWhiteSpace(signup.SubJob)
            ? signup.MainJob!
            : $"{signup.MainJob}/{signup.SubJob}";
    }

    // Shorten a character name (…) so its slot line "🔵 name — JOB/SUB" doesn't wrap the
    // column. An embed row is ~64 "characters" wide on desktop, shared by `columns`
    // inline party fields; reserve the role dot (~3), the optional 👑 (~3) and " — {jobs}"
    // and give the rest to the name. Capped at the FFXI 15-char max and floored at 4 so a
    // name never collapses to just an ellipsis. Discord's proportional font makes this
    // approximate — it errs toward fitting; the rendered image board shows the full name.
    private static string FitSlotName(string? name, string jobs, bool hasCrown, int columns)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var lineBudget = columns >= 2 ? 64 / columns : 64;
        var reserved = 3 + (hasCrown ? 3 : 0) + (jobs.Length > 0 ? 3 + jobs.Length : 0);
        var budget = Math.Clamp(lineBudget - reserved, 4, 15);
        return Truncate(trimmed, budget);
    }

    // "Role - MAIN/SUB" for a no-slot attendee (mirrors SignedUpJobs for slots).
    private static string GeneralSignupJobs(EventSignupLine g)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(g.JobType)) { parts.Add(g.JobType!); }
        if (!string.IsNullOrWhiteSpace(g.JobName))
        {
            parts.Add(string.IsNullOrWhiteSpace(g.SubJobName)
                ? g.JobName!
                : $"{g.JobName}/{g.SubJobName}");
        }
        return string.Join(" - ", parts);
    }

    private static string GeneralRoleIcon(string? jobType) => jobType?.Trim().ToLowerInvariant() switch
    {
        "tank" => "🔵",
        "heal" or "healer" => "🟢",
        "support" => "🟡",
        "dps" => "🔴",
        _ => "⚪",
    };

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
