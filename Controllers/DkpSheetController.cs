using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

// The always-on DKP sheet: the linkshell's live DKP rendered straight from the
// app's own data (DkpSheetService) — no Google. Any member can view it; "Export to
// Excel" downloads a styled .xlsx. Posting the sheet to a Discord channel is now
// configured in the channel-routes editor (tick "DKP sheet" on a route — see
// Customize Linkshell / the Activity Configurations tab), so there's no per-page
// picker here.
[Authorize]
public sealed class DkpSheetController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<AppUser> _userManager;
    private readonly DkpSheetService _dkpSheet;

    public DkpSheetController(
        ApplicationDbContext db,
        UserManager<AppUser> userManager,
        DkpSheetService dkpSheet)
    {
        _db = db;
        _userManager = userManager;
        _dkpSheet = dkpSheet;
    }

    [HttpGet("/linkshells/{linkshellId:int}/dkp-sheet")]
    public async Task<IActionResult> Index(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var membership = await GetMembershipAsync(user.Id, linkshellId, cancellationToken);
        if (membership is null) return Forbid();

        var data = await _dkpSheet.BuildAsync(linkshellId, cancellationToken);
        return View(new DkpSheetPageViewModel { Data = data });
    }

    // Downloads the live DKP table as a styled .xlsx.
    [HttpGet("/linkshells/{linkshellId:int}/dkp-sheet/export.xlsx")]
    public async Task<IActionResult> ExportExcel(int linkshellId, CancellationToken cancellationToken)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (await GetMembershipAsync(user.Id, linkshellId, cancellationToken) is null) return Forbid();

        var data = await _dkpSheet.BuildAsync(linkshellId, cancellationToken);
        var bytes = DkpWorkbookBuilder.Build(data);
        var fileName = DkpWorkbookBuilder.FileName(data.LinkshellName, DateTime.UtcNow);
        return File(bytes, DkpWorkbookBuilder.ContentType, fileName);
    }

    private Task<AppUserLinkshell?> GetMembershipAsync(string appUserId, int linkshellId, CancellationToken cancellationToken)
        => _db.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUserId && link.LinkshellId == linkshellId, cancellationToken);
}
