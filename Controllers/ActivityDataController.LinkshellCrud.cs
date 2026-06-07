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
    [HttpPost("linkshells")]
    public async Task<IActionResult> CreateLinkshellAsync(
        [FromBody] ActivityCreateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to create a linkshell."
            });
        }

        var trimmedName = request.Name.Trim();
        var duplicateLinkshell = await _dbContext.Linkshells
            .AnyAsync(
                linkshell => linkshell.AppUserId == appUser.Id && linkshell.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicateLinkshell)
        {
            return BadRequest(new { error = "A linkshell with that name already exists for the current app user." });
        }

        var linkshell = new Linkshell
        {
            AppUserId = appUser.Id,
            LinkshellName = trimmedName,
            Details = request.Details?.Trim(),
            Status = "Active"
        };

        _dbContext.Linkshells.Add(linkshell);
        await _dbContext.SaveChangesAsync(cancellationToken);

        _dbContext.AppUserLinkshells.Add(new AppUserLinkshell
        {
            AppUserId = appUser.Id,
            LinkshellId = linkshell.Id,
            CharacterName = appUser.CharacterName ?? appUser.UserName,
            Rank = LinkshellRanks.Leader,
            Status = "Active",
            LinkshellDkp = 0,
            DateJoined = DateTime.UtcNow
        });

        appUser.PrimaryLinkshellId ??= linkshell.Id;
        appUser.PrimaryLinkshellName ??= linkshell.LinkshellName;

        // UserManager.UpdateAsync uses the same DbContext (Identity is wired to ApplicationDbContext)
        // and calls SaveChangesAsync internally, which flushes the AppUserLinkshell add too.
        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true, linkshellId = linkshell.Id });
    }

    [HttpGet("linkshells/{linkshellId:int}")]
    public async Task<IActionResult> GetLinkshellDetailAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to load linkshell details."
            });
        }

        // GetMembershipAsync also enforces the per-linkshell Discord guild lock.
        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells
            .Include(item => item.AppUserLinkshells)
            .ThenInclude(link => link.AppUser)
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        return Ok(new ActivityLinkshellDetailDto(
            linkshell.Id,
            linkshell.LinkshellName ?? "Unknown linkshell",
            linkshell.AppUserLinkshells.Count,
            linkshell.Details,
            linkshell.Status,
            linkshell.AppUserLinkshells
                .OrderBy(link => link.CharacterName)
                .Select(link => new ActivityMemberDto(
                    link.Id,
                    link.AppUserId,
                    link.CharacterName ?? link.AppUser?.UserName ?? "Unknown member",
                    link.AppUser?.AltCharacterName1,
                    link.AppUser?.AltCharacterName2,
                    link.Rank,
                    link.Status,
                    link.LinkshellDkp,
                    link.DateJoined))
                .ToList()));
    }

    [HttpPost("linkshells/{linkshellId:int}/primary")]
    public async Task<IActionResult> SetPrimaryLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the primary linkshell."
            });
        }

        var membership = await _dbContext.AppUserLinkshells
            .Include(link => link.Linkshell)
            .AsNoTracking()
            .FirstOrDefaultAsync(link => link.AppUserId == appUser.Id && link.LinkshellId == linkshellId, cancellationToken);

        if (membership?.Linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        appUser.PrimaryLinkshellId = membership.LinkshellId;
        appUser.PrimaryLinkshellName = membership.Linkshell.LinkshellName;

        await _userManager.UpdateAsync(appUser);

        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/update")]
    public async Task<IActionResult> UpdateLinkshellAsync(
        int linkshellId,
        [FromBody] ActivityUpdateLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Linkshell name is required." });
        }

        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to update the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var trimmedName = request.Name.Trim();
        var duplicate = await _dbContext.Linkshells
            .AnyAsync(
                item => item.Id != linkshellId &&
                        item.AppUserId == linkshell.AppUserId &&
                        item.LinkshellName == trimmedName,
                cancellationToken);

        if (duplicate)
        {
            return BadRequest(new { error = "Another linkshell with that name already exists." });
        }

        linkshell.LinkshellName = trimmedName;
        linkshell.Details = request.Details?.Trim();

        if (!string.IsNullOrWhiteSpace(request.LootStructure))
        {
            var requestedStructure = request.LootStructure.Trim();
            if (!IsValidLootStructure(requestedStructure))
            {
                return BadRequest(new { error = "Loot Structure must be Dkp, LootCouncil, or Hybrid." });
            }
            linkshell.LootStructure = NormalizeLootStructure(requestedStructure);
        }

        if (!string.IsNullOrWhiteSpace(request.LinkshellType))
        {
            if (!LinkshellTypes.IsValid(request.LinkshellType.Trim()))
            {
                return BadRequest(new { error = "Linkshell Type must be SkySeaDynamis, HnmOnly, or Both." });
            }
            linkshell.LinkshellType = LinkshellTypes.Normalize(request.LinkshellType);
        }

        if (request.EnableHnmSection.HasValue) linkshell.EnableHnmSection = request.EnableHnmSection.Value;
        if (request.EnableMissions.HasValue) linkshell.EnableMissions = request.EnableMissions.Value;
        if (request.EnableAuctions.HasValue) linkshell.EnableAuctions = request.EnableAuctions.Value;
        if (request.EnableToDs.HasValue) linkshell.EnableToDs = request.EnableToDs.Value;
        if (request.EnableEndgame.HasValue) linkshell.EnableEndgame = request.EnableEndgame.Value;
        if (request.EnableEvents.HasValue) linkshell.EnableEvents = request.EnableEvents.Value;
        if (request.EnableDkp.HasValue) linkshell.EnableDkp = request.EnableDkp.Value;
        if (request.EnableItems.HasValue) linkshell.EnableItems = request.EnableItems.Value;
        if (request.EnableRevenue.HasValue) linkshell.EnableRevenue = request.EnableRevenue.Value;
        if (!string.IsNullOrWhiteSpace(request.DkpRoundingIncrement))
        {
            linkshell.DkpRoundingIncrement = NormalizeDkpRounding(request.DkpRoundingIncrement);
        }
        // null in the request = leave unchanged. An explicitly empty list
        // clears the hidden-mob list. The serializer trims, drops blanks,
        // and de-dupes so the stored value stays clean.
        if (request.HiddenTodMonsters is not null)
        {
            linkshell.HiddenTodMonsters = SerializeHiddenTodMonsters(request.HiddenTodMonsters);
        }
        // null in the request = leave unchanged. Empty/whitespace clears the lock
        // (any member may access). A non-empty value must be a Discord snowflake
        // (digits only, <= 20 chars) and locks the linkshell to that server.
        if (request.DiscordGuildId is not null)
        {
            var trimmedGuildId = request.DiscordGuildId.Trim();
            if (trimmedGuildId.Length == 0)
            {
                linkshell.DiscordGuildId = null;
            }
            else if (trimmedGuildId.Length <= 20 && trimmedGuildId.All(char.IsDigit))
            {
                linkshell.DiscordGuildId = trimmedGuildId;
            }
            else
            {
                return BadRequest(new { error = "Discord Server ID must be the numeric server ID (digits only). Leave blank to unlock." });
            }
        }

        var memberships = await _dbContext.AppUserLinkshells
            .Where(link => link.LinkshellId == linkshellId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var memberIds = memberships
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (memberIds.Count > 0)
        {
            var users = await _dbContext.Users.Where(user => memberIds.Contains(user.Id)).ToListAsync(cancellationToken);
            foreach (var user in users.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                user.PrimaryLinkshellName = trimmedName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Lock this linkshell to the Discord server the Activity is launched in.
    // The guild id is taken from the request header (the server the caller is
    // actually in) so a member can only lock to their current server, never an
    // arbitrary one. Requires the CanCustomizeLinkshell permission.
    [HttpPost("linkshells/{linkshellId:int}/lock-guild")]
    public async Task<IActionResult> LockLinkshellToGuildAsync(
        int linkshellId,
        [FromBody] ActivityLockLinkshellRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to lock the linkshell to a server." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var guildId = GetRequestGuildId();
        if (string.IsNullOrWhiteSpace(guildId))
        {
            return BadRequest(new { error = "Open the Activity inside the Discord server you want to lock to." });
        }

        // Don't let a member lock a linkshell to a server they can't currently
        // see it from (their request must originate from that guild). Since the
        // guild id is read from the header, this is implicitly satisfied, but we
        // also reject if the linkshell is already locked to a different guild
        // the caller isn't in (they'd be blocked from the overview anyway).
        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        if (IsBlockedByGuildLock(linkshell))
        {
            return Forbid();
        }

        linkshell.LockedToDiscordGuildId = guildId;
        var name = request.GuildName?.Trim();
        linkshell.LockedToDiscordGuildName = string.IsNullOrWhiteSpace(name) ? null : name;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // Remove the guild lock so the linkshell is accessible from any server again.
    [HttpPost("linkshells/{linkshellId:int}/unlock-guild")]
    public async Task<IActionResult> UnlockLinkshellGuildAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to unlock the linkshell." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        // Only someone currently in the locked guild (or with no lock) can
        // unlock — IsBlockedByGuildLock guards against unlocking from elsewhere.
        if (IsBlockedByGuildLock(linkshell))
        {
            return Forbid();
        }

        linkshell.LockedToDiscordGuildId = null;
        linkshell.LockedToDiscordGuildName = null;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    // --- Discord channel posting config (Phase 2), mirrored from the web
    // Customize page so officers can set it from the Activity too. The bot posts
    // event/auction/loot announcements directly to these channels. ---

    [HttpGet("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> GetDiscordChannelsAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage Discord channels." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = membership?.Linkshell
            ?? await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var guild = !string.IsNullOrWhiteSpace(linkshell.DiscordGuildId)
            ? linkshell.DiscordGuildId
            : linkshell.LockedToDiscordGuildId;
        var available = string.IsNullOrWhiteSpace(guild)
            ? null
            : await _discordBot.ListTextChannelsAsync(guild, cancellationToken);

        var existing = await _dbContext.LinkshellDiscordChannels
            .AsNoTracking()
            .Where(channel => channel.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        var channels = DiscordChannelPurposes.All.Select(purpose =>
        {
            var row = existing.FirstOrDefault(channel => channel.Purpose == purpose);
            return new
            {
                purpose,
                label = DiscordChannelPurposes.Label(purpose),
                channelId = row?.ChannelId,
                channelName = row?.ChannelName
            };
        }).ToList();

        return Ok(new
        {
            guildConfigured = !string.IsNullOrWhiteSpace(guild),
            availableChannels = (available ?? Array.Empty<DiscordChannelInfo>())
                .Select(channel => new { id = channel.Id, name = channel.Name })
                .ToList(),
            channels
        });
    }

    [HttpPost("linkshells/{linkshellId:int}/discord-channels")]
    public async Task<IActionResult> SaveDiscordChannelsAsync(
        int linkshellId,
        [FromBody] ActivitySaveDiscordChannelsRequest request,
        CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new { error = "Sign in to manage Discord channels." });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!await CanAsync(membership, r => r.CanCustomizeLinkshell, cancellationToken))
        {
            return Forbid();
        }

        var linkshell = membership?.Linkshell
            ?? await _dbContext.Linkshells.FirstOrDefaultAsync(item => item.Id == linkshellId, cancellationToken);
        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        var bindings = request.Channels ?? Array.Empty<ActivityDiscordChannelBinding>();
        var guild = !string.IsNullOrWhiteSpace(linkshell.DiscordGuildId)
            ? linkshell.DiscordGuildId
            : linkshell.LockedToDiscordGuildId;
        var available = string.IsNullOrWhiteSpace(guild)
            ? null
            : await _discordBot.ListTextChannelsAsync(guild, cancellationToken);

        var existing = await _dbContext.LinkshellDiscordChannels
            .Where(channel => channel.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        foreach (var purpose in DiscordChannelPurposes.All)
        {
            var chosen = bindings
                .FirstOrDefault(binding => string.Equals(binding.Purpose, purpose, StringComparison.OrdinalIgnoreCase))?
                .ChannelId?.Trim();
            var row = existing.FirstOrDefault(channel => channel.Purpose == purpose);

            // Empty / non-snowflake clears the binding for that purpose.
            var valid = !string.IsNullOrEmpty(chosen) && chosen!.Length <= 20 && chosen.All(char.IsDigit);
            if (!valid)
            {
                if (row is not null)
                {
                    _dbContext.LinkshellDiscordChannels.Remove(row);
                }
                continue;
            }

            var name = available?.FirstOrDefault(channel => channel.Id == chosen)?.Name;
            if (row is not null)
            {
                row.ChannelId = chosen!;
                row.ChannelName = name;
            }
            else
            {
                _dbContext.LinkshellDiscordChannels.Add(new LinkshellDiscordChannel
                {
                    LinkshellId = linkshellId,
                    Purpose = purpose,
                    ChannelId = chosen!,
                    ChannelName = name,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/delete")]
    public async Task<IActionResult> DeleteLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to delete the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (!IsLeader(membership))
        {
            return Forbid();
        }

        return await DeleteLinkshellCoreAsync(linkshellId, cancellationToken);
    }

    private async Task<IActionResult> DeleteLinkshellCoreAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var linkshell = await _dbContext.Linkshells
            .Include(ls => ls.AppUserLinkshells)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.AppUserEvents)
            .Include(ls => ls.Events)
                .ThenInclude(evt => evt.EventLootDetails)
            .Include(ls => ls.EventHistories)
                .ThenInclude(history => history.AppUserEventHistories)
            .FirstOrDefaultAsync(ls => ls.Id == linkshellId, cancellationToken);

        if (linkshell is null)
        {
            return NotFound(new { error = "The selected linkshell was not found." });
        }

        if (linkshell.AppUserLinkshells.Count > 1)
        {
            return BadRequest(new
            {
                error = "Remove the remaining members or transfer ownership before deleting this linkshell."
            });
        }

        if (linkshell.Events.Count > 0)
        {
            return BadRequest(new
            {
                error = "Cancel or end all active and queued events before deleting this linkshell."
            });
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        var impactedUserIds = linkshell.AppUserLinkshells
            .Where(link => !string.IsNullOrWhiteSpace(link.AppUserId))
            .Select(link => link.AppUserId!)
            .Distinct()
            .ToList();

        if (impactedUserIds.Count > 0)
        {
            var impactedUsers = await _dbContext.Users
                .Where(user => impactedUserIds.Contains(user.Id))
                .ToListAsync(cancellationToken);

            foreach (var user in impactedUsers.Where(user => user.PrimaryLinkshellId == linkshellId))
            {
                var fallback = await _dbContext.AppUserLinkshells
                    .Include(link => link.Linkshell)
                    .Where(link => link.AppUserId == user.Id && link.LinkshellId != linkshellId)
                    .OrderBy(link => link.Linkshell!.LinkshellName)
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);

                user.PrimaryLinkshellId = fallback?.LinkshellId;
                user.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
            }
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        _dbContext.AppUserLinkshells.RemoveRange(linkshell.AppUserLinkshells);
        _dbContext.AppUserEvents.RemoveRange(linkshell.Events.SelectMany(evt => evt.AppUserEvents));
        _dbContext.EventLootDetails.RemoveRange(linkshell.Events.SelectMany(evt => evt.EventLootDetails));
        _dbContext.Events.RemoveRange(linkshell.Events);
        _dbContext.AppUserEventHistories.RemoveRange(linkshell.EventHistories.SelectMany(history => history.AppUserEventHistories));
        _dbContext.EventHistories.RemoveRange(linkshell.EventHistories);
        _dbContext.Linkshells.Remove(linkshell);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { success = true });
    }

    [HttpPost("linkshells/{linkshellId:int}/leave")]
    public async Task<IActionResult> LeaveLinkshellAsync(int linkshellId, CancellationToken cancellationToken)
    {
        var appUser = await ResolveAppUserAsync(cancellationToken);
        if (appUser is null)
        {
            return Unauthorized(new
            {
                error = "Sign in with ASP.NET Identity or provide a Discord bearer token to leave the linkshell."
            });
        }

        var membership = await GetMembershipAsync(appUser.Id, linkshellId, cancellationToken);
        if (membership is null)
        {
            return NotFound(new { error = "The selected linkshell membership was not found." });
        }

        var memberCount = await _dbContext.AppUserLinkshells
            .CountAsync(link => link.LinkshellId == linkshellId, cancellationToken);

        if (IsLeader(membership) && memberCount > 1)
        {
            return BadRequest(new { error = "Leaders must transfer ownership or remove remaining members before leaving." });
        }

        if (IsLeader(membership) && memberCount == 1)
        {
            // Sole-leader leaves -> deletes the linkshell. Delegate to the shared core
            // (which runs inside its own transaction) before this method has touched
            // the change tracker, so the two flows do not interleave.
            return await DeleteLinkshellCoreAsync(linkshellId, cancellationToken);
        }

        _dbContext.AppUserLinkshells.Remove(membership);

        if (appUser.PrimaryLinkshellId == linkshellId)
        {
            var fallback = await _dbContext.AppUserLinkshells
                .Include(link => link.Linkshell)
                .Where(link => link.AppUserId == appUser.Id && link.LinkshellId != linkshellId)
                .OrderBy(link => link.Linkshell!.LinkshellName)
                .AsNoTracking()
                .FirstOrDefaultAsync(cancellationToken);

            appUser.PrimaryLinkshellId = fallback?.LinkshellId;
            appUser.PrimaryLinkshellName = fallback?.Linkshell?.LinkshellName;
        }

        var eventParticipations = await _dbContext.AppUserEvents
            .Include(participation => participation.Event)
            .Where(participation => participation.AppUserId == appUser.Id && participation.Event!.LinkshellId == linkshellId)
            .ToListAsync(cancellationToken);

        if (eventParticipations.Count > 0)
        {
            _dbContext.AppUserEvents.RemoveRange(eventParticipations);
        }

        var pendingInvites = await _dbContext.Invites
            .Where(invite => invite.LinkshellId == linkshellId && invite.AppUserId == appUser.Id)
            .ToListAsync(cancellationToken);

        if (pendingInvites.Count > 0)
        {
            _dbContext.Invites.RemoveRange(pendingInvites);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new { success = true });
    }
}
