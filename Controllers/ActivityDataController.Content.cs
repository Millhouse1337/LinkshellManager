using System.Globalization;
using System.Net.Http.Headers;
using LinkshellManagerDiscordApp.Data;
using LinkshellManagerDiscordApp.Models;
using LinkshellManagerDiscordApp.Services;
using LinkshellManagerDiscordApp.ViewModels;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace LinkshellManagerDiscordApp.Controllers;

public sealed partial class ActivityDataController
{
    [HttpPost("linkshells/{linkshellId:int}/rules")]
    public async Task<IActionResult> CreateRuleAsync(
        int linkshellId,
        [FromBody] ActivityCreateRuleRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Rule title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Rule details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create rules."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.AsNoTracking().FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var rule = new Rule
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            RuleTitle = title,
            RuleDetails = details,
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Rules.Add(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = rule.Id });
    }

    [HttpPost("rules/{ruleId:int}/update")]
    public async Task<IActionResult> UpdateRuleAsync(
        int ruleId,
        [FromBody] ActivityCreateRuleRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Rule title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Rule details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update rules."
            });
        }

        var rule = await _dbContext.Rules.FirstOrDefaultAsync(item => item.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound(new { error = "The rule was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, rule.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        rule.RuleTitle = title;
        rule.RuleDetails = details;
        rule.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("rules/{ruleId:int}/delete")]
    public async Task<IActionResult> DeleteRuleAsync(int ruleId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete rules."
            });
        }

        var rule = await _dbContext.Rules.FirstOrDefaultAsync(item => item.Id == ruleId, cancellationToken);
        if (rule is null)
        {
            return NotFound(new { error = "The rule was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, rule.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageRules, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Rules.Remove(rule);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/announcements")]
    public async Task<IActionResult> CreateAnnouncementAsync(
        int linkshellId,
        [FromBody] ActivityCreateAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Announcement title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Announcement details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create announcements."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.AsNoTracking().FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var announcement = new Announcement
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            AnnouncementTitle = title,
            AnnouncementDetails = details,
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.Announcements.Add(announcement);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = announcement.Id });
    }

    [HttpPost("announcements/{announcementId:int}/update")]
    public async Task<IActionResult> UpdateAnnouncementAsync(
        int announcementId,
        [FromBody] ActivityCreateAnnouncementRequest request,
        CancellationToken cancellationToken)
    {
        var title = request.Title?.Trim() ?? string.Empty;
        var details = request.Details?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            return BadRequest(new { error = "Announcement title is required." });
        }
        if (string.IsNullOrWhiteSpace(details))
        {
            return BadRequest(new { error = "Announcement details are required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update announcements."
            });
        }

        var announcement = await _dbContext.Announcements.FirstOrDefaultAsync(item => item.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return NotFound(new { error = "The announcement was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, announcement.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        announcement.AnnouncementTitle = title;
        announcement.AnnouncementDetails = details;
        announcement.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("announcements/{announcementId:int}/delete")]
    public async Task<IActionResult> DeleteAnnouncementAsync(int announcementId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete announcements."
            });
        }

        var announcement = await _dbContext.Announcements.FirstOrDefaultAsync(item => item.Id == announcementId, cancellationToken);
        if (announcement is null)
        {
            return NotFound(new { error = "The announcement was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, announcement.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageAnnouncements, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Announcements.Remove(announcement);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/items")]
    public async Task<IActionResult> CreateItemAsync(
        int linkshellId,
        [FromBody] ActivityCreateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemName = request.ItemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (request.Quantity < 0)
        {
            return BadRequest(new { error = "Quantity cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.AsNoTracking().FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var now = DateTime.UtcNow;
        var item = new Item
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            ItemName = itemName,
            ItemType = string.IsNullOrWhiteSpace(request.ItemType) ? null : request.ItemType.Trim(),
            Quantity = request.Quantity,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = now,
            UpdatedAt = now
        };
        _dbContext.Items.Add(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = item.Id });
    }

    [HttpPost("items/{itemId:int}/update")]
    public async Task<IActionResult> UpdateItemAsync(
        int itemId,
        [FromBody] ActivityUpdateItemRequest request,
        CancellationToken cancellationToken)
    {
        var itemName = request.ItemName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return BadRequest(new { error = "Item name is required." });
        }
        if (request.Quantity < 0)
        {
            return BadRequest(new { error = "Quantity cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        item.ItemName = itemName;
        item.ItemType = string.IsNullOrWhiteSpace(request.ItemType) ? null : request.ItemType.Trim();
        item.Quantity = request.Quantity;
        item.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("items/{itemId:int}/delete")]
    public async Task<IActionResult> DeleteItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage items."
            });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.Items.Remove(item);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Mark an item sold for a price → record the income in the treasury (a matching
    // RevenueEntry). The item stays flagged Sold; "unsell" reverses both.
    [HttpPost("items/{itemId:int}/mark-sold")]
    public async Task<IActionResult> MarkItemSoldAsync(
        int itemId,
        [FromBody] ActivityMarkItemSoldRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to sell items." });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }
        if (item.IsSold)
        {
            return Ok(new { success = true });
        }

        var salePrice = request?.SalePrice ?? 0;
        if (salePrice < 0) { salePrice = 0; }

        var characterName = membership!.CharacterName ?? appUser.CharacterName;
        await _itemSales.RecordSaleAsync(
            item, salePrice, new TreasuryActor(appUser.Id, characterName), cancellationToken);

        return Ok(new { success = true });
    }

    [HttpPost("items/{itemId:int}/unsell")]
    public async Task<IActionResult> UnsellItemAsync(int itemId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage items." });
        }

        var item = await _dbContext.Items.FirstOrDefaultAsync(entry => entry.Id == itemId, cancellationToken);
        if (item is null)
        {
            return NotFound(new { error = "The item was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, item.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageInventory, cancellationToken))
        {
            return Forbid();
        }

        var characterName = membership!.CharacterName ?? appUser.CharacterName;
        await _itemSales.ReverseSaleAsync(
            item, new TreasuryActor(appUser.Id, characterName), cancellationToken);

        return Ok(new { success = true });
    }

    // --- Deprecated revenue routes ------------------------------------------------------------
    //
    // Kept for ONE release as shims onto the treasury endpoints, because the Angular bundle ships
    // separately from the server and there is no API versioning: a browser holding a cached bundle
    // still calls these. They advertise their own retirement with Deprecation/Sunset headers, and the
    // next release replaces the bodies with 410 Gone pointing at the treasury routes.
    //
    // The old "Income"/"Expense" pair maps onto the catch-all categories. It has to: those two words
    // said which DIRECTION gil moved and nothing about what happened, so there is no honest way to
    // guess a more specific category from them.

    [HttpPost("linkshells/{linkshellId:int}/revenue")]
    [Obsolete("Use POST linkshells/{linkshellId}/treasury/entries.")]
    public async Task<IActionResult> CreateRevenueEntryAsync(
        int linkshellId,
        [FromBody] ActivityCreateRevenueRequest request,
        CancellationToken cancellationToken)
    {
        MarkRevenueRouteDeprecated("/api/activity/linkshells/{linkshellId}/treasury/entries");

        var kind = LegacyKindFor(request.EntryType);
        if (kind is null)
        {
            return BadRequest(new { error = "Entry type must be Income or Expense." });
        }
        if (request.Value < 0)
        {
            return BadRequest(new { error = "Value cannot be negative." });
        }

        return await RecordTreasuryEntryAsync(
            linkshellId,
            new ActivityRecordTreasuryEntryRequest(
                kind,
                request.Value,
                request.OccurredAt,
                // The old free-text category is folded into the note so nothing the officer typed is
                // lost, even though it no longer classifies anything.
                Memo: JoinLegacyMemo(request.Category, request.Details),
                CounterpartyAppUserId: null,
                CounterpartyCharacterName: null,
                Confirm: true),
            cancellationToken);
    }

    // A confirmed entry cannot be edited, so an old client's "update" becomes a fix: the original is
    // reversed and a replacement recorded. That is what the caller actually wanted — the numbers to end
    // up right — and it leaves a trail instead of quietly rewriting history.
    [HttpPost("revenue/{entryId:int}/update")]
    [Obsolete("Use POST treasury/entries/{entryId}/fix.")]
    public async Task<IActionResult> UpdateRevenueEntryAsync(
        int entryId,
        [FromBody] ActivityCreateRevenueRequest request,
        CancellationToken cancellationToken)
    {
        MarkRevenueRouteDeprecated("/api/activity/treasury/entries/{entryId}/fix");

        var kind = LegacyKindFor(request.EntryType);
        if (kind is null)
        {
            return BadRequest(new { error = "Entry type must be Income or Expense." });
        }
        if (request.Value < 0)
        {
            return BadRequest(new { error = "Value cannot be negative." });
        }

        var entry = await _dbContext.JournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return NotFound(new { error = "That entry was not found." });
        }

        // A draft can still be edited in place.
        if (JournalEntryStatuses.IsDraft(entry.Status))
        {
            return await UpdateTreasuryDraftAsync(
                entryId,
                new ActivityRecordTreasuryEntryRequest(
                    kind, request.Value, request.OccurredAt,
                    JoinLegacyMemo(request.Category, request.Details), null, null, Confirm: false),
                cancellationToken);
        }

        return await FixTreasuryEntryAsync(
            entryId,
            new ActivityFixTreasuryEntryRequest(
                kind,
                request.Value,
                request.OccurredAt,
                JoinLegacyMemo(request.Category, request.Details),
                CounterpartyAppUserId: null,
                CounterpartyCharacterName: null,
                Reason: "Edited from an older version of the app."),
            cancellationToken);
    }

    // Nothing is deleted any more. An old client's "delete" reverses the entry, which is the honest
    // version of what it was asking for: the gil should not count, and the record that it once did
    // should survive.
    [HttpPost("revenue/{entryId:int}/delete")]
    [Obsolete("Use POST treasury/entries/{entryId}/reverse.")]
    public async Task<IActionResult> DeleteRevenueEntryAsync(int entryId, CancellationToken cancellationToken)
    {
        MarkRevenueRouteDeprecated("/api/activity/treasury/entries/{entryId}/reverse");

        var entry = await _dbContext.JournalEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return NotFound(new { error = "That entry was not found." });
        }

        if (JournalEntryStatuses.IsDraft(entry.Status))
        {
            return await DiscardTreasuryDraftAsync(entryId, cancellationToken);
        }

        return await ReverseTreasuryEntryAsync(
            entryId,
            new ActivityReverseTreasuryEntryRequest("Deleted from an older version of the app."),
            cancellationToken);
    }

    private static string? LegacyKindFor(string? entryType)
    {
        var normalized = entryType?.Trim() ?? string.Empty;
        if (string.Equals(normalized, "Income", StringComparison.OrdinalIgnoreCase))
        {
            return TreasuryTransactionKinds.OtherMoneyIn;
        }
        return string.Equals(normalized, "Expense", StringComparison.OrdinalIgnoreCase)
            ? TreasuryTransactionKinds.OtherMoneyOut
            : null;
    }

    private static string? JoinLegacyMemo(string? category, string? details)
    {
        var parts = new[] { category?.Trim(), details?.Trim() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var joined = string.Join(" · ", parts);
        return string.IsNullOrWhiteSpace(joined) ? null : joined;
    }

    private void MarkRevenueRouteDeprecated(string replacement)
    {
        Response.Headers["Deprecation"] = "true";
        Response.Headers["Link"] = $"<{replacement}>; rel=\"successor-version\"";
        // One release. The next one turns these into 410 Gone.
        Response.Headers["Sunset"] = DateTime.UtcNow.AddDays(30).ToString("R");
    }

    // Projects a treasury entry back into the shape the old revenue payload had, so a cached client
    // still reads a correct balance for the one release the field survives.
    private static ActivityRevenueEntryDto ToLegacyRevenueDto(JournalEntry entry)
    {
        var lines = entry.Lines.OrderBy(line => line.LineNumber).ToList();
        var cashDelta = lines
            .Where(line => line.AccountNumber == TreasuryAccounts.GilOnHand)
            .Sum(line => line.Amount);
        var category = lines
            .FirstOrDefault(line => line.AccountNumber != TreasuryAccounts.GilOnHand)?.AccountName;

        return new ActivityRevenueEntryDto(
            entry.Id,
            entry.LinkshellId,
            // The old client computed its totals by matching exactly these two words.
            cashDelta < 0 ? "Expense" : "Income",
            category,
            Math.Abs(cashDelta),
            entry.Memo,
            entry.TransactionDate,
            entry.CreatedByAppUserId,
            entry.ConfirmedByCharacterName ?? entry.CreatedByCharacterName,
            entry.CreatedAt);
    }
}
