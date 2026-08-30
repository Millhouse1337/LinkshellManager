using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.ViewComponents;

// The Items half of Treasury, rendered inside the one Treasury page.
//
// A view component rather than a partial off the finances view model, for one reason that matters:
// items answer to a DIFFERENT permission rule than gil does. Gil uses the granular
// CanManageTreasury role flag; items use leader-or-officer, which is what ManageItemController has
// always used and what every write action on an item still checks. Loading the list here keeps that
// rule in one place — embedding items in the gil page must not quietly change who can sell one.
//
// It is also why the query is a copy of ManageItemController.Index's rather than a call into it:
// the controller action is now a redirect, and a view component cannot borrow an action's body.
public class TreasuryItemsViewComponent : ViewComponent
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly AdminOverrideService _adminOverride;

    public TreasuryItemsViewComponent(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        AdminOverrideService adminOverride)
    {
        _context = context;
        _userManager = userManager;
        _adminOverride = adminOverride;
    }

    // `view` is "sold" for the archive of things already gone; anything else is what is still held.
    public async Task<IViewComponentResult> InvokeAsync(string? view = null)
    {
        var model = new TreasuryItemsViewModel
        {
            ShowingSold = string.Equals(view, "sold", StringComparison.OrdinalIgnoreCase),
        };

        var user = await _userManager.GetUserAsync(UserClaimsPrincipal);
        if (user is null)
        {
            return View(model);
        }

        var linkshellId = user.PrimaryLinkshellId;
        if (!linkshellId.HasValue)
        {
            return View(model);
        }

        model.CanManage = await CanManageAsync(user.Id, linkshellId.Value);
        model.Items = await _context.Items
            .AsNoTracking()
            .Where(item => item.LinkshellId == linkshellId.Value)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new ManageItemViewModel
            {
                Id = item.Id,
                LinkshellId = item.LinkshellId,
                LinkshellName = item.LinkshellName,
                ItemName = item.ItemName,
                ItemType = item.ItemType,
                Quantity = item.Quantity,
                Notes = item.Notes,
                CreatedByCharacterName = item.CreatedByCharacterName,
                CreatedAt = item.CreatedAt,
                UpdatedAt = item.UpdatedAt,
                IsSold = item.IsSold,
                SoldPrice = item.SoldPrice,
                SoldByCharacterName = item.SoldByCharacterName,
                CanManage = model.CanManage,
            })
            .ToListAsync(HttpContext.RequestAborted);

        // Only for the sell modal's "who sold it" box, so it is skipped for a reader who has none.
        if (model.CanManage)
        {
            model.Roster = await _context.AppUserLinkshells
                .AsNoTracking()
                .Where(member => member.LinkshellId == linkshellId.Value
                    && member.CharacterName != null
                    && member.CharacterName != "")
                .OrderBy(member => member.CharacterName)
                .Select(member => member.CharacterName!)
                .ToListAsync(HttpContext.RequestAborted);
        }

        return View(model);
    }

    // Leader-or-officer, the same rule every write action in ManageItemController checks. NOT the
    // granular CanManageInventory flag: the sidebar gates its "Add item" link on that one, but no
    // item endpoint has ever enforced it, and a list that offered buttons the server refuses would
    // be worse than one that offers the buttons the server actually honours.
    private async Task<bool> CanManageAsync(string appUserId, int linkshellId)
    {
        var membership = await _context.AppUserLinkshells
            .AsNoTracking()
            .FirstOrDefaultAsync(
                link => link.AppUserId == appUserId && link.LinkshellId == linkshellId,
                HttpContext.RequestAborted);
        if (membership is null)
        {
            return false;
        }
        return LinkshellRanks.IsLeaderOrOfficer(membership.Rank)
            || await _adminOverride.IsActiveForAsync(appUserId, HttpContext.RequestAborted);
    }
}
