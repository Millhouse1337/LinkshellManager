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
public class ManageItemController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly UserManager<AppUser> _userManager;
    private readonly ItemSaleRecorder _itemSales;

    public ManageItemController(
        ApplicationDbContext context,
        UserManager<AppUser> userManager,
        ItemSaleRecorder itemSales)
    {
        _context = context;
        _userManager = userManager;
        _itemSales = itemSales;
    }

    // `view=sold` shows the archive of things already gone; anything else shows what is still held.
    public async Task<IActionResult> Index(string? view = null)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var linkshellId = user.PrimaryLinkshellId;
        var canManage = await CanManageAsync(user.Id, linkshellId);

        var items = new List<ManageItemViewModel>();
        if (linkshellId.HasValue)
        {
            items = await _context.Items
                .Where(i => i.LinkshellId == linkshellId.Value)
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new ManageItemViewModel
                {
                    Id = i.Id,
                    LinkshellId = i.LinkshellId,
                    LinkshellName = i.LinkshellName,
                    ItemName = i.ItemName,
                    ItemType = i.ItemType,
                    Quantity = i.Quantity,
                    Notes = i.Notes,
                    CreatedByCharacterName = i.CreatedByCharacterName,
                    CreatedAt = i.CreatedAt,
                    UpdatedAt = i.UpdatedAt,
                    IsSold = i.IsSold,
                    SoldPrice = i.SoldPrice,
                    SoldByCharacterName = i.SoldByCharacterName,
                    CanManage = canManage
                })
                .ToListAsync();
        }

        ViewBag.CanManage = canManage;
        ViewBag.LinkshellName = user.PrimaryLinkshellName;
        ViewBag.View = view;
        return View(items);
    }

    public async Task<IActionResult> AddItem()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);
        if (manageableLinkshells.Count == 0) return Forbid();

        var defaultLinkshellId = manageableLinkshells.Any(l => l.Id == user.PrimaryLinkshellId)
            ? user.PrimaryLinkshellId ?? manageableLinkshells[0].Id
            : manageableLinkshells[0].Id;

        return View(new ManageItemViewModel
        {
            Linkshells = manageableLinkshells,
            LinkshellId = defaultLinkshellId,
            LinkshellName = manageableLinkshells.First(l => l.Id == defaultLinkshellId).LinkshellName
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(ManageItemViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var manageableLinkshells = await GetManageableLinkshellsAsync(user.Id);
        var selectedLinkshell = manageableLinkshells.FirstOrDefault(l => l.Id == user.PrimaryLinkshellId)
            ?? manageableLinkshells.FirstOrDefault();
        if (selectedLinkshell is null)
        {
            return Forbid();
        }

        model.LinkshellId = selectedLinkshell.Id;
        model.LinkshellName = selectedLinkshell.LinkshellName;
        ModelState.Remove(nameof(model.LinkshellId));

        if (!ModelState.IsValid)
        {
            model.Linkshells = manageableLinkshells;
            return View(model);
        }

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == user.Id && ul.LinkshellId == model.LinkshellId);

        var now = DateTime.UtcNow;
        _context.Items.Add(new Item
        {
            LinkshellId = model.LinkshellId,
            LinkshellName = selectedLinkshell.LinkshellName,
            ItemName = model.ItemName.Trim(),
            ItemType = model.ItemType?.Trim(),
            Quantity = model.Quantity,
            Notes = model.Notes?.Trim(),
            CreatedByAppUserId = user.Id,
            CreatedByCharacterName = membership?.CharacterName ?? user.CharacterName,
            CreatedAt = now,
            UpdatedAt = now
        });
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();

        if (!await CanManageAsync(user.Id, item.LinkshellId)) return Forbid();

        var linkshells = await GetManageableLinkshellsAsync(user.Id);
        return View(new ManageItemViewModel
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
            CanManage = true,
            Linkshells = linkshells
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ManageItemViewModel model)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();

        if (!await CanManageAsync(user.Id, item.LinkshellId)) return Forbid();

        if (!ModelState.IsValid)
        {
            model.Id = item.Id;
            model.LinkshellId = item.LinkshellId;
            model.LinkshellName = item.LinkshellName;
            model.Linkshells = await GetManageableLinkshellsAsync(user.Id);
            return View(model);
        }

        item.ItemName = model.ItemName.Trim();
        item.ItemType = model.ItemType?.Trim();
        item.Quantity = model.Quantity;
        item.Notes = model.Notes?.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();

        if (!await CanManageAsync(user.Id, item.LinkshellId)) return Forbid();

        _context.Items.Remove(item);
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // Mark an item sold for a price → record the income in the treasury. The item
    // stays in the list flagged Sold (a record); the linked RevenueEntry is what
    // updates Finances. Reversible via Unsell.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkSold(int id, long salePrice)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        if (!await CanManageAsync(user.Id, item.LinkshellId)) return Forbid();
        if (item.IsSold) return RedirectToAction(nameof(Index));

        if (salePrice < 0) salePrice = 0;

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == user.Id && ul.LinkshellId == item.LinkshellId);
        var characterName = membership?.CharacterName ?? user.CharacterName;

        // Shared with the Activity's mark-sold endpoint, so the two surfaces cannot record a sale
        // differently.
        await _itemSales.RecordSaleAsync(
            item, salePrice, new TreasuryActor(user.Id, characterName), HttpContext.RequestAborted);

        return RedirectToAction(nameof(Index));
    }

    // Undo a sale: the treasury entry is reversed, not deleted, so the record that the item was once
    // sold — and for how much — survives.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsell(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        var item = await _context.Items.FirstOrDefaultAsync(i => i.Id == id);
        if (item is null) return NotFound();
        if (!await CanManageAsync(user.Id, item.LinkshellId)) return Forbid();

        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == user.Id && ul.LinkshellId == item.LinkshellId);
        await _itemSales.ReverseSaleAsync(
            item,
            new TreasuryActor(user.Id, membership?.CharacterName ?? user.CharacterName),
            HttpContext.RequestAborted);

        // Unselling happens from the Sold archive, so stay there rather than bouncing to the stash.
        return RedirectToAction(nameof(Index), new { view = "sold" });
    }

    private async Task<bool> CanManageAsync(string appUserId, int? linkshellId)
    {
        if (!linkshellId.HasValue) return false;
        var membership = await _context.AppUserLinkshells
            .FirstOrDefaultAsync(ul => ul.AppUserId == appUserId && ul.LinkshellId == linkshellId.Value);
        return membership is not null && LinkshellRanks.IsLeaderOrOfficer(membership.Rank);
    }

    private async Task<List<Linkshell>> GetManageableLinkshellsAsync(string appUserId)
    {
        return await _context.AppUserLinkshells
            .Where(ul => ul.AppUserId == appUserId
                         && (ul.Rank == LinkshellRanks.Leader || ul.Rank == LinkshellRanks.Officer))
            .Select(ul => ul.Linkshell!)
            .Where(l => l != null)
            .OrderBy(l => l.LinkshellName)
            .ToListAsync();
    }
}
