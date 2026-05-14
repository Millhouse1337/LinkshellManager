using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

[Authorize]
public sealed class LinkshellDkpSheetController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;

    public LinkshellDkpSheetController(ApplicationDbContext db, UserManager<AppUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    [HttpGet("/linkshells/{linkshellId:int}/dkp-sheet")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        // Membership-only gate: any linkshell member (regardless of rank)
        // can view the DKP board. Officers/leaders still go through
        // /linkshells/{id}/sheet for configuration.
        var isMember = await _db.AppUserLinkshells
            .AsNoTracking()
            .AnyAsync(link => link.AppUserId == user.Id && link.LinkshellId == linkshellId, cancellationToken);
        if (!isMember) return Forbid();

        var linkshell = await _db.Linkshells
            .AsNoTracking()
            .Where(l => l.Id == linkshellId)
            .Select(l => new { l.LinkshellName, l.GoogleSpreadsheetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (linkshell is null) return NotFound();

        var viewModel = new LinkshellDkpSheetViewModel
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            SpreadsheetId = linkshell.GoogleSpreadsheetId,
            IsConfigured = !string.IsNullOrWhiteSpace(linkshell.GoogleSpreadsheetId),
        };

        if (viewModel.IsConfigured)
        {
            viewModel.EmbedUrl = $"https://docs.google.com/spreadsheets/d/{viewModel.SpreadsheetId}/preview";
            viewModel.OpenInSheetsUrl = $"https://docs.google.com/spreadsheets/d/{viewModel.SpreadsheetId}/edit";
        }

        return View(viewModel);
    }
}
