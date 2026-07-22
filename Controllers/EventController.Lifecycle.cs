using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public partial class EventController
{
    public async Task<IActionResult> Index()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        ViewBag.CharacterName = user.CharacterName;

        var linkshellIds = await _context.AppUserLinkshells
            .Where(link => link.AppUserId == user.Id)
            .Select(link => link.LinkshellId)
            .ToListAsync();

        int? selectedLinkshellId = user.PrimaryLinkshellId ?? linkshellIds.Cast<int?>().FirstOrDefault();

        var events = await _context.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.PartySetup)
            .Where(evt => !selectedLinkshellId.HasValue || evt.LinkshellId == selectedLinkshellId.Value)
            .OrderBy(evt => evt.StartTime)
            .ToListAsync();

        var creatorIds = events
            .Where(evt => !string.IsNullOrWhiteSpace(evt.CreatorUserId))
            .Select(evt => evt.CreatorUserId!)
            .Distinct()
            .ToList();

        var creators = await _context.Users
            .Where(appUser => creatorIds.Contains(appUser.Id))
            .ToDictionaryAsync(appUser => appUser.Id, appUser => appUser.CharacterName ?? appUser.UserName ?? appUser.Id);

        // Eager-load the full alliance/party/slot tree for every linked Party
        // Setup so the inline "View & Sign Up" panel renders without a second
        // round-trip. Batch by id to avoid N+1.
        var linkedSetupIds = events
            .Where(evt => evt.PartySetupId.HasValue)
            .Select(evt => evt.PartySetupId!.Value)
            .Distinct()
            .ToList();

        // Party setups are reusable templates, so the roster is per EVENT — build a
        // board per event from the template structure with that event's own slot
        // signups overlaid (keeps one event's signups from showing on another, and
        // keeps the web in sync with the Discord board + Activity panel).
        Dictionary<int, PartySetupBoardViewModel> boardsByEvent = new();
        if (linkedSetupIds.Count > 0)
        {
            var linkedSetups = await _context.PartySetups
                .AsNoTracking()
                .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
                .Where(ps => linkedSetupIds.Contains(ps.Id))
                .ToListAsync();
            var templatesById = linkedSetups.ToDictionary(ps => ps.Id);

            var eventIdsWithSetup = events.Where(e => e.PartySetupId.HasValue).Select(e => e.Id).ToList();
            var signupRows = await _context.EventPartySlotSignups
                .AsNoTracking()
                .Where(s => eventIdsWithSetup.Contains(s.EventId))
                .ToListAsync();
            var signupsByEvent = signupRows
                .GroupBy(s => s.EventId)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyDictionary<int, EventPartySlotSignup>)g.ToDictionary(s => s.PartySetupSlotId, s => s));
            var emptySignups = (IReadOnlyDictionary<int, EventPartySlotSignup>)new Dictionary<int, EventPartySlotSignup>();

            foreach (var evt in events.Where(e => e.PartySetupId.HasValue))
            {
                if (!templatesById.TryGetValue(evt.PartySetupId!.Value, out var template))
                {
                    continue;
                }
                signupsByEvent.TryGetValue(evt.Id, out var sign);
                boardsByEvent[evt.Id] = BuildPartySetupBoard(template, sign ?? emptySignups);
            }
        }

        // Pre-fill data for the "Edit ToD" modal on defeated/awaiting-repost HNM boards —
        // load the source ToD of each so the modal opens with its logged values.
        var sourceTodIds = events
            .Where(evt => evt.HnmDefeatedAt.HasValue && evt.SourceTodId.HasValue)
            .Select(evt => evt.SourceTodId!.Value)
            .Distinct()
            .ToList();
        var sourceTodsById = sourceTodIds.Count > 0
            ? await _context.Tods.AsNoTracking()
                .Where(t => sourceTodIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id)
            : new Dictionary<int, Tod>();

        // Drive the manage badge + slot dropdown options on the inline panel.
        ViewBag.CurrentAppUserId = user.Id;
        // Characters this member can sign up as (main + alts) — drives the
        // "sign up as" picker that only appears when there's more than one.
        ViewBag.SignupCharacters = SignupCharacters.ForMember(user, null);
        ViewBag.SignUpRoleOptions = LinkshellManagerDiscordApp.Utils.EventJobCatalog.JobTypeOptions.ToList();
        ViewBag.SignUpMainJobOptions = LinkshellManagerDiscordApp.Utils.EventJobCatalog.MainJobOptions.ToList();
        ViewBag.SignUpSubJobOptions = LinkshellManagerDiscordApp.Utils.EventJobCatalog.SubJobOptions.ToList();

        var viewModels = events.Select(evt =>
        {
            PartySetupBoardViewModel? board = null;
            var userOwnsSlot = false;
            if (evt.PartySetupId.HasValue && boardsByEvent.TryGetValue(evt.Id, out var loaded))
            {
                board = loaded;
                userOwnsSlot = loaded.Alliances
                    .SelectMany(a => a.Parties)
                    .SelectMany(p => p.Slots)
                    .Any(s => s.SignedUpAppUserId == user.Id);
            }

            return new EventViewModel
            {
                Event = new Event
                {
                    Id = evt.Id,
                    LinkshellId = evt.LinkshellId,
                    EventName = evt.EventName,
                    EventType = evt.EventType,
                    EventLocation = evt.EventLocation,
                    CreatorUserId = evt.CreatorUserId,
                    StartTime = ConvertUtcToUserTimeZone(evt.StartTime, user.TimeZone),
                    EndTime = ConvertUtcToUserTimeZone(evt.EndTime, user.TimeZone),
                    CommencementStartTime = ConvertUtcToUserTimeZone(evt.CommencementStartTime, user.TimeZone),
                    Duration = evt.Duration,
                    DkpPerHour = evt.DkpPerHour,
                    EventDkp = evt.EventDkp,
                    Details = evt.Details,
                    TimeStamp = evt.TimeStamp,
                    PartySetupId = evt.PartySetupId,
                    // HNM signup-board state: drives the Post ToD / Edit ToD button, the
                    // "defeated / awaiting re-post" banner, and the monster shown in the ToD
                    // modal. HnmRepostAt is displayed, so convert it to the viewer's zone.
                    AssignedMonsterName = evt.AssignedMonsterName,
                    HnmDefeatedAt = ConvertUtcToUserTimeZone(evt.HnmDefeatedAt, user.TimeZone),
                    HnmRepostAt = ConvertUtcToUserTimeZone(evt.HnmRepostAt, user.TimeZone),
                    SourceTodId = evt.SourceTodId
                },
                PartySetupId = evt.PartySetupId,
                LinkedPartySetupName = evt.PartySetup?.Name,
                LinkedPartySetupMonsterName = evt.PartySetup?.AssignedMonsterName,
                LinkedPartySetupBoard = board,
                BoardTod = evt.HnmDefeatedAt.HasValue && evt.SourceTodId.HasValue
                    && sourceTodsById.TryGetValue(evt.SourceTodId.Value, out var srcTod)
                    ? new EventBoardTodPrefill
                    {
                        TimeLocal = ConvertUtcToUserTimeZone(srcTod.Time, user.TimeZone)?.ToString("yyyy-MM-ddTHH:mm:ss"),
                        Cooldown = srcTod.Cooldown,
                        Interval = srcTod.Interval,
                        DayNumber = srcTod.DayNumber,
                        Claim = srcTod.Claim,
                        Hq = srcTod.Hq
                    }
                    : null,
                CurrentUserOwnsLinkedPartySetupSlot = userOwnsSlot,
                AppUserEvents = evt.AppUserEvents.ToList(),
                CreatorCharacterName = evt.CreatorUserId is not null && creators.TryGetValue(evt.CreatorUserId, out var creatorName)
                    ? creatorName
                    : "Unknown"
            };
        }).ToList();

        return View(viewModels);
    }

    public async Task<IActionResult> Create()
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var model = await BuildEventViewModelAsync(user);
        // Fresh create form starts at DKP/hour = 1 (officers can zero it via the
        // "No DKP" toggle for social / no-dkp runs).
        model.Event.DkpPerHour ??= 1;
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(EventViewModel eventViewModel)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        eventViewModel.Event ??= new Event();
        eventViewModel.Event.LinkshellId = await ResolveActiveManageableLinkshellIdAsync(user);
        eventViewModel.LinkshellId = eventViewModel.Event.LinkshellId;
        ModelState.Remove("Event.LinkshellId");
        ModelState.Remove(nameof(EventViewModel.LinkshellId));

        var createMembership = await GetMembershipAsync(user.Id, eventViewModel.Event.LinkshellId);
        if (!CanManageLinkshell(createMembership))
        {
            ModelState.AddModelError(string.Empty, "Leader or officer access is required to create events for this linkshell.");
        }

        // HNM signup boards are created/managed in the Discord Activity now (gated by HNM
        // Outside Sign Up), so HNM isn't offered in the web create dropdown. This branch is
        // only reachable by a crafted POST; keep it correct by gating on the HNM toggle.
        var isHnm = string.Equals((eventViewModel.Event.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        string? monsterName = null;
        if (isHnm)
        {
            var outsideEnabled = createMembership?.Linkshell?.HnmOutsideSignupEnabled == true;
            if (!outsideEnabled)
            {
                ModelState.AddModelError("Event.EventType", "HNM signup boards require HNM Outside Sign Up to be enabled for this linkshell.");
            }
            monsterName = eventViewModel.Event.AssignedMonsterName?.Trim();
            if (string.IsNullOrWhiteSpace(monsterName))
            {
                ModelState.AddModelError("Event.AssignedMonsterName", "Select a monster for the HNM event.");
            }
        }

        if (!ModelState.IsValid)
        {
            var retryModel = await BuildEventViewModelAsync(user, eventViewModel);
            return View(retryModel);
        }

        // Cross-linkshell defense: only allow attaching a PartySetup that
        // belongs to this event's linkshell.
        var requestedPartySetupId = eventViewModel.PartySetupId;
        if (requestedPartySetupId.HasValue &&
            !await PartySetupBelongsToLinkshellAsync(requestedPartySetupId.Value, eventViewModel.Event.LinkshellId))
        {
            requestedPartySetupId = null;
        }

        var newEvent = new Event
        {
            LinkshellId = eventViewModel.Event.LinkshellId,
            EventName = eventViewModel.Event.EventName,
            EventType = eventViewModel.Event.EventType,
            EventLocation = eventViewModel.Event.EventLocation,
            StartTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.StartTime, user.TimeZone),
            EndTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.EndTime, user.TimeZone),
            Duration = eventViewModel.Event.Duration,
            DkpPerHour = eventViewModel.Event.DkpPerHour,
            Details = eventViewModel.Event.Details,
            AutoStart = eventViewModel.Event.AutoStart,
            CountsTowardActive = eventViewModel.Event.CountsTowardActive,
            PartySetupId = requestedPartySetupId,
            CreatorUserId = user.Id,
            TimeStamp = DateTime.UtcNow
        };

        if (isHnm)
        {
            // No-DKP signup board (mirrors the Activity HNM create path).
            newEvent.AssignedMonsterName = monsterName;
            newEvent.DayNumber = eventViewModel.Event.DayNumber;
            if (string.IsNullOrWhiteSpace(newEvent.EventLocation))
            {
                newEvent.EventLocation = HnmConfig.ZoneFor(monsterName);
            }
            newEvent.WindowCountOverride = HnmConfig.GetWindowCount(monsterName);
            newEvent.EndTime = null;
            newEvent.Duration = null;
            newEvent.DkpPerHour = 0;
            newEvent.AutoStart = false;
            newEvent.CountsTowardActive = false;
        }

        _context.Events.Add(newEvent);
        await _context.SaveChangesAsync();

        // Repeat-on-ToD: persist/refresh (or disable) the recurring-board template.
        // Works for a custom monster too — recurrence keys on the (case-insensitive)
        // AssignedMonsterName the ToD records; UpsertAsync self-guards null/whitespace.
        if (isHnm && eventViewModel.RepeatOnTod)
        {
            await HnmRecurringBoardService.UpsertAsync(_context, newEvent, eventViewModel.RepeatLeadHours, user.Id, HttpContext.RequestAborted);
        }
        else if (isHnm && !eventViewModel.RepeatOnTod)
        {
            await HnmRecurringBoardService.DisableAsync(_context, newEvent.LinkshellId, monsterName, HttpContext.RequestAborted);
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<bool> PartySetupBelongsToLinkshellAsync(int partySetupId, int linkshellId)
    {
        return await _context.PartySetups
            .AnyAsync(setup => setup.Id == partySetupId && setup.LinkshellId == linkshellId);
    }

    // Claim a linked party-setup slot FOR THIS EVENT (per-event roster, shared with
    // the Discord board + Activity panel). Any linkshell member may sign up.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignUpPartySlot(
        int eventId, int slotId, string? role, string? mainJob, string? subJob, string? returnUrl, bool asLeader = false, string? selectedCharacter = null, bool force = false)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null || eventEntity.PartySetupId is null)
        {
            return NotFound();
        }

        var slot = await _context.PartySetupSlots
            .Include(s => s.Party!).ThenInclude(p => p.Alliance!)
            .FirstOrDefaultAsync(s => s.Id == slotId);
        if (slot is null || slot.Party?.Alliance?.PartySetupId != eventEntity.PartySetupId)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
        if (membership is null)
        {
            TempData["Error"] = "You're not a member of this linkshell.";
            return SafeLocalRedirect(returnUrl);
        }

        var characterName = SignupCharacters.Resolve(user, membership, selectedCharacter);

        // "Fill earlier alliances first" nudge: if the linkshell wants it and there's still
        // an open slot this member's job can fill in an EARLIER alliance, stash a prompt and
        // bounce back to the board (no commit). "Sign up here anyway" re-posts with force.
        if (!force)
        {
            var fillInOrder = await _context.Linkshells
                .Where(l => l.Id == eventEntity.LinkshellId)
                .Select(l => l.FillAlliancesInOrder)
                .FirstOrDefaultAsync();
            if (fillInOrder)
            {
                var jobs = PartySetupSignupService.ResolveSignupJobs(slot, role, mainJob, subJob);
                if (jobs.Success)
                {
                    var setup = await _context.PartySetups
                        .Include(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
                        .FirstOrDefaultAsync(ps => ps.Id == eventEntity.PartySetupId.Value);
                    if (setup is not null)
                    {
                        var signups = await EventPartySignupService.GetSignupsForEventAsync(_context, eventId, HttpContext.RequestAborted);
                        var suggestion = PartyFillSuggestion.SuggestEarlierSlot(setup, signups, slot, jobs.Role, jobs.MainJob);
                        if (suggestion is not null && suggestion.Id != slot.Id)
                        {
                            TempData["SignupNudge"] = System.Text.Json.JsonSerializer.Serialize(new SignupNudgePayload(
                                eventId, slotId, suggestion.Id,
                                PartyFillSuggestion.DescribeSlot(setup, suggestion),
                                PartyFillSuggestion.RequirementLabel(suggestion),
                                jobs.Role, jobs.MainJob, jobs.SubJob, asLeader, selectedCharacter, returnUrl));
                            return SafeLocalRedirect(returnUrl);
                        }
                    }
                }
            }
        }

        var result = await EventPartySignupService.ClaimSlotAsync(
            _context, eventId, slot, user.Id, characterName, role, mainJob, subJob, HttpContext.RequestAborted, asLeader);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return SafeLocalRedirect(returnUrl);
        }
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A simultaneous claim won this slot first and tripped the unique
            // (EventId, PartySetupSlotId) index. Surface the friendly message
            // instead of a 500.
            TempData["Error"] = "That slot was just taken by another member. Pick another open slot.";
            return SafeLocalRedirect(returnUrl);
        }
        // Pre-start: drop their no-slot attendance. Live: materialize the claim as a
        // participation so a late joiner lands in the running event immediately.
        await EventPartySignupService.SyncParticipationAfterClaimAsync(_context, eventEntity, user.Id, HttpContext.RequestAborted);
        await _context.SaveChangesAsync();
        // Auto-promote earliest signup if the party just filled with no leader.
        await EventPartySignupService.ResolvePartyLeadershipAsync(_context, eventId, slot.PartySetupPartyId, HttpContext.RequestAborted);
        EnqueueEventBoardRefresh(eventId);

        return SafeLocalRedirect(returnUrl);
    }

    // "Make me alliance lead": the member — who must already hold a slot in this
    // event — takes their whole alliance's lead (👑 by the alliance name), moving it
    // off whoever currently holds it. Mirrors the Discord/Activity "Make Me Alliance
    // Lead" button. Purely a board designation (no perms).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MakeAllianceLead(int eventId, string? returnUrl)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null || eventEntity.PartySetupId is null)
        {
            return NotFound();
        }

        var result = await EventPartySignupService.MakeAllianceLeaderAsync(
            _context, eventId, user.Id, null, HttpContext.RequestAborted);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return SafeLocalRedirect(returnUrl);
        }
        await _context.SaveChangesAsync();
        EnqueueEventBoardRefresh(eventId);

        return SafeLocalRedirect(returnUrl);
    }

    // Builds a per-event party board (the template tree with this event's slot
    // signups overlaid). Shared by the queued Index view and the live Start view so
    // both render the same alliance → party → slot board. `sign` maps slot id → the
    // event's signup for that slot.
    internal static PartySetupBoardViewModel BuildPartySetupBoard(
        PartySetup ps, IReadOnlyDictionary<int, EventPartySlotSignup> sign) => new()
    {
        Id = ps.Id,
        Name = ps.Name,
        Alliances = ps.Alliances.OrderBy(a => a.SortOrder).Select(a => new PartySetupAllianceView
        {
            AllianceId = a.Id,
            Name = string.IsNullOrWhiteSpace(a.Name) ? $"Alliance {a.SortOrder + 1}" : a.Name,
            // The alliance lead (if any): the one signup in this alliance carrying the crown.
            LeadAppUserId = a.Parties.SelectMany(p => p.Slots)
                .Select(s => sign.TryGetValue(s.Id, out var su) ? su : null)
                .FirstOrDefault(su => su is { IsAllianceLeader: true })?.AppUserId,
            LeadCharacterName = a.Parties.SelectMany(p => p.Slots)
                .Select(s => sign.TryGetValue(s.Id, out var su) ? su : null)
                .FirstOrDefault(su => su is { IsAllianceLeader: true })?.CharacterName,
            Parties = a.Parties.OrderBy(p => p.SortOrder).Select(p => new PartySetupPartyView
            {
                PartyId = p.Id,
                Name = string.IsNullOrWhiteSpace(p.Name) ? $"Party {p.SortOrder + 1}" : p.Name!,
                Slots = p.Slots.OrderBy(s => s.SortOrder).Select(s =>
                {
                    sign.TryGetValue(s.Id, out var su);
                    return new PartySetupSlotView
                    {
                        SlotId = s.Id,
                        Position = s.SortOrder + 1,
                        RequirementType = s.RequirementType,
                        Role = s.Role,
                        MainJob = s.MainJob,
                        SubJob = s.SubJob,
                        Label = s.Label,
                        IsPartyLeader = s.IsPartyLeader,
                        SignedUpAppUserId = su?.AppUserId,
                        SignedUpCharacterName = su?.CharacterName,
                        SignedUpIsPartyLeader = su?.IsPartyLeader ?? false,
                        SignedUpRole = su?.Role,
                        SignedUpMainJob = su?.MainJob,
                        SignedUpSubJob = su?.SubJob
                    };
                }).ToList()
            }).ToList()
        }).ToList()
    };

    // Drop a party slot in this event. With no slotId, the caller drops their own
    // slot (pre-start only). With a slotId (officer "Clear" on the live board), an
    // officer frees that slot — and, once the event is live, that also removes the
    // member's participation so the board and the DKP roster stay consistent.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawPartySlot(int eventId, int? slotId, string? returnUrl)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
        var isOfficer = CanManageLinkshell(membership);

        var signup = slotId is { } sid
            ? await _context.EventPartySlotSignups.Include(s => s.PartySetupSlot)
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.PartySetupSlotId == sid)
            : await _context.EventPartySlotSignups.Include(s => s.PartySetupSlot)
                .FirstOrDefaultAsync(s => s.EventId == eventId && s.AppUserId == user.Id);
        if (signup is null)
        {
            return SafeLocalRedirect(returnUrl);
        }

        var isHolder = signup.AppUserId == user.Id;
        // Once live, only an officer can clear a slot — no member self-withdraw mid-run.
        if (!EventPartySignupService.MemberCanWithdraw(eventEntity) && !isOfficer)
        {
            TempData["Error"] = "The event is live — ask an officer to free your slot.";
            return SafeLocalRedirect(returnUrl);
        }
        if (!isHolder && !isOfficer)
        {
            return Forbid();
        }

        var affectedPartyId = signup.PartySetupSlot?.PartySetupPartyId;
        if (eventEntity.CommencementStartTime is not null)
        {
            var startTime = eventEntity.CommencementStartTime ?? eventEntity.StartTime;
            affectedPartyId = await EventPartySignupService.MoveSlotSignupToNoSlotAsync(
                _context, eventId, signup, startTime, HttpContext.RequestAborted);
        }
        else
        {
            _context.EventPartySlotSignups.Remove(signup);
        }
        await _context.SaveChangesAsync();
        await EventPartySignupService.ResolvePartyLeadershipAsync(_context, eventId, affectedPartyId, HttpContext.RequestAborted);
        EnqueueEventBoardRefresh(eventId);

        return SafeLocalRedirect(returnUrl);
    }

    private IActionResult SafeLocalRedirect(string? returnUrl)
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? Redirect(returnUrl)
            : RedirectToAction(nameof(Index));

    public async Task<IActionResult> Edit(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToEdit = await _context.Events
            .FirstOrDefaultAsync(evt => evt.Id == id);

        if (eventToEdit is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToEdit.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var model = await BuildEventViewModelAsync(user);
        model.Event = new Event
        {
            Id = eventToEdit.Id,
            LinkshellId = eventToEdit.LinkshellId,
            EventName = eventToEdit.EventName,
            EventType = eventToEdit.EventType,
            EventLocation = eventToEdit.EventLocation,
            StartTime = ConvertUtcToUserTimeZone(eventToEdit.StartTime, user.TimeZone),
            EndTime = ConvertUtcToUserTimeZone(eventToEdit.EndTime, user.TimeZone),
            Duration = eventToEdit.Duration,
            DkpPerHour = eventToEdit.DkpPerHour,
            Details = eventToEdit.Details,
            AutoStart = eventToEdit.AutoStart,
            CountsTowardActive = eventToEdit.CountsTowardActive,
            // Carry the HNM monster so the monster picker pre-selects it on edit.
            AssignedMonsterName = eventToEdit.AssignedMonsterName,
            PartySetupId = eventToEdit.PartySetupId
        };
        model.PartySetupId = eventToEdit.PartySetupId;
        model.LinkshellId = eventToEdit.LinkshellId;

        // Pre-fill the "Repeat post when ToD is updated" controls from the linkshell's
        // recurring-board template for this monster (parity with the Activity edit form).
        var editMonster = eventToEdit.AssignedMonsterName?.Trim();
        if (!string.IsNullOrWhiteSpace(editMonster))
        {
            var board = await _context.HnmRecurringBoards
                .FirstOrDefaultAsync(b => b.LinkshellId == eventToEdit.LinkshellId
                    && b.MonsterName.ToLower() == editMonster.ToLower());
            model.RepeatOnTod = board?.Enabled == true;
            model.RepeatLeadHours = board?.LeadHours;
        }

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, EventViewModel eventViewModel)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        if (!ModelState.IsValid)
        {
            var retryModel = await BuildEventViewModelAsync(user, eventViewModel);
            return View(retryModel);
        }

        var eventToUpdate = await _context.Events
            .FirstOrDefaultAsync(evt => evt.Id == id);

        if (eventToUpdate is null)
        {
            return NotFound();
        }

        var currentMembership = await GetMembershipAsync(user.Id, eventToUpdate.LinkshellId);
        var targetMembership = await GetMembershipAsync(user.Id, eventViewModel.Event.LinkshellId);
        if (!CanManageLinkshell(currentMembership) || !CanManageLinkshell(targetMembership))
        {
            return Forbid();
        }

        // Converting an event TO an HNM signup board is gated by HNM Outside Sign Up the
        // same way Create is — the web form only offers HNM for already-HNM events, so a
        // non-HNM→HNM conversion can only arrive via a crafted POST. Gate on the TARGET
        // linkshell (where the event ends up). Editing an existing HNM event is unaffected.
        var becomingHnm = string.Equals((eventViewModel.Event.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        var wasHnm = string.Equals((eventToUpdate.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (becomingHnm && !wasHnm && targetMembership?.Linkshell?.HnmOutsideSignupEnabled != true)
        {
            ModelState.AddModelError("Event.EventType", "HNM signup boards require HNM Outside Sign Up to be enabled for this linkshell.");
            var retryModel = await BuildEventViewModelAsync(user, eventViewModel);
            return View(retryModel);
        }

        // If this event's board was customized into a per-event snapshot (which the
        // template picker can't represent), keep it — its slots are managed from the
        // board editor, not this form — so editing event details never wipes the board.
        var currentIsSnapshot = eventToUpdate.PartySetupId is { } curSetupId
            && await _context.PartySetups.AnyAsync(ps => ps.Id == curSetupId && ps.OwnerEventId == eventToUpdate.Id);

        eventToUpdate.LinkshellId = eventViewModel.Event.LinkshellId;
        eventToUpdate.EventName = eventViewModel.Event.EventName;
        eventToUpdate.EventType = eventViewModel.Event.EventType;
        eventToUpdate.EventLocation = eventViewModel.Event.EventLocation;
        eventToUpdate.StartTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.StartTime, user.TimeZone);
        eventToUpdate.EndTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.EndTime, user.TimeZone);
        eventToUpdate.Duration = eventViewModel.Event.Duration;
        eventToUpdate.DkpPerHour = eventViewModel.Event.DkpPerHour;
        eventToUpdate.Details = eventViewModel.Event.Details;
        eventToUpdate.AutoStart = eventViewModel.Event.AutoStart;
        eventToUpdate.CountsTowardActive = eventViewModel.Event.CountsTowardActive;

        // HNM signup boards never award DKP or feed activity tracking and carry no
        // end/duration. HNM is created/managed in the Discord Activity now, but an existing
        // HNM event can still be edited here, so re-assert those fields to keep the board a
        // no-DKP, untracked board regardless of what the form posted.
        var isHnm = string.Equals((eventViewModel.Event.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase);
        if (isHnm)
        {
            var monsterName = eventViewModel.Event.AssignedMonsterName?.Trim();
            if (!string.IsNullOrWhiteSpace(monsterName))
            {
                eventToUpdate.AssignedMonsterName = monsterName;
                eventToUpdate.WindowCountOverride = HnmConfig.GetWindowCount(monsterName);
                if (string.IsNullOrWhiteSpace(eventToUpdate.EventLocation))
                {
                    eventToUpdate.EventLocation = HnmConfig.ZoneFor(monsterName);
                }
            }
            eventToUpdate.DayNumber = eventViewModel.Event.DayNumber;
            eventToUpdate.EndTime = null;
            eventToUpdate.Duration = null;
            eventToUpdate.DkpPerHour = 0;
            eventToUpdate.AutoStart = false;
            eventToUpdate.CountsTowardActive = false;
        }

        if (!currentIsSnapshot)
        {
            var requestedPartySetupId = eventViewModel.PartySetupId;
            if (requestedPartySetupId.HasValue &&
                !await PartySetupBelongsToLinkshellAsync(requestedPartySetupId.Value, eventViewModel.Event.LinkshellId))
            {
                requestedPartySetupId = null;
            }
            // Changing the linked setup orphans the old slot signups (they're keyed to the
            // old setup's slots), so move them to "Also Attending" rather than silently
            // dropping the roster (parity with the Activity UpdateEvent path).
            if (requestedPartySetupId != eventToUpdate.PartySetupId)
            {
                await EventPartySignupService.MoveSlotSignupsToNoSlotAsync(
                    _context, eventToUpdate.Id, eventToUpdate.StartTime, HttpContext.RequestAborted);
            }
            eventToUpdate.PartySetupId = requestedPartySetupId;
        }

        await _context.SaveChangesAsync();

        // Repeat-on-ToD: persist/refresh (or disable) the recurring-board template, matching
        // the Create path (and the Activity's update endpoint).
        if (isHnm)
        {
            var monsterName = eventToUpdate.AssignedMonsterName?.Trim();
            if (eventViewModel.RepeatOnTod
                && !string.IsNullOrWhiteSpace(monsterName))
            {
                await HnmRecurringBoardService.UpsertAsync(_context, eventToUpdate, eventViewModel.RepeatLeadHours, user.Id, HttpContext.RequestAborted);
            }
            else if (!eventViewModel.RepeatOnTod)
            {
                await HnmRecurringBoardService.DisableAsync(_context, eventToUpdate.LinkshellId, monsterName, HttpContext.RequestAborted);
            }
        }

        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Delete(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToDelete = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == id);
        if (eventToDelete is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToDelete.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        // Match the POST-side guard in CancelEvent: a live event can't be
        // deleted, only ended. Block the confirmation page so a hand-crafted
        // URL (or stale browser tab) doesn't take the user somewhere the
        // submit would just bounce.
        if (eventToDelete.CommencementStartTime.HasValue)
        {
            TempData["Error"] = "Live events cannot be deleted. End the event first.";
            return RedirectToAction(nameof(Index));
        }

        return View(eventToDelete);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        return await CancelEvent(id);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelEvent(int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToDelete = await _context.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId);

        if (eventToDelete is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToDelete.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        if (eventToDelete.CommencementStartTime.HasValue)
        {
            return BadRequest("Live events cannot be canceled. End the event instead.");
        }

        _context.AppUserEvents.RemoveRange(eventToDelete.AppUserEvents);
        _context.EventLootDetails.RemoveRange(eventToDelete.EventLootDetails);
        _context.Events.Remove(eventToDelete);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartEvent(int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToStart = await _context.Events
            .Include(evt => evt.AppUserEvents)
            .FirstOrDefaultAsync(evt => evt.Id == eventId);

        if (eventToStart is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToStart.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        eventToStart.CommencementStartTime ??= DateTime.UtcNow;
        eventToStart.StarterUserId ??= user.Id;

        // Bring party-slot signups (Discord post / Activity) into the live event as
        // pending attendees — without this they'd never appear in the started event.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(_context, eventToStart, default);

        foreach (var participation in eventToStart.AppUserEvents)
        {
            participation.StartTime ??= eventToStart.CommencementStartTime;
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitLootDetails(int eventId, string itemName, string itemWinner, int winningDkpSpent)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        // Loot validation errors surface as a dismissable toast on the event page
        // (the message rides back in TempData over a redirect) instead of replacing
        // the page with a bare 400 body.
        IActionResult LootError(string message)
        {
            TempData["LootError"] = message;
            return RedirectToAction(nameof(Start), new { eventId });
        }

        const int MaxItemNameLength = 200;
        const int MaxLootDkp = 1_000_000;

        var trimmedItemName = (itemName ?? string.Empty).Trim();
        var trimmedWinner = (itemWinner ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(trimmedItemName) || trimmedItemName.Length > MaxItemNameLength)
        {
            return LootError("Item name is required and must be 200 characters or fewer.");
        }
        if (string.IsNullOrEmpty(trimmedWinner) || trimmedWinner.Length > MaxItemNameLength)
        {
            return LootError("Item winner is required and must be 200 characters or fewer.");
        }
        if (winningDkpSpent < 0 || winningDkpSpent > MaxLootDkp)
        {
            return LootError($"Winning DKP must be between 0 and {MaxLootDkp:N0}.");
        }

        // Winner must be a current linkshell member's character — MAIN or either ALT
        // (alts share the main's account). An officer can't assign loot to a non-roster
        // name. The matched character name is stored as-is so the loot log shows who
        // actually won; the DKP is deducted from the owning account at close (see
        // ResolveLootWinnerMembership) and balance-checked against it here.
        var rosterMembers = await _context.AppUserLinkshells
            .Include(link => link.AppUser)
            .Where(link => link.LinkshellId == eventEntity.LinkshellId && link.AppUserId != null)
            .ToListAsync();
        string? MatchName(string? name) =>
            !string.IsNullOrWhiteSpace(name) && string.Equals(name.Trim(), trimmedWinner, StringComparison.OrdinalIgnoreCase)
                ? name.Trim()
                : null;
        var rosterMatch = rosterMembers
            .Select(link => MatchName(link.CharacterName) ?? MatchName(link.AppUser?.AltCharacterName1) ?? MatchName(link.AppUser?.AltCharacterName2))
            .FirstOrDefault(matched => matched is not null);
        if (rosterMatch is null)
        {
            return LootError("Winner must be a current linkshell member (main or alt).");
        }

        // Block awarding loot the winner can't afford (DKP is deducted at close,
        // so this is the only point we can stop the balance going negative).
        var insufficient = await LootDkpGuard.CheckEventLootAsync(
            _context, _dkpPools, _dkpPoolBalances, eventId, eventEntity.LinkshellId,
            rosterMatch, winningDkpSpent, default);
        if (insufficient is not null)
        {
            return LootError(insufficient);
        }

        _context.EventLootDetails.Add(new EventLootDetail
        {
            EventId = eventId,
            ItemName = trimmedItemName,
            ItemWinner = rosterMatch,
            WinningDkpSpent = winningDkpSpent
        });

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EndEvent(int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId);

        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        // Can't end while attendees are still pending confirmation (IsVerified == null).
        // Every pending member must be confirmed present or removed first.
        var pendingCount = eventEntity.AppUserEvents.Count(p => p.IsVerified == null);
        if (pendingCount > 0)
        {
            return BadRequest($"Confirm or remove the {pendingCount} member(s) still pending in attendance before ending the event.");
        }

        await EndEventCoreAsync(_context, _dkpLedger, _dkpPools, eventEntity);

        return RedirectToAction(nameof(Index), "EventHistory");
    }

    // Shared end-event logic. Caller is responsible for loading the Event with
    // its AppUserEvents and EventLootDetails included, and for verifying auth
    // (linkshell membership / management permission) before calling. This
    // helper writes the EventHistory + DkpLedgerEntry rows, removes the
    // related AppUserEvents / EventLootDetails / Event, and saves.
    internal sealed record EndEventParticipantSummary(
        string? CharacterName,
        string? JobName,
        string? SubJobName,
        double? DurationHours,
        double? DkpEarned,
        int? WindowsAttended);

    internal sealed record EndEventResult(
        DateTime EndTimeUtc,
        IReadOnlyList<EndEventParticipantSummary> Participants,
        int WindowCount,
        int EventHistoryId,
        bool HasLootDeductions);

    internal static async Task<EndEventResult> EndEventCoreAsync(
        ApplicationDbContext dbContext, DkpLedgerWriter dkpLedger, DkpPoolResolver dkpPools, Event eventEntity)
    {
        var endTimeUtc = DateTime.UtcNow;
        var participantSummaries = new List<EndEventParticipantSummary>();

        // The event's type decides which DKP pool it pays INTO and which pool its loot is paid
        // OUT of — resolved once here, not per participant. An event has one type, so a Sky event
        // credits the Sky pool and its loot is bought with Sky-pool DKP. Unmapped/custom/null
        // types land in the linkshell's default pool, which is where everything lives until an
        // officer partitions their event types.
        var eventPool = DkpPoolRef.Derived(eventEntity.EventType);

        // Backfill any party-board signups that never became AppUserEvents so the
        // closed event history matches the live roster and loot UI.
        await EventPartySignupService.MaterializeSignupsAsParticipantsAsync(dbContext, eventEntity, CancellationToken.None);

        // Windowed events (HNM Style / Claim/Kill) award DKP per window attended,
        // not per hour of presence: the DkpPerHour column is reused as
        // DkpPerWindow when WindowCount > 1, and the per-participation total is
        // (windowsAttended * dkpPerWindow). Count windows attended once up front
        // so the per-participation loop below can read from a dictionary.
        var windowCount = eventEntity.WindowCountOverride
            ?? LinkshellManagerDiscordApp.Services.HnmConfig.GetWindowCount(eventEntity.EventName);
        var isWindowed = windowCount > 1;
        Dictionary<int, int> windowsAttendedByParticipationId = isWindowed
            ? await dbContext.AppUserEventWindows
                .Where(w => w.EventAttendanceWindow!.EventId == eventEntity.Id)
                .GroupBy(w => w.AppUserEventId)
                .Select(g => new { ParticipationId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.ParticipationId, x => x.Count)
            : new Dictionary<int, int>();
        var history = new EventHistory
        {
            LinkshellId = eventEntity.LinkshellId,
            EventName = eventEntity.EventName,
            EventType = eventEntity.EventType,
            EventLocation = eventEntity.EventLocation,
            StartDate = eventEntity.StartTime?.Date,
            StartTime = eventEntity.StartTime,
            EndTime = endTimeUtc,
            CommencementStartTime = eventEntity.CommencementStartTime,
            Duration = eventEntity.CommencementStartTime.HasValue ? (endTimeUtc - eventEntity.CommencementStartTime.Value).TotalHours : eventEntity.Duration,
            DkpPerHour = eventEntity.DkpPerHour,
            EventDkp = eventEntity.EventDkp,
            Details = eventEntity.Details,
            CountsTowardActive = eventEntity.CountsTowardActive,
            TimeStamp = DateTime.UtcNow,
            AppUserEventHistories = new List<AppUserEventHistory>()
        };

        var linkshellMemberships = await dbContext.AppUserLinkshells
            .Include(link => link.AppUser) // alt names, for resolving alt-won loot to the account
            .Where(link => link.LinkshellId == eventEntity.LinkshellId && link.AppUserId != null)
            .ToListAsync();
        var membershipsByAppUserId = linkshellMemberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .GroupBy(link => link.AppUserId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var participantsByCharacterName = eventEntity.AppUserEvents
            .Where(participation => !string.IsNullOrWhiteSpace(participation.CharacterName))
            .GroupBy(participation => participation.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // One history/ledger row per account. There is no DB uniqueness on AppUserEvent, so an
        // account can hold two participations for an event (e.g. a website join + an addon post
        // under an alt); a second history row would violate the unique (EventHistoryId,
        // AppUserId) index and throw at save. Account-less rows (null AppUserId) are outside
        // that filtered index, so they're exempt from this dedup.
        var creditedAppUserIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
            if (!string.IsNullOrWhiteSpace(participation.AppUserId) && !creditedAppUserIds.Add(participation.AppUserId))
            {
                continue;
            }
            var durationHours = CalculateAccumulatedDurationHours(participation, endTimeUtc, eventEntity.CommencementStartTime);
            int? windowsAttended = isWindowed
                ? windowsAttendedByParticipationId.GetValueOrDefault(participation.Id, 0)
                : (int?)null;
            // Pay DKP for the ACTUAL time present, snapping the DKP value (not the
            // duration) to the linkshell's increment. Rounding the duration first
            // floored sub-quarter-hour events to 0h and paid present members 0 DKP.
            var eventDkp = isWindowed
                ? (windowsAttended ?? 0) * (eventEntity.DkpPerHour ?? 0)
                : DkpRounding.Round(durationHours * (eventEntity.DkpPerHour ?? 0), DkpRounding.StepFor(eventEntity.Linkshell?.DkpRoundingIncrement));

            participation.Duration = durationHours;
            participation.EventDkp = eventDkp;

            participantSummaries.Add(new EndEventParticipantSummary(
                participation.CharacterName,
                participation.JobName,
                participation.SubJobName,
                durationHours,
                eventDkp,
                windowsAttended));

            history.AppUserEventHistories.Add(new AppUserEventHistory
            {
                AppUserId = participation.AppUserId,
                CharacterName = participation.CharacterName,
                JobName = participation.JobName,
                SubJobName = participation.SubJobName,
                JobType = participation.JobType,
                StartTime = participation.StartTime,
                Duration = durationHours,
                EventDkp = eventDkp,
                IsQuickJoin = participation.IsQuickJoin,
                IsVerified = participation.IsVerified,
                Proctor = participation.Proctor,
                ActiveCredit = eventEntity.CountsTowardActive
            });

            if (!string.IsNullOrWhiteSpace(participation.AppUserId) &&
                membershipsByAppUserId.TryGetValue(participation.AppUserId, out var linkshellMembership))
            {
                await dkpLedger.AppendAsync(
                    linkshellMembership,
                    "EventEarned",
                    eventDkp,
                    endTimeUtc,
                    eventPool,
                    new DkpEntryContext(
                        CharacterName: participation.CharacterName,
                        EventName: eventEntity.EventName,
                        EventType: eventEntity.EventType,
                        EventLocation: eventEntity.EventLocation,
                        EventStartTime: eventEntity.StartTime,
                        EventEndTime: endTimeUtc,
                        Details: "DKP earned from completed event.",
                        EventHistory: history),
                    CancellationToken.None);
            }
        }

        dbContext.EventHistories.Add(history);
        var hasLootDeductions = false;
        foreach (var lootDetail in eventEntity.EventLootDetails.OrderBy(detail => detail.Id))
        {
            if (lootDetail.WinningDkpSpent.GetValueOrDefault() <= 0)
            {
                continue;
            }

            var winnerMembership = ResolveLootWinnerMembership(
                lootDetail.ItemWinner,
                membershipsByAppUserId,
                participantsByCharacterName,
                linkshellMemberships);
            if (winnerMembership is null || string.IsNullOrWhiteSpace(winnerMembership.AppUserId))
            {
                continue;
            }

            var amount = -lootDetail.WinningDkpSpent.GetValueOrDefault();

            // Stamp the actual deducted amount onto the loot row so future
            // Loot History edits can refund precisely (matches the ToD
            // ActualDeductedDkp pattern in HelpersTods.AdjustTodLootDkpAsync).
            lootDetail.ActualDeductedDkp = Math.Abs(amount);

            // Same pool the event earned into: you buy this event's loot with this event's DKP.
            await dkpLedger.AppendAsync(
                winnerMembership,
                "LootSpent",
                amount,
                endTimeUtc,
                eventPool,
                new DkpEntryContext(
                    CharacterName: winnerMembership.CharacterName,
                    EventName: eventEntity.EventName,
                    EventType: eventEntity.EventType,
                    EventLocation: eventEntity.EventLocation,
                    EventStartTime: eventEntity.StartTime,
                    EventEndTime: endTimeUtc,
                    ItemName: lootDetail.ItemName,
                    Details: $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.",
                    EventHistory: history,
                    SourceEventLootDetailId: lootDetail.Id),
                CancellationToken.None);
            hasLootDeductions = true;
        }

        // Preserve EventLootDetails post-close so officers can edit them via
        // Loot History. Re-parent each row to the new EventHistory and detach
        // the EventId before the parent Event is deleted below. The
        // EventLootDetail.EventId FK was changed to SetNull in
        // AddLootHistoryAudit, so the Event delete won't cascade-remove them.
        foreach (var lootDetail in eventEntity.EventLootDetails)
        {
            lootDetail.EventHistory = history;
            lootDetail.Event = null;
            lootDetail.EventId = null;
        }
        // Participations materialized earlier in THIS close (late board signups) are still in
        // the Added state with TEMPORARY keys — EF can't transition those to Deleted. Detach
        // them (never persisted; history + DKP already recorded) and delete only the rows that
        // actually exist. Mirrors the Activity EndEventAsync guard.
        foreach (var participation in eventEntity.AppUserEvents.ToList())
        {
            var entry = dbContext.Entry(participation);
            if (entry.State == EntityState.Added)
            {
                // Detach AND drop from the navigation so EF's change detector can't re-track it
                // (and re-attempt the temp-key delete) when the event is removed below.
                entry.State = EntityState.Detached;
                eventEntity.AppUserEvents.Remove(participation);
            }
            else
            {
                dbContext.AppUserEvents.Remove(participation);
            }
        }
        dbContext.Events.Remove(eventEntity);
        await dbContext.SaveChangesAsync();

        // A counting event just closed → recompute each member's Active/Inactive
        // status from attendance (no-op when the linkshell hasn't enabled tracking).
        // Best-effort: the close is already committed above, so a recompute failure must NOT
        // fail the request (mirrors the Activity EndEventAsync guard). Status re-derives on the
        // next close / roster load.
        try
        {
            await new MemberActivityService(dbContext).ApplyComputedStatusAsync(eventEntity.LinkshellId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Swallow — the event is ended; activity status will recompute next time. But LOG
            // it (resolving the app logger from the DbContext, since this is a static helper)
            // so a persistently-failing recompute isn't invisible. Mirrors the Activity twin.
            dbContext.GetService<ILoggerFactory>()?
                .CreateLogger(typeof(EventController).FullName!)
                .LogError(ex, "Active/Inactive recompute failed after ending event for linkshell {LinkshellId}.",
                    eventEntity.LinkshellId);
        }

        return new EndEventResult(endTimeUtc, participantSummaries, windowCount, history.Id, hasLootDeductions);
    }
}
