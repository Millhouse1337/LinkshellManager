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

    [HttpPost("linkshells/{linkshellId:int}/revenue")]
    public async Task<IActionResult> CreateRevenueEntryAsync(
        int linkshellId,
        [FromBody] ActivityCreateRevenueRequest request,
        CancellationToken cancellationToken)
    {
        var entryType = request.EntryType?.Trim() ?? string.Empty;
        if (!string.Equals(entryType, "Income", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(entryType, "Expense", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = "Entry type must be Income or Expense." });
        }
        if (request.Value < 0)
        {
            return BadRequest(new { error = "Value cannot be negative." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage revenue."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageTreasury, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.AsNoTracking().FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var normalizedType = string.Equals(entryType, "Income", StringComparison.OrdinalIgnoreCase) ? "Income" : "Expense";
        var occurredAt = request.OccurredAt?.ToUniversalTime() ?? DateTime.UtcNow;
        var entry = new RevenueEntry
        {
            LinkshellId = linkshellId,
            LinkshellName = linkshell.LinkshellName,
            EntryType = normalizedType,
            Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim(),
            Value = request.Value,
            Details = string.IsNullOrWhiteSpace(request.Details) ? null : request.Details.Trim(),
            OccurredAt = occurredAt,
            CreatedByAppUserId = appUser.Id,
            CreatedByCharacterName = membership!.CharacterName ?? appUser.CharacterName,
            CreatedAt = DateTime.UtcNow
        };
        _dbContext.RevenueEntries.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true, id = entry.Id });
    }

    [HttpPost("revenue/{entryId:int}/delete")]
    public async Task<IActionResult> DeleteRevenueEntryAsync(int entryId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to manage revenue."
            });
        }

        var entry = await _dbContext.RevenueEntries.FirstOrDefaultAsync(item => item.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return NotFound(new { error = "The revenue entry was not found." });
        }

        var membership = await GetMembershipAsync(appUser.Id, entry.LinkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanManageTreasury, cancellationToken))
        {
            return Forbid();
        }

        _dbContext.RevenueEntries.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
