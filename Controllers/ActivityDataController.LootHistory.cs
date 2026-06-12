using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    // GET /api/activity/loot-history?source=all|tod|event&page=1&pageSize=20
    //
    // Unified list of loot rows across TodLootDetail (joined to Tod) and
    // EventLootDetail (joined to Event for active events or EventHistory for
    // closed events). Scoped to the caller's primary linkshell. The CanEdit
    // flag on each row is derived from the caller's CanAddLoot role flag so
    // the activity client can show/hide the Edit button per row.
    [HttpGet("loot-history")]
    public async Task<IActionResult> GetLootHistoryAsync(
        [FromQuery] string? source = "all",
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
        var normalizedSource = (source ?? "all").Trim().ToLowerInvariant();

        // Two materialised lists (ToD + Event), unioned and sorted by
        // occurred-at desc in memory. The dataset for a single linkshell is
        // small enough that this is simpler than crafting a UNION query
        // across two different shapes.
        var todRows = normalizedSource is "all" or "tod"
            ? await _dbContext.TodLootDetails
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
                .ToListAsync(cancellationToken)
            : new();

        var eventRows = normalizedSource is "all" or "event"
            ? await _dbContext.EventLootDetails
                .AsNoTracking()
                .Where(detail =>
                    (detail.Event != null && detail.Event.LinkshellId == linkshellId) ||
                    (detail.EventHistory != null && detail.EventHistory.LinkshellId == linkshellId))
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
                        : (detail.Event != null ? (DateTime?)(detail.Event.EndTime ?? detail.Event.CommencementStartTime ?? detail.Event.StartTime) : null),
                    detail.ItemName,
                    detail.ItemWinner,
                    detail.WinningDkpSpent,
                    detail.ActualDeductedDkp,
                    detail.EditedAt,
                    detail.EditedByCharacterName,
                    detail.LastEditReason
                })
                .ToListAsync(cancellationToken)
            : new();

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
    // LootHistoryController.Add flow exactly: a lightweight backing ToD carries
    // the loot through the shared ToD-loot DKP deduction + ManualPoints sheet
    // pipeline, so it shows in history (Source = ToD) with full edit support.
    [HttpPost("loot-history")]
    public async Task<IActionResult> AddLootHistoryAsync(
        [FromBody] ActivityLootAddRequest request,
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

        var itemName = request.ItemName?.Trim();
        var itemWinner = request.ItemWinner?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (string.IsNullOrWhiteSpace(itemWinner))
        {
            return BadRequest(new { error = "A winner is required." });
        }

        var dkpSpent = request.WinningDkpSpent.GetValueOrDefault();
        if (dkpSpent < 0)
        {
            return BadRequest(new { error = "DKP spent can't be negative." });
        }

        // Winner must be a current roster member (mirrors the web add-loot guard).
        var roster = await _dbContext.AppUserLinkshells
            .AsNoTracking()
            .Where(link => link.LinkshellId == linkshellId
                           && link.CharacterName != null && link.CharacterName != "")
            .Select(link => link.CharacterName!)
            .ToListAsync(cancellationToken);
        if (!roster.Any(name => string.Equals(name, itemWinner, StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest(new { error = "Winner must be a current linkshell member." });
        }

        var context = string.IsNullOrWhiteSpace(request.Context) ? null : request.Context!.Trim();
        var nowUtc = DateTime.UtcNow;

        var tod = new Tod
        {
            LinkshellId = linkshellId,
            MonsterName = context ?? "Manual Loot",
            Claim = true,
            Time = nowUtc,
            TimeStamp = nowUtc,
            TotalClaims = 1
        };
        var detail = new TodLootDetail
        {
            Tod = tod,
            ItemName = itemName,
            ItemWinner = itemWinner,
            WinningDkpSpent = dkpSpent
        };
        _dbContext.Tods.Add(tod);
        _dbContext.TodLootDetails.Add(detail);
        await AdjustTodLootDkpAsync(_dbContext, tod, new[] { detail }, nowUtc, isRefund: false, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { success = true, lootDetailId = detail.Id });
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
