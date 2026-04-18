using Microsoft.AspNetCore.Mvc;
using LinkshellManager.ViewModels;
using LinkshellManager.Models;
using LinkshellManager.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManager.Controllers
{
    public class RuleController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUser> _userManager;

        public RuleController(ApplicationDbContext context, UserManager<AppUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index()
        {
            var user = await _userManager.GetUserAsync(User);
            var primaryLinkshellId = user.PrimaryLinkshellId;

            var rules = await _context.Rules
                .Where(r => r.LinkshellId == primaryLinkshellId)
                .Select(r => new RuleViewModel
                {
                    Id = r.Id,
                    LinkshellId = r.LinkshellId,
                    LinkshellName = r.LinkshellName,
                    RuleTitle = r.RuleTitle,
                    RuleDetails = r.RuleDetails
                }).ToListAsync();

            return View(rules);
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

    var viewModel = new RuleViewModel
    {
        Linkshells = userLinkshells,
        LinkshellId = user.PrimaryLinkshellId ?? 0 // Assuming PrimaryLinkshellId is nullable
    };

    return View(viewModel);
}


        [HttpPost]
        public IActionResult Create(RuleViewModel model)
        {
            Console.WriteLine("Linkshell ID: " + model.LinkshellId);
            Console.WriteLine("Rule Title: " + model.RuleTitle);
            Console.WriteLine("Rule Details: " + model.RuleDetails);

            if (ModelState.IsValid)
            {
                // Check if LinkshellId exists
                var linkshell = _context.Linkshells.Find(model.LinkshellId);
                if (linkshell == null)
                {
                    ModelState.AddModelError("LinkshellId", "Invalid LinkshellId.");
                    return View(model);
                }

                var rule = new Rule
                {
                    LinkshellId = model.LinkshellId,
                    LinkshellName = linkshell.LinkshellName,
                    RuleTitle = model.RuleTitle,
                    RuleDetails = model.RuleDetails
                };
                _context.Rules.Add(rule);
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