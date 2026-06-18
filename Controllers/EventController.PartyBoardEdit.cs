using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.Utils;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinkshellManagerDiscordApp.Controllers;

// Officer party-board editing for the WEB event page (drag-drop + per-slot job edits).
// These are fetch()-called (JSON in, JSON out) with the antiforgery token in the
// configured header, so they return { success, error } rather than redirecting. They
// mirror the Activity endpoints (ActivityDataController.EventPartyBoard.cs) and delegate
// to the same EventPartyBoardEditService (which lazily clones the shared template into a
// per-event snapshot on first edit, so edits never touch the template or other events).
// Gated by CanManageLinkshell (the web's officer check, matching the existing "Clear").
public partial class EventController
{
    public sealed record EditSlotReqBody(int EventId, int SlotId, string? Role, string? MainJob, string? SubJob);
    public sealed record MoveSlotBody(int EventId, int SlotId, int TargetPartyId, int TargetIndex);
    public sealed record MoveMemberBody(int EventId, int? FromSlotId, int? ToSlotId, string? AppUserId, string? DiscordUserId);
    public sealed record AddSlotBody(int EventId, int PartyId, string? Role, string? MainJob, string? SubJob);
    public sealed record DeleteSlotBody(int EventId, int SlotId);
    public sealed record AddPartyBody(int EventId, int AllianceId, string? Name);
    public sealed record RemovePartyBody(int EventId, int PartyId);
    public sealed record RenamePartyBody(int EventId, int? AllianceId, int? PartyId, string? Name);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> EditSlotRequirement([FromBody] EditSlotReqBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.ChangeSlotRequirementAsync(
            _context, body.EventId, body.SlotId, body.Role, body.MainJob, body.SubJob, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> MoveSlot([FromBody] MoveSlotBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.MoveSlotAsync(
            _context, body.EventId, body.SlotId, body.TargetPartyId, body.TargetIndex, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> MoveMember([FromBody] MoveMemberBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.MoveMemberAsync(
            _context, body.EventId, body.FromSlotId, body.ToSlotId, body.AppUserId, body.DiscordUserId, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddBoardSlot([FromBody] AddSlotBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.AddSlotAsync(
            _context, body.EventId, body.PartyId, body.Role, body.MainJob, body.SubJob, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteBoardSlot([FromBody] DeleteSlotBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.DeleteSlotAsync(
            _context, body.EventId, body.SlotId, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddBoardParty([FromBody] AddPartyBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.AddPartyAsync(
            _context, body.EventId, body.AllianceId, body.Name, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RemoveBoardParty([FromBody] RemovePartyBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.RemovePartyAsync(
            _context, body.EventId, body.PartyId, HttpContext.RequestAborted));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RenameBoard([FromBody] RenamePartyBody body)
        => RunBoardEditAsync(body.EventId, () => EventPartyBoardEditService.RenameAsync(
            _context, body.EventId, body.AllianceId, body.PartyId, body.Name, HttpContext.RequestAborted));

    // Re-renders just the party-board panel after a fetch-driven edit, so the page's
    // live timers aren't disturbed by a full reload. Returns the same _PartyBoardPanel
    // partial the Start view uses.
    [HttpGet]
    public async Task<IActionResult> PartyBoardPartial(int eventId)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Challenge();

        var ev = await _context.Events.AsNoTracking()
            .Include(e => e.PartySetup!)
                .ThenInclude(ps => ps.Alliances).ThenInclude(a => a.Parties).ThenInclude(p => p.Slots)
            .Include(e => e.AppUserEvents)
            .FirstOrDefaultAsync(e => e.Id == eventId);
        if (ev is null || ev.PartySetup is null) return NotFound();

        var membership = await GetMembershipAsync(user.Id, ev.LinkshellId);
        if (membership is null) return Forbid();

        var signups = await EventPartySignupService.GetSignupsForEventAsync(_context, eventId, HttpContext.RequestAborted);
        var board = BuildPartySetupBoard(ev.PartySetup, signups);

        var slotNames = new HashSet<string>(
            board.Alliances.SelectMany(a => a.Parties).SelectMany(p => p.Slots)
                .Where(s => !s.IsOpen && !string.IsNullOrWhiteSpace(s.SignedUpCharacterName))
                .Select(s => s.SignedUpCharacterName!.Trim()),
            StringComparer.OrdinalIgnoreCase);
        var alsoAttending = ev.AppUserEvents
            .Where(p => !string.IsNullOrWhiteSpace(p.CharacterName) && !slotNames.Contains(p.CharacterName!.Trim()))
            .Select(p => new PartyBoardPanelViewModel.AlsoAttendingRow
            {
                AppUserId = p.AppUserId,
                CharacterName = p.CharacterName,
                JobType = p.JobType,
                JobName = p.JobName,
                SubJobName = p.SubJobName,
            })
            .ToList();

        var vm = new PartyBoardPanelViewModel
        {
            EventId = eventId,
            LinkshellId = ev.LinkshellId,
            Board = board,
            CanManageParties = CanManageLinkshell(membership),
            CurrentUserId = user.Id,
            IsLive = ev.CommencementStartTime is not null,
            SignupCharacters = SignupCharacters.ForMember(user, membership),
            RoleOptions = EventJobCatalog.JobTypeOptions.ToList(),
            MainJobOptions = EventJobCatalog.MainJobOptions.ToList(),
            SubJobOptions = EventJobCatalog.SubJobOptions.ToList(),
            AlsoAttending = alsoAttending,
            ReturnUrl = Url.Action("Start", "Event", new { eventId }) ?? string.Empty,
        };
        return PartialView("_PartyBoardPanel", vm);
    }

    private async Task<IActionResult> RunBoardEditAsync(
        int eventId, Func<Task<EventPartyBoardEditService.EditResult>> op)
    {
        var user = await RequireCurrentUserAsync();
        if (user is null) return Json(new { success = false, error = "Sign in to edit the board." });

        var ev = await _context.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId);
        if (ev is null) return Json(new { success = false, error = "Event not found." });

        var membership = await GetMembershipAsync(user.Id, ev.LinkshellId);
        if (!CanManageLinkshell(membership))
        {
            return Json(new { success = false, error = "Only leaders or officers can edit the board." });
        }

        var result = await op();
        if (!result.Success) return Json(new { success = false, error = result.Error });
        EnqueueEventBoardRefresh(eventId);
        // Push to the live change feed so other viewers (web long-poll + Activity)
        // refresh instantly. Member moves/seats already notify via the
        // EventPartySlotSignup save hook; this covers structural edits (empty-slot job
        // change, add/delete slot, add/remove party, rename) that only touch PartySetup* rows.
        HttpContext.RequestServices.GetRequiredService<LinkshellChangeNotifier>()
            .Notify(ev.LinkshellId, LinkshellChangeNotifier.Areas.Parties);
        return Json(new { success = true });
    }
}
