using LinkshellManager.Data;
using LinkshellManager.Models;
using LinkshellManager.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace LinkshellManager.Controllers
{
    public class ManageTeamController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ManageTeamController(ApplicationDbContext context)
        {
            _context = context;
        }
        
        public async Task<IActionResult> Index(int? selectedLinkshellId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            var userLinkshells = await _context.AppUserLinkshells
                .Include(ul => ul.Linkshell)
                .Where(ul => ul.AppUserId == userId)
                .Select(ul => ul.Linkshell)
                .ToListAsync();

            if (!userLinkshells.Any())
            {
                var emptyViewModel = new ManageTeamViewModel
                {
                    Linkshells = new List<Linkshell>(),
                    Members = new List<AppUserLinkshell>(), // Change this line
                    SelectedLinkshellId = 0
                };

                ViewData["Title"] = "Manage Team";
                ViewData["Message"] = "You are not part of any linkshells.";
                return View(emptyViewModel);
            }

            selectedLinkshellId ??= userLinkshells.FirstOrDefault()?.Id ?? 0;

            var members = await _context.AppUserLinkshells
                .Include(ul => ul.AppUser)
                .Where(ul => ul.LinkshellId == selectedLinkshellId)
                .ToListAsync();

            var viewModel = new ManageTeamViewModel
            {
                Linkshells = userLinkshells,
                Members = members,
                SelectedLinkshellId = selectedLinkshellId.Value
            };

            ViewData["Title"] = "Manage Team";
            return View(viewModel);
        }

        // Method to render the search view
        public IActionResult SearchPlayers()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userLinkshells = _context.AppUserLinkshells
                .Include(ul => ul.Linkshell)
                .Where(ul => ul.AppUserId == userId)
                .Select(ul => ul.Linkshell)
                .ToList();

            var viewModel = new ManageTeamViewModel
            {
                Linkshells = userLinkshells
            };

            ViewData["Title"] = "Player Search";
            return View("PlayerSearch", viewModel);
        }

        // Method to handle the search action
        [HttpPost]
        public async Task<IActionResult> SearchPlayers(string searchTerm)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var userLinkshells = await _context.AppUserLinkshells
                .Include(ul => ul.Linkshell)
                .Where(ul => ul.AppUserId == userId)
                .Select(ul => ul.Linkshell)
                .ToListAsync();

            var players = new List<AppUser>();
            if (!string.IsNullOrEmpty(searchTerm))
            {
                players = await _context.Users
                    .Where(u => u.CharacterName.Contains(searchTerm) && u.Id != userId)
                    .OrderBy(u => u.CharacterName) // Order by CharacterName alphabetically
                    .ToListAsync();
            }

            var viewModel = new ManageTeamViewModel
            {
                SearchTerm = searchTerm,
                Players = players,
                Linkshells = userLinkshells
            };

            ViewData["Title"] = "Player Search";
            return View("PlayerSearch", viewModel);
        }

        // POST: Linkshell/SendInvite
        [HttpPost]
        public async Task<IActionResult> SendInvite(string userId, int linkshellId)
        {
            var invite = new Invite
            {
                AppUserId = userId,
                LinkshellId = linkshellId,
                Status = "Pending"
            };

            _context.Invites.Add(invite);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        // POST: Linkshell/AcceptInvite
[HttpPost]
public async Task<IActionResult> AcceptInvite(int inviteId)
{
    var invite = await _context.Invites
        .Include(i => i.Linkshell) // Include the Linkshell entity
        .FirstOrDefaultAsync(i => i.Id == inviteId);

    if (invite == null)
    {
        return NotFound();
    }

    var appUser = await _context.Users.FindAsync(invite.AppUserId);
    if (appUser == null)
    {
        return NotFound();
    }

    var appUserLinkshell = new AppUserLinkshell
    {
        AppUserId = invite.AppUserId,
        LinkshellId = invite.LinkshellId,
        LinkshellDkp = 0, // Initialize DKP if needed
        DateJoined = DateTime.UtcNow, // Set the DateJoined to the current UTC time
        CharacterName = appUser.CharacterName, // Set the CharacterName from the AppUser
        Rank = "Member",
        Status = "Pending"
    };

    _context.AppUserLinkshells.Add(appUserLinkshell);
    _context.Invites.Remove(invite);
    await _context.SaveChangesAsync();

    // Set the PrimaryLinkshellId and PrimaryLinkshellName fields of the AppUser
    appUser.PrimaryLinkshellId = invite.LinkshellId;
    appUser.PrimaryLinkshellName = invite.Linkshell.LinkshellName;

    _context.Update(appUser);
    await _context.SaveChangesAsync();

    return RedirectToAction(nameof(Index));
}

        // POST: Linkshell/DeclineInvite
        [HttpPost]
        public async Task<IActionResult> DeclineInvite(int inviteId)
        {
            var invite = await _context.Invites.FindAsync(inviteId);
            if (invite == null)
            {
                return NotFound();
            }

            _context.Invites.Remove(invite);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        // GET: Linkshell/Invites
        public async Task<IActionResult> ViewInvites()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var pendingInvites = await _context.Invites
                .Include(i => i.Linkshell)
                .Include(i => i.AppUser) // Include AppUser to access CharacterName
                .Where(i => i.AppUserId == userId && i.Status == "Pending")
                .ToListAsync();

            var sentInvites = await _context.Invites
                .Include(i => i.Linkshell)
                .Include(i => i.AppUser) // Include AppUser to access CharacterName
                .Where(i => i.Linkshell.AppUserLinkshells.Any(ul => ul.AppUserId == userId) && i.Status == "Pending")
                .ToListAsync();

            var viewModel = new ManageTeamViewModel
            {
                PendingInvites = pendingInvites ?? new List<Invite>(), // Ensure the list is initialized
                SentInvites = sentInvites ?? new List<Invite>() // Ensure the list is initialized
            };

            ViewData["Title"] = "Invites";
            return View(viewModel);
        }

        [HttpPost]
        public async Task<IActionResult> UndoInvite(int inviteId)
        {
            var invite = await _context.Invites.FindAsync(inviteId);
            if (invite != null)
            {
                _context.Invites.Remove(invite);
                await _context.SaveChangesAsync();
            }
            return RedirectToAction("ViewInvites");
        }

        [HttpPost]
        public async Task<IActionResult> ModifyRankStatus(int id, string rank, string status)
        {
            var member = await _context.AppUserLinkshells.FindAsync(id);
            if (member == null)
            {
                return NotFound();
            }

            member.Rank = rank;
            member.Status = status;

            _context.Update(member);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> DkpAudit(int id, int linkshellDkp, string details)
        {
            var member = await _context.AppUserLinkshells.FindAsync(id);
            if (member == null)
            {
                return NotFound();
            }

            var dkpAudit = new DkpAudit
            {
                AppUserLinkshellId = id,
                PreviousDkp = member.LinkshellDkp,
                NewDkp = linkshellDkp,
                Details = details
            };

            member.LinkshellDkp = linkshellDkp;

            _context.DkpAudits.Add(dkpAudit);
            _context.Update(member);
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Index));
        }
    }
}
