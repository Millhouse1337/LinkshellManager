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
    // "Fill earlier alliances first" nudge buttons. Tail: {eventId}:{slotId}:{L}:{role}:{main}:{sub}
    // ("-" = none). Take = claim the suggested earlier slot; Keep = claim the chosen one anyway.
    public const string PartyNudgeTakePrefix = "evt:psnT:";
    public const string PartyNudgeKeepPrefix = "evt:psnK:";
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

    // "Make Me Party Lead" button — a member who ALREADY holds a slot takes their
    // party's leadership (👑), overriding whoever currently holds it. Distinct from
    // "Sign Up as Party Leader" (which claims an open slot, first-claim-wins): this
    // needs no slot pick (a member holds at most one slot per event) and deliberately
    // replaces the existing leader.
    //   evt:mkleader:{eventId}
    public const string MakeLeaderPrefix = "evt:mkleader:";

    // "Make Me Alliance Lead" button — same shape as "Make Me Party Lead", one rung up:
    // a member who ALREADY holds a slot takes their whole ALLIANCE's lead (👑 shown next
    // to the alliance name), overriding whoever currently holds it. Only shown on
    // multi-alliance boards (a single-alliance board has no alliance header to mark).
    //   evt:mkalead:{eventId}
    public const string MakeAllianceLeaderPrefix = "evt:mkalead:";

    // "Next Window" / "Prev Window" buttons on the window-cycle HNM boards
    // (Tiamat/Jormungand/Vrtra): officers-only; wipe the signups and step
    // Event.HnmWindowNumber within [1, MaxWindow]. Prev is the undo for an accidental
    // Next double-click.
    //   evt:nextwindow:{eventId}
    //   evt:prevwindow:{eventId}
    public const string NextWindowPrefix = "evt:nextwindow:";
    public const string PrevWindowPrefix = "evt:prevwindow:";

    // "🔒 Stay Next Window" (member) + "🔒 Lock Member (officers)" on the window-cycle HNM
    // boards. A locked signup SURVIVES the officer "Next Window" wipe instead of being
    // cleared with the rest of the roster; it persists until unlocked, withdrawn, or an
    // officer removes the member. The member button toggles the CLICKER's own slot; the
    // officer button opens a seated-member picker whose select toggles the chosen slot
    // (value = s:{slotId}, same source token the Set-Leader picker uses).
    //   evt:lockwin:{eventId}       — member self-toggle
    //   evt:olock:{eventId}         — officer "Lock Member" button → seated-member picker
    //   evt:olockpick:{eventId}     — officer picker select (value = s:{slotId}) → toggle
    public const string LockNextWindowPrefix = "evt:lockwin:";
    public const string OfficerLockButtonPrefix = "evt:olock:";
    public const string OfficerLockPickPrefix = "evt:olockpick:";

    // Officer-only "Add Member" — manually seat a roster member (or a brand-new placeholder)
    // into a slot on behalf of someone who didn't sign up themselves. The button is shared
    // by every viewer (Discord can't per-user gate one), so the click handler enforces the
    // officer check and the label says "(officers)". The flow: button → ephemeral member
    // picker → (an existing member, or "add a new player" via a name modal) → the normal
    // slot picker + job wizard, but routed through the officer-add variants below so the
    // claim is attributed to the chosen TARGET (held in OfficerAddTargetCache) rather than
    // the clicking officer.
    //   evt:oadd:{eventId}                          — "Add Member" button
    //   evt:oaddpick:{eventId}                       — member-picker select (AppUserId | "__new__")
    //   evt:oaddnew:{eventId}                        — "add a new player" name modal
    //   evt:oaclaim:{eventId}                        — officer-add slot-picker select → claim
    //   evt:oawr / oawm / oaws:{eventId}:{slotId}…   — officer-add job wizard (role → main → sub)
    public const string OfficerAddButtonPrefix = "evt:oadd:";
    public const string OfficerAddMemberPickPrefix = "evt:oaddpick:";
    public const string OfficerAddNewModalPrefix = "evt:oaddnew:";
    public const string OfficerAddNewNameFieldId = "oadd_name";
    public const string OfficerAddSlotClaimPrefix = "evt:oaclaim:";
    public const string OfficerAddWizardRolePrefix = "evt:oawr:";
    public const string OfficerAddWizardMainPrefix = "evt:oawm:";
    public const string OfficerAddWizardSubPrefix = "evt:oaws:";
    // Member-picker sentinel value selecting "add a new player" (opens the name modal).
    public const string OfficerAddNewSentinel = "__new__";

    // Officer-only member-management controls (mirror the Add-Member flow). Each is a
    // shared board button gated in the handler. The chosen member rides in the picker
    // option's VALUE as a "source token": s:{slotId} (a seated member), a:{appUserId}
    // (an Also-Attending account), or d:{discordUserId} (an Also-Attending board-only
    // member). AppUserIds are GUID strings and DiscordUserIds are numeric, so the token
    // is exactly two colon-separated parts — safe to embed in a custom_id.
    //   evt:omove:{eventId}                  — "Move Member" button → source picker
    //   evt:omovesrc:{eventId}[:{g}]         — source picker select (value = source token)
    //   evt:omovedst:{eventId}:{src}[:{ai}]  — destination open-slot picker → MoveMember
    //   evt:omovebench:{eventId}:{src}       — "Bench → Also Attending" button (seated src)
    //   evt:osetlead:{eventId}               — "Set Leader" button → seated-member picker
    //   evt:osetleadpick:{eventId}[:{g}]     — picker select (value = s:{slotId}) → set crown
    //   evt:owithdraw:{eventId}              — "Remove Member" button → source picker
    //   evt:owithdrawpick:{eventId}[:{g}]    — picker select (value = source token) → confirm
    //   evt:owithdrawgo:{eventId}:{src}      — "Confirm remove" button → remove completely
    public const string MoveMemberButtonPrefix = "evt:omove:";
    public const string MoveSourcePickPrefix = "evt:omovesrc:";
    public const string MoveDestClaimPrefix = "evt:omovedst:";
    public const string MoveBenchPrefix = "evt:omovebench:";
    public const string SetLeaderButtonPrefix = "evt:osetlead:";
    public const string SetLeaderPickPrefix = "evt:osetleadpick:";
    public const string WithdrawMemberButtonPrefix = "evt:owithdraw:";
    public const string WithdrawMemberPickPrefix = "evt:owithdrawpick:";
    public const string WithdrawMemberConfirmPrefix = "evt:owithdrawgo:";

    // Sign-up DRILL-DOWN: "Sign Up" / "Sign Up as Party Leader" narrow the pick in
    // ephemeral steps — Alliance → Party → Slot — each a select that MORPHS the previous
    // in place. Single-choice levels are skipped (never a list of one). The final slot
    // select reuses the existing claim prefixes, so picking a slot runs the same claim +
    // job wizard. Custom_id tails carry the event id (ParseTrailingId reads the leading
    // id); the alliance select's VALUE is the alliance index (by SortOrder), the party
    // select's VALUE is the party id, the slot select's VALUE is the slot id.
    public const string AlliancePickPrefix = "evt:psA:";
    public const string AlliancePickLeaderPrefix = "evt:psLA:";
    public const string PartyPickPrefix = "evt:psP:";
    public const string PartyPickLeaderPrefix = "evt:psLP:";

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
                content = BuildStartHeading(ev, signupsBySlot.Values.Count(s => s.StayNextWindow)),
                embeds = new[] { BuildBoardEmbed(ev, partySetup, signupsBySlot, signups) },
                components = BuildBoardComponents(ev, signupsBySlot.Count > 0, signups.Count > 0, partySetup.Alliances.Count > 1),
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
            content = BuildStartHeading(ev, slotSignups.Values.Count(s => s.StayNextWindow)),
            embeds = new[] { BuildBoardEmbed(ev, setup, slotSignups, signups, fileName) },
            components = BuildBoardComponents(ev, slotSignups.Count > 0, signups.Count > 0, setup.Alliances.Count > 1),
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Discord message flag IS_COMPONENTS_V2 (1 << 15). When set, the message uses the
    // Components V2 tree (containers/text-displays/media-galleries) and MUST NOT carry
    // `content` or `embeds`. Discord rejects toggling this flag on edit, so a board that
    // posts with it must keep it for every subsequent edit (image refresh AND fallback).
    private const int IsComponentsV2Flag = 1 << 15; // 32768

    // The party board as a Components V2 message: a container (blurple accent bar) holding
    // the start heading (text display) and the rendered PNG in a single-item MEDIA GALLERY
    // (attachment://file) — which fills the message column with no embed padding/left-bar
    // and no bare-attachment height clamp, so the board reads wider than the embed version.
    // The same action rows sit below the container. No `content`/`embeds` (V2 forbids them).
    // `attachments:[{id:0,...}]` references the file uploaded as files[0].
    public static object BuildBoardImageV2Message(
        Event ev, IReadOnlyList<EventSignupLine> signups, PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, string fileName)
    {
        var container = new
        {
            type = 17, // Container
            accent_color = EmbedColor,
            components = new object[]
            {
                new { type = 10, content = BuildV2Heading(ev, slotSignups) }, // Text Display
                new { type = 14, divider = true, spacing = 1 },               // Separator
                new
                {
                    type = 12, // Media Gallery — exactly ONE item (multiple tile smaller)
                    items = new object[]
                    {
                        new { media = new { url = $"attachment://{fileName}" }, description = "Event party board" },
                    },
                },
            },
        };

        var components = new List<object> { container };
        components.AddRange(BuildBoardComponents(ev, slotSignups.Count > 0, signups.Count > 0, setup.Alliances.Count > 1));

        return new
        {
            flags = IsComponentsV2Flag,
            components = components.ToArray(),
            attachments = new object[] { new { id = 0, filename = fileName } },
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Components V2 fallback for when the image renderer is unavailable. MUST be V2 too
    // (the flag can't be toggled on edit), so the board can refresh between the image
    // version and this one. A container with the heading + the roster as text displays
    // (each ≤4000 chars — less truncated than the embed's 1024-char fields), no media
    // gallery, no attachments. The same action rows sit below.
    public static object BuildBoardV2FallbackMessage(
        Event ev, IReadOnlyList<EventSignupLine> signups, PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        var children = new List<object>
        {
            new { type = 10, content = BuildV2Heading(ev, slotSignups) }, // Text Display (heading)
            new { type = 14, divider = true, spacing = 1 },               // Separator
        };
        // Roster as markdown text blocks; pack into ≤7 text displays so container children
        // stay under Discord's 10-per-container cap (heading + separator already use 2).
        foreach (var block in BuildRosterTextBlocks(ev, setup, slotSignups, signups).Take(7))
        {
            children.Add(new { type = 10, content = block });
        }

        var container = new
        {
            type = 17, // Container
            accent_color = EmbedColor,
            components = children.ToArray(),
        };

        var components = new List<object> { container };
        components.AddRange(BuildBoardComponents(ev, slotSignups.Count > 0, signups.Count > 0, setup.Alliances.Count > 1));

        return new
        {
            flags = IsComponentsV2Flag,
            components = components.ToArray(),
            attachments = Array.Empty<object>(), // no file
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // Components V2 "defeated" notice — the V2 equivalent of HnmBoardNoticeService's classic
    // note, used when a V2-mode board's HNM is logged as down. A container with just the
    // note text (no buttons, no image), keeping the V2 flag so the edit is accepted.
    public static object BuildV2DefeatedNoticeMessage(string title, string description)
    {
        var text = string.IsNullOrWhiteSpace(description) ? title : $"## {title}\n\n{description}";
        return new
        {
            flags = IsComponentsV2Flag,
            components = new object[]
            {
                new
                {
                    type = 17, // Container
                    accent_color = 0x6B7280,
                    components = new object[] { new { type = 10, content = Truncate(text, 3900) } },
                },
            },
            attachments = Array.Empty<object>(),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
    }

    // The board heading for the V2 container: the start heading (window/day + start time)
    // plus the event title on its own line. Mirrors the classic board's content+embed title.
    private static string BuildV2Heading(Event ev, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        var heading = BuildStartHeading(ev, slotSignups.Values.Count(s => s.StayNextWindow));
        var typePrefix = string.IsNullOrWhiteSpace(ev.EventType) ? string.Empty : $"{ev.EventType!.Trim()}: ";
        var title = Truncate($"## ⚔️ {typePrefix}{ev.EventName ?? $"Event #{ev.Id}"}", 250);
        // Start/window heading above the title, matching the classic board (content heading
        // renders above the embed title).
        return string.Join("\n", new[] { heading, title }.Where(s => !string.IsNullOrEmpty(s)));
    }

    // Renders the roster as full-markdown text blocks for the V2 text fallback. Reuses the
    // same slot-line logic as the embed (RoleIcon/SignedUpJobs/SlotRequirement, 👑/🔒 marks)
    // but WITHOUT the narrow-column name trimming — the text display is full width — and
    // packs alliance/party sections into ≤4000-char chunks.
    private static List<string> BuildRosterTextBlocks(
        Event ev, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<EventSignupLine> generalSignups)
    {
        var sections = new List<string>();

        var colorKey = "🔵 Tank · 🟢 Healer · 🟡 Support · 🔴 DPS · ⚪ Any";
        if (HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            colorKey += "\n🔒 Staying next window (survives the window advance)";
        }
        sections.Add($"-# {colorKey}");

        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        for (var ai = 0; ai < alliances.Count; ai++)
        {
            var parties = alliances[ai].Parties.OrderBy(p => p.SortOrder).ToList();
            if (parties.Count == 0)
            {
                continue;
            }
            var sb = new StringBuilder();
            if (multiAlliance)
            {
                var allianceName = string.IsNullOrWhiteSpace(alliances[ai].Name)
                    ? $"Alliance {ai + 1}"
                    : alliances[ai].Name!.Trim();
                // The alliance lead (if claimed) rides on the header, right of the name.
                var lead = AllianceLeadName(alliances[ai], slotSignups);
                var leadSuffix = string.IsNullOrEmpty(lead) ? string.Empty : $" 👑 Alliance Lead: {Escape(lead)}";
                sb.Append($"## {Escape(allianceName)}{leadSuffix}\n");
            }
            for (var pi = 0; pi < parties.Count; pi++)
            {
                var party = parties[pi];
                var slots = party.Slots.OrderBy(s => s.SortOrder).ToList();
                var filled = slots.Count(s => slotSignups.ContainsKey(s.Id));
                // Crown the empty designated-leader seat so signups can see it's the leader
                // slot — unless someone already claimed leadership (their filled slot wears it).
                var hasSignedUpLeader = slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader);
                var partyName = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!.Trim();
                sb.Append($"### {Escape(partyName)} ({filled}/{slots.Count})\n");
                if (slots.Count == 0)
                {
                    sb.Append("-# _No slots_\n");
                }
                foreach (var slot in slots)
                {
                    slotSignups.TryGetValue(slot.Id, out var signup);
                    var icon = RoleIcon(slot, signup);
                    var isLeaderSeat = signup is not null ? signup.IsPartyLeader : (slot.IsPartyLeader && !hasSignedUpLeader);
                    var crown = isLeaderSeat ? "👑 " : string.Empty;
                    var lockMark = (signup?.StayNextWindow ?? false) ? "🔒 " : string.Empty;
                    if (signup is not null)
                    {
                        var jobs = SignedUpJobs(signup);
                        sb.Append($"{icon} {crown}{lockMark}**{Escape(signup.CharacterName ?? "Member")}**"
                            + (string.IsNullOrEmpty(jobs) ? string.Empty : $" — {Escape(jobs)}") + "\n");
                    }
                    else
                    {
                        sb.Append($"-# {icon} {crown}{Escape(SlotRequirement(slot, compact: false))}\n");
                    }
                }
            }
            sections.Add(sb.ToString().TrimEnd());
        }

        // Also-attending (no-slot) roster, same shape as the embed's section.
        var slotNames = new HashSet<string>(
            slotSignups.Values
                .Where(s => !string.IsNullOrWhiteSpace(s.CharacterName))
                .Select(s => s.CharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var extra = generalSignups
            .Where(g => !string.IsNullOrWhiteSpace(g.CharacterName) && !slotNames.Contains(g.CharacterName.Trim()))
            .ToList();
        if (extra.Count > 0)
        {
            var sb = new StringBuilder("### Also Attending\n");
            foreach (var g in extra)
            {
                var icon = GeneralRoleIcon(g.JobType);
                var jobs = GeneralSignupJobs(g);
                sb.Append($"{icon} **{Escape(g.CharacterName!)}**"
                    + (string.IsNullOrEmpty(jobs) ? string.Empty : $" — {Escape(jobs)}") + "\n");
            }
            sections.Add(sb.ToString().TrimEnd());
        }

        // Pack sections into ≤4000-char text-display blocks (Discord's Text Display cap),
        // starting a new block when the next section would overflow. A single oversized
        // section is hard-truncated so it still posts.
        var blocks = new List<string>();
        var current = new StringBuilder();
        foreach (var section in sections)
        {
            var piece = Truncate(section, 3900);
            if (current.Length > 0 && current.Length + piece.Length + 2 > 3900)
            {
                blocks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) { current.Append("\n\n"); }
            current.Append(piece);
        }
        if (current.Length > 0) { blocks.Add(current.ToString()); }
        return blocks;
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
    // `claimPrefixOverride` routes the select to a different claim handler than the default
    // self-signup ones — the officer-add flow passes OfficerAddSlotClaimPrefix so picking a
    // slot seats the chosen target member instead of the clicker.
    // `idSuffixOverride` (e.g. ":s:42") is appended right after the event id, before
    // the per-alliance ":{ai}". The officer "Move Member" flow uses it to carry the
    // chosen source member in the destination-picker custom_id; all existing callers
    // pass null and are unaffected.
    public static object[] BuildSlotPickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        bool asLeader = false, string? claimPrefixOverride = null, string? idSuffixOverride = null)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        var claimPrefix = claimPrefixOverride ?? (asLeader ? PartySlotClaimLeaderPrefix : PartySlotClaimPrefix);
        var idSuffix = idSuffixOverride ?? string.Empty;
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
                // Leadership is no longer locked to a designated slot — any open slot in a
                // leaderless party can be claimed as leader (first-claim-wins).
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
                        custom_id = multiAlliance ? $"{claimPrefix}{eventId}{idSuffix}:{ai}" : $"{claimPrefix}{eventId}{idSuffix}",
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

    // Step 1 of the sign-up drill-down: one select of the alliances that still have an
    // open slot (for the leader flow, an open slot in a leaderless party). The option
    // VALUE is the alliance's index by SortOrder, so the party step can re-find it.
    // Returns an empty array when nothing is open (caller shows the "full" notice).
    public static object[] BuildAlliancePickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var options = new List<object>();
        for (var ai = 0; ai < alliances.Count && options.Count < 25; ai++)
        {
            var openCount = CountOpenSlots(alliances[ai], slotSignups, asLeader);
            if (openCount == 0)
            {
                continue;
            }
            var name = string.IsNullOrWhiteSpace(alliances[ai].Name) ? $"Alliance {ai + 1}" : alliances[ai].Name!.Trim();
            options.Add(new { label = Truncate($"{name} — {openCount} open", 100), value = ai.ToString() });
        }
        if (options.Count == 0)
        {
            return Array.Empty<object>();
        }
        var prefix = asLeader ? AlliancePickLeaderPrefix : AlliancePickPrefix;
        return PickerSelectRow(prefix, eventId.ToString(), asLeader ? "Pick an alliance to lead in" : "Pick an alliance", options.ToArray());
    }

    // Step 2: one select of the parties in the chosen alliance that still have an open
    // slot (leader flow: leaderless parties only). Option VALUE is the party id.
    public static object[] BuildPartyPickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        int allianceIndex, bool asLeader)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        if (allianceIndex < 0 || allianceIndex >= alliances.Count)
        {
            return Array.Empty<object>();
        }
        var parties = alliances[allianceIndex].Parties.OrderBy(p => p.SortOrder).ToList();
        var options = new List<object>();
        for (var pi = 0; pi < parties.Count && options.Count < 25; pi++)
        {
            var party = parties[pi];
            if (asLeader && HasPartyLeader(party, slotSignups))
            {
                continue; // leader flow: only leaderless parties
            }
            var open = party.Slots.Count(s => !slotSignups.ContainsKey(s.Id));
            if (open == 0)
            {
                continue;
            }
            var name = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!.Trim();
            options.Add(new { label = Truncate($"{name} ({party.Slots.Count - open}/{party.Slots.Count})", 100), value = party.Id.ToString() });
        }
        if (options.Count == 0)
        {
            return Array.Empty<object>();
        }
        var prefix = asLeader ? PartyPickLeaderPrefix : PartyPickPrefix;
        return PickerSelectRow(prefix, eventId.ToString(), asLeader ? "Pick a party to lead" : "Pick a party", options.ToArray());
    }

    // Step 3: one select of the OPEN slots in the chosen party. Reuses the claim prefix,
    // so picking a slot runs HandlePartySlotClaimAsync (job wizard + claim) unchanged.
    public static object[] BuildPartySlotPickerComponents(
        int eventId, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        int partyId, bool asLeader)
    {
        var party = setup.Alliances.SelectMany(a => a.Parties).FirstOrDefault(p => p.Id == partyId);
        if (party is null)
        {
            return Array.Empty<object>();
        }
        var options = new List<object>();
        foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
        {
            if (slotSignups.ContainsKey(slot.Id) || options.Count >= 25)
            {
                continue;
            }
            options.Add(new
            {
                label = Truncate(SlotShortLabel(slot), 100),
                value = slot.Id.ToString(),
                emoji = new { name = RoleIcon(slot, null) },
            });
        }
        if (options.Count == 0)
        {
            return Array.Empty<object>();
        }
        var prefix = asLeader ? PartySlotClaimLeaderPrefix : PartySlotClaimPrefix;
        return PickerSelectRow(prefix, eventId.ToString(), asLeader ? "Pick a slot to lead" : "Pick a slot to claim", options.ToArray());
    }

    // Open slots in an alliance (leader flow: only slots in parties with no leader yet).
    private static int CountOpenSlots(
        PartySetupAlliance alliance, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups, bool asLeader)
    {
        var count = 0;
        foreach (var party in alliance.Parties)
        {
            if (asLeader && HasPartyLeader(party, slotSignups))
            {
                continue;
            }
            count += party.Slots.Count(s => !slotSignups.ContainsKey(s.Id));
        }
        return count;
    }

    private static bool HasPartyLeader(PartySetupParty party, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
        => party.Slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader);

    // The character name of the member designated this alliance's lead (👑 next to the
    // alliance header), or null when nobody has claimed it. At most one signup per
    // alliance carries IsAllianceLeader.
    private static string? AllianceLeadName(
        PartySetupAlliance alliance, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups)
    {
        foreach (var party in alliance.Parties)
        {
            foreach (var slot in party.Slots)
            {
                if (slotSignups.TryGetValue(slot.Id, out var su) && su.IsAllianceLeader)
                {
                    return string.IsNullOrWhiteSpace(su.CharacterName) ? "Member" : su.CharacterName!.Trim();
                }
            }
        }
        return null;
    }

    // One ephemeral action row holding a single string select. Shared by the drill-down
    // steps; the tail rides after the prefix and ParseTrailingId reads the leading id.
    private static object[] PickerSelectRow(string prefix, string idTail, string placeholder, object[] options)
        => new object[]
        {
            new
            {
                type = 1, // action row
                components = new object[]
                {
                    new
                    {
                        type = 3, // string select
                        custom_id = $"{prefix}{idTail}",
                        placeholder,
                        min_values = 1,
                        max_values = 1,
                        options,
                    },
                },
            },
        };

    // Action rows for the officer "pick a participant" step shared by Move / Set
    // Leader / Remove. Lists members currently ON the board: each occupied slot
    // (value "s:{slotId}") grouped one select per alliance (≤25 opts, ≤5 rows), and —
    // unless `seatedOnly` (Set Leader: a leader must hold a slot) — one extra select of
    // the "Also Attending" members the caller supplies (value "a:{appUserId}" /
    // "d:{discordUserId}"). Each select's custom_id is {customIdPrefix}{eventId}:{group}
    // (unique per select); the handler reads the leading event id and the chosen source
    // token from the select VALUE.
    public static object[] BuildMoveSourceComponents(
        int eventId, PartySetup setup,
        IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<(string Label, string Value)> attendeeOptions,
        string customIdPrefix, bool seatedOnly)
    {
        var alliances = setup.Alliances.OrderBy(a => a.SortOrder).ToList();
        var multiAlliance = alliances.Count > 1;
        var rows = new List<object>();
        var group = 0;

        foreach (var alliance in alliances)
        {
            if (rows.Count >= 5) { break; }
            var parties = alliance.Parties.OrderBy(p => p.SortOrder).ToList();
            var options = new List<object>();
            for (var pi = 0; pi < parties.Count && options.Count < 25; pi++)
            {
                var party = parties[pi];
                var partyName = string.IsNullOrWhiteSpace(party.Name) ? $"Party {pi + 1}" : party.Name!.Trim();
                foreach (var slot in party.Slots.OrderBy(s => s.SortOrder))
                {
                    if (options.Count >= 25 || !slotSignups.TryGetValue(slot.Id, out var su))
                    {
                        continue;
                    }
                    var name = string.IsNullOrWhiteSpace(su.CharacterName) ? "Member" : su.CharacterName!.Trim();
                    var jobs = SignupJobsLabel(su);
                    options.Add(new
                    {
                        label = Truncate(jobs.Length > 0 ? $"{partyName}: {name} — {jobs}" : $"{partyName}: {name}", 100),
                        value = $"s:{slot.Id}",
                        emoji = new { name = RoleIcon(slot, su) },
                    });
                }
            }
            if (options.Count == 0) { continue; }

            var allianceName = string.IsNullOrWhiteSpace(alliance.Name) ? $"Alliance {group + 1}" : alliance.Name!.Trim();
            rows.Add(new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 3,
                        custom_id = $"{customIdPrefix}{eventId}:{group}",
                        placeholder = multiAlliance ? Truncate($"{allianceName} — pick a member", 150) : "Pick a member",
                        min_values = 1,
                        max_values = 1,
                        options = options.ToArray(),
                    },
                },
            });
            group++;
        }

        if (!seatedOnly && attendeeOptions.Count > 0 && rows.Count < 5)
        {
            var opts = attendeeOptions
                .Take(25)
                .Select(o => (object)new { label = Truncate(o.Label, 100), value = o.Value })
                .ToArray();
            rows.Add(new
            {
                type = 1,
                components = new object[]
                {
                    new
                    {
                        type = 3,
                        custom_id = $"{customIdPrefix}{eventId}:{group}",
                        placeholder = "Also Attending — pick a member",
                        min_values = 1,
                        max_values = 1,
                        options = opts,
                    },
                },
            });
        }

        return rows.ToArray();
    }

    // Compact "Role - Main/Sub" for a slot signup, used in the officer member picker.
    private static string SignupJobsLabel(EventPartySlotSignup su)
    {
        var role = string.IsNullOrWhiteSpace(su.Role) ? null : su.Role!.Trim();
        var job = string.IsNullOrWhiteSpace(su.MainJob)
            ? null
            : (string.IsNullOrWhiteSpace(su.SubJob) ? su.MainJob!.Trim() : $"{su.MainJob!.Trim()}/{su.SubJob!.Trim()}");
        return string.Join(" - ", new[] { role, job }.Where(s => !string.IsNullOrEmpty(s)));
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
        // NB: the day number is NOT a field here — it rides on the big monster heading
        // above the embed (BuildStartHeading), next to the monster name.
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
    private static string BuildStartHeading(Event ev, int lockedCount = 0)
    {
        // Every HNM board leads with the assigned monster name (e.g. "🪟 Aspidochelone")
        // so the curated monster is surfaced even when the event is custom-named. The
        // window-cycle HNMs (Tiamat/Jormungand/Vrtra) additionally append "· Window N of 25",
        // advanced by the officer-only "Next Window" button → "🪟 Tiamat · Window 1 of 25".
        // A combined "Base/Stronger" pair collapses to just the base on early days (day <
        // HnmConfig.CombinedFromDay) — only the weaker version pops then; later days show both.
        var monster = HnmConfig.DisplayMonsterName(ev.AssignedMonsterName, ev.DayNumber)?.Trim();
        string? windowLine = null;
        if (!string.IsNullOrEmpty(monster))
        {
            windowLine = HnmConfig.SupportsWindowAdvance(monster)
                ? $"## 🪟 {monster} · Window {Math.Clamp(ev.HnmWindowNumber, 1, HnmConfig.MaxWindow)} of {HnmConfig.MaxWindow}"
                : $"## 🪟 {monster}";
        }

        // The day number rides on the big monster heading, right next to the name
        // ("🪟 Fafnir · Day 5"), instead of a separate embed field below. Standalone
        // "📅 Day N" is the fallback if an event carries a day but no monster name.
        if (ev.DayNumber is { } dayNumber)
        {
            windowLine = string.IsNullOrEmpty(windowLine) ? $"## 📅 Day {dayNumber}" : $"{windowLine} · Day {dayNumber}";
        }

        // Window-cycle HNMs predict each window's pop ~1h after the previous, so a
        // not-yet-started board shows StartTime + (window − 1) hours. This is COMPUTED
        // on the fly — the stored StartTime stays the Window 1 anchor, so stepping
        // windows is non-destructive and editing the time still behaves predictably.
        // Once the event is live (CommencementStartTime set), show the real start.
        DateTime? when = ev.CommencementStartTime ?? ev.StartTime;
        if (ev.CommencementStartTime is null
            && ev.StartTime is not null
            && HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            var windowOffset = Math.Clamp(ev.HnmWindowNumber, 1, HnmConfig.MaxWindow) - 1;
            when = ev.StartTime.Value.AddHours(windowOffset);
        }
        string? startLine = null;
        if (when is not null)
        {
            var unix = ((DateTimeOffset)DateTime.SpecifyKind(when.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
            var label = ev.CommencementStartTime is not null ? "Started" : "Starts";
            // :D long date + :T long time (which includes seconds) — Discord has no single
            // date+time-with-seconds token, so combine the two (both still per-viewer local).
            startLine = $"## 🕒 {label}: <t:{unix}:D> <t:{unix}:T> · <t:{unix}:R>";
        }

        // "N staying next window" as Discord subtext (-#) under the heading, so officers
        // see at a glance how many slots carry over (and thus how many will open up) after
        // the next advance. Only on window-cycle HNMs, and only when someone's actually locked.
        string? stayingLine = null;
        if (lockedCount > 0 && HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            stayingLine = $"-# 🔒 {lockedCount} staying next window";
        }

        return string.Join("\n", new[] { windowLine, startLine, stayingLine }.Where(line => !string.IsNullOrEmpty(line)));
    }

    // Board embed (the fallback when the image renderer is unavailable): event
    // details + one field per party listing each slot with a role-colored dot —
    // claimed slots show the member + jobs, open slots show the requirement.
    private static object BuildBoardEmbed(
        Event ev, PartySetup setup, IReadOnlyDictionary<int, EventPartySlotSignup> slotSignups,
        IReadOnlyList<EventSignupLine> generalSignups, string? imageFileName = null)
    {
        var fields = BuildEventDetailFields(ev);

        // Color key for the dots in the party lists below, so the colours aren't a
        // mystery. Full-width (inline:false) so it sits on its own row above the parties.
        // Window-cycle HNMs also explain the 🔒 (a signup that survives the window advance).
        var colorKey = "🔵 Tank · 🟢 Healer · 🟡 Support · 🔴 DPS · ⚪ Any";
        if (HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            colorKey += "\n🔒 Staying next window (survives the window advance)";
        }
        fields.Add(new
        {
            name = "Color Key",
            value = colorKey,
            inline = false,
        });

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
                // The alliance lead (if claimed) rides on the header, right of the name:
                // "────── Alliance 1 ────── 👑 Alliance Lead: Millh".
                var lead = AllianceLeadName(alliances[ai], slotSignups);
                var allianceHeader = string.IsNullOrEmpty(lead)
                    ? $"────── {allianceName} ──────"
                    : $"────── {allianceName} ────── 👑 Alliance Lead: {lead}";
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
                // An empty designated-leader seat is pre-crowned so signups can see up front
                // that taking it makes them leader. Once someone claims leadership, the 👑
                // follows that ACTUAL signed-up leader instead (their filled slot wears it).
                var hasSignedUpLeader = slots.Any(s => slotSignups.TryGetValue(s.Id, out var su) && su.IsPartyLeader);
                var sb = new StringBuilder();
                foreach (var slot in slots)
                {
                    slotSignups.TryGetValue(slot.Id, out var signup);
                    var icon = RoleIcon(slot, signup);
                    var isLeaderSeat = signup is not null ? signup.IsPartyLeader : (slot.IsPartyLeader && !hasSignedUpLeader);
                    var crown = isLeaderSeat ? "👑 " : string.Empty;
                    // 🔒 marks a signup that will SURVIVE the next window advance (it's staying).
                    var lockMark = (signup?.StayNextWindow ?? false) ? "🔒 " : string.Empty;
                    string line;
                    if (signup is not null)
                    {
                        var jobs = SignedUpJobs(signup);
                        // Hard-trim the name (no ellipsis) to keep "name — jobs" short in the
                        // narrow inline columns. Full names stay legible on the rendered image board.
                        var name = FitSlotName(signup.CharacterName ?? "Member", jobs, !string.IsNullOrEmpty(crown), !string.IsNullOrEmpty(lockMark), columns);
                        line = $"{icon} {crown}{lockMark}**{Escape(name)}**"
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
    // claimed slot as the party's leader (👑) on the rendered board. "Make Me Party
    // Lead" lets a member who's ALREADY in a slot take their party's crown from the
    // current holder. "Sign Up (No Slot)" is attendance-only, "Withdraw" drops out.
    // Shown below the board image (or embed fallback). Discord allows up to five
    // buttons per row — the leader actions sit adjacent so the row stays within that.
    // `hasSignups` = at least one party SLOT is filled (gates "Make Me Party Lead" +
    // "Set Leader", which need a seated member). `hasAttendees` = at least one general
    // (no-slot) signup exists; together they mean "someone is on the board", which gates
    // Move/Remove (you can move/remove a bench member even before any slot is filled).
    private static object[] BuildBoardComponents(Event ev, bool hasSignups, bool hasAttendees, bool multiAlliance)
    {
        var eventId = ev.Id;
        var isHnm = string.Equals((ev.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);

        var firstRow = new List<object>
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
        };
        // "Sign Up (No Slot)" is general attendance — omitted on HNM outside-signup boards,
        // which are slot-only (no DKP / attendance tracking) so a no-slot join makes no sense.
        if (!isHnm)
        {
            firstRow.Add(new
            {
                type = 2, // button
                style = 2, // secondary — general attendance, no party slot
                label = "Sign Up (No Slot)",
                custom_id = $"{PartyJoinEventPrefix}{eventId}",
            });
        }
        // "🔒 Stay Next Window" — window-cycle HNM only, and only once somebody holds a slot
        // (you can only lock a slot you're already in). Shared button: toggles the CLICKER's
        // own lock. Placed before Withdraw. The "Make Me …" leadership buttons live on their
        // own row below (see leadershipRow), so this row stays well within Discord's 5-button
        // cap — the "Sign Up (No Slot)" button is already omitted for HNM above.
        if (HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName) && hasSignups)
        {
            firstRow.Add(new
            {
                type = 2, // button
                style = 2, // secondary — toggles the clicker's "stay next window" lock
                label = "🔒 Stay Next Window",
                custom_id = $"{LockNextWindowPrefix}{eventId}",
            });
        }
        firstRow.Add(new
        {
            type = 2, // button
            style = 2, // secondary — drops both the slot AND general attendance
            label = "Withdraw",
            custom_id = $"{PartySlotLeavePrefix}{eventId}",
        });

        // The non-HNM-with-signups case already fills this row to Discord's hard cap of 5
        // buttons; a future always-shown button would push it to 6, which Discord rejects —
        // failing the WHOLE board post. The assert trips loudly in dev so the overflow is
        // caught before shipping; the clamp degrades gracefully in production (drops the
        // overflow button) so the board still posts rather than vanishing.
        System.Diagnostics.Debug.Assert(firstRow.Count <= 5, "Discord allows at most 5 buttons per action row.");
        if (firstRow.Count > 5)
        {
            firstRow = firstRow.Take(5).ToList();
        }

        var rows = new List<object>
        {
            new
            {
                type = 1, // action row
                components = firstRow.ToArray(),
            },
        };

        // Leadership row: "Make Me Party Lead" (take your party's 👑) and, on multi-alliance
        // boards, "Make Me Alliance Lead" (take your whole alliance's 👑). Both are only for
        // members who ALREADY hold a slot, so the row appears once at least one slot is
        // claimed. Discord components are shared by every viewer — a button can't be shown
        // per-user — so this board-level gate (hide until somebody has signed up) is the
        // closest we can get to "only show it once the person signs up". Kept off the sign-up
        // row so the two leadership actions read as a group and neither row nears the 5-cap.
        if (hasSignups)
        {
            var leadershipRow = new List<object>
            {
                new
                {
                    type = 2, // button
                    style = 2, // secondary — for members ALREADY in a slot; takes the party crown
                    label = "👑 Make Me Party Lead",
                    custom_id = $"{MakeLeaderPrefix}{eventId}",
                },
            };
            if (multiAlliance)
            {
                leadershipRow.Add(new
                {
                    type = 2, // button
                    style = 2, // secondary — takes the alliance crown (one rung up)
                    label = "👑 Make Me Alliance Lead",
                    custom_id = $"{MakeAllianceLeaderPrefix}{eventId}",
                });
            }
            rows.Add(new
            {
                type = 1, // action row
                components = leadershipRow.ToArray(),
            });
        }

        // Officer-only "Add Member" on its own row — the first row is already at Discord's
        // 5-button cap in the busy case, so this can't share it. The button is shown to
        // everyone (Discord can't per-user gate a component); the click handler enforces the
        // officer check, hence the "(officers)" label. Manually seats a roster member (or a
        // new placeholder) into a slot for someone who didn't sign up themselves.
        // Add Member always shows (you can seat someone onto an empty board). The
        // member-management controls (move/set-leader/remove) only appear once there's
        // somebody to manage. 4 buttons max — within Discord's 5-per-row cap.
        var officerButtons = new List<object>
        {
            new
            {
                type = 2, // button
                style = 2, // secondary
                label = "➕ Add Member (officers)",
                custom_id = $"{OfficerAddButtonPrefix}{eventId}",
            },
        };
        var hasAnyone = hasSignups || hasAttendees;
        // Move/Remove work on anyone on the board — including a bench (no-slot) member,
        // so they show even before a slot is filled (e.g. to seat the bench into a slot).
        if (hasAnyone)
        {
            officerButtons.Add(new
            {
                type = 2, style = 2, // secondary
                label = "↔ Move Member (officers)",
                custom_id = $"{MoveMemberButtonPrefix}{eventId}",
            });
        }
        // Set Leader needs a seated member (the leader holds a slot), so it's slot-gated.
        if (hasSignups)
        {
            officerButtons.Add(new
            {
                type = 2, style = 2, // secondary
                label = "👑 Set Leader (officers)",
                custom_id = $"{SetLeaderButtonPrefix}{eventId}",
            });
        }
        if (hasAnyone)
        {
            officerButtons.Add(new
            {
                type = 2, style = 4, // danger — fully removes the member
                label = "✖ Remove Member (officers)",
                custom_id = $"{WithdrawMemberButtonPrefix}{eventId}",
            });
        }
        rows.Add(new
        {
            type = 1, // action row
            components = officerButtons.ToArray(),
        });

        // Window-cycle HNMs (Tiamat/Jormungand/Vrtra) get officer-only "Prev Window" /
        // "Next Window" buttons on their own row. Next wipes the signups and advances
        // "Window N"; Prev steps back one (in case Next was clicked by accident). Both are
        // visible to everyone (Discord can't per-user gate a button); the handler enforces
        // officers. Prev is disabled on the first window, Next on the final window.
        if (HnmConfig.SupportsWindowAdvance(ev.AssignedMonsterName))
        {
            var atMax = ev.HnmWindowNumber >= HnmConfig.MaxWindow;
            var atMin = ev.HnmWindowNumber <= 1;
            var windowButtons = new List<object>
            {
                new
                {
                    type = 2,
                    style = 2, // secondary
                    label = atMin ? "Window 1 (first)" : "◀ Prev Window (officers)",
                    custom_id = $"{PrevWindowPrefix}{eventId}",
                    disabled = atMin,
                },
                new
                {
                    type = 2,
                    style = 1, // primary
                    label = atMax ? $"Window {HnmConfig.MaxWindow} (final)" : "▶ Next Window (officers)",
                    custom_id = $"{NextWindowPrefix}{eventId}",
                    disabled = atMax,
                },
            };
            // Officer "🔒 Lock Member" — pin anyone's slot so it survives the Next Window
            // wipe (e.g. hold a key camp job across windows). Only useful once somebody's
            // seated. 3 buttons stays within Discord's 5-per-row cap.
            if (hasSignups)
            {
                windowButtons.Add(new
                {
                    type = 2,
                    style = 2, // secondary
                    label = "🔒 Lock Member (officers)",
                    custom_id = $"{OfficerLockButtonPrefix}{eventId}",
                });
            }
            rows.Add(new
            {
                type = 1,
                components = windowButtons.ToArray(),
            });
        }

        return rows.ToArray();
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
    // inline party fields; reserve the role dot (~3), the optional 👑 / 🔒 (~3 each) and " — {jobs}"
    // and give the rest to the name. Capped at the FFXI 15-char max and floored at 4 so a
    // name never collapses to just an ellipsis. Discord's proportional font makes this
    // approximate — it errs toward fitting; the rendered image board shows the full name.
    private static string FitSlotName(string? name, string jobs, bool hasCrown, bool hasLock, int columns)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var lineBudget = columns >= 2 ? 64 / columns : 64;
        var reserved = 3 + (hasCrown ? 3 : 0) + (hasLock ? 3 : 0) + (jobs.Length > 0 ? 3 + jobs.Length : 0);
        var budget = Math.Clamp(lineBudget - reserved, 4, 15);
        // Hard cut with NO ellipsis: the "…" didn't reliably stop "name — jobs" wrapping in
        // the narrow inline columns, and a clean cut shows one more name char without it.
        return trimmed.Length <= budget ? trimmed : trimmed[..budget].TrimEnd();
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
