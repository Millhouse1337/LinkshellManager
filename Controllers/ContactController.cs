using LinkshellManager.Data;
using LinkshellManager.Models;
using LinkshellManager.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;

namespace LinkshellManager.Controllers
{
    public class ContactController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<AppUser> _userManager;

        public ContactController(ApplicationDbContext context, UserManager<AppUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

public async Task<IActionResult> Index()
{
    var user = await _userManager.GetUserAsync(User);
    var receivedMessages = _context.AppUserMessages
        .Where(m => m.CharacterNameReceiver == user.CharacterName)
        .ToList();

    var sentMessages = _context.AppUserMessages
        .Where(m => m.CharacterNameSender == user.CharacterName)
        .ToList();

    var model = new ContactViewModel
    {
        ReceivedMessages = receivedMessages,
        SentMessages = sentMessages
    };

    ViewBag.AppUserId = user.Id;
    ViewBag.CharacterNameSender = user.CharacterName;

    return View(model);
}
[HttpGet]
public async Task<IActionResult> GetDialogue(string senderName, string receiverName)
{
    var messages = await _context.AppUserMessages
        .Where(m => (m.CharacterNameSender == senderName && m.CharacterNameReceiver == receiverName) ||
                    (m.CharacterNameSender == receiverName && m.CharacterNameReceiver == senderName))
        .OrderBy(m => m.TimeStamp)
        .ToListAsync();

    return Json(messages);
}

[HttpGet]
public async Task<IActionResult> SendMessage()
{
    var user = await _userManager.GetUserAsync(User);
    if (user == null)
    {
        throw new Exception("User not found.");
    }

    var linkshells = await _context.AppUserLinkshells
        .Where(aul => aul.AppUserId == user.Id)
        .Select(aul => aul.Linkshell)
        .ToListAsync();

    if (linkshells == null || !linkshells.Any())
    {
        throw new Exception("No linkshells found for the user.");
    }

    var linkshellMembers = new Dictionary<int, List<string>>();
    foreach (var linkshell in linkshells)
    {
        var members = await _context.AppUserLinkshells
            .Where(aul => aul.LinkshellId == linkshell.Id)
            .Select(aul => aul.AppUser.CharacterName)
            .ToListAsync();
        linkshellMembers[linkshell.Id] = members;
    }

    var model = new ContactViewModel
    {
        Linkshells = linkshells,
        LinkshellMembers = linkshellMembers,
        CharacterNameSender = user.CharacterName,
        AppUserId = user.Id
    };

    return View(model);
}

[HttpPost]
public async Task<IActionResult> SendMessage(ContactViewModel model)
{
    if (!ModelState.IsValid)
    {
        var user = await _userManager.GetUserAsync(User);
        model.Linkshells = await _context.AppUserLinkshells
            .Where(aul => aul.AppUserId == user.Id)
            .Select(aul => aul.Linkshell)
            .ToListAsync();

        return View(model);
    }

    var sender = await _userManager.GetUserAsync(User);
    var receiver = _context.Users.FirstOrDefault(u => u.CharacterName == model.ReceiverCharacterName);

    if (receiver == null)
    {
        ModelState.AddModelError(string.Empty, "Receiver not found.");
        var user = await _userManager.GetUserAsync(User);
        model.Linkshells = await _context.AppUserLinkshells
            .Where(aul => aul.AppUserId == user.Id)
            .Select(aul => aul.Linkshell)
            .ToListAsync();

        return View(model);
    }

    var message = new Message
    {
        AppUserId = sender.Id,
        CharacterNameSender = sender.CharacterName,
        MessageDetails = model.MessageDetails,
        TimeStamp = DateTime.UtcNow
    };

    _context.Messages.Add(message);
    await _context.SaveChangesAsync();

    var appUserMessage = new AppUserMessage
    {
        AppUserId = receiver.Id,
        MessageId = message.Id,
        CharacterNameSender = sender.CharacterName,
        CharacterNameReceiver = receiver.CharacterName,
        MessageDetails = model.MessageDetails,
        TimeStamp = DateTime.UtcNow
    };

    _context.AppUserMessages.Add(appUserMessage);

    // Add a new notification
    var notification = new Notification
    {
        AppUserId = receiver.Id,
        NotificationType = "Message",
        CharacterNameSender = sender.CharacterName,
        NotificationDetails = model.MessageDetails,
        CreatedAt = DateTime.UtcNow
    };

    _context.Notifications.Add(notification);
    await _context.SaveChangesAsync();

    return RedirectToAction("Index");
}

[HttpGet]
public async Task<IActionResult> GetMembers(int linkshellId)
{
    var members = await _context.AppUserLinkshells
        .Where(aul => aul.LinkshellId == linkshellId)
        .Select(aul => aul.AppUser.CharacterName)
        .ToListAsync();

    return Json(members);
}

[HttpPost]
public async Task<IActionResult> RemoveNotification(int id)
{
    var notification = await _context.Notifications.FindAsync(id);
    if (notification != null)
    {
        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync();
    }
    return RedirectToAction("Index", "Home"); // Redirect to the appropriate page
}
[HttpPost]
public async Task<IActionResult> RemoveMessage(int id)
{
    var notification = await _context.Notifications.FindAsync(id);
    if (notification != null)
    {
        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync();
    }
    return RedirectToAction("Index", "Home"); // Redirect to the appropriate page
}

    }
}