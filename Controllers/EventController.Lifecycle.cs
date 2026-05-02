using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
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
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
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

        var viewModels = events.Select(evt => new EventViewModel
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
                TimeStamp = evt.TimeStamp
            },
            Jobs = evt.Jobs.ToList(),
            AppUserEvents = evt.AppUserEvents.ToList(),
            CreatorCharacterName = evt.CreatorUserId is not null && creators.TryGetValue(evt.CreatorUserId, out var creatorName)
                ? creatorName
                : "Unknown"
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

        return View(await BuildEventViewModelAsync(user));
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

        var createMembership = await GetMembershipAsync(user.Id, eventViewModel.Event.LinkshellId);
        if (!CanManageLinkshell(createMembership))
        {
            ModelState.AddModelError(string.Empty, "Leader or officer access is required to create events for this linkshell.");
        }

        if (!ModelState.IsValid)
        {
            var retryModel = await BuildEventViewModelAsync(user, eventViewModel);
            return View(retryModel);
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
            CreatorUserId = user.Id,
            TimeStamp = DateTime.UtcNow
        };

        _context.Events.Add(newEvent);
        await _context.SaveChangesAsync();

        foreach (var job in eventViewModel.Jobs.Where(job => !string.IsNullOrWhiteSpace(job.JobName)))
        {
            job.EventId = newEvent.Id;
            job.SignedUp = 0;
            job.Enlisted ??= new List<string>();
            _context.Jobs.Add(job);
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToEdit = await _context.Events
            .Include(evt => evt.Jobs)
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
            Details = eventToEdit.Details
        };
        model.Jobs = eventToEdit.Jobs.ToList();
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
            .Include(evt => evt.Jobs)
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

        eventToUpdate.LinkshellId = eventViewModel.Event.LinkshellId;
        eventToUpdate.EventName = eventViewModel.Event.EventName;
        eventToUpdate.EventType = eventViewModel.Event.EventType;
        eventToUpdate.EventLocation = eventViewModel.Event.EventLocation;
        eventToUpdate.StartTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.StartTime, user.TimeZone);
        eventToUpdate.EndTime = ConvertUserTimeZoneToUtc(eventViewModel.Event.EndTime, user.TimeZone);
        eventToUpdate.Duration = eventViewModel.Event.Duration;
        eventToUpdate.DkpPerHour = eventViewModel.Event.DkpPerHour;
        eventToUpdate.Details = eventViewModel.Event.Details;

        _context.Jobs.RemoveRange(eventToUpdate.Jobs);
        await _context.SaveChangesAsync();

        foreach (var job in eventViewModel.Jobs.Where(job => !string.IsNullOrWhiteSpace(job.JobName)))
        {
            job.Id = 0;
            job.EventId = eventToUpdate.Id;
            job.SignedUp = job.Enlisted?.Count ?? 0;
            job.Enlisted ??= new List<string>();
            _context.Jobs.Add(job);
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
            .Include(evt => evt.Jobs)
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

        _context.Jobs.RemoveRange(eventToDelete.Jobs);
        _context.AppUserEvents.RemoveRange(eventToDelete.AppUserEvents);
        _context.EventLootDetails.RemoveRange(eventToDelete.EventLootDetails);
        _context.Events.Remove(eventToDelete);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
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

        foreach (var participation in eventToStart.AppUserEvents)
        {
            participation.StartTime ??= eventToStart.CommencementStartTime;
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
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

        _context.EventLootDetails.Add(new EventLootDetail
        {
            EventId = eventId,
            ItemName = itemName,
            ItemWinner = itemWinner,
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

        await EndEventCoreAsync(_context, eventEntity);

        return RedirectToAction(nameof(Index), "EventHistory");
    }

    // Shared end-event logic. Caller is responsible for loading the Event with
    // its AppUserEvents and EventLootDetails included, and for verifying auth
    // (linkshell membership / management permission) before calling. This
    // helper writes the EventHistory + DkpLedgerEntry rows, removes the
    // related Jobs / AppUserEvents / EventLootDetails / Event, and saves.
    internal sealed record EndEventParticipantSummary(
        string? CharacterName,
        string? JobName,
        string? SubJobName,
        double? DurationHours,
        double? DkpEarned);

    internal sealed record EndEventResult(
        DateTime EndTimeUtc,
        IReadOnlyList<EndEventParticipantSummary> Participants);

    internal static async Task<EndEventResult> EndEventCoreAsync(ApplicationDbContext dbContext, Event eventEntity)
    {
        var endTimeUtc = DateTime.UtcNow;
        var participantSummaries = new List<EndEventParticipantSummary>();
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
            TimeStamp = DateTime.UtcNow,
            AppUserEventHistories = new List<AppUserEventHistory>()
        };

        var linkshellMemberships = await dbContext.AppUserLinkshells
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
            var roundedDuration = Math.Round(durationHours * 4) / 4;
            var eventDkp = roundedDuration * (eventEntity.DkpPerHour ?? 0);

            participation.Duration = roundedDuration;
            participation.EventDkp = eventDkp;

            participantSummaries.Add(new EndEventParticipantSummary(
                participation.CharacterName,
                participation.JobName,
                participation.SubJobName,
                roundedDuration,
                eventDkp));

            history.AppUserEventHistories.Add(new AppUserEventHistory
            {
                AppUserId = participation.AppUserId,
                CharacterName = participation.CharacterName,
                JobName = participation.JobName,
                SubJobName = participation.SubJobName,
                JobType = participation.JobType,
                StartTime = participation.StartTime,
                Duration = roundedDuration,
                EventDkp = eventDkp,
                IsQuickJoin = participation.IsQuickJoin,
                IsVerified = participation.IsVerified,
                Proctor = participation.Proctor
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
                Details = $"DKP spent on loot: {lootDetail.ItemName ?? "Unknown item"}."
            });
            nextSequenceByAppUserId[winnerMembership.AppUserId] = currentSequence + 1;
        }

        dbContext.DkpLedgerEntries.AddRange(ledgerEntries);
        dbContext.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        dbContext.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);

        var eventJobs = await dbContext.Jobs.Where(job => job.EventId == eventEntity.Id).ToListAsync();
        dbContext.Jobs.RemoveRange(eventJobs);
        dbContext.Events.Remove(eventEntity);
        await dbContext.SaveChangesAsync();

        return new EndEventResult(endTimeUtc, participantSummaries);
    }
}
