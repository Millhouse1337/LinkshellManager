using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // GET /api/activity/loot-history?page=1&pageSize=20
    //
    // Unified list of loot rows across TodLootDetail (joined to Tod) and
    // EventLootDetail (joined to Event for active events or EventHistory for
    // closed events). Scoped to the caller's primary linkshell. The CanEdit
    // flag on each row is derived from the caller's CanAddLoot role flag so
    // the activity client can show/hide the Edit button per row.
    [HttpGet("loot-history")]
    public async Task<IActionResult> GetLootHistoryAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to view loot history." });
        }

        if (!appUser.PrimaryLinkshellId.HasValue)
        {
            return Ok(new ActivityLootHistoryListDto(1, pageSize, 0, Array.Empty<ActivityLootHistoryItemDto>()));
        }

        var linkshellId = appUser.PrimaryLinkshellId.Value;
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var canEdit = await CanAsync(membership, role => role.CanAddLoot, cancellationToken);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        // Two materialised lists (ToD + Event), unioned and sorted by
        // occurred-at desc in memory. The dataset for a single linkshell is
        // small enough that this is simpler than crafting a UNION query
        // across two different shapes.
        // Loot is no longer split by where it came from -- every new row is an EventLootDetail
        // filed against a live event, a past event, or nothing -- so the source filter went with
        // it. ToD rows are still READ: the addon and the old Log ToD form wrote real ones.
        var todRows = await _dbContext.TodLootDetails
                .AsNoTracking()
                .Where(detail => detail.Tod != null && detail.Tod.LinkshellId == linkshellId)
                .Select(detail => new
                {
                    detail.Id,
                    ParentId = detail.TodId ?? 0,
                    Context = detail.Tod!.MonsterName,
                    OccurredAt = (DateTime?)(detail.Tod.Time ?? detail.Tod.TimeStamp),
                    detail.ItemName,
                    detail.ItemWinner,
                    detail.WinningDkpSpent,
                    detail.ActualDeductedDkp,
                    detail.EditedAt,
                    detail.EditedByCharacterName,
                    detail.LastEditReason
                })
                .ToListAsync(cancellationToken);

        var eventRows = await _dbContext.EventLootDetails
                .AsNoTracking()
                // LinkshellId leads: a "No event" row has neither parent to reach a linkshell
                // through. The parent tests stay for rows written before that column existed.
                .Where(detail =>
                    detail.LinkshellId == linkshellId
                    || (detail.Event != null && detail.Event.LinkshellId == linkshellId)
                    || (detail.EventHistory != null && detail.EventHistory.LinkshellId == linkshellId))
                .Select(detail => new
                {
                    detail.Id,
                    ParentId = detail.EventHistoryId ?? detail.EventId ?? 0,
                    Context = detail.EventHistory != null
                        ? detail.EventHistory.EventName
                        : (detail.Event != null ? detail.Event.EventName : null),
                    // Live-event loot has no end time yet — order it by when the event
                    // actually went live (CommencementStartTime) so it sorts as recent,
                    // not at its scheduled StartTime (which can be far in the past/future).
                    OccurredAt = detail.EventHistory != null
                        ? (DateTime?)detail.EventHistory.EndTime
                        : (detail.Event != null ? (DateTime?)(detail.Event.EndTime ?? detail.Event.CommencementStartTime ?? detail.Event.StartTime) : detail.DkpDebitedAt),
                    detail.ItemName,
                    detail.ItemWinner,
                    detail.WinningDkpSpent,
                    detail.ActualDeductedDkp,
                    detail.EditedAt,
                    detail.EditedByCharacterName,
                    detail.LastEditReason
                })
                .ToListAsync(cancellationToken);

        var unified = todRows
            .Select(row => new ActivityLootHistoryItemDto(
                LootDetailId: row.Id,
                Source: "Tod",
                ParentId: row.ParentId,
                Context: row.Context,
                OccurredAt: row.OccurredAt,
                ItemName: row.ItemName,
                ItemWinner: row.ItemWinner,
                WinningDkpSpent: row.WinningDkpSpent,
                ActualDeductedDkp: row.ActualDeductedDkp,
                IsEdited: row.EditedAt.HasValue,
                LastEditReason: row.LastEditReason,
                EditedAt: row.EditedAt,
                EditedByCharacterName: row.EditedByCharacterName,
                CanEdit: canEdit))
            .Concat(eventRows.Select(row => new ActivityLootHistoryItemDto(
                LootDetailId: row.Id,
                Source: "Event",
                ParentId: row.ParentId,
                Context: row.Context,
                OccurredAt: row.OccurredAt,
                ItemName: row.ItemName,
                ItemWinner: row.ItemWinner,
                WinningDkpSpent: row.WinningDkpSpent,
                ActualDeductedDkp: row.ActualDeductedDkp,
                IsEdited: row.EditedAt.HasValue,
                LastEditReason: row.LastEditReason,
                EditedAt: row.EditedAt,
                EditedByCharacterName: row.EditedByCharacterName,
                CanEdit: canEdit)))
            .OrderByDescending(item => item.OccurredAt ?? DateTime.MinValue)
            .ThenByDescending(item => item.LootDetailId)
            .ToList();

        var totalCount = unified.Count;
        var pageItems = unified
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Ok(new ActivityLootHistoryListDto(page, pageSize, totalCount, pageItems));
    }

    // POST /api/activity/loot-history
    //
    // Manual loot entry for the caller's primary linkshell. Mirrors the web
    // LootHistoryController.Add flow exactly: the loot is filed against a LIVE event, a PAST
    // event, or nothing at all.
    //
    // It used to mint a throwaway ToD per submission and hang a TodLootDetail off it, which is why
    // every hand-entered drop showed up in history as source "ToD".
    [HttpPost("loot-history")]
    public async Task<IActionResult> AddLootHistoryAsync(
        [FromBody] ActivityLootAddRequest request,
        [FromServices] ManualLootService manualLoot,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to add loot." });
        }

        if (!appUser.PrimaryLinkshellId.HasValue || appUser.PrimaryLinkshellId.Value == 0)
        {
            return BadRequest(new { error = "Select a primary linkshell before adding loot." });
        }

        var linkshellId = appUser.PrimaryLinkshellId.Value;
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }
        if (!await CanAsync(membership, role => role.CanAddLoot, cancellationToken))
        {
            return Forbid();
        }

        // Everything below — roster match, affordability, the debit, and the DkpDebitedAt stamp
        // that stops a live event's close charging this a second time — is ManualLootService's,
        // shared with the web form so the two surfaces cannot drift on DKP.
        var result = await manualLoot.AddAsync(
            linkshellId,
            ManualLootTarget.Parse(request.SourceKind, request.EventId, request.EventHistoryId),
            request.ItemName,
            request.ItemWinner,
            request.WinningDkpSpent.GetValueOrDefault(),
            request.DkpPoolId,
            cancellationToken);

        if (!result.Success)
        {
            return BadRequest(new { error = result.Error ?? "Adding loot failed." });
        }

        return Ok(new { success = true, lootDetailId = result.Detail!.Id });
    }

    // GET /api/activity/loot-history/event-options?q=…
    //
    // Live events plus the most recent past ones for the Add loot pickers. Past events are capped
    // and searchable rather than listed whole: a linkshell accumulates hundreds, and a flat list
    // would put the older half out of reach.
    [HttpGet("loot-history/event-options")]
    public async Task<IActionResult> GetLootEventOptionsAsync(
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        const int RecentPastEventCount = 25;

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to add loot." });
        }
        if (!appUser.PrimaryLinkshellId.HasValue || appUser.PrimaryLinkshellId.Value == 0)
        {
            return Ok(new ActivityLootEventOptionsDto(
                Array.Empty<ActivityLootEventOptionDto>(), Array.Empty<ActivityLootEventOptionDto>(), q));
        }

        var linkshellId = appUser.PrimaryLinkshellId.Value;
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var live = await _dbContext.Events
            .AsNoTracking()
            .Where(evt => evt.LinkshellId == linkshellId)
            .OrderByDescending(evt => evt.CommencementStartTime ?? evt.StartTime)
            .Select(evt => new ActivityLootEventOptionDto(evt.Id, evt.EventName ?? "Event", evt.EventType))
            .ToListAsync(cancellationToken);

        var pastQuery = _dbContext.EventHistories
            .AsNoTracking()
            .Where(history => history.LinkshellId == linkshellId);

        var search = q?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            pastQuery = pastQuery.Where(history =>
                (history.EventName != null && EF.Functions.ILike(history.EventName, pattern))
                || (history.EventType != null && EF.Functions.ILike(history.EventType, pattern)));
        }

        var past = await pastQuery
            .OrderByDescending(history => history.StartTime ?? history.TimeStamp)
            .Take(RecentPastEventCount)
            .Select(history => new ActivityLootEventOptionDto(
                history.Id, history.EventName ?? "Event", history.EventType))
            .ToListAsync(cancellationToken);

        return Ok(new ActivityLootEventOptionsDto(live, past, search));
    }


    [HttpPost("loot-history/tod/{lootDetailId:int}/edit")]
    public async Task<IActionResult> EditTodLootHistoryAsync(
        int lootDetailId,
        [FromBody] ActivityLootEditRequest request,
        [FromServices] LootEditService lootEditService,
        CancellationToken cancellationToken)
    {
        return await EditLootInternalAsync(
            request,
            lootEditService,
            isTod: true,
            lootDetailId,
            cancellationToken);
    }

    [HttpPost("loot-history/event/{lootDetailId:int}/edit")]
    public async Task<IActionResult> EditEventLootHistoryAsync(
        int lootDetailId,
        [FromBody] ActivityLootEditRequest request,
        [FromServices] LootEditService lootEditService,
        CancellationToken cancellationToken)
    {
        return await EditLootInternalAsync(
            request,
            lootEditService,
            isTod: false,
            lootDetailId,
            cancellationToken);
    }

    private async Task<IActionResult> EditLootInternalAsync(
        ActivityLootEditRequest request,
        LootEditService lootEditService,
        bool isTod,
        int lootDetailId,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to edit loot history." });
        }

        // Resolve the parent linkshell from the loot row so we can run the
        // permission check before LootEditService touches any data.
        int? linkshellId;
        if (isTod)
        {
            linkshellId = await _dbContext.TodLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => (int?)(detail.Tod != null ? detail.Tod.LinkshellId : 0))
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            linkshellId = await _dbContext.EventLootDetails
                .Where(detail => detail.Id == lootDetailId)
                .Select(detail => detail.EventHistory != null
                    ? (int?)detail.EventHistory.LinkshellId
                    : (detail.Event != null ? (int?)detail.Event.LinkshellId : null))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (!linkshellId.HasValue || linkshellId.Value == 0)
        {
            return NotFound(new { error = "Loot record not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId.Value, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        if (!await CanAsync(membership, role => role.CanAddLoot, cancellationToken))
        {
            return Forbid();
        }

        var serviceRequest = new LootEditRequest(
            ItemName: request.ItemName,
            ItemWinner: request.ItemWinner,
            WinningDkpSpent: request.WinningDkpSpent,
            Reason: request.Reason ?? string.Empty);

        var occurredAtUtc = DateTime.UtcNow;
        LootEditResult result;
        try
        {
            result = isTod
                ? await lootEditService.EditTodLootAsync(lootDetailId, serviceRequest, appUser, occurredAtUtc, cancellationToken)
                : await lootEditService.EditEventLootAsync(lootDetailId, serviceRequest, appUser, occurredAtUtc, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        if (!result.Success)
        {
            return BadRequest(new { error = result.ErrorMessage ?? "Loot edit failed." });
        }

        return Ok(new { success = true, lootDetailId = result.LootDetailId, source = result.Source });
    }
}
