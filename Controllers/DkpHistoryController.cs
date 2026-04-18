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
public class DkpHistoryController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly IDateTimeZoneProvider _dateTimeZoneProvider;

    public DkpHistoryController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        IDateTimeZoneProvider dateTimeZoneProvider)
    {
        _context = context;
        _userManager = userManager;
        _dateTimeZoneProvider = dateTimeZoneProvider;
    }

    public async Task<IActionResult> Index(int? linkshellId, string? appUserId)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null)
        {
            return Challenge();
        }

        var accessibleMemberships = await _context.AppUserLinkshells
            .Include(link => link.Linkshell)
            .Where(link => link.AppUserId == user.Id)
            .OrderBy(link => link.Linkshell!.LinkshellName)
            .ToListAsync();

        var viewModel = new DkpHistoryViewModel
        {
            Linkshells = accessibleMemberships
                .Select(link => new DkpHistoryLinkshellOptionViewModel
                {
                    Id = link.LinkshellId,
                    Name = link.Linkshell?.LinkshellName ?? "Unknown linkshell"
                })
                .GroupBy(link => link.Id)
                .Select(group => group.First())
                .ToList()
        };

        if (viewModel.Linkshells.Count == 0)
        {
            return View(viewModel);
        }

        var selectedLinkshellId = linkshellId
            ?? user.PrimaryLinkshellId
            ?? viewModel.Linkshells.First().Id;

        if (viewModel.Linkshells.All(link => link.Id != selectedLinkshellId))
        {
            selectedLinkshellId = viewModel.Linkshells.First().Id;
        }

        var linkshellMembers = await _context.AppUserLinkshells
            .Where(link => link.LinkshellId == selectedLinkshellId && link.AppUserId != null)
            .OrderBy(link => link.CharacterName)
            .ToListAsync();

        viewModel.SelectedLinkshellId = selectedLinkshellId;
        viewModel.SelectedLinkshellName = viewModel.Linkshells.First(link => link.Id == selectedLinkshellId).Name;
        viewModel.Members = linkshellMembers
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => new DkpHistoryMemberOptionViewModel
            {
                AppUserId = link.AppUserId!,
                CharacterName = link.CharacterName ?? "Unknown member",
                CurrentBalance = link.LinkshellDkp ?? 0
            })
            .ToList();

        if (viewModel.Members.Count == 0)
        {
            return View(viewModel);
        }

        var selectedAppUserId = appUserId;
        if (string.IsNullOrWhiteSpace(selectedAppUserId) || viewModel.Members.All(member => member.AppUserId != selectedAppUserId))
        {
            selectedAppUserId = viewModel.Members.FirstOrDefault(member => member.AppUserId == user.Id)?.AppUserId
                ?? viewModel.Members.First().AppUserId;
        }

        var ledgerEntries = await _context.DkpLedgerEntries
            .Where(entry => entry.LinkshellId == selectedLinkshellId && entry.AppUserId == selectedAppUserId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Sequence)
            .ToListAsync();

        var runningBalance = 0d;
        viewModel.SelectedAppUserId = selectedAppUserId;
        viewModel.SelectedMemberName = viewModel.Members.First(member => member.AppUserId == selectedAppUserId).CharacterName;
        viewModel.CurrentBalance = viewModel.Members.First(member => member.AppUserId == selectedAppUserId).CurrentBalance;
        viewModel.Entries = ledgerEntries.Select(entry =>
        {
            runningBalance += entry.Amount;
            return new DkpHistoryEntryViewModel
            {
                Id = entry.Id,
                EntryType = entry.EntryType,
                Amount = entry.Amount,
                RunningBalance = runningBalance,
                OccurredAt = ConvertUtcToUserTimeZone(entry.OccurredAt, user.TimeZone),
                EventName = entry.EventName,
                EventType = entry.EventType,
                EventLocation = entry.EventLocation,
                EventStartTime = ConvertUtcToUserTimeZone(entry.EventStartTime, user.TimeZone),
                EventEndTime = ConvertUtcToUserTimeZone(entry.EventEndTime, user.TimeZone),
                ItemName = entry.ItemName,
                Details = entry.Details
            };
        }).ToList();

        return View(viewModel);
    }

    private DateTime? ConvertUtcToUserTimeZone(DateTime? utcDateTime, string? timeZoneId)
    {
        if (!utcDateTime.HasValue)
        {
            return null;
        }

        var instant = Instant.FromDateTimeUtc(DateTime.SpecifyKind(utcDateTime.Value, DateTimeKind.Utc));
        var zone = ResolveTimeZone(timeZoneId);
        return instant.InZone(zone).ToDateTimeUnspecified();
    }

    private DateTimeZone ResolveTimeZone(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId) && _dateTimeZoneProvider.Ids.Contains(timeZoneId))
        {
            return _dateTimeZoneProvider[timeZoneId];
        }

        return DateTimeZone.Utc;
    }
}
