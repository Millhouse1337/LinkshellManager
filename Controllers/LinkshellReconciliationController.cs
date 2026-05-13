using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellReconciliationController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly SheetMigrationService _migration;
    private readonly SheetSyncQueue _sheetSync;

    public LinkshellReconciliationController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        SheetMigrationService migration,
        SheetSyncQueue sheetSync)
    {
        _db = db;
        _userManager = userManager;
        _migration = migration;
        _sheetSync = sheetSync;
    }

    [HttpGet("/linkshells/{linkshellId:int}/reconcile")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var linkshell = await _db.Linkshells.AsNoTracking().FirstOrDefaultAsync(l => l.Id == linkshellId, cancellationToken);
        if (linkshell is null) return NotFound();

        var viewModel = new LinkshellReconciliationViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            IsSheetConfigured = !string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId)
                                 && !string.IsNullOrWhiteSpace(linkshell.GoogleOAuthRefreshTokenEnc),
        };

        if (!viewModel.IsSheetConfigured)
        {
            viewModel.ErrorMessage = "Configure the Google Sheet and connect a Google account first.";
            return View(viewModel);
        }

        var memberships = await _db.AppUserLinkshells
            .AsNoTracking()
            .Include(l => l.AppUser)
            .Where(l => l.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var discordUsers = memberships
            .Where(m => m.AppUser != null && !string.IsNullOrWhiteSpace(m.AppUserId))
            .Select(m => new DiscordUserOption
            {
                AppUserId = m.AppUserId!,
                DisplayName = m.AppUser!.CharacterName ?? m.AppUser.UserName ?? m.AppUserId!,
                CharacterName = m.AppUser.CharacterName,
                AltCharacterName1 = m.AppUser.AltCharacterName1,
                AltCharacterName2 = m.AppUser.AltCharacterName2,
            })
            .GroupBy(u => u.AppUserId)
            .Select(g => g.First())
            .OrderBy(u => u.DisplayName)
            .ToList();

        viewModel.AssignableUsers = discordUsers;

        ReconciliationSheetData sheetData;
        try
        {
            sheetData = await _migration.ReadForReconciliationAsync(
                linkshellId,
                linkshell.GoogleSpreadsheetId!,
                linkshell.GoogleSheetTabName,
                cancellationToken);
        }
        catch (Exception ex)
        {
            viewModel.ErrorMessage = $"Failed to read sheet: {ex.Message}";
            return View(viewModel);
        }

        viewModel.SheetColumnHeaders = sheetData.Headers.ToList();

        var placeholderByName = memberships
            .Where(m => m.AppUserId == null && !string.IsNullOrWhiteSpace(m.CharacterName))
            .GroupBy(m => m.CharacterName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var matchedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sheetRow in sheetData.Rows.OrderBy(r => r.CharacterName, StringComparer.OrdinalIgnoreCase))
        {
            var trimmed = sheetRow.CharacterName.Trim();
            matchedSheetNames.Add(trimmed);

            var row = new ReconciliationRow
            {
                SheetCharacterName = trimmed,
                SheetDkp = sheetRow.Dkp,
                SheetCells = sheetRow.Cells.ToList(),
            };

            var suggestion = discordUsers.FirstOrDefault(u =>
                NameMatches(u.CharacterName, trimmed) ||
                NameMatches(u.AltCharacterName1, trimmed) ||
                NameMatches(u.AltCharacterName2, trimmed));

            if (suggestion is not null)
            {
                row.SuggestedAppUserId = suggestion.AppUserId;
                var match = memberships.FirstOrDefault(m => m.AppUserId == suggestion.AppUserId);
                if (match is not null)
                {
                    row.CurrentlyLinkedDiscordDisplay = suggestion.DisplayName;
                    row.IsConfirmed = NameMatches(match.CharacterName, trimmed)
                                      && Math.Abs((match.LinkshellDkp ?? 0) - sheetRow.Dkp) < 0.0001;
                }
            }

            if (placeholderByName.TryGetValue(trimmed, out var placeholder))
            {
                row.PlaceholderMembershipId = placeholder.Id;
            }

            viewModel.Rows.Add(row);
        }

        viewModel.DiscordUsersWithoutSheetRow = memberships
            .Where(m => !string.IsNullOrWhiteSpace(m.AppUserId) && m.AppUser != null)
            .Where(m => !matchedSheetNames.Contains(m.AppUser!.CharacterName ?? string.Empty)
                        && !matchedSheetNames.Contains(m.AppUser.AltCharacterName1 ?? string.Empty)
                        && !matchedSheetNames.Contains(m.AppUser.AltCharacterName2 ?? string.Empty))
            .Select(m => new DiscordUserSummary
            {
                AppUserId = m.AppUserId!,
                DisplayName = m.AppUser!.CharacterName ?? m.AppUser.UserName ?? m.AppUserId!,
                CharacterName = m.AppUser.CharacterName,
                AltCharacterName1 = m.AppUser.AltCharacterName1,
                AltCharacterName2 = m.AppUser.AltCharacterName2,
                Rank = m.Rank,
                LinkshellDkp = m.LinkshellDkp ?? 0,
            })
            .OrderBy(u => u.DisplayName)
            .ToList();

        return View(viewModel);
    }

    [HttpPost("/linkshells/{linkshellId:int}/reconcile/confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(int linkshellId, [FromForm] string? sheetCharacterName, [FromForm] string? appUserId, [FromForm] double sheetDkp, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        if (string.IsNullOrWhiteSpace(sheetCharacterName) || string.IsNullOrWhiteSpace(appUserId))
        {
            TempData["ReconcileError"] = "Pick a Discord user before confirming.";
            return RedirectToAction(nameof(Index), new { linkshellId });
        }

        var trimmedName = sheetCharacterName.Trim();
        var now = DateTime.UtcNow;

        var existing = await _db.AppUserLinkshells.FirstOrDefaultAsync(
            l => l.LinkshellId == linkshellId && l.AppUserId == appUserId, cancellationToken);

        double oldDkp;
        AppUserLinkshell target;
        if (existing is not null)
        {
            oldDkp = existing.LinkshellDkp ?? 0;
            existing.CharacterName = trimmedName;
            existing.LinkshellDkp = sheetDkp;
            existing.Status ??= "Active";
            existing.Rank ??= "Member";
            existing.DateJoined ??= now;
            target = existing;
        }
        else
        {
            oldDkp = 0;
            target = new AppUserLinkshell
            {
                AppUserId = appUserId,
                LinkshellId = linkshellId,
                CharacterName = trimmedName,
                LinkshellDkp = sheetDkp,
                Rank = "Member",
                Status = "Active",
                DateJoined = now,
            };
            _db.AppUserLinkshells.Add(target);
        }

        var placeholders = await _db.AppUserLinkshells
            .Where(l => l.LinkshellId == linkshellId
                        && l.AppUserId == null
                        && l.CharacterName != null
                        && l.CharacterName.ToLower() == trimmedName.ToLower())
            .ToListAsync(cancellationToken);

        foreach (var placeholder in placeholders)
        {
            if (existing is null || placeholder.Id != existing.Id)
            {
                _db.AppUserLinkshells.Remove(placeholder);
            }
        }

        var delta = sheetDkp - oldDkp;
        if (Math.Abs(delta) > 0.0001)
        {
            _db.DkpLedgerEntries.Add(new DkpLedgerEntry
            {
                AppUserId = appUserId,
                LinkshellId = linkshellId,
                EntryType = "SheetImport",
                Amount = delta,
                Sequence = 1,
                OccurredAt = now,
                CharacterName = trimmedName,
                Details = $"Reconciled with Google Sheet. Old: {oldDkp}, New: {sheetDkp}.",
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
        await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);

        TempData["ReconcileSuccess"] = $"Linked {trimmedName} → {GetDisplayName(target)} at {sheetDkp:0.##} DKP.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    [HttpPost("/linkshells/{linkshellId:int}/reconcile/delete-placeholder")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlaceholder(int linkshellId, [FromForm] int membershipId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!await CanManageAsync(user.Id, linkshellId)) return Forbid();

        var placeholder = await _db.AppUserLinkshells.FirstOrDefaultAsync(
            l => l.Id == membershipId && l.LinkshellId == linkshellId && l.AppUserId == null, cancellationToken);
        if (placeholder is null) return NotFound();

        _db.AppUserLinkshells.Remove(placeholder);
        await _db.SaveChangesAsync(cancellationToken);
        await _sheetSync.EnqueueAsync(linkshellId, cancellationToken);
        TempData["ReconcileSuccess"] = $"Deleted placeholder for {placeholder.CharacterName}.";
        return RedirectToAction(nameof(Index), new { linkshellId });
    }

    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId);
        return membership is not null
               && !string.IsNullOrWhiteSpace(membership.Rank)
               && (membership.Rank.Equals("Leader", StringComparison.OrdinalIgnoreCase)
                   || membership.Rank.Equals("Officer", StringComparison.OrdinalIgnoreCase));
    }

    private static bool NameMatches(string? candidate, string sheetName)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return string.Equals(candidate.Trim(), sheetName.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDisplayName(AppUserLinkshell membership)
    {
        return membership.AppUser?.CharacterName
               ?? membership.AppUser?.UserName
               ?? membership.CharacterName
               ?? membership.AppUserId
               ?? "user";
    }
}
