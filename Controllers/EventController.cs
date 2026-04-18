using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public class EventController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly ILogger<EventController> _logger;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public EventController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        ILogger<EventController> logger,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _logger = logger;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }
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
    public async Task<IActionResult> SignUp(int jobId, int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var job = await _context.Jobs
            .Include(item => item.Event)
            .FirstOrDefaultAsync(item => item.Id == jobId && item.EventId == eventId);
        if (job is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, job.Event!.LinkshellId);
        if (membership is null)
        {
            return Forbid();
        }

        var existingEventSignup = await _context.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);

        if (existingEventSignup is not null)
        {
            var previousJob = await _context.Jobs.FirstOrDefaultAsync(item => item.EventId == eventId && item.JobName == existingEventSignup.JobName && item.SubJobName == existingEventSignup.SubJobName);
            if (previousJob is not null)
            {
                previousJob.Enlisted.RemoveAll(name => name == user.CharacterName);
                previousJob.SignedUp = previousJob.Enlisted.Count;
            }

            _context.AppUserEvents.Remove(existingEventSignup);
        }

        job.Enlisted ??= new List<string>();
        if (!string.IsNullOrWhiteSpace(user.CharacterName) && !job.Enlisted.Contains(user.CharacterName))
        {
            job.Enlisted.Add(user.CharacterName);
        }
        job.SignedUp = job.Enlisted.Count;

        _context.AppUserEvents.Add(new AppUserEvent
        {
            AppUserId = user.Id,
            EventId = eventId,
            CharacterName = user.CharacterName,
            JobName = job.JobName,
            SubJobName = job.SubJobName,
            JobType = job.JobType,
            EventDkp = 0
        });

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Unsign(int jobId, int eventId)
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
        if (membership is null)
        {
            return Forbid();
        }

        var job = await _context.Jobs.FirstOrDefaultAsync(item => item.Id == jobId && item.EventId == eventId);
        var participation = await _context.AppUserEvents.FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);

        if (job is not null && !string.IsNullOrWhiteSpace(user.CharacterName))
        {
            job.Enlisted.RemoveAll(name => name == user.CharacterName);
            job.SignedUp = job.Enlisted.Count;
        }

        if (participation is not null)
        {
            _context.AppUserEvents.Remove(participation);
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> ConfirmAttendance(int eventId, Dictionary<string, string> attendance)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToConfirm = await _context.Events
            .Include(evt => evt.AppUserEvents)
            .Include(evt => evt.Jobs)
            .FirstOrDefaultAsync(evt => evt.Id == eventId);

        if (eventToConfirm is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToConfirm.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        foreach (var participation in eventToConfirm.AppUserEvents.ToList())
        {
            if (attendance.TryGetValue($"attendance_{participation.CharacterName}", out var status) && status == "deny")
            {
                var job = eventToConfirm.Jobs.FirstOrDefault(item => item.JobName == participation.JobName && item.SubJobName == participation.SubJobName);
                if (job is not null && !string.IsNullOrWhiteSpace(participation.CharacterName))
                {
                    job.Enlisted.RemoveAll(name => name == participation.CharacterName);
                    job.SignedUp = job.Enlisted.Count;
                }

                _context.AppUserEvents.Remove(participation);
            }
        }

        await _context.SaveChangesAsync();
        return await StartEvent(eventId);
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
    public async Task<IActionResult> Start(int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var eventToStart = await _context.Events
            .Include(evt => evt.Jobs)
            .Include(evt => evt.AppUserEvents)
                .ThenInclude(participation => participation.StatusLedgerEntries)
            .Include(evt => evt.EventLootDetails)
            .FirstOrDefaultAsync(evt => evt.Id == eventId);

        if (eventToStart is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(user.Id, eventToStart.LinkshellId);
        if (membership is null)
        {
            return Forbid();
        }

        var model = new EventViewModel
        {
            Event = new Event
            {
                Id = eventToStart.Id,
                LinkshellId = eventToStart.LinkshellId,
                EventName = eventToStart.EventName,
                EventType = eventToStart.EventType,
                EventLocation = eventToStart.EventLocation,
                CreatorUserId = eventToStart.CreatorUserId,
                StartTime = ConvertUtcToUserTimeZone(eventToStart.StartTime, user.TimeZone),
                EndTime = ConvertUtcToUserTimeZone(eventToStart.EndTime, user.TimeZone),
                CommencementStartTime = ConvertUtcToUserTimeZone(eventToStart.CommencementStartTime, user.TimeZone),
                Duration = eventToStart.Duration,
                DkpPerHour = eventToStart.DkpPerHour,
                EventDkp = eventToStart.EventDkp,
                Details = eventToStart.Details
            },
            Jobs = eventToStart.Jobs.ToList(),
            AppUserEvents = eventToStart.AppUserEvents
                .OrderBy(item => item.IsQuickJoin)
                .ThenBy(item => item.CharacterName)
                .Select(item => new AppUserEvent
                {
                    Id = item.Id,
                    AppUserId = item.AppUserId,
                    EventId = item.EventId,
                    CharacterName = item.CharacterName,
                    JobName = item.JobName,
                    SubJobName = item.SubJobName,
                    JobType = item.JobType,
                    StartTime = ConvertUtcToUserTimeZone(item.StartTime, user.TimeZone),
                    EndTime = ConvertUtcToUserTimeZone(item.EndTime, user.TimeZone),
                    Duration = item.Duration,
                    EventDkp = item.EventDkp,
                      IsQuickJoin = item.IsQuickJoin,
                      IsVerified = item.IsVerified,
                      Proctor = item.Proctor,
                      IsOnBreak = item.IsOnBreak,
                      PauseTime = ConvertUtcToUserTimeZone(item.PauseTime, user.TimeZone),
                      ResumeTime = ConvertUtcToUserTimeZone(item.ResumeTime, user.TimeZone),
                      StatusLedgerEntries = item.StatusLedgerEntries
                          .OrderBy(entry => entry.OccurredAt)
                          .Select(entry => new AppUserEventStatusLedger
                          {
                              Id = entry.Id,
                              AppUserEventId = entry.AppUserEventId,
                              EventId = entry.EventId,
                              AppUserId = entry.AppUserId,
                              ActionType = entry.ActionType,
                              OccurredAt = ConvertUtcToUserTimeZone(entry.OccurredAt, user.TimeZone) ?? entry.OccurredAt,
                              RequiresVerification = entry.RequiresVerification,
                              VerifiedAt = ConvertUtcToUserTimeZone(entry.VerifiedAt, user.TimeZone),
                              VerifiedBy = entry.VerifiedBy
                          })
                          .ToList()
                  })
                  .ToList(),
            EventLootDetails = eventToStart.EventLootDetails.OrderByDescending(item => item.Id).ToList(),
            LinkshellMembers = eventToStart.AppUserEvents.Select(item => item.CharacterName ?? string.Empty).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().OrderBy(name => name).ToList(),
            CommencementStartTime = ConvertUtcToUserTimeZone(eventToStart.CommencementStartTime, user.TimeZone)
        };

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> QuickJoin(int eventId, string jobName, string subJobName, string jobType)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        var existing = await _context.AppUserEvents.FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);
        if (existing is null)
        {
            var eventEntity = await _context.Events.FirstOrDefaultAsync(item => item.Id == eventId);
            if (eventEntity is null)
            {
                return NotFound();
            }

            var membership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
            if (membership is null)
            {
                return Forbid();
            }

            if (!eventEntity.CommencementStartTime.HasValue)
            {
                return BadRequest("The event must be live before quick join is available.");
            }

            _context.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = user.Id,
                EventId = eventId,
                CharacterName = user.CharacterName,
                JobName = jobName,
                SubJobName = subJobName,
                JobType = jobType,
                StartTime = DateTime.UtcNow,
                EventDkp = 0,
                IsQuickJoin = true
            });

            await _context.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TakeBreak(int eventId)
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
        if (membership is null)
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest("Break status is only available after the event has started.");
        }

        var participation = await _context.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);

        if (participation is null)
        {
            return BadRequest("Join the live event before taking a break.");
        }

        if (participation.IsOnBreak == true)
        {
            return RedirectToAction(nameof(Start), new { eventId });
        }

        var nowUtc = DateTime.UtcNow;
        participation.Duration = CalculateAccumulatedDurationHours(participation, nowUtc, eventEntity.CommencementStartTime);
        participation.IsOnBreak = true;
        participation.PauseTime = nowUtc;
        participation.ResumeTime = null;
        _context.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = user.Id,
            ActionType = "BreakStart",
            OccurredAt = nowUtc,
            RequiresVerification = false
        });

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReturnFromBreak(int eventId)
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
        if (membership is null)
        {
            return Forbid();
        }

        if (!eventEntity.CommencementStartTime.HasValue)
        {
            return BadRequest("Break status is only available after the event has started.");
        }

        var participation = await _context.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);

        if (participation is null)
        {
            return BadRequest("Join the live event before returning from break.");
        }

        if (participation.IsOnBreak != true)
        {
            return RedirectToAction(nameof(Start), new { eventId });
        }

        participation.IsOnBreak = false;
        participation.PauseTime = null;
        participation.ResumeTime = DateTime.UtcNow;
        _context.AppUserEventStatusLedgers.Add(new AppUserEventStatusLedger
        {
            AppUserEventId = participation.Id,
            EventId = eventId,
            AppUserId = user.Id,
            ActionType = "BreakReturn",
            OccurredAt = participation.ResumeTime.Value,
            RequiresVerification = true
        });

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    public async Task<IActionResult> VerifyUser(int eventId, string characterName, bool isVerified)
    {
        var currentUser = await RequireCurrentUserAsync();
        if (currentUser is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(currentUser.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var userEvent = await _context.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.CharacterName == characterName);

        if (userEvent is null)
        {
            return NotFound();
        }

        if (userEvent.IsVerified.HasValue)
        {
            return BadRequest("Initial attendance has already been verified. Undo it first if you need to change it.");
        }

        userEvent.IsVerified = isVerified;
        userEvent.Proctor = currentUser?.CharacterName;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    public async Task<IActionResult> UndoVerification(int eventId, string characterName)
    {
        var currentUser = await RequireCurrentUserAsync();
        if (currentUser is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(currentUser.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var userEvent = await _context.AppUserEvents
            .FirstOrDefaultAsync(item => item.EventId == eventId && item.CharacterName == characterName);

        if (userEvent is null)
        {
            return NotFound();
        }

        userEvent.IsVerified = null;
        userEvent.Proctor = null;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Start), new { eventId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyReturn(int eventId, int ledgerEntryId)
    {
        var currentUser = await RequireCurrentUserAsync();
        if (currentUser is null)
        {
            return Challenge();
        }

        var eventEntity = await _context.Events.FirstOrDefaultAsync(evt => evt.Id == eventId);
        if (eventEntity is null)
        {
            return NotFound();
        }

        var membership = await GetMembershipAsync(currentUser.Id, eventEntity.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Forbid();
        }

        var ledgerEntry = await _context.AppUserEventStatusLedgers
            .FirstOrDefaultAsync(item => item.Id == ledgerEntryId && item.EventId == eventId && item.ActionType == "BreakReturn");

        if (ledgerEntry is null)
        {
            return NotFound();
        }

        if (!ledgerEntry.RequiresVerification || ledgerEntry.VerifiedAt.HasValue)
        {
            return RedirectToAction(nameof(Start), new { eventId });
        }

        ledgerEntry.VerifiedAt = DateTime.UtcNow;
        ledgerEntry.VerifiedBy = currentUser.CharacterName ?? currentUser.UserName;
        ledgerEntry.RequiresVerification = false;
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

        var endTimeUtc = DateTime.UtcNow;
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

        var linkshellMemberships = await _context.AppUserLinkshells
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

        _context.EventHistories.Add(history);
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

        _context.DkpLedgerEntries.AddRange(ledgerEntries);
        _context.EventLootDetails.RemoveRange(eventEntity.EventLootDetails);
        _context.AppUserEvents.RemoveRange(eventEntity.AppUserEvents);

        var eventJobs = await _context.Jobs.Where(job => job.EventId == eventId).ToListAsync();
        _context.Jobs.RemoveRange(eventJobs);
        _context.Events.Remove(eventEntity);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index), "EventHistory");
    }

    private async Task<EventViewModel> BuildEventViewModelAsync(AppUser user, EventViewModel? source = null)
    {
        var linkshells = await _context.AppUserLinkshells
            .Where(link =>
                link.AppUserId == user.Id &&
                (link.Rank == "Leader" || link.Rank == "Officer"))
            .Select(link => link.Linkshell!)
            .OrderBy(linkshell => linkshell.LinkshellName)
            .ToListAsync();

        var selectedLinkshellId = source?.LinkshellId ?? source?.Event?.LinkshellId ?? user.PrimaryLinkshellId ?? linkshells.FirstOrDefault()?.Id ?? 0;
        if (selectedLinkshellId > 0 && linkshells.All(linkshell => linkshell.Id != selectedLinkshellId))
        {
            selectedLinkshellId = linkshells.FirstOrDefault()?.Id ?? 0;
        }

        return new EventViewModel
        {
            Event = source?.Event ?? new Event(),
            Jobs = source?.Jobs ?? new List<Job>(),
            Linkshells = linkshells,
            LinkshellId = selectedLinkshellId
        };
    }

    private async Task<AppUser?> RequireCurrentUserAsync() => await _userManager.GetUserAsync(User);

    private async Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId)
    {
        return await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId);
    }

    private static bool CanManageLinkshell(AppUserLinkshell? membership)
    {
        if (membership is null || string.IsNullOrWhiteSpace(membership.Rank))
        {
            return false;
        }

        return membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase) ||
               membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase);
    }

    private static double CalculateAccumulatedDurationHours(AppUserEvent participation, DateTime referenceUtc, DateTime? eventStartUtc)
    {
        var accumulatedHours = Math.Max(0, participation.Duration ?? 0);
        if (participation.IsOnBreak == true)
        {
            return accumulatedHours;
        }

        var segmentStart = participation.ResumeTime ?? participation.StartTime ?? eventStartUtc;
        if (!segmentStart.HasValue)
        {
            return accumulatedHours;
        }

        var segmentHours = Math.Max(0, (referenceUtc - segmentStart.Value).TotalHours);
        return accumulatedHours + segmentHours;
    }

    private static AppUserLinkshell? ResolveLootWinnerMembership(
        string? itemWinner,
        IReadOnlyDictionary<string, AppUserLinkshell> membershipsByAppUserId,
        IReadOnlyDictionary<string, AppUserEvent> participantsByCharacterName,
        IEnumerable<AppUserLinkshell> linkshellMemberships)
    {
        var normalizedWinner = NormalizeLookupKey(itemWinner);
        if (normalizedWinner is null)
        {
            return null;
        }

        if (participantsByCharacterName.TryGetValue(normalizedWinner, out var participation) &&
            !string.IsNullOrWhiteSpace(participation.AppUserId) &&
            membershipsByAppUserId.TryGetValue(participation.AppUserId, out var participantMembership))
        {
            return participantMembership;
        }

        return linkshellMemberships.FirstOrDefault(link =>
            string.Equals(NormalizeLookupKey(link.CharacterName), normalizedWinner, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NormalizeLookupKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTime? ConvertUserTimeZoneToUtc(DateTime? localDateTime, string? timeZoneId)
    {
        if (!localDateTime.HasValue)
        {
            return null;
        }

        var zone = ResolveTimeZone(timeZoneId);
        var zonedDateTime = zone.AtLeniently(LocalDateTime.FromDateTime(localDateTime.Value));
        return zonedDateTime.ToDateTimeUtc();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        _logger.LogWarning("Unknown time zone '{TimeZoneId}', falling back to UTC.", timeZoneId);
        return DateTimeZone.Utc;
    }
}


