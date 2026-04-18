using Microsoft.AspNetCore.Mvc;
using LinkshellManager.Models;
using System.Linq;
using LinkshellManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using LinkshellManager.ViewModels;

namespace LinkshellManager.Controllers
{
    public class ManageItemController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUser> _userManager;
        public ManageItemController(ApplicationDbContext context, UserManager<AppUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public IActionResult Index(string linkshellName)
        {
            var userId = _userManager.GetUserId(User);
            var userLinkshells = _context.AppUserLinkshells
                .Where(ul => ul.AppUserId == userId)
                .Select(ul => ul.Linkshell)
                .ToList();

            ViewBag.Linkshells = userLinkshells;

            var items = _context.Items.AsQueryable();

            if (!string.IsNullOrEmpty(linkshellName))
            {
                items = items.Where(i => i.Linkshell.LinkshellName == linkshellName);
            }

            return View(items.ToList());
        }

[HttpGet]
public async Task<IActionResult> AddItem()
{
    var user = await _userManager.GetUserAsync(User);
    if (user == null)
    {
        // _logger.LogWarning("User is not authenticated.");
        return Challenge(); // Redirect to login if user is not authenticated
    }

    var userLinkshells = await _context.AppUserLinkshells
        .Where(ul => ul.AppUserId == user.Id)
        .Select(ul => ul.Linkshell)
        .ToListAsync();

    var viewModel = new ManageItemViewModel
    {
        Linkshells = userLinkshells,
        LinkshellId = user.PrimaryLinkshellId ?? 0 // Assuming PrimaryLinkshellId is nullable
    };

    return View(viewModel);
}

        [HttpPost]
        public IActionResult AddItem(Item item)
        {
            if (ModelState.IsValid)
            {
                var linkshell = _context.Linkshells.Find(item.LinkshellId);
                if (linkshell != null)
                {
                    item.LinkshellName = linkshell.LinkshellName;
                }
                item.TimeStamp = DateTime.UtcNow; // Set the TimeStamp to the current UTC time
                _context.Items.Add(item);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }
            ViewBag.Linkshells = _context.Linkshells.ToList();
            return View(item);
        }

        public IActionResult Edit(int id)
        {
            var item = _context.Items.Find(id);
            if (item == null)
            {
                return NotFound();
            }
            ViewBag.Linkshells = _context.Linkshells.ToList();
            return View(item);
        }

[HttpPost]
public IActionResult Edit(Item item)
{
    if (ModelState.IsValid)
    {
        var existingItem = _context.Items.AsNoTracking().FirstOrDefault(i => i.Id == item.Id);
        if (existingItem != null)
        {
            item.TimeStamp = existingItem.TimeStamp; // Preserve the original TimeStamp
            _context.Items.Update(item);
            _context.SaveChanges();
            return RedirectToAction("Index");
        }
        return NotFound();
    }
    ViewBag.Linkshells = _context.Linkshells.ToList();
    return View(item);
}

        public IActionResult Delete(int id)
        {
            var item = _context.Items.Find(id);
            if (item == null)
            {
                return NotFound();
            }
            return View(item);
        }

        [HttpPost, ActionName("Delete")]
        public IActionResult DeleteConfirmed(int id)
        {
            var item = _context.Items.Find(id);
            if (item != null)
            {
                _context.Items.Remove(item);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }
    }
}