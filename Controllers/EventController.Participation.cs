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
            .Include(evt => evt.AttendanceWindows)
                .ThenInclude(window => window.Attendees)
                    .ThenInclude(attendee => attendee.AppUserEvent)
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
            CommencementStartTime = ConvertUtcToUserTimeZone(eventToStart.CommencementStartTime, user.TimeZone),
            WindowCount = LinkshellManagerDiscordApp.Services.HnmConfig.GetWindowCount(eventToStart.EventName),
            AttendanceWindows = eventToStart.AttendanceWindows
                .OrderBy(window => window.SequenceNumber)
                .Select(window => new EventAttendanceWindowViewModel
                {
                    Id = window.Id,
                    SequenceNumber = window.SequenceNumber,
                    Label = window.Label,
                    PostedAt = ConvertUtcToUserTimeZone(window.PostedAt, user.TimeZone) ?? window.PostedAt,
                    Attendees = window.Attendees
                        .OrderBy(att => att.AppUserEvent != null ? att.AppUserEvent.CharacterName : string.Empty)
                        .Select(att => new AttendanceWindowAttendeeViewModel
                        {
                            Id = att.Id,
                            CharacterName = att.AppUserEvent?.CharacterName,
                            JobName = att.AppUserEvent?.JobName,
                            SubJobName = att.AppUserEvent?.SubJobName,
                            Zone = att.Zone,
                            VerifiedAt = ConvertUtcToUserTimeZone(att.VerifiedAt, user.TimeZone) ?? att.VerifiedAt,
                            VerifiedBy = att.VerifiedBy
                        })
                        .ToList()
                })
                .ToList()
        };

        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> SignUp(int jobId, int eventId, string? jobName = null, string? subJobName = null, string? jobType = null)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null)
        {
            return Challenge();
        }

        // Ad-hoc signup path: event has no pre-defined Jobs. The form supplies
        // the user's Main/Sub/Role directly so they can still register their
        // attendance intent + character info.
        if (jobId <= 0)
        {
            var eventEntity = await _context.Events
                .Include(item => item.Jobs)
                .FirstOrDefaultAsync(item => item.Id == eventId);

            if (eventEntity is null) return NotFound();
            if (eventEntity.Jobs.Count > 0)
            {
                // Caller should have used the per-job button instead.
                return BadRequest("This event already has predefined jobs; pick one of those.");
            }

            var adHocMembership = await GetMembershipAsync(user.Id, eventEntity.LinkshellId);
            if (adHocMembership is null) return Forbid();

            var existingAdHoc = await _context.AppUserEvents
                .FirstOrDefaultAsync(item => item.EventId == eventId && item.AppUserId == user.Id);
            if (existingAdHoc is not null)
            {
                _context.AppUserEvents.Remove(existingAdHoc);
            }

            static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            _context.AppUserEvents.Add(new AppUserEvent
            {
                AppUserId = user.Id,
                EventId = eventId,
                CharacterName = user.CharacterName,
                JobName = Clean(jobName),
                SubJobName = Clean(subJobName),
                JobType = Clean(jobType),
                EventDkp = 0,
            });
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
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
}
