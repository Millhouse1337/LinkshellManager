using Microsoft.AspNetCore.Mvc;
using LinkshellManager.Models;
using LinkshellManager.Data;
using Microsoft.AspNetCore.Identity;
using System.Linq;
using LinkshellManager.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManager.Controllers
{
    public class ManageRevenueController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUser> _userManager;

        public ManageRevenueController(ApplicationDbContext context, UserManager<AppUser> userManager)
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

            var incomes = _context.Incomes.AsQueryable();

            if (!string.IsNullOrEmpty(linkshellName))
            {
                incomes = incomes.Where(i => i.Linkshell.LinkshellName == linkshellName);
            }

            return View(incomes.ToList());
        }

public async Task<IActionResult> AddIncome()
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

    var viewModel = new ManageRevenueViewModel
    {
        Linkshells = userLinkshells,
        LinkshellId = user.PrimaryLinkshellId ?? 0 // Assuming PrimaryLinkshellId is nullable
    };

    return View(viewModel);
}

        [HttpPost]
        public IActionResult AddIncome(Income income)
        {
            Console.WriteLine(income.LinkshellId);
            Console.WriteLine(income.LinkshellName);
            Console.WriteLine(income.MethodOfIncome);
            Console.WriteLine(income.Value);
            Console.WriteLine(income.Details);
            Console.WriteLine(income.TimeStamp);
            if (ModelState.IsValid)
            {
                var linkshell = _context.Linkshells.Find(income.LinkshellId);
                if (linkshell != null)
                {
                    income.LinkshellName = linkshell.LinkshellName;
                }
                income.TimeStamp = DateTime.UtcNow; // Set the TimeStamp to the current UTC time
                _context.Incomes.Add(income);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }
            ViewBag.Linkshells = _context.Linkshells.ToList();
            return View(income);
        }

        public IActionResult Edit(int id)
        {
            var income = _context.Incomes.Find(id);
            if (income == null)
            {
                return NotFound();
            }
            ViewBag.Linkshells = _context.Linkshells.ToList();
            return View(income);
        }

        [HttpPost]
        public IActionResult Edit(Income income)
        {
            if (ModelState.IsValid)
            {
                var linkshell = _context.Linkshells.Find(income.LinkshellId);
                if (linkshell != null)
                {
                    income.LinkshellName = linkshell.LinkshellName;
                }
                income.TimeStamp = DateTime.UtcNow; // Set the TimeStamp to the current UTC time

                _context.Incomes.Update(income);
                _context.SaveChanges();
                return RedirectToAction("Index");
            }
            ViewBag.Linkshells = _context.Linkshells.ToList();
            return View(income);
        }

        public IActionResult Delete(int id)
        {
            var income = _context.Incomes.Find(id);
            if (income == null)
            {
                return NotFound();
            }
            return View(income);
        }

        [HttpPost, ActionName("Delete")]
        public IActionResult DeleteConfirmed(int id)
        {
            var income = _context.Incomes.Find(id);
            if (income != null)
            {
                _context.Incomes.Remove(income);
                _context.SaveChanges();
            }
            return RedirectToAction("Index");
        }
    }
}