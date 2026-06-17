using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
                    PartySetupId = evt.PartySetupId
                },
                PartySetupId = evt.PartySetupId,
                LinkedPartySetupName = evt.PartySetup?.Name,
                LinkedPartySetupMonsterName = evt.PartySetup?.AssignedMonsterName,
                LinkedPartySetupBoard = board,
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

        // HNM events are created automatically by the in-game addon (from member
        // ToD captures), never by hand -- so reject manual creation of the "HNM"
        // type. This also closes the "Other" free-text loophole in the picker.
        if (string.Equals((eventViewModel.Event.EventType ?? string.Empty).Trim(), "HNM", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Event.EventType", "HNM events are created automatically by the in-game addon and can't be added manually.");
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

        _context.Events.Add(newEvent);
        await _context.SaveChangesAsync();

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
        int eventId, int slotId, string? role, string? mainJob, string? subJob, string? returnUrl, bool asLeader = false, string? selectedCharacter = null)
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
        _context.EventPartySlotSignups.Remove(signup);
        if (eventEntity.CommencementStartTime is not null && signup.AppUserId is not null)
        {
            await EventPartySignupService.RemoveParticipationAsync(_context, eventId, signup.AppUserId, HttpContext.RequestAborted);
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
            PartySetupId = eventToEdit.PartySetupId
        };
        model.PartySetupId = eventToEdit.PartySetupId;
        model.LinkshellId = eventToEdit.LinkshellId;

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
            _context, eventId, eventEntity.LinkshellId, rosterMatch, winningDkpSpent, default);
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

        await EndEventCoreAsync(_context, eventEntity);

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

    internal static async Task<EndEventResult> EndEventCoreAsync(ApplicationDbContext dbContext, Event eventEntity)
    {
        var endTimeUtc = DateTime.UtcNow;
        var participantSummaries = new List<EndEventParticipantSummary>();

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
        var ledgerEntries = new List<DkpLedgerEntry>();
        var nextSequenceByAppUserId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var participation in eventEntity.AppUserEvents)
        {
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
                linkshellMembership.LinkshellDkp = (linkshellMembership.LinkshellDkp ?? 0) + eventDkp;
                nextSequenceByAppUserId[participation.AppUserId] = 2;
            }

            if (!string.IsNullOrWhiteSpace(participation.AppUserId))
            {
                ledgerEntries.Add(new DkpLedgerEntry
                {
                    AppUserId = participation.AppUserId,
                    EventHistory = history,
                    LinkshellId = eventEntity.LinkshellId,
                    EntryType = "EventEarned",
                    Amount = eventDkp,
                    Sequence = 1,
                    OccurredAt = endTimeUtc,
                    CharacterName = participation.CharacterName,
                    EventName = eventEntity.EventName,
                    EventType = eventEntity.EventType,
                    EventLocation = eventEntity.EventLocation,
                    EventStartTime = eventEntity.StartTime,
                    EventEndTime = endTimeUtc,
                    Details = "DKP earned from completed event."
                });
            }
        }

        dbContext.EventHistories.Add(history);
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
            winnerMembership.LinkshellDkp = (winnerMembership.LinkshellDkp ?? 0) + amount;

            // Stamp the actual deducted amount onto the loot row so future
            // Loot History edits can refund precisely (matches the ToD
            // ActualDeductedDkp pattern in HelpersTods.AdjustTodLootDkpAsync).
            lootDetail.ActualDeductedDkp = Math.Abs(amount);

            var currentSequence = nextSequenceByAppUserId.GetValueOrDefault(winnerMembership.AppUserId, 2);
            ledgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = winnerMembership.AppUserId,
                EventHistory = history,
                LinkshellId = eventEntity.LinkshellId,
                EntryType = "LootSpent",
                Amount = amount,
                Sequence = currentSequence,
                OccurredAt = endTimeUtc,
                CharacterName = winnerMembership.CharacterName,
                EventName = eventEntity.EventName,
                EventType = eventEntity.EventType,
                EventLocation = eventEntity.EventLocation,
                EventStartTime = eventEntity.StartTime,
                EventEndTime = endTimeUtc,
                ItemName = lootDetail.ItemName,
                Details = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}.",
                SourceEventLootDetailId = lootDetail.Id
            });
            nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;
        }

        var hasLootDeductions = ledgerEntries.Any(entry => entry.EntryType == "LootSpent");
        dbContext.DkpLedgerEntries.AddRange(ledgerEntries);

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
        dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);
        dbContext.Events.Remove(eventEntity);
        await dbContext.SaveChangesAsync();

        // A counting event just closed → recompute each member's Active/Inactive
        // status from attendance (no-op when the linkshell hasn't enabled tracking).
        await new MemberActivityService(dbContext).ApplyComputedStatusAsync(eventEntity.LinkshellId, CancellationToken.None);

        return new EndEventResult(endTimeUtc, participantSummaries, windowCount, history.Id, hasLootDeductions);
    }
}
