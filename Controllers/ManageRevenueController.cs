using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinkshellManagerDiscordApp.Controllers;

// Redirects the old Manage Revenue URLs to Management → Finances → Treasury.
//
// The feature moved to ManageFinancesController, which reads the treasury journal rather than the old
// RevenueEntries table. This shim exists so links people have bookmarked, pasted into Discord, or
// written into notes still land somewhere useful instead of 404ing.
//
// Nothing here touches data. The old actions were:
//   Index      -> the list, now Treasury
//   AddIncome  -> the create form, now Record
//   Edit       -> in-place editing, which no longer exists: a confirmed entry is fixed or reversed, so
//                 the id cannot be carried across and this lands on the list instead
//   Delete     -> nothing is deleted any more, so there is no POST target to preserve
//
// Views/ManageRevenue/*.cshtml and ViewModels/ManageRevenueViewModel.cs are now unreferenced and can be
// deleted.
[Authorize]
public class ManageRevenueController : Controller
{
    public IActionResult Index() => RedirectToActionPermanent("Index", "ManageFinances");

    public IActionResult AddIncome() => RedirectToActionPermanent("Record", "ManageFinances");

    // Deliberately drops the id: the old Edit rewrote an entry in place, and there is no equivalent.
    // Sending someone to a Fix form for an entry they may not have meant would be worse than the list.
    public IActionResult Edit() => RedirectToActionPermanent("Index", "ManageFinances");
}
