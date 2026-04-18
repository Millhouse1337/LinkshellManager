using Microsoft.AspNetCore.Mvc;
using LinkshellManager.ViewModels;
using LinkshellManager.Models;
using LinkshellManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManager.Controllers
{
    public class AnnouncementController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUser> _userManager;

        public AnnouncementController(ApplicationDbContext context, UserManager<AppUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            var primaryLinkshellId = user.PrimaryLinkshellId;

            var announcements = await _context.Announcements
                .Where(a => a.LinkshellId == primaryLinkshellId)
                .Select(a => new AnnouncementViewModel
                {
                    Id = a.Id,
                    LinkshellId = a.LinkshellId,
                    LinkshellName = a.LinkshellName,
                    AnnouncementTitle = a.AnnouncementTitle,
                    AnnouncementDetails = a.AnnouncementDetails
                }).ToListAsync();

            return View(announcements);
        }

        public async Task<IActionResult> Create()
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

            var viewModel = new AnnouncementViewModel
            {
                Linkshells = userLinkshells,
                LinkshellId = user.PrimaryLinkshellId ?? 0 // Assuming PrimaryLinkshellId is nullable
            };

            return View(viewModel);
        }

        [HttpPost]
        public IActionResult Create(AnnouncementViewModel model)
        {
            Console.WriteLine("Linkshell ID: " + model.LinkshellId);

            if (ModelState.IsValid)
            {
                // Check if LinkshellId exists
                var linkshell = _context.Linkshells.Find(model.LinkshellId);
                if (linkshell == null)
                {
                    ModelState.AddModelError("LinkshellId", "Invalid LinkshellId.");
                    return View(model);
                }

                var announcement = new Announcement
                {
                    LinkshellId = model.LinkshellId,
                    LinkshellName = linkshell.LinkshellName,
                    AnnouncementTitle = model.AnnouncementTitle,
                    AnnouncementDetails = model.AnnouncementDetails
                };
                _context.Announcements.Add(announcement);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }
            else
            {
                foreach (var error in ModelState.Values.SelectMany(v => v.Errors))
                {
                    Console.WriteLine("ModelState Error: " + error.ErrorMessage);
                }
            }
            return View(model);
        }

    }
}