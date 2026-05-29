using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Legacy /Contact route surfaced in the sidebar as "Messages". The original
// ContactController and its tables (AppUserMessages, Messages) were removed
// during the project rename. This controller replaces it with a "coming soon"
// stub so the menu link doesn't 404.
[Authorize]
[Route("Messages")]
[Route("Contact")]
public sealed class MessagesController : Controller
{
    [HttpGet("")]
    [HttpGet("Index")]
    public IActionResult Index() => View();
}
