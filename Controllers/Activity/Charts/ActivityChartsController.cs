using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Charts: the farming boards (pop and drop items, credits), the wishlist, key item progress and
// the chart holdings Treasury lists. Split out of ActivityDataController; the endpoints, URLs and
// rules are unchanged.
[ApiController]
[Route("api/activity")]
public sealed partial class ActivityChartsController : ActivityControllerBase
{
    private readonly ChartBoardService _chartBoards;
    private readonly ChartWishlistService _chartWishlist;
    private readonly ChartKeyItemService _chartKeyItems;

    public ActivityChartsController(
        ApplicationDbContext dbContext,
        DiscordIdentityService discordIdentityService,
        UserManager<AppUser> userManager,
        ILogger<ActivityChartsController> logger,
        ChartBoardService chartBoards,
        ChartWishlistService chartWishlist,
        ChartKeyItemService chartKeyItems,
        LinkshellPermissions permissions)
        : base(dbContext, discordIdentityService, userManager, logger, permissions)
    {
        _chartBoards = chartBoards;
        _chartWishlist = chartWishlist;
        _chartKeyItems = chartKeyItems;
    }
}
